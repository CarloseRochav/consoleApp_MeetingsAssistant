namespace MeetingAssistant.Core.Abstractions;

public interface IAudioCaptureService
{
    Task<AudioCaptureResult> CaptureAsync(
        TimeSpan duration,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}

public sealed record AudioCaptureResult(
    string AudioPath,
    TimeSpan Duration,
    string LoopbackDevice,
    string MicrophoneDevice);
