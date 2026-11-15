using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EkranKaydedici.Services;

/// <summary>
/// Captures Windows WASAPI system loopback and/or microphone, mixes them to
/// 48 kHz 16-bit stereo PCM at a steady real-time cadence, and streams into a named pipe.
/// </summary>
public sealed class AudioCapturePipe : IDisposable
{
    private const int TargetSampleRate = 48000;
    private const int TargetChannels = 2;
    private const int ChunkMs = 20;
    private const int BytesPerChunk = TargetSampleRate * TargetChannels * 2 * ChunkMs / 1000; // 3840

    private readonly CancellationTokenSource _cts = new();
    private readonly List<IWaveIn> _captures = [];
    private readonly NamedPipeServerStream _pipeServer;
    private readonly Task _pumpTask;
    private bool _disposed;

    public string PipePath { get; }
    public bool HasActiveSource { get; }

    private AudioCapturePipe(
        string pipeName,
        List<IWaveIn> captures,
        IWaveProvider outputWaveProvider)
    {
        PipePath = $@"\\.\pipe\{pipeName}";
        _captures = captures;
        HasActiveSource = captures.Count > 0;

        _pipeServer = new NamedPipeServerStream(
            pipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            BytesPerChunk * 16);

        _pumpTask = Task.Run(() => PumpLoopAsync(outputWaveProvider, _cts.Token));
    }

    public static AudioCapturePipe? TryCreate(bool includeSystem, bool includeMic)
    {
        if (!includeSystem && !includeMic)
        {
            return null;
        }

        var captures = new List<IWaveIn>();
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(TargetSampleRate, TargetChannels))
        {
            ReadFully = true,
        };

        if (includeSystem)
        {
            try
            {
                var loopback = new WasapiLoopbackCapture();
                AttachCapture(loopback, captures, mixer);
            }
            catch
            {
                // System playback device unavailable.
            }
        }

        if (includeMic)
        {
            try
            {
                var mic = new WasapiCapture();
                AttachCapture(mic, captures, mixer);
            }
            catch
            {
                // Microphone unavailable.
            }
        }

        if (captures.Count == 0)
        {
            return null;
        }

        var pipeName = $"EkranKaydediciAudio_{Environment.ProcessId}_{Guid.NewGuid():N}";
        var pcm16 = new SampleToWaveProvider16(mixer);
        return new AudioCapturePipe(pipeName, captures, pcm16);
    }

    private static void AttachCapture(IWaveIn capture, List<IWaveIn> captures, MixingSampleProvider mixer)
    {
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
            BufferDuration = TimeSpan.FromMilliseconds(500),
        };

        capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0)
            {
                buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            }
        };

        ISampleProvider sample = buffer.ToSampleProvider();
        if (sample.WaveFormat.Channels == 1)
        {
            sample = new MonoToStereoSampleProvider(sample);
        }
        else if (sample.WaveFormat.Channels > 2)
        {
            var mux = new MultiplexingSampleProvider([sample], 2);
            mux.ConnectInputToOutput(0, 0);
            mux.ConnectInputToOutput(1, 1);
            sample = mux;
        }

        if (sample.WaveFormat.SampleRate != TargetSampleRate)
        {
            sample = new WdlResamplingSampleProvider(sample, TargetSampleRate);
        }

        mixer.AddMixerInput(sample);
        captures.Add(capture);
    }

    public void Start()
    {
        foreach (var cap in _captures)
        {
            try
            {
                cap.StartRecording();
            }
            catch
            {
                // Keep remaining sources active.
            }
        }
    }

    private async Task PumpLoopAsync(IWaveProvider outputProvider, CancellationToken ct)
    {
        try
        {
            await _pipeServer.WaitForConnectionAsync(ct).ConfigureAwait(false);
            var chunk = new byte[BytesPerChunk];
            var sw = Stopwatch.StartNew();
            long nextTickMs = ChunkMs;

            while (!ct.IsCancellationRequested && _pipeServer.IsConnected)
            {
                var read = outputProvider.Read(chunk, 0, chunk.Length);
                if (read < chunk.Length)
                {
                    Array.Clear(chunk, read, chunk.Length - read);
                }

                await _pipeServer.WriteAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false);

                var sleepMs = (int)(nextTickMs - sw.ElapsedMilliseconds);
                if (sleepMs > 1)
                {
                    await Task.Delay(sleepMs, ct).ConfigureAwait(false);
                }
                nextTickMs += ChunkMs;
            }
        }
        catch
        {
            // Pipe disconnected or cancelled on stop.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _cts.Cancel();
        foreach (var cap in _captures)
        {
            try { cap.StopRecording(); } catch { }
            try { cap.Dispose(); } catch { }
        }
        _captures.Clear();

        try { _pipeServer.Dispose(); } catch { }
        try { _pumpTask.Wait(500); } catch { }
        _cts.Dispose();
    }
}
