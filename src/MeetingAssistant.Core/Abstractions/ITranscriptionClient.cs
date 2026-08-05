namespace MeetingAssistant.Core.Abstractions;

public interface ITranscriptionClient
{
    Task<TranscriptionResult> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default);
}

public sealed record TranscriptionResult(
    string Transcript,
    TimeSpan AudioDuration,
    TimeSpan Latency,
    string? DetectedLanguage,
    IReadOnlyList<DiarizedUtterance> Utterances);

public sealed record DiarizedUtterance(string Speaker, string Transcript);
