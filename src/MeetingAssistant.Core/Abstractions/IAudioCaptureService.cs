namespace MeetingAssistant.Core.Abstractions;

public interface IAudioCaptureService
{
    bool IsCapturing { get; }
    Task StartAsync(string outputDirectory, CancellationToken cancellationToken = default);
    Task<AudioCaptureResult> StopAsync(CancellationToken cancellationToken = default);
}

public sealed record AudioCaptureResult(
    string AudioPath,
    TimeSpan Duration,
    string LoopbackDevice,
    string MicrophoneDevice);
