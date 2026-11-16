using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EkranKaydedici.Models;

namespace EkranKaydedici.Services;

public sealed class ReplayService : IDisposable
{
    private readonly object _lock = new();
    private readonly SemaphoreSlim _clipSemaphore = new(1, 1);

    private string? _ffmpegPath;
    private CaptureProfile? _profile;
    private DateTime _lastSpawnUtc;
    private string? _lastFfmpegError;

    private Process? _bufferProcess;
    private AudioCapturePipe? _bufferAudio;
    private CancellationTokenSource? _supervisorCts;
    private bool _wantReplayRunning;

    private Process? _manualProcess;
    private AudioCapturePipe? _manualAudio;
    private string? _manualOutputPath;

    private int _replaySeconds = 30;
    private bool _includeSystemAudio = true;
    private bool _includeMic;
    private int _fps = 30;

    public string BufferDir { get; } = Path.Combine(Path.GetTempPath(), "EkranKaydediciBuffer");

    public event Action<bool, string>? ReplayStateChanged;

    public bool IsReplayRunning
    {
        get
        {
            lock (_lock)
            {
                return _bufferProcess is { HasExited: false };
            }
        }
    }

    public bool IsManualRecording
    {
        get
        {
            lock (_lock)
            {
                return _manualProcess is { HasExited: false };
            }
        }
    }

    public async Task<string?> EnsureReadyAsync(Action<string>? onProgress = null)
    {
        if (_ffmpegPath is not null && _profile is not null)
        {
            return null;
        }

        var path = await FfmpegProbe.EnsureFfmpegExecutableAsync(onProgress).ConfigureAwait(false);
        if (path is null)
        {
            return "ffmpeg.exe hazırlanamadı (internet bağlantısı veya yazma izni yok).";
        }

        _ffmpegPath = path;
        onProgress?.Invoke("Ekran ve kodlayıcı kontrol ediliyor...");
        _profile = await Task.Run(() => FfmpegProbe.DetectProfile(path)).ConfigureAwait(false);
        return null;
    }

    public string? EnsureReady() => EnsureReadyAsync().GetAwaiter().GetResult();

    public string GetEncoderSummary()
    {
        if (_profile is null)
        {
            return "Hazırlanıyor";
        }
        return $"{_profile.CaptureMethod} + {_profile.EncoderName}";
    }

    public void StartReplay(int replaySeconds, bool includeSystemAudio, bool includeMic, int fps)
    {
        var err = EnsureReady();
        if (err is not null)
        {
            ReplayStateChanged?.Invoke(false, err);
            return;
        }

        lock (_lock)
        {
            _replaySeconds = replaySeconds;
            _includeSystemAudio = includeSystemAudio;
            _includeMic = includeMic;
            _fps = fps;
            _wantReplayRunning = true;

            StopBufferProcessesLocked();
            SpawnReplayPipelineLocked();

            if (_supervisorCts is null || _supervisorCts.IsCancellationRequested)
            {
                _supervisorCts = new CancellationTokenSource();
                var ct = _supervisorCts.Token;
                _ = Task.Run(() => SupervisorLoopAsync(ct), ct);
            }
        }

        ReplayStateChanged?.Invoke(true, $"Son {replaySeconds} sn RAM/Temp tamponda hazır · F8");
    }

    public void StopReplay()
    {
        lock (_lock)
        {
            _wantReplayRunning = false;
            _supervisorCts?.Cancel();
            _supervisorCts = null;
            StopBufferProcessesLocked();
            TryDeleteDirectory(BufferDir);
        }

        ReplayStateChanged?.Invoke(false, "Anlık Tekrar kapalı");
    }

    private void SpawnReplayPipelineLocked()
    {
        if (_ffmpegPath is null || _profile is null)
        {
            return;
        }

        TryDeleteDirectory(BufferDir);
        Directory.CreateDirectory(BufferDir);

        var audioPipe = AudioCapturePipe.TryCreate(_includeSystemAudio, _includeMic);
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
        args.AddRange(FfmpegProbe.BuildVideoInputArgs(_profile, _fps, region: null));

        if (audioPipe is not null)
        {
            args.AddRange(["-f", "s16le", "-ar", "48000", "-ac", "2", "-thread_queue_size", "512", "-i", audioPipe.PipePath]);
        }

        args.AddRange(FfmpegProbe.BuildVideoOutputFormatArgs(_profile, region: null));
        args.AddRange(FfmpegProbe.BuildEncoderArgs(_profile.EncoderName, _fps));

        if (audioPipe is not null)
        {
            args.AddRange(["-c:a", "aac", "-b:a", "128k"]);
        }

        var listSize = Math.Max(5, _replaySeconds + 3);
        var m3u8Path = Path.Combine(BufferDir, "stream.m3u8");
        args.AddRange([
            "-f", "hls",
            "-hls_time", "1",
            "-hls_list_size", listSize.ToString(CultureInfo.InvariantCulture),
            "-hls_flags", "delete_segments+temp_file",
            m3u8Path,
        ]);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg başlatılamadı.");
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _lastFfmpegError = e.Data;
            }
        };
        proc.BeginErrorReadLine();
        try
        {
            proc.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Ignore priority errors.
        }

        audioPipe?.Start();
        _bufferAudio = audioPipe;
        _bufferProcess = proc;
        _lastSpawnUtc = DateTime.UtcNow;
    }

    private void StopBufferProcessesLocked()
    {
        StopFfmpegGracefully(_bufferProcess);
        _bufferProcess = null;
        _bufferAudio?.Dispose();
        _bufferAudio = null;
    }

    private async Task SupervisorLoopAsync(CancellationToken ct)
    {
        var recentFailures = new List<DateTime>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            lock (_lock)
            {
                if (!_wantReplayRunning || ct.IsCancellationRequested)
                {
                    break;
                }

                if (_bufferProcess is { HasExited: false })
                {
                    continue;
                }

                var now = DateTime.UtcNow;
                var quickFail = (now - _lastSpawnUtc).TotalSeconds < 5.0;
                if (quickFail && _profile is not null)
                {
                    if (_profile.SupportsDdagrab)
                    {
                        _profile = _profile with { CaptureMethod = "gdigrab", SupportsDdagrab = false };
                    }
                    else if (!string.Equals(_profile.EncoderName, "libx264", StringComparison.OrdinalIgnoreCase))
                    {
                        _profile = _profile with { EncoderName = "libx264" };
                    }
                }

                recentFailures.RemoveAll(t => (now - t).TotalSeconds > 20);
                recentFailures.Add(now);
                if (recentFailures.Count >= 5)
                {
                    _wantReplayRunning = false;
                    StopBufferProcessesLocked();
                    var detail = string.IsNullOrWhiteSpace(_lastFfmpegError)
                        ? "Arka plan tamponu durduruldu"
                        : $"Durduruldu: {_lastFfmpegError}";
                    ReplayStateChanged?.Invoke(false, detail);
                    break;
                }

                try
                {
                    StopBufferProcessesLocked();
                    SpawnReplayPipelineLocked();
                }
                catch
                {
                    // Retry on next cycle.
                }
            }
        }
    }

    public async Task<string?> SaveClipAsync()
    {
        if (!await _clipSemaphore.WaitAsync(0).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            if (!IsReplayRunning || _ffmpegPath is null)
            {
                return null;
            }

            // Wait one HLS chunk boundary so the moment F8 was pressed is finalized.
            await Task.Delay(1100).ConfigureAwait(false);

            var m3u8Path = Path.Combine(BufferDir, "stream.m3u8");
            if (!File.Exists(m3u8Path))
            {
                return null;
            }

            var content = await File.ReadAllTextAsync(m3u8Path).ConfigureAwait(false);
            var segments = PlaylistHelper.ParseM3u8Segments(content, _replaySeconds);
            if (segments.Count == 0)
            {
                return null;
            }

            var snapDir = Path.Combine(Path.GetTempPath(), $"EkranSnap_{Guid.NewGuid():N}");
            Directory.CreateDirectory(snapDir);
            try
            {
                var copied = new List<string>();
                foreach (var seg in segments)
                {
                    var src = Path.Combine(BufferDir, seg);
                    if (File.Exists(src))
                    {
                        var dst = Path.Combine(snapDir, seg);
                        File.Copy(src, dst, overwrite: true);
                        copied.Add(dst);
                    }
                }

                if (copied.Count == 0)
                {
                    return null;
                }

                var concatFile = Path.Combine(snapDir, "list.txt");
                await File.WriteAllLinesAsync(
                    concatFile,
                    copied.Select(p => $"file '{p.Replace('\\', '/').Replace("'", "'\\''")}'"))
                    .ConfigureAwait(false);

                Directory.CreateDirectory(AppConfig.OutputDir);
                var outPath = Path.Combine(
                    AppConfig.OutputDir,
                    $"klip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in new[]
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-f", "concat", "-safe", "0", "-i", concatFile,
                    "-c", "copy", "-movflags", "+faststart", outPath,
                })
                {
                    psi.ArgumentList.Add(a);
                }

                using var concatProc = Process.Start(psi);
                if (concatProc is null)
                {
                    return null;
                }
                await concatProc.WaitForExitAsync().ConfigureAwait(false);

                if (concatProc.ExitCode == 0 && File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                {
                    return outPath;
                }

                if (File.Exists(outPath))
                {
                    File.Delete(outPath);
                }
                return null;
            }
            finally
            {
                TryDeleteDirectory(snapDir);
            }
        }
        finally
        {
            _clipSemaphore.Release();
        }
    }

    public string? StartManualRecording(Rectangle? region, bool includeSystemAudio, bool includeMic, int fps)
    {
        var err = EnsureReady();
        if (err is not null)
        {
            return err;
        }

        lock (_lock)
        {
            if (_manualProcess is { HasExited: false })
            {
                return "Kayıt zaten devam ediyor.";
            }

            if (_wantReplayRunning || _bufferProcess is not null)
            {
                _supervisorCts?.Cancel();
                _supervisorCts = null;
                StopBufferProcessesLocked();
            }

            Directory.CreateDirectory(AppConfig.OutputDir);
            var outPath = Path.Combine(
                AppConfig.OutputDir,
                $"ekran_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

            var audioPipe = AudioCapturePipe.TryCreate(includeSystemAudio, includeMic);
            var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
            args.AddRange(FfmpegProbe.BuildVideoInputArgs(_profile!, fps, region));

            if (audioPipe is not null)
            {
                args.AddRange(["-f", "s16le", "-ar", "48000", "-ac", "2", "-thread_queue_size", "512", "-i", audioPipe.PipePath]);
            }

            args.AddRange(FfmpegProbe.BuildVideoOutputFormatArgs(_profile!, region));
            args.AddRange(FfmpegProbe.BuildEncoderArgs(_profile!.EncoderName, fps));

            if (audioPipe is not null)
            {
                args.AddRange(["-c:a", "aac", "-b:a", "128k"]);
            }

            args.AddRange(["-movflags", "+faststart", outPath]);

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            try
            {
                var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg başlatılamadı.");
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        _lastFfmpegError = e.Data;
                    }
                };
                proc.BeginErrorReadLine();
                audioPipe?.Start();
                _manualAudio = audioPipe;
                _manualProcess = proc;
                _manualOutputPath = outPath;
                return null;
            }
            catch (Exception ex)
            {
                audioPipe?.Dispose();
                return ex.Message;
            }
        }
    }

    public string? StopManualRecording()
    {
        Process? proc;
        AudioCapturePipe? audio;
        string? outPath;

        lock (_lock)
        {
            proc = _manualProcess;
            audio = _manualAudio;
            outPath = _manualOutputPath;
            _manualProcess = null;
            _manualAudio = null;
            _manualOutputPath = null;
        }

        StopFfmpegGracefully(proc);
        audio?.Dispose();

        if (!string.IsNullOrEmpty(outPath) && File.Exists(outPath) && new FileInfo(outPath).Length > 0)
        {
            return outPath;
        }
        return null;
    }

    public static string? GetLatestRecording()
    {
        if (!Directory.Exists(AppConfig.OutputDir))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(AppConfig.OutputDir, "*.mp4")
            .Select(p => new FileInfo(p))
            .Where(fi => fi.Length > 0)
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .Select(fi => fi.FullName)
            .FirstOrDefault();
    }

    private static void StopFfmpegGracefully(Process? proc)
    {
        if (proc is null)
        {
            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                try
                {
                    proc.StandardInput.WriteLine("q");
                    proc.StandardInput.Flush();
                }
                catch
                {
                    // Stdin might already be closed.
                }

                if (!proc.WaitForExit(5000))
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2000);
                }
            }
        }
        catch
        {
            // Ignore shutdown races.
        }
        finally
        {
            proc.Dispose();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore locked temp files.
        }
    }

    public void Dispose()
    {
        StopManualRecording();
        StopReplay();
        _clipSemaphore.Dispose();
    }
}
