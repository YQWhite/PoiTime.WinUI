using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using PoiTime.WinUI.Models;
using System.Collections.Generic;

namespace PoiTime.WinUI.Services
{
    public class PoiAudioService
    {
        private MediaPlayer _mediaPlayer;
        private PeriodicTimer? _timer;
        private CancellationTokenSource? _cts;

        public VoiceConfig? CurrentConfig { get; private set; }
        public string CurrentShipId { get; private set; } = "144";

        private string _basePath;
        // ======== 新增：播放结束事件与播放状态 ========
        public event EventHandler? VoiceFinished;
        public bool IsPlaying => _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

        public PoiAudioService()
        {
            _mediaPlayer = new MediaPlayer();
            _mediaPlayer.MediaEnded += (s, e) => VoiceFinished?.Invoke(this, EventArgs.Empty);
            _mediaPlayer.MediaFailed += (s, e) => VoiceFinished?.Invoke(this, EventArgs.Empty);
            // 获取程序运行时的根目录
            _basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "voices");
        }
        // ======== 新增：停止播放的方法 ========
        public void StopVoice()
        {
            _mediaPlayer.Pause();
            _mediaPlayer.Position = TimeSpan.Zero;
            VoiceFinished?.Invoke(this, EventArgs.Empty); // 手动触发结束事件以重置 UI
        }

        public void LoadConfig(string shipId)
        {
            CurrentShipId = shipId;
            string configPath = Path.Combine(_basePath, shipId, "config.json");

            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                CurrentConfig = JsonSerializer.Deserialize<VoiceConfig>(json);
            }
            else
            {
                CurrentConfig = null;
            }
        }

        public List<(string ShipId, string Name)> GetAvailableShips()
        {
            var result = new List<(string, string)>();

            // 如果没有 voices 文件夹，直接返回空列表
            if (!Directory.Exists(_basePath)) return result;

            // 遍历 voices 文件夹下的所有子文件夹
            string[] directories = Directory.GetDirectories(_basePath);
            foreach (string dir in directories)
            {
                string shipId = Path.GetFileName(dir); // 文件夹名，比如 "007a", "144"
                string configPath = Path.Combine(dir, "config.json");

                if (File.Exists(configPath))
                {
                    try
                    {
                        // 读取并解析 config.json
                        string json = File.ReadAllText(configPath);
                        var config = JsonSerializer.Deserialize<VoiceConfig>(json);

                        // 提取舰娘名字，如果没有则显示“未知舰娘”
                        string name = config?.name ?? "未知舰娘";
                        result.Add((shipId, name));
                    }
                    catch
                    {
                        // 如果某个配置解析出错（比如 JSON 格式不对），直接跳过它，防止程序崩溃
                    }
                }
            }
            return result;
        }

        public void PlayVoice(string? fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return;

            string filePath = Path.Combine(_basePath, CurrentShipId, fileName);
            if (File.Exists(filePath))
            {
                _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(filePath));
                _mediaPlayer.Play();
            }
        }

        public void TestCurrentHourVoice()
        {
            PlayHourlyVoice(DateTime.Now.Hour);
        }

        public void StartTimer()
        {
            if (_cts != null) return;

            _cts = new CancellationTokenSource();
            _timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            _ = RunLoopAsync();
        }

        public void StopTimer()
        {
            _cts?.Cancel();
            _cts = null;
            _timer?.Dispose();
            _timer = null;
        }

        private async Task RunLoopAsync()
        {
            if (_timer == null || _cts == null) return;

            try
            {
                while (await _timer.WaitForNextTickAsync(_cts.Token))
                {
                    DateTime now = DateTime.Now;
                    // 检测是否是整点 00分00秒
                    if (now.Minute == 0 && now.Second == 0)
                    {
                        PlayHourlyVoice(now.Hour);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private void PlayHourlyVoice(int hour)
        {
            if (CurrentConfig?.voices == null) return;

            var voice = CurrentConfig.voices.FirstOrDefault(v => v.hour == hour && v.minute == 0);
            if (voice != null)
            {
                PlayVoice(voice.fileName);
            }
        }
    }
}