using ABI.System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Win32;
using PoiTime.WinUI.Models;
using PoiTime.WinUI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Graphics;
using Exception = System.Exception;
using TimeSpan = System.TimeSpan;
using Uri = System.Uri;

namespace PoiTime.WinUI
{
    public sealed partial class MainWindow : Window
    {
        // ======== 主页分页变量 ========
        private List<VoicePackModel> _allInstalledPacks = new();
        private ObservableCollection<VoicePackModel> _pagedInstalledPacks = new();
        private string _homeSearchQuery = "";
        private int _homeCurrentPage = 1;
        private int _homeItemsPerPage = 5;

        // ======== 窗口与服务 ========
        private AppWindow? _appWindow;
        private PoiAudioService _audioService;
        private bool _isInitializing = true;
        private bool _isUiReady;
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // ======== 下载队列机制 ========
        private bool _isDownloading = false;
        private CancellationTokenSource? _toastCts;
        private Windows.Media.Playback.MediaPlayer? _previewPlayer;
        private VoicePackModel? _currentlyPreviewingPack;
        private Queue<(VoicePackModel Model, Button UIBtn)> _downloadQueue = new();

        // ======== 下载页分页与状态 ========
        private List<VoicePackModel> _allVoicePacks = new();
        private ObservableCollection<VoicePackModel> _pagedVoicePacks = new();
        private ObservableCollection<VoicePackModel> _installedVoicePacks = new();
        private int _currentPage = 1;
        private int _itemsPerPage = 5;
        private string _searchQuery = "";

        // ======== 订阅源配置 ========
        private const string DefaultUpdateJsonUrl = "https://shamirocloud.cn/update.json";
        private const string LegacyDefaultUpdateJsonUrl = "https://cs.thorgan.icu/update.json";
        private const string UpdateJsonUrlSettingKey = "CustomUpdateJsonUrl";
        private const string MaxThreadsSettingKey = "MaxDownloadThreads";
        private const int DefaultMaxThreads = 5;
        private static readonly string SettingsFilePath = Path.Combine(AppContext.BaseDirectory, "app_settings.json");

        // XamlUI 命令绑定
        public XamlUICommand ShowWindowCommand { get; } = new XamlUICommand();
        public XamlUICommand TestVoiceCommand { get; } = new XamlUICommand();
        public XamlUICommand ExitCommand { get; } = new XamlUICommand();
        public string AppVersion => typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";

        public MainWindow()
        {
            this.InitializeComponent();
            _isUiReady = true;

            ShowWindowCommand.ExecuteRequested += (s, e) => RestoreMainWindow();
            TestVoiceCommand.ExecuteRequested += (s, e) => _audioService?.TestCurrentHourVoice();
            ExitCommand.ExecuteRequested += (s, e) => ExitApp();

            _audioService = new PoiAudioService();
            _audioService.VoiceFinished += AudioService_VoiceFinished;

            try
            {
                if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
                    this.SystemBackdrop = new MicaBackdrop();
                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    this.ExtendsContentIntoTitleBar = true;
                    this.SetTitleBar(AppTitleBar);
                }
            }
            catch (Exception exception)
            {
                StartupDiagnostics.Write("Window appearance unavailable; using system defaults", exception);
            }

            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(wndId);

            // 托盘模式判断
            var args = Environment.GetCommandLineArgs();
            bool isAutoStart = args.Contains("/autostart");

            if (_appWindow != null)
            {
                _appWindow.Resize(new SizeInt32(1100, 680));
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "favicon.ico");
                try
                {
                    if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
                }
                catch (Exception exception)
                {
                    StartupDiagnostics.Write("Window icon unavailable", exception);
                }
                _appWindow.Closing += AppWindow_Closing;

                // 如果带有自启参数，直接把窗口隐藏，只留右下角托盘
                if (isAutoStart)
                {
                    _appWindow.Hide();
                }
            }

            _audioService.LoadConfig(GetSettingFromDisk("ActiveVoicePackId") ?? "");
            _audioService.StartTimer();
            LoadAvailableShipsToUI();
        }

        // ================= 辅助方法 =================

        private string GetSavedUpdateJsonUrl()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (settings != null && settings.TryGetValue(UpdateJsonUrlSettingKey, out string? url) && !string.IsNullOrEmpty(url))
                    {
                        return url == LegacyDefaultUpdateJsonUrl ? DefaultUpdateJsonUrl : url;
                    }
                }
            }
            catch { }
            return DefaultUpdateJsonUrl;
        }

        private string? GetSettingFromDisk(string key)
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (settings != null && settings.TryGetValue(key, out string? value))
                    {
                        return value;
                    }
                }
            }
            catch { }
            return null;
        }

        private void SaveSettingToDisk(string key, string value)
        {
            try
            {
                var settings = new Dictionary<string, string>();
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                }
                settings[key] = value;

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(settings, options));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存设置失败: {ex.Message}");
            }
        }

        private void MaxThreadsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (MaxThreadsCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                SaveSettingToDisk(MaxThreadsSettingKey, item.Tag.ToString()!);
            }
        }

        // ================= 导航与加载 =================

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (!_isUiReady) return;
            if (args.SelectedItemContainer is NavigationViewItem item)
            {
                // 修复 CS8600/CS8602 警告
                string tag = item.Tag?.ToString() ?? "";

                if (HomeContent != null) HomeContent.Visibility = Visibility.Collapsed;
                if (DownloadContent != null) DownloadContent.Visibility = Visibility.Collapsed;
                if (SettingsContent != null) SettingsContent.Visibility = Visibility.Collapsed;
                if (AboutContent != null) AboutContent.Visibility = Visibility.Collapsed;

                switch (tag)
                {
                    case "Home":
                        if (HomeContent != null) ShowPage(HomeContent);
                        break;
                    case "Download":
                        if (DownloadContent != null) ShowPage(DownloadContent);
                        if (_allVoicePacks.Count == 0) LoadUpdateJsonFromServer();
                        break;
                    case "Settings":
                        if (SettingsContent != null) ShowPage(SettingsContent);
                        break;
                    case "About":
                        if (AboutContent != null) ShowPage(AboutContent);
                        break;
                }
            }
        }

        private static void ShowPage(Grid page)
        {
            page.Visibility = Visibility.Visible;
            page.Opacity = 0;

            var transform = new TranslateTransform { Y = 14 };
            page.RenderTransform = transform;

            var duration = new Duration(TimeSpan.FromMilliseconds(240));
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = easing };
            var slide = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = easing };

            Storyboard.SetTarget(fade, page);
            Storyboard.SetTargetProperty(fade, "Opacity");
            Storyboard.SetTarget(slide, transform);
            Storyboard.SetTargetProperty(slide, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Children.Add(slide);
            storyboard.Begin();
        }

        private void LoadAvailableShipsToUI()
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                _allInstalledPacks.Clear();
                string basePath = AppContext.BaseDirectory;
                string targetFolder = Path.Combine(basePath, "voices");
                string activeId = GetSettingFromDisk("ActiveVoicePackId") ?? "";

                if (Directory.Exists(targetFolder))
                {
                    var dirs = Directory.GetDirectories(targetFolder);
                    foreach (var dir in dirs)
                    {
                        string id = new DirectoryInfo(dir).Name;
                        string shipName = $"本地舰娘 ({id})";

                        string configPath = Path.Combine(dir, "config.json");
                        if (File.Exists(configPath))
                        {
                            try
                            {
                                string json = File.ReadAllText(configPath);
                                using var doc = System.Text.Json.JsonDocument.Parse(json);
                                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                                    shipName = nameProp.GetString() ?? shipName;
                            }
                            catch { }
                        }

                        _allInstalledPacks.Add(new VoicePackModel
                        {
                            Id = id,
                            Name = shipName,
                            IsInstalled = true,
                            IsActiveShip = (id == activeId)
                        });
                    }
                }
                UpdateHomePaging();
            });
        }

        private void UpdateHomePaging()
        {
            if (!_isUiReady) return;
            var filtered = _allInstalledPacks
                .Where(p => p.Name.Contains(_homeSearchQuery, StringComparison.OrdinalIgnoreCase) ||
                            p.Id.Contains(_homeSearchQuery, StringComparison.OrdinalIgnoreCase))
                .ToList();

            int totalItems = filtered.Count;
            int totalPages = (int)Math.Ceiling((double)totalItems / _homeItemsPerPage);
            if (totalPages < 1) totalPages = 1;
            if (_homeCurrentPage > totalPages) _homeCurrentPage = totalPages;

            var paged = filtered.Skip((_homeCurrentPage - 1) * _homeItemsPerPage).Take(_homeItemsPerPage);

            _pagedInstalledPacks.Clear();
            foreach (var item in paged) _pagedInstalledPacks.Add(item);

            HomePageInfoText.Text = $"{_homeCurrentPage} / {totalPages}";
            HomePrevPageBtn.IsEnabled = _homeCurrentPage > 1;
            HomeNextPageBtn.IsEnabled = _homeCurrentPage < totalPages;

            InstalledVoicePackListView.ItemsSource = _pagedInstalledPacks;
        }

        private void HomeSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUiReady) return;
            _homeSearchQuery = HomeSearchBox.Text.Trim();
            _homeCurrentPage = 1;
            UpdateHomePaging();
        }

        private void HomePrevPageBtn_Click(object sender, RoutedEventArgs e) { _homeCurrentPage--; UpdateHomePaging(); }
        private void HomeNextPageBtn_Click(object sender, RoutedEventArgs e) { _homeCurrentPage++; UpdateHomePaging(); }
        private void HomeJumpPageBtn_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(HomeJumpPageBox.Text, out int page)) { _homeCurrentPage = page; UpdateHomePaging(); }
        }
        private void HomeItemsPerPageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady) return;
            if (HomeItemsPerPageCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int limit))
            {
                _homeItemsPerPage = limit;
                _homeCurrentPage = 1;
                UpdateHomePaging();
            }
        }

        // ================= 语音测试与设置 =================

        private void TestVoiceCardBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is VoicePackModel model)
            {
                if (_previewPlayer == null)
                {
                    _previewPlayer = new Windows.Media.Playback.MediaPlayer();
                    _previewPlayer.MediaEnded += (s, args) =>
                    {
                        this.DispatcherQueue.TryEnqueue(() =>
                        {
                            _currentlyPreviewingPack?.ResetPreviewState();
                            _currentlyPreviewingPack = null;
                        });
                    };
                }

                int currentHour = DateTime.Now.Hour;
                string voicePath = PoiAudioService.GetHourlyVoicePath(model.Id, currentHour);

                if (!File.Exists(voicePath)) return;

                if (_currentlyPreviewingPack != null && _currentlyPreviewingPack.Id == model.Id)
                {
                    if (_currentlyPreviewingPack.IsPreviewPlaying)
                    {
                        _previewPlayer.Pause();
                        model.ResetPreviewState();
                        _currentlyPreviewingPack = null;
                        return;
                    }
                }

                if (_currentlyPreviewingPack != null)
                {
                    _previewPlayer.Pause();
                    _currentlyPreviewingPack.ResetPreviewState();
                }

                try
                {
                    _previewPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(voicePath));
                    _previewPlayer.Play();

                    _currentlyPreviewingPack = model;
                    model.PreviewButtonText = "停止试听";
                    model.IsPreviewPlaying = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"播放失败: {ex.Message}");
                    _previewPlayer.Pause();
                    model.ResetPreviewState();
                    _currentlyPreviewingPack = null;
                }
            }
        }

        public static Visibility GetEmptyVisibility(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public static Style? GetPreviewButtonStyle(bool isPlaying)
        {
            if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() == null) return null;

            if (isPlaying)
            {
                return App.Current.Resources["AccentButtonStyle"] as Style;
            }
            else
            {
                return null;
            }
        }

        private async void SetActiveCardBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is VoicePackModel model)
            {
                SaveSettingToDisk("ActiveVoicePackId", model.Id);
                _audioService.LoadConfig(model.Id);

                foreach (var pack in _allInstalledPacks)
                {
                    pack.IsActiveShip = (pack.Id == model.Id);
                }

                foreach (var pack in _installedVoicePacks)
                {
                    pack.IsActiveShip = (pack.Id == model.Id);
                }

                await ShowToastAsync($"已切换到 {model.Name}，整点报时语音已就绪");
            }
        }

        private void CheckAndPlaySpecialVoice()
        {
            string activeId = GetSettingFromDisk("ActiveVoicePackId") ?? "";
            if (string.IsNullOrEmpty(activeId)) return;

            string configPath = Path.Combine(AppContext.BaseDirectory, "voices", activeId, "config.json");
            if (!File.Exists(configPath)) return;

            try
            {
                string json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<VoiceConfig>(json);

                if (config?.special != null && config.special.Count > 0)
                {
                    string todayFull = DateTime.Now.ToString("yyyy-MM-dd");
                    string todayShort = DateTime.Now.ToString("MM-dd");

                    var specialVoice = config.special.FirstOrDefault(s => s.date == todayFull || s.date == todayShort);

                    if (specialVoice != null && !string.IsNullOrEmpty(specialVoice.fileName))
                    {
                        string lastPlayedDate = GetSettingFromDisk("LastSpecialVoiceDate") ?? "";

                        if (lastPlayedDate != todayFull)
                        {
                            string voicePath = Path.Combine(AppContext.BaseDirectory, "voices", activeId, specialVoice.fileName);
                            if (File.Exists(voicePath))
                            {
                                Task.Run(async () =>
                                {
                                    await Task.Delay(2000);
                                    this.DispatcherQueue.TryEnqueue(() =>
                                    {
                                        _audioService.PlayVoice(specialVoice.fileName);
                                        SaveSettingToDisk("LastSpecialVoiceDate", todayFull);
                                    });
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取特殊语音配置失败: {ex.Message}");
            }
        }

        private void AudioService_VoiceFinished(object? sender, EventArgs e) { }

        // ================= 窗口生命周期 =================

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            args.Cancel = true;
            sender.Hide();
        }

        private void HideWindow_Click(object sender, RoutedEventArgs e)
        {
            _appWindow?.Hide();
        }

        private void RestoreMainWindow()
        {
            _appWindow?.Show();
            if (_appWindow?.Presenter is OverlappedPresenter presenter)
            {
                presenter.Restore();
            }
        }

        private void ShowMainWindow_Click(object sender, RoutedEventArgs e)
        {
            RestoreMainWindow();
        }

        private void TrayIcon_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            RestoreMainWindow();
        }

        private void ExitApp()
        {
            _audioService?.StopTimer();
            TrayIcon?.Dispose();
            Application.Current.Exit();
        }

        // ================= 开机自启与提权逻辑 =================

        private bool IsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private async Task<bool> RequestAdminPrivilegesAsync()
        {
            var dialog = new ContentDialog
            {
                Title = "需要管理员权限",
                Content = "因为程序放置在受系统保护的目录中，需要管理员权限才能正常下载和配置语音包。\n是否立即以管理员身份重新启动？",
                PrimaryButtonText = "以管理员身份重启",
                CloseButtonText = "仅浏览 (不下载)",
                XamlRoot = this.Content!.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                try
                {
                    var exeName = Process.GetCurrentProcess().MainModule?.FileName;
                    if (string.IsNullOrEmpty(exeName)) return false;

                    ProcessStartInfo startInfo = new ProcessStartInfo(exeName)
                    {
                        UseShellExecute = true,
                        Verb = "runas"
                    };

                    Process.Start(startInfo);
                    ExitApp();
                    return true;
                }
                catch (Exception)
                {
                    await ShowToastAsync("已取消管理员授权，部分功能暂不可用", true);
                    return false;
                }
            }
            return false;
        }

        private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            _isInitializing = true;
            InstalledVoicePackListView.ItemsSource = _installedVoicePacks;
            AutoStartToggle.IsEnabled = false;
            HighPriorityToggle.IsEnabled = false;

            bool hadLegacyAutoStart = await Task.Run(AutoStartManager.HasLegacyAutoStartTask);
            await Task.Run(AutoStartManager.RemoveLegacyAutoStartTask);
            var autoStartMode = await Task.Run(AutoStartManager.GetCurrentMode);
            if (hadLegacyAutoStart && autoStartMode == AutoStartMode.Disabled)
            {
                AutoStartManager.SetStandardAutoStart(true);
                autoStartMode = AutoStartMode.Standard;
            }
            else if (autoStartMode == AutoStartMode.Standard)
            {
                AutoStartManager.SetStandardAutoStart(true);
            }
            UpdateAutoStartControls(autoStartMode);

            UpdateJsonUrlBox.Text = GetSavedUpdateJsonUrl();
            LoadAvailableShipsToUI();

            string? savedThreadsStr = GetSettingFromDisk(MaxThreadsSettingKey);
            int targetThreads = int.TryParse(savedThreadsStr, out int t) ? t : DefaultMaxThreads;

            foreach (ComboBoxItem item in MaxThreadsCombo.Items)
            {
                if (item.Tag?.ToString() == targetThreads.ToString())
                {
                    MaxThreadsCombo.SelectedItem = item;
                    break;
                }
            }
            LoadUpdateJsonFromServer();
            CheckAndPlaySpecialVoice();
        }

        private void UpdateAutoStartControls(AutoStartMode mode)
        {
            _isInitializing = true;
            AutoStartToggle.IsOn = mode != AutoStartMode.Disabled;
            HighPriorityToggle.IsOn = mode == AutoStartMode.HighPriority;
            HighPriorityContainer.Visibility = mode == AutoStartMode.Disabled ? Visibility.Collapsed : Visibility.Visible;
            AutoStartToggle.IsEnabled = mode != AutoStartMode.HighPriority;
            HighPriorityToggle.IsEnabled = mode != AutoStartMode.Disabled;
            _isInitializing = false;
        }

        private async void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            bool enable = AutoStartToggle.IsOn;
            AutoStartToggle.IsEnabled = false;
            try
            {
                AutoStartManager.SetStandardAutoStart(enable);
                UpdateAutoStartControls(enable ? AutoStartMode.Standard : AutoStartMode.Disabled);
                await ShowToastAsync(enable ? "已开启开机自动启动" : "已关闭开机自动启动");
            }
            catch
            {
                UpdateAutoStartControls(AutoStartManager.GetCurrentMode());
                await ShowToastAsync("启动设置保存失败，请检查系统权限", true);
            }
        }

        private async void HighPriorityToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            bool enable = HighPriorityToggle.IsOn;
            AutoStartToggle.IsEnabled = false;
            HighPriorityToggle.IsEnabled = false;

            if (!await ConfirmHighPriorityChangeAsync(enable))
            {
                UpdateAutoStartControls(AutoStartManager.GetCurrentMode());
                return;
            }

            var result = await AutoStartManager.ConfigureHighPriorityAsync(enable);
            if (result == ElevationResult.Success)
            {
                AutoStartManager.SetStandardAutoStart(!enable);
                UpdateAutoStartControls(enable ? AutoStartMode.HighPriority : AutoStartMode.Standard);
                await ShowToastAsync(enable ? "高优先级启动已启用，将在登录时更早运行" : "已切换为普通开机启动");
                return;
            }

            UpdateAutoStartControls(AutoStartManager.GetCurrentMode());
            string message = result switch
            {
                ElevationResult.Cancelled => "已取消管理员授权，启动方式未更改",
                ElevationResult.MissingHelper => "未找到启动服务组件，请完整解压程序后再试",
                _ => "启动服务配置失败，请检查安全软件拦截后重试"
            };
            await ShowToastAsync(message, result != ElevationResult.Cancelled);
        }

        private async Task<bool> ConfirmHighPriorityChangeAsync(bool enable)
        {
            var content = new StackPanel { Spacing = 18 };
            content.Children.Add(new Border
            {
                Width = 52,
                Height = 52,
                CornerRadius = new CornerRadius(16),
                Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new FontIcon
                {
                    Glyph = "\uE72E",
                    FontSize = 25,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            content.Children.Add(new TextBlock
            {
                Text = enable
                    ? "PoiTime 将安装一个 Windows 启动服务，在每次登录时更早唤醒报时程序。"
                    : "关闭高优先级需要移除 Windows 启动服务，并恢复为普通开机启动。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                LineHeight = 23
            });
            content.Children.Add(new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Child = new TextBlock
                {
                    Text = "接下来会出现 Windows UAC 提示，仅用于配置启动服务。\n主程序仍以普通权限运行，此选项不会提升运行性能。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    LineHeight = 20,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                }
            });

            var dialog = new ContentDialog
            {
                Title = enable ? "启用高优先级启动" : "关闭高优先级启动",
                Content = content,
                PrimaryButtonText = "继续并授权",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content!.XamlRoot
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private async Task ShowToastAsync(string message, bool isError = false)
        {
            _toastCts?.Cancel();
            _toastCts = new CancellationTokenSource();
            var cancellationToken = _toastCts.Token;

            ToastNotification.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
            ToastNotification.Message = message;
            ToastNotification.IsOpen = true;
            try
            {
                await Task.Delay(3200, cancellationToken);
                ToastNotification.IsOpen = false;
            }
            catch (OperationCanceledException)
            {
            }
        }

        // ================= 设置页 UI 事件 =================

        private void UpdateJsonUrlBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SaveCustomUrl();
        }

        private void UpdateJsonUrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                SaveCustomUrl();
                if (NavView != null) NavView.Focus(FocusState.Programmatic);
            }
        }

        private void SaveCustomUrl()
        {
            string url = UpdateJsonUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                url = DefaultUpdateJsonUrl;
                UpdateJsonUrlBox.Text = url;
            }
            SaveSettingToDisk(UpdateJsonUrlSettingKey, url);
        }

        private void RestoreDefaultUrl_Click(object sender, RoutedEventArgs e)
        {
            UpdateJsonUrlBox.Text = DefaultUpdateJsonUrl;
            SaveSettingToDisk(UpdateJsonUrlSettingKey, DefaultUpdateJsonUrl);
        }

        // ================= 网络与下载逻辑 =================

        private void ApplyAnnouncement(AnnouncementConfig? announcement)
        {
            AnnouncementTitleText.Text = string.IsNullOrWhiteSpace(announcement?.Title)
                ? "欢迎来到 PoiTime!"
                : announcement.Title.Trim();
            AnnouncementContentText.Text = string.IsNullOrWhiteSpace(announcement?.Content)
                ? "选择语音包、试听报时，剩下的交给后台。"
                : announcement.Content.Trim();

            var background = ColorHelper.FromArgb(255, 0x3F, 0x6F, 0x8F);
            if (TryParseColor(announcement?.BackgroundColor, out var customBackground))
            {
                background = customBackground;
            }

            double luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255;
            var foreground = luminance > 0.62 ? Colors.Black : Colors.White;
            AnnouncementCard.Background = new SolidColorBrush(background);
            AnnouncementTitleText.Foreground = new SolidColorBrush(foreground);
            AnnouncementEyebrowText.Foreground = new SolidColorBrush(foreground) { Opacity = 0.85 };
            AnnouncementContentText.Foreground = new SolidColorBrush(foreground) { Opacity = 0.85 };
        }

        private static bool TryParseColor(string? value, out Windows.UI.Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string hex = value.Trim().TrimStart('#');
            if (hex.Length == 6) hex = "FF" + hex;
            if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed))
                return false;

            color = ColorHelper.FromArgb(
                (byte)(packed >> 24),
                (byte)(packed >> 16),
                (byte)(packed >> 8),
                (byte)packed);
            return true;
        }

        private async void LoadUpdateJsonFromServer()
        {
            if (DownloadLoadingRing != null)
            {
                DownloadLoadingRing.IsActive = true;
                DownloadLoadingRing.Visibility = Visibility.Visible;
            }
            if (VoicePackListView != null) VoicePackListView.Visibility = Visibility.Collapsed;

            try
            {
                if (VoicePackListView != null) VoicePackListView.ItemsSource = _pagedVoicePacks;

                string updateJsonUrl = GetSavedUpdateJsonUrl();
                string jsonString = await _httpClient.GetStringAsync(updateJsonUrl);

                var config = JsonSerializer.Deserialize<UpdateJsonConfig>(jsonString);
                ApplyAnnouncement(config?.Announcement);
                if (config?.VoicePacks != null)
                {
                    _allVoicePacks.Clear();
                    string basePath = AppContext.BaseDirectory;

                    foreach (var pack in config.VoicePacks)
                    {
                        pack.CheckLocalStatus(basePath);
                        _allVoicePacks.Add(pack);
                    }

                    _currentPage = 1;
                    _searchQuery = SearchBox?.Text.Trim() ?? "";
                    ApplyFilterAndPaging();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取语音列表失败: {ex.Message}");
            }
            finally
            {
                if (DownloadLoadingRing != null)
                {
                    DownloadLoadingRing.IsActive = false;
                    DownloadLoadingRing.Visibility = Visibility.Collapsed;
                }
                if (VoicePackListView != null) VoicePackListView.Visibility = Visibility.Visible;
            }
        }

        private void ApplyFilterAndPaging()
        {
            if (_allVoicePacks.Count == 0) return;

            var filteredList = _allVoicePacks.Where(p =>
                string.IsNullOrEmpty(_searchQuery) ||
                p.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                p.Id.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
            ).ToList();

            int totalItems = filteredList.Count;
            int totalPages = (int)Math.Ceiling((double)totalItems / _itemsPerPage);
            if (totalPages == 0) totalPages = 1;

            if (_currentPage > totalPages) _currentPage = totalPages;
            if (_currentPage < 1) _currentPage = 1;

            var pageData = filteredList.Skip((_currentPage - 1) * _itemsPerPage).Take(_itemsPerPage).ToList();

            _pagedVoicePacks.Clear();
            foreach (var item in pageData)
            {
                _pagedVoicePacks.Add(item);
            }

            if (PageInfoText != null) PageInfoText.Text = $"{_currentPage} / {totalPages}";
            if (JumpPageBox != null) JumpPageBox.Text = _currentPage.ToString();
            if (PrevPageBtn != null) PrevPageBtn.IsEnabled = _currentPage > 1;
            if (NextPageBtn != null) NextPageBtn.IsEnabled = _currentPage < totalPages;
        }

        private void RefreshUpdateJson_Click(object sender, RoutedEventArgs e) { LoadUpdateJsonFromServer(); }
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUiReady) return;
            _searchQuery = SearchBox.Text.Trim();
            _currentPage = 1;
            ApplyFilterAndPaging();
        }
        private void PrevPageBtn_Click(object sender, RoutedEventArgs e) { _currentPage--; ApplyFilterAndPaging(); }
        private void NextPageBtn_Click(object sender, RoutedEventArgs e) { _currentPage++; ApplyFilterAndPaging(); }
        private void JumpPageBtn_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(JumpPageBox.Text.Trim(), out int targetPage))
            {
                _currentPage = targetPage;
                ApplyFilterAndPaging();
            }
            else
            {
                JumpPageBox.Text = _currentPage.ToString();
            }
        }
        private void ItemsPerPageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady) return;
            if (ItemsPerPageCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                if (int.TryParse(item.Tag.ToString(), out int newLimit))
                {
                    _itemsPerPage = newLimit;
                    _currentPage = 1;
                    ApplyFilterAndPaging();
                }
            }
        }

        private string FormatSpeed(double bytesPerSecond)
        {
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
            int unitIndex = 0;
            double speed = bytesPerSecond;

            while (speed >= 1024 && unitIndex < units.Length - 1)
            {
                speed /= 1024;
                unitIndex++;
            }
            return $"{speed:F1} {units[unitIndex]}";
        }

        private void DownloadVoiceBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is VoicePackModel model)
            {
                if (_downloadQueue.Any(q => q.Model.Id == model.Id)) return;

                btn.IsEnabled = false;
                btn.Content = "排队中...";

                _downloadQueue.Enqueue((model, btn));

                if (FloatingProgressPanel != null) FloatingProgressPanel.Visibility = Visibility.Visible;
                if (DownloadExpander != null) DownloadExpander.IsExpanded = true;

                if (!_isDownloading)
                {
                    _ = ProcessDownloadQueueAsync();
                }
                else
                {
                    if (DownloadDetailText != null) DownloadDetailText.Text = $"已加入队列，等待下载的语音包：{_downloadQueue.Count} 个";
                }
            }
        }

        private async Task ProcessDownloadQueueAsync()
        {
            _isDownloading = true;
            if (MiniProgressRing != null) MiniProgressRing.IsActive = true;

            while (_downloadQueue.Count > 0)
            {
                var task = _downloadQueue.Dequeue();
                var model = task.Model;
                var btn = task.UIBtn;
                string displayName = model.Name.Length > 12 ? model.Name.Substring(0, 11) + "..." : model.Name;

                this.DispatcherQueue.TryEnqueue(() =>
                {
                    btn.Content = "下载中...";
                    if (DownloadProgressBar != null) DownloadProgressBar.Value = 0;
                    if (DownloadProgressText != null) DownloadProgressText.Text = "正在准备文件...";
                    if (DownloadPercentText != null) DownloadPercentText.Text = "0%";
                    if (DownloadStatusTitle != null) DownloadStatusTitle.Text = $"正在下载：{displayName}";
                    if (DownloadDetailText != null) DownloadDetailText.Text = "正在连接下载服务器...";
                });

                var stopwatch = new System.Diagnostics.Stopwatch();
                long totalBytesDownloaded = 0;
                int completedTasks = 0;

                try
                {
                    string targetFolder = Path.Combine(AppContext.BaseDirectory, "voices", model.Id);
                    if (!Directory.Exists(targetFolder)) Directory.CreateDirectory(targetFolder);

                    var filesToDownload = new List<(string Url, string SavePath)>();

                    if (!model.SkipKCDownload)
                    {
                        for (int hour = 0; hour < 24; hour++)
                        {
                            filesToDownload.Add((
                                $"https://zh.kcwiki.cn/wiki/Special:Redirect/file/{model.Id}-{hour:D2}00.mp3",
                                Path.Combine(targetFolder, $"{model.Id}-{hour:D2}00.mp3")
                            ));
                        }
                    }

                    string configSavePath = Path.Combine(targetFolder, "config.json");
                    if (!string.IsNullOrEmpty(model.ConfigUrl))
                    {
                        filesToDownload.Add((model.ConfigUrl, configSavePath));
                    }

                    if (model.ExtraDownloadUrls != null && model.ExtraDownloadUrls.Count > 0)
                    {
                        foreach (string extraUrl in model.ExtraDownloadUrls)
                        {
                            string fileName = Path.GetFileName(new Uri(extraUrl).LocalPath);
                            if (string.IsNullOrEmpty(fileName)) fileName = Guid.NewGuid().ToString() + ".mp3";
                            filesToDownload.Add((extraUrl, Path.Combine(targetFolder, fileName)));
                        }
                    }

                    int totalTasks = filesToDownload.Count;
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (DownloadProgressText != null) DownloadProgressText.Text = $"0 / {totalTasks} 个文件";
                    });

                    string? savedThreadsStr = GetSettingFromDisk(MaxThreadsSettingKey);
                    int maxThreads = int.TryParse(savedThreadsStr, out int t) ? t : DefaultMaxThreads;

                    using var semaphore = new SemaphoreSlim(maxThreads);
                    var downloadTasks = new List<Task>();

                    stopwatch.Start();

                    foreach (var file in filesToDownload)
                    {
                        downloadTasks.Add(Task.Run(async () =>
                        {
                            await semaphore.WaitAsync();
                            try
                            {
                                long fileSize = await DownloadFileAsync(file.Url, file.SavePath);

                                this.DispatcherQueue.TryEnqueue(() =>
                                {
                                    totalBytesDownloaded += fileSize;
                                    completedTasks++;

                                    double percentage = ((double)completedTasks / totalTasks) * 100;
                                    if (DownloadProgressBar != null) DownloadProgressBar.Value = percentage;
                                    if (DownloadProgressText != null) DownloadProgressText.Text = $"{completedTasks} / {totalTasks} 个文件";
                                    if (DownloadPercentText != null) DownloadPercentText.Text = $"{percentage:F0}%";

                                    string queueInfo = _downloadQueue.Count > 0 ? $" · 队列 {_downloadQueue.Count} 个" : "";
                                    if (DownloadStatusTitle != null) DownloadStatusTitle.Text = $"正在下载：{displayName}{queueInfo}";

                                    double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                                    if (elapsedSeconds > 0)
                                    {
                                        double bytesPerSecond = totalBytesDownloaded / elapsedSeconds;
                                        if (DownloadDetailText != null) DownloadDetailText.Text = $"平均下载速度：{FormatSpeed(bytesPerSecond)}";
                                    }
                                });
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }));
                    }

                    await Task.WhenAll(downloadTasks);
                    stopwatch.Stop();

                    if (string.IsNullOrEmpty(model.ConfigUrl))
                    {
                        GenerateDefaultConfig(model, configSavePath);
                    }

                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        model.IsInstalled = true;
                        btn.Content = "下载";
                        btn.IsEnabled = true;
                        LoadAvailableShipsToUI();
                    });
                }
                catch (UnauthorizedAccessException)
                {
                    stopwatch.Stop();
                    this.DispatcherQueue.TryEnqueue(async () =>
                    {
                        btn.Content = "权限不足";
                        btn.IsEnabled = true;

                        _downloadQueue.Clear();
                        _isDownloading = false;
                        if (FloatingProgressPanel != null) FloatingProgressPanel.Visibility = Visibility.Collapsed;

                        if (!IsAdministrator())
                        {
                            await RequestAdminPrivilegesAsync();
                        }
                        else
                        {
                            await ShowToastAsync("管理员也无法写入该目录，请将程序移到可写位置", true);
                        }
                    });
                    return;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        btn.Content = "失败(重试)";
                        btn.IsEnabled = true;
                        if (DownloadStatusTitle != null) DownloadStatusTitle.Text = $"下载失败：{displayName}";
                        if (DownloadDetailText != null) DownloadDetailText.Text = "请检查网络连接，然后点击语音包上的重试按钮";
                    });
                    System.Diagnostics.Debug.WriteLine($"下载 {model.Name} 失败: {ex.Message}");
                }
            }

            _isDownloading = false;
            if (DownloadStatusTitle != null) DownloadStatusTitle.Text = "所有队列下载完成";
            if (DownloadProgressText != null) DownloadProgressText.Text = "全部文件处理完成";
            if (DownloadPercentText != null) DownloadPercentText.Text = "100%";
            if (DownloadDetailText != null) DownloadDetailText.Text = "所选语音包已安装，可以返回主页试听";
            if (MiniProgressRing != null) MiniProgressRing.IsActive = false;
            if (DownloadProgressBar != null) DownloadProgressBar.Value = 100;

            await Task.Delay(3000);
            if (!_isDownloading && FloatingProgressPanel != null)
            {
                FloatingProgressPanel.Visibility = Visibility.Collapsed;
            }
        }

        private async void DeleteVoiceBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is VoicePackModel model)
            {
                var dialog = new ContentDialog
                {
                    Title = "确认删除",
                    Content = $"确定要删除 {model.Name} 的本地语音包吗？\n删除后如果当前正在使用该舰娘，需要手动切换。",
                    PrimaryButtonText = "确认删除",
                    CloseButtonText = "取消",
                    XamlRoot = this.Content!.XamlRoot
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    try
                    {
                        string targetFolder = Path.Combine(AppContext.BaseDirectory, "voices", model.Id);
                        if (Directory.Exists(targetFolder))
                        {
                            Directory.Delete(targetFolder, true);
                        }

                        model.IsInstalled = false;
                        LoadAvailableShipsToUI();
                    }
                    catch (UnauthorizedAccessException)
                    {
                        if (!IsAdministrator())
                        {
                            await RequestAdminPrivilegesAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"删除失败: {ex.Message}");
                    }
                }
            }
        }

        private async void OpenCustomDownload_Click(object sender, RoutedEventArgs e)
        {
            var panel = new StackPanel { Spacing = 10 };
            var idBox = new TextBox { Header = "语音包 ID (如: 007a)", Text = "007a" };
            var nameBox = new TextBox { Header = "语音包名称 (如: 加贺)", Text = "加贺" };
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 24, Value = 0, Visibility = Visibility.Collapsed };
            var statusText = new TextBlock { Text = "准备下载...", Visibility = Visibility.Collapsed };

            panel.Children.Add(idBox);
            panel.Children.Add(nameBox);
            panel.Children.Add(progressBar);
            panel.Children.Add(statusText);

            var dialog = new ContentDialog
            {
                Title = "自定义下载",
                Content = panel,
                PrimaryButtonText = "开始拉取",
                CloseButtonText = "取消",
                XamlRoot = this.Content!.XamlRoot
            };

            dialog.PrimaryButtonClick += async (s, args) =>
            {
                string shipId = idBox.Text.Trim();
                string shipName = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(shipId) || string.IsNullOrEmpty(shipName)) return;

                var deferral = args.GetDeferral();
                args.Cancel = true;

                dialog.IsPrimaryButtonEnabled = false;
                idBox.IsEnabled = false;
                nameBox.IsEnabled = false;
                progressBar.Visibility = Visibility.Visible;
                statusText.Visibility = Visibility.Visible;

                try
                {
                    string targetFolder = Path.Combine(AppContext.BaseDirectory, "voices", shipId);
                    if (!Directory.Exists(targetFolder))
                    {
                        Directory.CreateDirectory(targetFolder);
                    }

                    for (int hour = 0; hour < 24; hour++)
                    {
                        string fileName = $"{shipId}-{hour:D2}00.mp3";
                        statusText.Text = $"正在下载 {fileName} ({hour + 1}/24)";
                        progressBar.Value = hour + 1;

                        string url = $"https://zh.kcwiki.cn/wiki/Special:Redirect/file/{fileName}";
                        string savePath = Path.Combine(targetFolder, fileName);

                        await DownloadFileAsync(url, savePath);
                    }

                    var tempModel = new VoicePackModel { Id = shipId, Name = shipName };
                    GenerateDefaultConfig(tempModel, Path.Combine(targetFolder, "config.json"));

                    statusText.Text = "下载完成！";
                    await Task.Delay(1000);
                    dialog.Hide();

                    LoadAvailableShipsToUI();
                }
                catch (Exception ex)
                {
                    statusText.Text = $"下载遇到意外错误: {ex.Message}";
                    dialog.IsPrimaryButtonEnabled = true;
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
        }

        private async Task<long> DownloadFileAsync(string url, string savePath)
        {
            try
            {
                using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    long totalBytes = response.Content.Headers.ContentLength ?? 0;

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        await stream.CopyToAsync(fs);
                        return fs.Length;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"文件缺失或下载失败: {url}, 错误: {ex.Message}");
                return 0;
            }
        }

        private void GenerateDefaultConfig(VoicePackModel model, string savePath)
        {
            var config = new VoiceConfig
            {
                voiceCount = 24,
                name = model.Name,
                voices = Enumerable.Range(0, 24).Select(h => new VoiceItem
                {
                    hour = h,
                    minute = 0,
                    fileName = $"{model.Id}-{h:D2}00.mp3"
                }).ToList()
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(savePath, JsonSerializer.Serialize(config, options));
        }
    }

    public class UpdateJsonConfig
    {
        [JsonPropertyName("voice_packs")]
        public ObservableCollection<VoicePackModel> VoicePacks { get; set; } = new();

        [JsonPropertyName("announcement")]
        public AnnouncementConfig? Announcement { get; set; }
    }

    public class AnnouncementConfig
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("background_color")]
        public string? BackgroundColor { get; set; }
    }

    public class VoicePackModel : INotifyPropertyChanged
    {
        private bool _isActiveShip;
        [JsonIgnore]
        public bool IsActiveShip
        {
            get => _isActiveShip;
            set
            {
                if (_isActiveShip != value)
                {
                    _isActiveShip = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ActiveButtonText));
                    OnPropertyChanged(nameof(IsActiveButtonEnabled));
                }
            }
        }

        [JsonIgnore]
        public string ActiveButtonText => IsActiveShip ? "当前" : "设置";

        [JsonIgnore]
        public bool IsActiveButtonEnabled => !IsActiveShip;

        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("config_url")]
        public string? ConfigUrl { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("extra_download_urls")]
        public List<string>? ExtraDownloadUrls { get; set; }

        [JsonPropertyName("special_tags")]
        public List<string>? SpecialTags { get; set; }

        [JsonPropertyName("skip_kcdownload")]
        public bool SkipKCDownload { get; set; } = false;

        [JsonIgnore]
        public string DisplayId => $"ID: {Id}";

        [JsonIgnore]
        public Visibility TagsVisibility => (SpecialTags != null && SpecialTags.Count > 0) ? Visibility.Visible : Visibility.Collapsed;

        private bool _isInstalled;
        [JsonIgnore]
        public bool IsInstalled
        {
            get => _isInstalled;
            set { if (_isInstalled != value) { _isInstalled = value; OnPropertyChanged(); OnPropertyChanged(nameof(DownloadBtnVisibility)); OnPropertyChanged(nameof(DeleteBtnVisibility)); } }
        }

        [JsonIgnore]
        public Visibility DownloadBtnVisibility => IsInstalled ? Visibility.Collapsed : Visibility.Visible;

        [JsonIgnore]
        public Visibility DeleteBtnVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;

        private string _previewButtonText = "试听报时";
        [JsonIgnore]
        public string PreviewButtonText
        {
            get => _previewButtonText;
            set { if (_previewButtonText != value) { _previewButtonText = value; OnPropertyChanged(); } }
        }

        private bool _isPreviewPlaying = false;
        [JsonIgnore]
        public bool IsPreviewPlaying
        {
            get => _isPreviewPlaying;
            set { if (_isPreviewPlaying != value) { _isPreviewPlaying = value; OnPropertyChanged(); } }
        }

        public void ResetPreviewState()
        {
            PreviewButtonText = "试听报时";
            IsPreviewPlaying = false;
        }

        public void CheckLocalStatus(string basePath)
        {
            string folder = Path.Combine(basePath, "voices", Id);
            IsInstalled = Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
