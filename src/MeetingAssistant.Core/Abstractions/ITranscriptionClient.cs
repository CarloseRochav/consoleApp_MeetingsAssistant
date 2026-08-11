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

/// <summary>
/// Preserves diagnostic context when a transcription provider cannot process an audio file.
/// It deliberately contains no provider-specific types, so callers can display useful details
/// without coupling Core to a transcription SDK.
/// </summary>
public sealed class TranscriptionFailedException : Exception
{
    public TranscriptionFailedException(
        string audioPath,
        long audioSizeBytes,
        TimeSpan audioDuration,
        int segmentNumber,
        int segmentCount,
        Exception innerException)
        : base(
            $"No se pudo transcribir el segmento {segmentNumber} de {segmentCount} " +
            $"del audio ({audioDuration:g}, {audioSizeBytes:N0} bytes): {innerException.Message}",
            innerException)
    {
        AudioPath = audioPath;
        AudioSizeBytes = audioSizeBytes;
        AudioDuration = audioDuration;
        SegmentNumber = segmentNumber;
        SegmentCount = segmentCount;
    }

    public string AudioPath { get; }
    public long AudioSizeBytes { get; }
    public TimeSpan AudioDuration { get; }
    public int SegmentNumber { get; }
    public int SegmentCount { get; }
}
