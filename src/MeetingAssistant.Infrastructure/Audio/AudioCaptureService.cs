using System.Diagnostics;
using MeetingAssistant.Core.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingAssistant.Infrastructure.Audio;

public sealed class AudioCaptureService : IAudioCaptureService
{
    private readonly object sync = new();
    private CancellationTokenSource? captureCancellation;
    private Task<AudioCaptureResult>? captureTask;
    private bool isCapturing;

    public bool IsCapturing
    {
        get { lock (sync) return isCapturing; }
    }

    public Task StartAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        lock (sync)
        {
            if (isCapturing)
            {
                throw new InvalidOperationException("Ya hay una captura de audio en curso.");
            }

            captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            isCapturing = true;
            captureTask = Task.Run(() => CaptureUntilStoppedAsync(outputDirectory, captureCancellation.Token));
        }

        return Task.CompletedTask;
    }

    public async Task<AudioCaptureResult> StopAsync(CancellationToken cancellationToken = default)
    {
        Task<AudioCaptureResult> task;
        CancellationTokenSource cancellation;
        lock (sync)
        {
            if (!isCapturing || captureTask is null || captureCancellation is null)
            {
                throw new InvalidOperationException("No hay una captura de audio en curso para detener.");
            }

            task = captureTask;
            cancellation = captureCancellation;
            cancellation.Cancel();
        }

        try
        {
            // Do not let the caller's cancellation interrupt cleanup: the WAV must
            // be flushed and its capture devices disposed before this method returns.
            return await task.ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(task, captureTask))
                {
                    captureTask = null;
                    captureCancellation?.Dispose();
                    captureCancellation = null;
                    isCapturing = false;
                }
            }
        }
    }

    private static async Task<AudioCaptureResult> CaptureUntilStoppedAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(outputDirectory, $"meeting-{DateTime.Now:yyyyMMdd-HHmmss}.wav");

        using var defaultRenderDevice = GetDefaultDevice(DataFlow.Render);
        using var defaultMicDevice = GetDefaultDevice(DataFlow.Capture);
        using var loopbackCapture = new WasapiLoopbackCapture(defaultRenderDevice);
        using var micCapture = new WasapiCapture(defaultMicDevice);
        var loopbackSampleProvider = new WaveInProvider(loopbackCapture).ToSampleProvider();
        var micSampleProvider = new WaveInProvider(micCapture).ToSampleProvider();
        var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        var mixer = new MixingSampleProvider(targetFormat) { ReadFully = true };
        mixer.AddMixerInput(NormalizeToTarget(loopbackSampleProvider, targetFormat));
        mixer.AddMixerInput(NormalizeToTarget(micSampleProvider, targetFormat));

        var mixedWaveProvider = new SampleToWaveProvider16(mixer);
        using var writer = new WaveFileWriter(outputPath, mixedWaveProvider.WaveFormat);
        const int blockDurationMilliseconds = 20;
        int blockBytes = mixedWaveProvider.WaveFormat.AverageBytesPerSecond * blockDurationMilliseconds / 1000;
        var buffer = new byte[blockBytes];
        loopbackCapture.StartRecording();
        micCapture.StartRecording();

        var recordingClock = Stopwatch.StartNew();
        long nextBlockStartMilliseconds = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long delayMilliseconds = nextBlockStartMilliseconds - recordingClock.ElapsedMilliseconds;
                if (delayMilliseconds > 0)
                {
                    try
                    {
                        await Task.Delay((int)delayMilliseconds, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                if (cancellationToken.IsCancellationRequested) break;
                int bytesRead = mixedWaveProvider.Read(buffer, 0, buffer.Length);
                if (bytesRead > 0) writer.Write(buffer, 0, bytesRead);
                nextBlockStartMilliseconds += blockDurationMilliseconds;
            }

            return new AudioCaptureResult(outputPath, recordingClock.Elapsed, defaultRenderDevice.FriendlyName, defaultMicDevice.FriendlyName);
        }
        finally
        {
            loopbackCapture.StopRecording();
            micCapture.StopRecording();
        }
    }

    private static ISampleProvider NormalizeToTarget(ISampleProvider input, WaveFormat targetFormat)
    {
        var mono = input.WaveFormat.Channels > 1 ? input.ToMono() : input;
        return mono.WaveFormat.SampleRate == targetFormat.SampleRate ? mono : new WdlResamplingSampleProvider(mono, targetFormat.SampleRate);
    }

    private static MMDevice GetDefaultDevice(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
    }
}
