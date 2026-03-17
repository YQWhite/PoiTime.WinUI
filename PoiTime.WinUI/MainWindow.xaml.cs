using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PoiTime.WinUI.Models;
using PoiTime.WinUI.Services;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Graphics;
using System.Net.Http;

namespace PoiTime.WinUI
{
    public sealed partial class MainWindow : Window
    {
        private AppWindow? _appWindow;
        private PoiAudioService _audioService;
        // ======== 核心修复 2：使用原生的 XamlUICommand 处理托盘事件 ========
        public XamlUICommand ShowWindowCommand { get; } = new XamlUICommand();
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
                _appWindow.Resize(new SizeInt32(400, 600));
                _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "favicon.ico"));
                _appWindow.Closing += AppWindow_Closing;
            }

            // 4. 启动定时器
            _audioService.StartTimer();

            // 5. 最后一步：手动选中下拉框的第一项。
            // 这时所有的控件和服务都已经准备就绪，触发 SelectionChanged 事件绝对安全！
            LoadAvailableShipsToUI();
        }

        // 修改下拉框事件，加上防御性拦截
        private void ShipSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 绝对防御：如果服务还没实例化，或者 UI 控件还没加载，直接退出，防止崩溃
            if (_audioService == null || CurrentShipTextBlock == null) return;

            if (ShipSelector.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                LoadShipConfig(item.Tag.ToString() ?? "144");
            }
        }

        private void LoadShipConfig(string shipId)
        {
            if (_audioService == null || CurrentShipTextBlock == null) return;

            _audioService.LoadConfig(shipId);

            if (_audioService.CurrentConfig != null)
            {
                CurrentShipTextBlock.Text = $"当前舰娘: {_audioService.CurrentConfig.name} (ID: {shipId})";
            }
            else
            {
                CurrentShipTextBlock.Text = $"未找到 ID: {shipId} 的配置文件";
            }
        }

        private void LoadAvailableShipsToUI()
        {
            if (_audioService == null) return;

            ShipSelector.Items.Clear();

            // 获取所有可用的舰娘
            var availableShips = _audioService.GetAvailableShips();

            foreach (var ship in availableShips)
            {
                // 动态创建下拉菜单项
                ShipSelector.Items.Add(new ComboBoxItem
                {
                    Content = $"{ship.Name} ({ship.ShipId})", // 显示文字，例如 "加贺 (007a)"
                    Tag = ship.ShipId                         // 绑定的 ID 数据
                });
            }

            // 如果扫描到了语音包，默认选中第一个
            if (ShipSelector.Items.Count > 0)
            {
                ShipSelector.SelectedIndex = 0;
            }
            else
            {
                CurrentShipTextBlock.Text = "当前舰娘: 未找到任何语音包";
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

        private void TestVoice_Click(object sender, RoutedEventArgs e)
        {
            if (_audioService.IsPlaying)
            {
                // 如果正在播放，则停止
                _audioService.StopVoice();
            }
            else
            {
                // 如果未播放，变红并开始播放
                TestVoiceButton.Content = "停止播放语音";
                TestVoiceButton.Background = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
                TestVoiceButton.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                _audioService.TestCurrentHourVoice();
            }
        }

        private void AudioService_VoiceFinished(object? sender, EventArgs e)
        {
            // 媒体播放事件在后台线程触发，必须通过 DispatcherQueue 回到主线程更新 UI
            DispatcherQueue.TryEnqueue(() =>
            {
                TestVoiceButton.Content = "测试当前语音";
                // 清除自定义颜色，恢复默认的主题样式
                TestVoiceButton.ClearValue(Button.BackgroundProperty);
                TestVoiceButton.ClearValue(Button.ForegroundProperty);
            });
        }

        // ================= 原生 WinUI 下载逻辑 =================
        private async void DownloadVoice_Click(object sender, RoutedEventArgs e)
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
                Title = "下载新语音包",
                Content = panel,
                PrimaryButtonText = "开始下载",
                CloseButtonText = "取消",
                XamlRoot = this.Content.XamlRoot
            };

            // 2. 绑定下载按钮事件
            dialog.PrimaryButtonClick += async (s, args) =>
            {
                string shipId = idBox.Text.Trim();
                string shipName = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(shipId)) return;

                // 阻止弹窗立即关闭，进入下载状态
                var deferral = args.GetDeferral();
                args.Cancel = true;
                dialog.IsPrimaryButtonEnabled = false;
                idBox.IsEnabled = false;
                nameBox.IsEnabled = false;
                progressBar.Visibility = Visibility.Visible;
                statusText.Visibility = Visibility.Visible;

                string targetFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "voices", shipId);
                Directory.CreateDirectory(targetFolder);

                try
                {
                    using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                    {
                        for (int hour = 0; hour < 24; hour++)
                        {
                            string fileName = $"{shipId}-{hour:D2}00.mp3";
                            statusText.Text = $"正在下载 {fileName} ({hour + 1}/24)";
                            progressBar.Value = hour + 1;

                            string url = $"https://zh.kcwiki.cn/wiki/Special:Redirect/file/{fileName}";
                            string savePath = Path.Combine(targetFolder, fileName);

                            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                            {
                                response.EnsureSuccessStatusCode();
                                using (var stream = await response.Content.ReadAsStreamAsync())
                                using (var fs = new FileStream(savePath, FileMode.Create))
                                {
                                    await stream.CopyToAsync(fs);
                                }
                            }
                        }
                    }

                    // 生成 config.json
                    var config = new VoiceConfig
                    {
                        voiceCount = 24,
                        name = shipName,
                        voices = Enumerable.Range(0, 24).Select(h => new VoiceItem { hour = h, minute = 0, fileName = $"{shipId}-{h:D2}00.mp3" }).ToList()
                    };
                    File.WriteAllText(Path.Combine(targetFolder, "config.json"), JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

                    statusText.Text = "下载完成！";
                    await Task.Delay(1000); // 稍微停顿让用户看到完成提示
                    dialog.Hide(); // 手动关闭弹窗

                    // 刷新主界面的下拉菜单 (调用我们在上个回合写的扫描方法)
                    LoadAvailableShipsToUI();
                }
                catch (Exception ex)
                {
                    statusText.Text = $"下载失败: {ex.Message}";
                    dialog.IsPrimaryButtonEnabled = true; // 允许用户重试
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
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
}