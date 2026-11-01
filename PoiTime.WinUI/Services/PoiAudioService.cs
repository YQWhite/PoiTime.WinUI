using PoiTime.WinUI.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace PoiTime.WinUI.Services;

public class PoiAudioService
{
    private readonly MediaPlayer _mediaPlayer = new();
    private readonly string _basePath = Path.Combine(AppContext.BaseDirectory, "voices");
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private DateTime? _lastChimeSlot;

    public VoiceConfig? CurrentConfig { get; private set; }
    public string CurrentShipId { get; private set; } = "144";
    public event EventHandler? VoiceFinished;
    public bool IsPlaying => _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public PoiAudioService()
    {
        _mediaPlayer.MediaEnded += (_, _) => VoiceFinished?.Invoke(this, EventArgs.Empty);
        _mediaPlayer.MediaFailed += (_, _) => VoiceFinished?.Invoke(this, EventArgs.Empty);
    }

    public void StopVoice()
    {
        _mediaPlayer.Pause();
        _mediaPlayer.Position = TimeSpan.Zero;
        VoiceFinished?.Invoke(this, EventArgs.Empty);
    }

    public void LoadConfig(string shipId)
    {
        CurrentShipId = shipId;
        CurrentConfig = null;
        if (string.IsNullOrWhiteSpace(shipId))
            return;

        string configPath = Path.Combine(_basePath, shipId, "config.json");
        if (!File.Exists(configPath))
            return;

        try
        {
            CurrentConfig = JsonSerializer.Deserialize<VoiceConfig>(File.ReadAllText(configPath));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取语音配置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the hourly voice file for a voice pack from its config.json "voices" list,
    /// falling back to the default "{shipId}-{HH}00.mp3" name when the config has no entry.
    /// </summary>
    public static string GetHourlyVoicePath(string shipId, int hour)
    {
        string packFolder = Path.Combine(AppContext.BaseDirectory, "voices", shipId);
        string? fileName = null;
        string configPath = Path.Combine(packFolder, "config.json");
        if (File.Exists(configPath))
        {
            try
            {
                var config = JsonSerializer.Deserialize<VoiceConfig>(File.ReadAllText(configPath));
                fileName = config?.voices?.FirstOrDefault(v => v.hour == hour && v.minute == 0)?.fileName;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"读取语音配置失败: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"{shipId}-{hour:D2}00.mp3";

        return Path.Combine(packFolder, fileName);
    }

    public List<(string ShipId, string Name)> GetAvailableShips()
    {
        var result = new List<(string, string)>();
        if (!Directory.Exists(_basePath))
            return result;

        foreach (string dir in Directory.GetDirectories(_basePath))
        {
            string shipId = Path.GetFileName(dir);
            string configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath))
                continue;

            try
            {
                var config = JsonSerializer.Deserialize<VoiceConfig>(File.ReadAllText(configPath));
                result.Add((shipId, config?.name ?? "未知舰娘"));
            }
            catch
            {
            }
        }
        return result;
    }

    public void PlayVoice(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(CurrentShipId))
            return;

        string filePath = Path.Combine(_basePath, CurrentShipId, fileName);
        if (!File.Exists(filePath))
            return;

        try
        {
            _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(filePath));
            _mediaPlayer.Play();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"播放语音失败: {ex.Message}");
        }
    }

    public void TestCurrentHourVoice()
    {
        PlayHourlyVoice(DateTime.Now.Hour);
    }

    public void StartTimer()
    {
        if (_cts is not null)
            return;

        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _ = RunLoopAsync(_timer, _cts.Token);
    }

    public void StopTimer()
    {
        _cts?.Cancel();
        _cts = null;
        _timer?.Dispose();
        _timer = null;
    }

    private async Task RunLoopAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    DateTime now = DateTime.Now;
                    if (now.Minute != 0)
                        continue;

                    DateTime chimeSlot = now.Date.AddHours(now.Hour);
                    if (_lastChimeSlot == chimeSlot)
                        continue;

                    _lastChimeSlot = chimeSlot;
                    LoadConfig(CurrentShipId);
                    PlayHourlyVoice(now.Hour);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"整点报时失败: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PlayHourlyVoice(int hour)
    {
        if (CurrentConfig?.voices is null)
            return;

        var voice = CurrentConfig.voices.FirstOrDefault(v => v.hour == hour && v.minute == 0);
        if (voice is not null)
            PlayVoice(voice.fileName);
    }
}
