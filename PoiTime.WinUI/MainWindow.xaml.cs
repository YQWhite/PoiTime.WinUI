using ABI.System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using PoiTime.WinUI.Models;
using PoiTime.WinUI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
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
        // ======== 报时核心变量 ========
        private Microsoft.UI.Xaml.DispatcherTimer? _hourlyTimer; // 解决 _hourlyTimer 不存在的问题 
        private int _lastChimeHour = -1; // 记录上次报时的小时，防止重复报时或漏报
        // ======== 主页分页变量 ========
        private List<VoicePackModel> _allInstalledPacks = new(); // 完整的本地列表
        private ObservableCollection<VoicePackModel> _pagedInstalledPacks = new(); // 当前页显示的列表
        private string _homeSearchQuery = "";
        private int _homeCurrentPage = 1;
        private int _homeItemsPerPage = 5;
        private AppWindow? _appWindow;
        private PoiAudioService _audioService;
        // ======== 新增：用来防止开关事件被错误触发的锁 ========
        private bool _isInitializing = false;
        // ======== 新增 1：复用 HttpClient 提高下载性能 ========
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // ======== 核心升级：下载队列机制 ========
        // 标记当前是否正在执行下载任务
        private bool _isDownloading = false;
        // ======== 核心修改：全局语音预览排他性管理器 ========
        // 使用一个全局唯一的 MediaPlayer，保证同一时间只能播放一个语音
        private Windows.Media.Playback.MediaPlayer? _previewPlayer;
        // 记录当前正在测试哪个舰娘，用于点击新的时重置旧的名字
        private VoicePackModel? _currentlyPreviewingPack;

        // 存放排队任务的队列。这里用一个简单的 Tuple 记录数据模型和它对应的 UI 按钮
        private Queue<(VoicePackModel Model, Button UIBtn)> _downloadQueue = new();

        // ======== 核心修改：分页与搜索状态变量 ========
        // 存放从服务器拉下来的所有原始数据
        private List<VoicePackModel> _allVoicePacks = new();
        // 仅仅存放当前页、过滤后要显示在 UI 上的数据
        private ObservableCollection<VoicePackModel> _pagedVoicePacks = new();
        // ======== 主页已安装列表 ========
        private ObservableCollection<VoicePackModel> _installedVoicePacks = new();

        private int _currentPage = 1;
        private int _itemsPerPage = 5;
        private string _searchQuery = "";
        // ======== 订阅源配置 ========
        private const string DefaultUpdateJsonUrl = "https://cs.thorgan.icu/update.json";
        private const string UpdateJsonUrlSettingKey = "CustomUpdateJsonUrl";
        private const string MaxThreadsSettingKey = "MaxDownloadThreads";
        private const int DefaultMaxThreads = 5; // 默认推荐 5 线程
        // 新增：便携版配置文件路径
        private static readonly string SettingsFilePath = Path.Combine(AppContext.BaseDirectory, "app_settings.json");

        // 辅助方法：从本地文件读取设置
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
                        return url;
                    }
                }
            }
            catch { } // 如果读取失败或文件损坏，静默处理，返回默认值
            return DefaultUpdateJsonUrl;
        }

        // 辅助方法：将设置保存到本地文件
        // 辅助方法：统一从本地文件读取配置
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

        // 辅助方法：统一将配置保存到本地
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

        // 线程下拉框的值改变时触发保存
        private void MaxThreadsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (MaxThreadsCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                SaveSettingToDisk(MaxThreadsSettingKey, item.Tag.ToString()!);
            }
        }

        // ======== 新增 2：存放服务器解析出来的语音包列表 ========
        private ObservableCollection<VoicePackModel> _availableVoicePacks = new();
        public XamlUICommand ShowWindowCommand { get; } = new XamlUICommand();
        // ======== 核心修复 2：使用原生的 XamlUICommand 处理托盘事件 ========
        public XamlUICommand TestVoiceCommand { get; } = new XamlUICommand();
        public XamlUICommand ExitCommand { get; } = new XamlUICommand();
        public MainWindow()
        {
            // 1. 最先执行，让 XAML 把所有 UI 控件（包括 TextBlock）都安全地创建出来
            this.InitializeComponent();
            ShowWindowCommand.ExecuteRequested += (s, e) => RestoreMainWindow();
            TestVoiceCommand.ExecuteRequested += (s, e) => _audioService?.TestCurrentHourVoice();
            ExitCommand.ExecuteRequested += (s, e) => ExitApp();

            // 2. 控件创建完后，再实例化后台音频服务
            _audioService = new PoiAudioService();
            _audioService.VoiceFinished += AudioService_VoiceFinished;

            // 3. 设置窗口样式和大小
            this.SystemBackdrop = new MicaBackdrop();
            this.ExtendsContentIntoTitleBar = true;

            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(wndId);
            if (_appWindow != null)
            {
                _appWindow.Resize(new SizeInt32(1000, 600));
                _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "favicon.ico"));
                _appWindow.Closing += AppWindow_Closing;
            }

            // 4. 启动定时器
            _audioService.StartTimer();

            // 5. 最后一步：手动选中下拉框的第一项。
            // 这时所有的控件和服务都已经准备就绪，触发 SelectionChanged 事件绝对安全！
            LoadAvailableShipsToUI();
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer is NavigationViewItem item)
            {
                string tag = item.Tag.ToString();

                HomeContent.Visibility = Visibility.Collapsed;
                DownloadContent.Visibility = Visibility.Collapsed;
                SettingsContent.Visibility = Visibility.Collapsed;
                AboutContent.Visibility = Visibility.Collapsed; // 加上关于也隐藏

                // 核心：处理关于页面导航
                switch (tag)
                {
                    case "Home": HomeContent.Visibility = Visibility.Visible; break;
                    case "Download":
                        DownloadContent.Visibility = Visibility.Visible;
                        if (_allVoicePacks.Count == 0) LoadUpdateJsonFromServer();
                        break;
                    case "Settings": SettingsContent.Visibility = Visibility.Visible; break;
                    case "About": AboutContent.Visibility = Visibility.Visible; break; // 显示关于
                }
            }
        }

        private void LoadShipConfig(string shipId)
        {
            if (_audioService == null) return;

            // 只保留核心的加载逻辑，删除了所有关于 CurrentShipTextBlock 的 UI 更新
            _audioService.LoadConfig(shipId);

            // 如果你想在后台确认一下加载结果，可以用 Debug 打印
            if (_audioService.CurrentConfig != null)
            {
                System.Diagnostics.Debug.WriteLine($"后台已成功切换舰娘: {_audioService.CurrentConfig.name} (ID: {shipId})");
            }
        }

        // 扫描本地已安装的舰娘，刷新主页列表
        // 扫描本地已安装的舰娘
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

                        // 读取本地 config.json 获取名字
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
                UpdateHomePaging(); // 触发分页显示
            });
        }

        // 主页分页与过滤核心逻辑
        private void UpdateHomePaging()
        {
            // 1. 过滤
            var filtered = _allInstalledPacks
                .Where(p => p.Name.Contains(_homeSearchQuery, StringComparison.OrdinalIgnoreCase) ||
                            p.Id.Contains(_homeSearchQuery, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // 2. 计算页数
            int totalItems = filtered.Count;
            int totalPages = (int)Math.Ceiling((double)totalItems / _homeItemsPerPage);
            if (totalPages < 1) totalPages = 1;
            if (_homeCurrentPage > totalPages) _homeCurrentPage = totalPages;

            // 3. 分页切片
            var paged = filtered.Skip((_homeCurrentPage - 1) * _homeItemsPerPage).Take(_homeItemsPerPage);

            // 4. 更新 UI
            _pagedInstalledPacks.Clear();
            foreach (var item in paged) _pagedInstalledPacks.Add(item);

            HomePageInfoText.Text = $"{_homeCurrentPage} / {totalPages}";
            HomePrevPageBtn.IsEnabled = _homeCurrentPage > 1;
            HomeNextPageBtn.IsEnabled = _homeCurrentPage < totalPages;

            // 确保 ListView 绑定了正确的集合
            InstalledVoicePackListView.ItemsSource = _pagedInstalledPacks;
        }
        private void HomeSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
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
            if (HomeItemsPerPageCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int limit))
            {
                _homeItemsPerPage = limit;
                _homeCurrentPage = 1;
                UpdateHomePaging();
            }
        }
        // ================= 主页卡片事件：测试语音 =================
        // ================= 核心修改 5：主页卡片测试按钮 - 独占式切换逻辑 =================
        private void TestVoiceCardBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is VoicePackModel model)
            {
                // 1. 初始化全局预览播放器
                if (_previewPlayer == null)
                {
                    _previewPlayer = new Windows.Media.Playback.MediaPlayer();
                    // 绑定 MediaEnded 事件：播放结束时自动重置按钮文字
                    _previewPlayer.MediaEnded += (s, args) =>
                    {
                        this.DispatcherQueue.TryEnqueue(() =>
                        {
                            _currentlyPreviewingPack?.ResetPreviewState();
                            _currentlyPreviewingPack = null;
                        });
                    };
                }

                // 计算当前时间要播放的文件路径
                int currentHour = DateTime.Now.Hour;
                string fileName = $"{model.Id}-{currentHour:D2}00.mp3";
                string voicePath = Path.Combine(AppContext.BaseDirectory, "voices", model.Id, fileName);

                if (!File.Exists(voicePath))
                {
                    // 可以选择在这里给个弱提示，告诉用户当前小时没语音
                    return;
                }

                // ================= 独占式切换逻辑核心 =================

                // 情况 A: 用户点击的是当前正在播放的同一个按钮 -> 切换为【停止】
                if (_currentlyPreviewingPack != null && _currentlyPreviewingPack.Id == model.Id)
                {
                    // 检查一下是不是真的在播（防止异常情况）
                    if (_currentlyPreviewingPack.IsPreviewPlaying)
                    {
                        _previewPlayer.Pause();
                        model.ResetPreviewState(); // 恢复为“测试当前时间报时”
                        _currentlyPreviewingPack = null;
                        return; // 点击同一按钮切换停止，逻辑结束
                    }
                }

                // 情况 B: 用户点击了一个新的按钮，或者之前没东西在播 -> 开始【播放新的】

                // 2. 核心：实现“独占播放”。在播新的之前，把旧的干掉。
                if (_currentlyPreviewingPack != null)
                {
                    _previewPlayer.Pause();
                    _currentlyPreviewingPack.ResetPreviewState(); // 悄悄把上一个舰娘卡片的按钮重置干净
                }

                // 3. 加载并播放新的语音
                try
                {
                    _previewPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(voicePath));
                    _previewPlayer.Play();

                    // 4. 更新 UI 状态
                    _currentlyPreviewingPack = model; // 记录新的当前预览
                    model.PreviewButtonText = "停止试听"; // 改变文字
                    model.IsPreviewPlaying = true; // 改变颜色样式
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

        // XAML 用于根据 IsPreviewPlaying 动态切换按钮样式的辅助方法
        public static Style GetPreviewButtonStyle(bool isPlaying)
        {
            // 获取资源字典中的 AccentButtonStyle 
            // （利用主线程的 DispatcherQueue 访问，确保不跨线程出错）
            if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() == null) return null;

            if (isPlaying)
            {
                // 播放时，使用蓝色的 AccentButtonStyle 高亮显示
                return App.Current.Resources["AccentButtonStyle"] as Style;
            }
            else
            {
                // 暂停或未播放时，使用默认样式
                return null;
            }
        }

        // ================= 设为当前秘书舰 =================
        private async void SetActiveCardBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is VoicePackModel model)
            {
                // 1. 保存设置
                SaveSettingToDisk("ActiveVoicePackId", model.Id);
                // 核心修复：更新全局数据源中的所有状态
                foreach (var pack in _allInstalledPacks)
                {
                    pack.IsActiveShip = (pack.Id == model.Id);
                }

                // 2. 遍历更新列表中的状态（这会让被点的按钮瞬间变成“当前秘书舰”并灰掉，其他的恢复蓝底）
                foreach (var pack in _installedVoicePacks)
                {
                    pack.IsActiveShip = (pack.Id == model.Id);
                }

                // 3. 呼出顶部绿色成功弹窗 (不再使用右下角的下载面板)
                ToastText.Text = $"成功加载 {model.Name} 语音包";
                ToastNotification.Visibility = Visibility.Visible;

                // 3秒后自动隐藏
                await Task.Delay(3000);
                ToastNotification.Visibility = Visibility.Collapsed;
            }
        }

        private void _hourlyTimer_Tick(object? sender, object e)
        {
            var now = DateTime.Now;

            // 逻辑：如果当前小时变了，且刚好是第 0 分钟，则报时
            if (now.Hour != _lastChimeHour && now.Minute == 0)
            {
                _lastChimeHour = now.Hour; // 立即标记，防止在一分钟内重复触发
                PlayHourlyVoice(now.Hour);
            }
        }

        private void PlayHourlyVoice(int hour)
        {
            string activeId = GetSettingFromDisk("ActiveVoicePackId") ?? "";
            if (string.IsNullOrEmpty(activeId)) return;

            string fileName = $"{activeId}-{hour:D2}00.mp3";
            string voicePath = Path.Combine(AppContext.BaseDirectory, "voices", activeId, fileName);

            if (File.Exists(voicePath))
            {
                // 调用你封装好的音频服务播放
                _audioService.PlayVoice(voicePath);
            }
        }

        // ================= 窗口生命周期与托盘逻辑 =================

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            // 点击右上角 X 时，取消关闭，改为隐藏窗口
            args.Cancel = true;
            _appWindow.Hide();
        }

        private void HideWindow_Click(object sender, RoutedEventArgs e)
        {
            _appWindow.Hide();
        }

        private void RestoreMainWindow()
        {
            _appWindow.Show();
            if (_appWindow.Presenter is OverlappedPresenter presenter)
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

        // ================= 功能逻辑 =================

        // ================= 功能逻辑 =================


        private void AudioService_VoiceFinished(object? sender, EventArgs e)
        {
            // 旧版的全局按钮已删除，UI 恢复逻辑已废弃。
            // 保持方法为空即可（以防你的 _audioService 还在事件里绑定着它）
        }

        // ================= 开机自启逻辑 (修复版) =================

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            // 绑定主页的已安装列表
            InstalledVoicePackListView.ItemsSource = _installedVoicePacks;
            _isInitializing = true;

            // 还原开机自启开关状态 (保留你原有的逻辑)
            AutoStartToggle.IsOn = CheckAutoStartRegistry();

            // 修复：改用我们自己写的本地读取方法
            UpdateJsonUrlBox.Text = GetSavedUpdateJsonUrl();
            LoadAvailableShipsToUI();

            // 3. 初始化报时计时器
            _hourlyTimer = new DispatcherTimer();
            _hourlyTimer.Interval = TimeSpan.FromSeconds(1);
            _hourlyTimer.Tick += _hourlyTimer_Tick;
            _hourlyTimer.Start();

            _isInitializing = false;
            // 3. 还原多线程设置
            string? savedThreadsStr = GetSettingFromDisk(MaxThreadsSettingKey);
            int targetThreads = int.TryParse(savedThreadsStr, out int t) ? t : DefaultMaxThreads;

            // 在 ComboBox 中找到对应的项并选中
            foreach (ComboBoxItem item in MaxThreadsCombo.Items)
            {
                if (item.Tag?.ToString() == targetThreads.ToString())
                {
                    MaxThreadsCombo.SelectedItem = item;
                    break;
                }
            }
        }

        // 当输入框失去焦点时，自动保存链接
        private void UpdateJsonUrlBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SaveCustomUrl();
        }

        // 当在输入框里按下回车时，也自动保存链接
        private void UpdateJsonUrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                SaveCustomUrl();

                // 核心修复：WinUI 3 中 Window 没有 Focus 方法
                // 我们让整个界面的根节点导航栏 (NavView) 把焦点“抢”走，输入框就会自动取消激活
                if (NavView != null)
                {
                    NavView.Focus(FocusState.Programmatic);
                }
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
            // 修复：改用我们自己写的写入方法
            SaveSettingToDisk(UpdateJsonUrlSettingKey, url);
        }

        // 恢复官方源按钮点击
        private void RestoreDefaultUrl_Click(object sender, RoutedEventArgs e)
        {
            UpdateJsonUrlBox.Text = DefaultUpdateJsonUrl;
            // 修复：重置时也写入本地文件
            SaveSettingToDisk(UpdateJsonUrlSettingKey, DefaultUpdateJsonUrl);
        }

        private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // 如果是在初始化阶段引发的 Toggled，直接跳过，什么都不做！
            if (_isInitializing) return;

            SetAutoStartRegistry(AutoStartToggle.IsOn);
        }

        private bool CheckAutoStartRegistry()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                string? registeredPath = key?.GetValue("PoiTimeWinUI") as string;

                if (string.IsNullOrEmpty(registeredPath)) return false;

                // 核心修复 1：把注册表字符串里的双引号剥离干净
                registeredPath = registeredPath.Trim('\"');
                string currentPath = Environment.ProcessPath ?? "";

                // 核心修复 2：使用 OrdinalIgnoreCase 忽略大小写进行比对
                return string.Equals(registeredPath, currentPath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private async void SetAutoStartRegistry(bool enable)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);

                if (enable)
                {
                    // 写入时，严格用双引号把物理路径包起来
                    string currentPath = $"\"{Environment.ProcessPath}\"";
                    key?.SetValue("PoiTimeWinUI", currentPath);
                }
                else
                {
                    key?.DeleteValue("PoiTimeWinUI", false);
                }
            }
            catch (Exception ex)
            {
                // 如果写入失败（比如权限不够），强制把开关拨回去，同时上锁防循环
                _isInitializing = true;
                AutoStartToggle.IsOn = !enable;
                _isInitializing = false;

                var dialog = new ContentDialog
                {
                    Title = "设置失败",
                    Content = $"无法修改开机启动项。\n错误信息: {ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.Content!.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        // ================= 原生 WinUI 下载逻辑 =================
        // ================= 全新：网络与下载逻辑 =================

        // 1. 从你的服务器获取 update.json
        // 1. 从你的服务器获取 update.json (修复 FindName 报错版)
        // 1. 获取服务器数据
        private async void LoadUpdateJsonFromServer()
        {
            DownloadLoadingRing.IsActive = true;
            DownloadLoadingRing.Visibility = Visibility.Visible;
            VoicePackListView.Visibility = Visibility.Collapsed;

            try
            {
                // 绑定 UI 的数据源为分页后的集合
                VoicePackListView.ItemsSource = _pagedVoicePacks;

                // 修复：直接从我们写的辅助方法拿地址，彻底告别系统 API
                string updateJsonUrl = GetSavedUpdateJsonUrl();
                string jsonString = await _httpClient.GetStringAsync(updateJsonUrl);

                var config = JsonSerializer.Deserialize<UpdateJsonConfig>(jsonString);
                if (config?.VoicePacks != null)
                {
                    _allVoicePacks.Clear();
                    string basePath = AppContext.BaseDirectory;

                    foreach (var pack in config.VoicePacks)
                    {
                        pack.CheckLocalStatus(basePath); // 检查本地安装状态
                        _allVoicePacks.Add(pack);
                    }

                    // 重置状态并执行一次过滤和分页
                    _currentPage = 1;
                    _searchQuery = SearchBox.Text.Trim();
                    ApplyFilterAndPaging();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取语音列表失败: {ex.Message}");
            }
            finally
            {
                DownloadLoadingRing.IsActive = false;
                DownloadLoadingRing.Visibility = Visibility.Collapsed;
                VoicePackListView.Visibility = Visibility.Visible;
            }
        }

        // ================= 新增：核心搜索与分页引擎 =================

        private void ApplyFilterAndPaging()
        {
            if (_allVoicePacks.Count == 0) return;

            // 1. 执行搜索过滤 (支持名称或ID，忽略大小写)
            var filteredList = _allVoicePacks.Where(p =>
                string.IsNullOrEmpty(_searchQuery) ||
                p.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                p.Id.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
            ).ToList();

            // 2. 计算总页数，并防止当前页越界
            int totalItems = filteredList.Count;
            int totalPages = (int)Math.Ceiling((double)totalItems / _itemsPerPage);
            if (totalPages == 0) totalPages = 1; // 哪怕没搜到东西，也显示 1/1 页

            if (_currentPage > totalPages) _currentPage = totalPages;
            if (_currentPage < 1) _currentPage = 1;

            // 3. 截取当前页的数据
            var pageData = filteredList.Skip((_currentPage - 1) * _itemsPerPage).Take(_itemsPerPage).ToList();

            // 4. 更新到 UI 集合中
            _pagedVoicePacks.Clear();
            foreach (var item in pageData)
            {
                _pagedVoicePacks.Add(item);
            }

            // 5. 更新底部按钮与文本状态
            PageInfoText.Text = $"{_currentPage} / {totalPages}";
            JumpPageBox.Text = _currentPage.ToString();
            PrevPageBtn.IsEnabled = _currentPage > 1;
            NextPageBtn.IsEnabled = _currentPage < totalPages;
        }

        // ================= 新增：各种控制事件 =================

        private void RefreshUpdateJson_Click(object sender, RoutedEventArgs e)
        {
            // 点击刷新按钮
            LoadUpdateJsonFromServer();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 搜索框输入文字时，重置回第一页并重新过滤
            _searchQuery = SearchBox.Text.Trim();
            _currentPage = 1;
            ApplyFilterAndPaging();
        }

        private void PrevPageBtn_Click(object sender, RoutedEventArgs e)
        {
            _currentPage--;
            ApplyFilterAndPaging();
        }

        private void NextPageBtn_Click(object sender, RoutedEventArgs e)
        {
            _currentPage++;
            ApplyFilterAndPaging();
        }

        private void JumpPageBtn_Click(object sender, RoutedEventArgs e)
        {
            // 解析跳转框的数字
            if (int.TryParse(JumpPageBox.Text.Trim(), out int targetPage))
            {
                _currentPage = targetPage;
                ApplyFilterAndPaging(); // 引擎内部会自动纠正越界的数字
            }
            else
            {
                JumpPageBox.Text = _currentPage.ToString(); // 输错了就重置回去
            }
        }

        private void ItemsPerPageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 切换每页显示数量
            if (ItemsPerPageCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                if (int.TryParse(item.Tag.ToString(), out int newLimit))
                {
                    _itemsPerPage = newLimit;
                    _currentPage = 1; // 数量变了，必须回到第一页
                    ApplyFilterAndPaging();
                }
            }
        }

        // 2. 列表卡片上的“下载”按钮点击事件
        // 2. 列表卡片上的“下载”按钮点击事件 (带悬浮进度条版)
        // ================= 核心升级：网络下载与速度计算逻辑 =================

        // 辅助方法：将字节数转换为用户友好的单位 (B/s, KB/s, MB/s)
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


        // 2. 列表卡片上的“下载”按钮点击事件 (升级：带实时速度版)
        // 1. 列表卡片上的“下载”按钮点击事件 (现已升级为：加入队列)
        private void DownloadVoiceBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is VoicePackModel model)
            {
                // 防止用户疯狂狂点重复加入
                if (_downloadQueue.Any(q => q.Model.Id == model.Id)) return;

                // 改变按钮状态为“排队中”
                btn.IsEnabled = false;
                btn.Content = "排队中...";

                // 将任务推入队列
                _downloadQueue.Enqueue((model, btn));

                // 唤起浮窗
                FloatingProgressPanel.Visibility = Visibility.Visible;
                DownloadExpander.IsExpanded = true;

                // 如果当前引擎闲置，则点火启动它！如果正在下载，它会自动按顺序处理。
                if (!_isDownloading)
                {
                    _ = ProcessDownloadQueueAsync();
                }
                else
                {
                    // 如果引擎已经在跑了，更新一下浮窗标题，告诉用户排上了
                    DownloadStatusTitle.Text = $"正在下载 (还有 {_downloadQueue.Count} 个排队中...)";
                }
            }
        }

        // 2. 核心：异步队列处理引擎
        // 2. 核心：异步多线程队列处理引擎
        private async Task ProcessDownloadQueueAsync()
        {
            _isDownloading = true;
            MiniProgressRing.IsActive = true;

            while (_downloadQueue.Count > 0)
            {
                var task = _downloadQueue.Dequeue();
                var model = task.Model;
                var btn = task.UIBtn;

                // 回到主线程更新 UI 状态
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    btn.Content = "下载中...";
                    DownloadProgressBar.Value = 0;
                });

                var stopwatch = new System.Diagnostics.Stopwatch();
                long totalBytesDownloaded = 0;
                int completedTasks = 0;

                try
                {
                    string targetFolder = Path.Combine(AppContext.BaseDirectory, "voices", model.Id);
                    if (!Directory.Exists(targetFolder)) Directory.CreateDirectory(targetFolder);

                    // ================= 新增：多线程任务清单准备 =================
                    var filesToDownload = new List<(string Url, string SavePath)>();

                    // 加入 24 个基础语音
                    for (int hour = 0; hour < 24; hour++)
                    {
                        filesToDownload.Add((
                            $"https://zh.kcwiki.cn/wiki/Special:Redirect/file/{model.Id}-{hour:D2}00.mp3",
                            Path.Combine(targetFolder, $"{model.Id}-{hour:D2}00.mp3")
                        ));
                    }

                    // 加入 config.json
                    string configSavePath = Path.Combine(targetFolder, "config.json");
                    if (!string.IsNullOrEmpty(model.ConfigUrl))
                    {
                        filesToDownload.Add((model.ConfigUrl, configSavePath));
                    }

                    // 加入额外文件
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

                    // 读取用户设置的并发数
                    string? savedThreadsStr = GetSettingFromDisk(MaxThreadsSettingKey);
                    int maxThreads = int.TryParse(savedThreadsStr, out int t) ? t : DefaultMaxThreads;

                    // ================= 核心：多线程调度器 (SemaphoreSlim) =================
                    using var semaphore = new SemaphoreSlim(maxThreads);
                    var downloadTasks = new List<Task>();

                    stopwatch.Start();

                    foreach (var file in filesToDownload)
                    {
                        // 启动独立的下载线程
                        downloadTasks.Add(Task.Run(async () =>
                        {
                            await semaphore.WaitAsync(); // 如果同时下载的文件超过了限制，就在这里排队等候
                            try
                            {
                                long fileSize = await DownloadFileAsync(file.Url, file.SavePath);

                                // ⚠️ 跨线程 UI 更新：所有跟界面相关的进度修改，必须回到主线程！
                                this.DispatcherQueue.TryEnqueue(() =>
                                {
                                    totalBytesDownloaded += fileSize;
                                    completedTasks++;

                                    double percentage = ((double)completedTasks / totalTasks) * 100;
                                    DownloadProgressBar.Value = percentage;

                                    // 核心修复：单独对舰娘名字进行长度限制（最大保留 10 个字符）
                                    string displayName = model.Name;
                                    if (displayName.Length > 10)
                                    {
                                        displayName = displayName.Substring(0, 9) + "..."; // 截断并加上省略号
                                    }

                                    string queueInfo = _downloadQueue.Count > 0 ? $" (等待中: {_downloadQueue.Count})" : "";
                                    // 拼凑时使用处理过的 displayName
                                    DownloadStatusTitle.Text = $"正在下载: {displayName}{queueInfo} - {completedTasks}/{totalTasks}";

                                    double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                                    if (elapsedSeconds > 0)
                                    {
                                        double bytesPerSecond = totalBytesDownloaded / elapsedSeconds;
                                        DownloadDetailText.Text = $"多线程并发速度: {FormatSpeed(bytesPerSecond)}";
                                    }
                                });
                            }
                            finally
                            {
                                semaphore.Release(); // 下载完一个，立刻释放一个名额给后面的文件
                            }
                        }));
                    }

                    // 挂起主逻辑，等待这 24+N 个文件全部下载完毕
                    await Task.WhenAll(downloadTasks);
                    stopwatch.Stop();

                    // ================= 收尾工作 =================
                    // 如果没有服务器 config，在所有文件下载完后生成默认 config
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
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        btn.Content = "失败(重试)";
                        btn.IsEnabled = true;
                    });
                    System.Diagnostics.Debug.WriteLine($"下载 {model.Name} 失败: {ex.Message}");
                }
            }

            this.DispatcherQueue.TryEnqueue(async () =>
            {
                _isDownloading = false;
                DownloadStatusTitle.Text = "所有队列下载完成";
                DownloadDetailText.Text = "所选语音包下载完成";
                MiniProgressRing.IsActive = false;
                DownloadProgressBar.Value = 100;

                await Task.Delay(3000);
                if (!_isDownloading)
                {
                    FloatingProgressPanel.Visibility = Visibility.Collapsed;
                }
            });
        }

        // ================= 新增：删除本地语音包事件 =================
        private async void DeleteVoiceBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is VoicePackModel model)
            {
                // 为了防止用户误触，弹出一个二次确认框
                var dialog = new ContentDialog
                {
                    Title = "确认删除",
                    Content = $"确定要删除 {model.Name} 的本地语音包吗？\n删除后如果当前正在使用该舰娘，需要手动切换。",
                    PrimaryButtonText = "确认删除",
                    CloseButtonText = "取消",
                    XamlRoot = this.Content!.XamlRoot
                };

                // 用户点击了确认
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    try
                    {
                        string targetFolder = Path.Combine(AppContext.BaseDirectory, "voices", model.Id);
                        if (Directory.Exists(targetFolder))
                        {
                            // true 表示递归删除，把文件夹和里面的所有 .mp3 一起扬了
                            Directory.Delete(targetFolder, true);
                        }

                        // 核心：告诉 UI 这个语音包没了，按钮会瞬间变回蓝色“下载”
                        model.IsInstalled = false;

                        // 刷新主页的下拉菜单
                        LoadAvailableShipsToUI();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"删除失败: {ex.Message}");
                    }
                }
            }
        }

        // ================= 高级选项：旧版自定义弹窗下载 =================
        private async void OpenCustomDownload_Click(object sender, RoutedEventArgs e)
        {
            // 1. 构建原生输入弹窗的 UI
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

            // 2. 绑定下载按钮事件
            dialog.PrimaryButtonClick += async (s, args) =>
            {
                string shipId = idBox.Text.Trim();
                string shipName = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(shipId) || string.IsNullOrEmpty(shipName)) return;

                // 阻止弹窗立即关闭，让它变成下载面板
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

                    // 循环拉取百科的 24 个文件
                    for (int hour = 0; hour < 24; hour++)
                    {
                        string fileName = $"{shipId}-{hour:D2}00.mp3";
                        statusText.Text = $"正在下载 {fileName} ({hour + 1}/24)";
                        progressBar.Value = hour + 1;

                        string url = $"https://zh.kcwiki.cn/wiki/Special:Redirect/file/{fileName}";
                        string savePath = Path.Combine(targetFolder, fileName);

                        // 使用我们的静默下载工具，哪怕某一个小时没语音也不报错中断
                        await DownloadFileAsync(url, savePath);
                    }

                    // 复用我们写好的生成方法，生成标准的 config.json
                    var tempModel = new VoicePackModel { Id = shipId, Name = shipName };
                    GenerateDefaultConfig(tempModel, Path.Combine(targetFolder, "config.json"));

                    statusText.Text = "下载完成！";
                    await Task.Delay(1000);
                    dialog.Hide();

                    // 刷新主界面的下拉菜单，新下载的舰娘就能马上被选了
                    LoadAvailableShipsToUI();
                }
                catch (Exception ex)
                {
                    statusText.Text = $"下载遇到意外错误: {ex.Message}";
                    dialog.IsPrimaryButtonEnabled = true; // 允许重新点击尝试
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
        }

        // 3. 稳健的单文件下载工具 (升级：现在会返回下载的文件大小 - Task<long>)
        private async Task<long> DownloadFileAsync(string url, string savePath)
        {
            try
            {
                using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    // 获取服务器返回的文件长度 (如果服务器提供)
                    long totalBytes = response.Content.Headers.ContentLength ?? 0;

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        await stream.CopyToAsync(fs);
                        // 核心：CopyToAsync 完成后，fs.Length 就是下载的文件实际大小
                        return fs.Length;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"文件缺失或下载失败: {url}, 错误: {ex.Message}");
                // 个别语音缺失时，返回 0 字节大小，且不中断整个任务
                return 0;
            }
        }

        // 4. 生成原版 Config
        private void GenerateDefaultConfig(VoicePackModel model, string savePath)
        {
            // 使用你之前的 Models.VoiceConfig 格式
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

        // ... 保留原有的 ExitApp 等方法 ...

        private void ExitApp()
        {
            // 真正退出程序
            _audioService?.StopTimer();
            TrayIcon?.Dispose();
            Application.Current.Exit();
        }
    }

    public class UpdateJsonConfig
    {
        [JsonPropertyName("voice_packs")]
        public ObservableCollection<VoicePackModel> VoicePacks { get; set; } = new();
    }

    // 继承 INotifyPropertyChanged，让数据发生改变时能“大喊一声”通知界面刷新
    // 继承 INotifyPropertyChanged，让数据发生改变时能立即通知界面刷新
    public class VoicePackModel : INotifyPropertyChanged
    {
        // ================= 核心修改：设为秘书舰 UI 状态控制 =================
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
        // 如果是当前激活的舰娘，显示“已设为当前”，否则显示“设为秘书舰”
        public string ActiveButtonText => IsActiveShip ? "当前" : "设置";

        [JsonIgnore]
        // 如果已经是秘书舰了，按钮变灰不可点击
        public bool IsActiveButtonEnabled => !IsActiveShip;
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        // 如果服务器配了名字就用配置的，否则显示 "本地 ID"
        public string Name { get; set; } = "";

        [JsonPropertyName("config_url")]
        public string? ConfigUrl { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("extra_download_urls")]
        public List<string>? ExtraDownloadUrls { get; set; }

        [JsonPropertyName("special_tags")]
        public List<string>? SpecialTags { get; set; }

        [JsonIgnore]
        public string DisplayId => $"ID: {Id}";

        [JsonIgnore]
        public Visibility TagsVisibility => (SpecialTags != null && SpecialTags.Count > 0) ? Visibility.Visible : Visibility.Collapsed;

        // ================= 下载/删除状态控制 (保留原逻辑) =================
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

        // ================= 核心修改：测试语音 UI 状态控制 =================
        private string _previewButtonText = "试听报时";
        [JsonIgnore]
        // 绑定到按钮 Content
        public string PreviewButtonText
        {
            get => _previewButtonText;
            set { if (_previewButtonText != value) { _previewButtonText = value; OnPropertyChanged(); } }
        }

        private bool _isPreviewPlaying = false;
        [JsonIgnore]
        // 绑定到按钮 Style 转换器
        public bool IsPreviewPlaying
        {
            get => _isPreviewPlaying;
            set { if (_isPreviewPlaying != value) { _isPreviewPlaying = value; OnPropertyChanged(); } }
        }

        // 重置按钮状态的辅助方法
        public void ResetPreviewState()
        {
            PreviewButtonText = "试听报时";
            IsPreviewPlaying = false;
        }

        // ================= 本地检测与通知机制 =================
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