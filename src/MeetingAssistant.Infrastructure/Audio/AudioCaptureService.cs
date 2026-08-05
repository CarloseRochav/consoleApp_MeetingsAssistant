using System.Diagnostics;
using MeetingAssistant.Core.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingAssistant.Infrastructure.Audio;

public sealed class AudioCaptureService : IAudioCaptureService
{
    public Task<AudioCaptureResult> CaptureAsync(TimeSpan duration, string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        return Task.Run(() => Capture(duration, outputDirectory, cancellationToken), cancellationToken);
    }

    private static AudioCaptureResult Capture(TimeSpan duration, string outputDirectory, CancellationToken cancellationToken)
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

        try
        {
            var recordingClock = Stopwatch.StartNew();
            long nextBlockStartMilliseconds = 0;
            while (recordingClock.Elapsed < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long delayMilliseconds = nextBlockStartMilliseconds - recordingClock.ElapsedMilliseconds;
                if (delayMilliseconds > 0) Thread.Sleep((int)delayMilliseconds);

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
