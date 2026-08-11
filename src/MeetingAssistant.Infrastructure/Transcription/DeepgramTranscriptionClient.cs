using System.Diagnostics;
using System.Text.Json;
using Deepgram;
using Deepgram.Clients.Interfaces.v1;
using Deepgram.Models.Listen.v1.REST;
using MeetingAssistant.Core.Abstractions;
using NAudio.Wave;

namespace MeetingAssistant.Infrastructure.Transcription;

public sealed class DeepgramTranscriptionClient : ITranscriptionClient
{
    // Deepgram's synchronous pre-recorded endpoint returns 504 for Nova requests
    // over 10 minutes. Leave one minute of margin for WAV metadata and rounding.
    private static readonly TimeSpan MaximumSegmentDuration = TimeSpan.FromMinutes(9);
    private readonly string apiKey;

    public DeepgramTranscriptionClient(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        this.apiKey = apiKey;
    }

    public async Task<TranscriptionResult> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        if (!File.Exists(audioPath)) throw new FileNotFoundException("No existe el archivo de audio indicado.", audioPath);

        FileInfo audioFile = new(audioPath);
        TimeSpan audioDuration = ReadAudioDuration(audioPath);
        IReadOnlyList<string> segments = CreateSegments(audioPath, audioDuration);

        Library.Initialize();
        try
        {
            var client = ClientFactory.CreateListenRESTClient(apiKey);
            var transcripts = new List<string>(segments.Count);
            var utterances = new List<DiarizedUtterance>();
            string? language = null;
            TimeSpan latency = TimeSpan.Zero;

            for (int index = 0; index < segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    TranscriptionResult segmentResult = await TranscribeSegmentAsync(client, segments[index], cancellationToken);
                    if (!string.IsNullOrWhiteSpace(segmentResult.Transcript)) transcripts.Add(segmentResult.Transcript);
                    utterances.AddRange(segmentResult.Utterances);
                    language ??= segmentResult.DetectedLanguage;
                    latency += segmentResult.Latency;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new TranscriptionFailedException(
                        audioPath, audioFile.Length, audioDuration, index + 1, segments.Count, exception);
                }
            }

            return new TranscriptionResult(string.Join(Environment.NewLine + Environment.NewLine, transcripts), audioDuration, latency, language, utterances);
        }
        finally
        {
            Library.Terminate();
            DeleteTemporarySegments(segments, audioPath);
        }
    }

    private static async Task<TranscriptionResult> TranscribeSegmentAsync(
        IListenRESTClient client,
        string segmentPath,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await client.TranscribeFile(
            await File.ReadAllBytesAsync(segmentPath, cancellationToken),
            new PreRecordedSchema { Model = "nova-3", Language = "multi", SmartFormat = true, DiarizeModel = "latest", Utterances = true });
        stopwatch.Stop();

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response));
        JsonElement root = document.RootElement;
        string transcript = ReadString(root, "results", "channels", 0, "alternatives", 0, "transcript") ?? string.Empty;
        string? language = ReadString(root, "results", "channels", 0, "detected_language");
        return new TranscriptionResult(transcript, ReadAudioDuration(segmentPath), stopwatch.Elapsed, language, ReadUtterances(root));
    }

    private static IReadOnlyList<string> CreateSegments(string audioPath, TimeSpan audioDuration)
    {
        if (audioDuration <= MaximumSegmentDuration) return [audioPath];

        string directory = Path.Combine(Path.GetTempPath(), "MeetingAssistant", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var segments = new List<string>();

        try
        {
            using WaveStream reader = CreateAudioReader(audioPath);
            int segmentBytes = checked((int)(reader.WaveFormat.AverageBytesPerSecond * MaximumSegmentDuration.TotalSeconds));
            segmentBytes -= segmentBytes % reader.WaveFormat.BlockAlign;
            var buffer = new byte[81920];

            while (reader.Position < reader.Length)
            {
                string segmentPath = Path.Combine(directory, $"segment-{segments.Count + 1:D3}.wav");
                using var writer = new WaveFileWriter(segmentPath, reader.WaveFormat);
                int remainingBytes = segmentBytes;
                while (remainingBytes > 0)
                {
                    int bytesRead = reader.Read(buffer, 0, Math.Min(buffer.Length, remainingBytes));
                    if (bytesRead == 0) break;
                    writer.Write(buffer, 0, bytesRead);
                    remainingBytes -= bytesRead;
                }
                segments.Add(segmentPath);
            }

            return segments;
        }
        catch
        {
            DeleteTemporarySegments(segments, audioPath);
            throw;
        }
    }

    private static void DeleteTemporarySegments(IReadOnlyList<string> segments, string originalAudioPath)
    {
        if (segments.Count == 1 && string.Equals(segments[0], originalAudioPath, StringComparison.OrdinalIgnoreCase)) return;

        foreach (string segment in segments)
        {
            try { File.Delete(segment); }
            catch (IOException) { }
        }

        if (segments.Count > 0)
        {
            try { Directory.Delete(Path.GetDirectoryName(segments[0])!, recursive: false); }
            catch (IOException) { }
        }
    }

    private static IReadOnlyList<DiarizedUtterance> ReadUtterances(JsonElement root)
    {
        if (!TryGetPath(root, out JsonElement utterances, "results", "utterances") || utterances.ValueKind != JsonValueKind.Array) return [];
        return utterances.EnumerateArray()
            .Select(item => new DiarizedUtterance(ReadString(item, "speaker") ?? "?", ReadString(item, "transcript") ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.Transcript)).ToArray();
    }

    private static string? ReadString(JsonElement root, params object[] path)
    {
        JsonElement current = root;
        foreach (object segment in path)
        {
            if (segment is string property)
            {
                if (!TryGetPropertyIgnoreCase(current, property, out current)) return null;
            }
            else if (segment is int arrayIndex)
            {
                if (current.ValueKind != JsonValueKind.Array || arrayIndex >= current.GetArrayLength()) return null;
                current = current[arrayIndex];
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static bool TryGetPath(JsonElement root, out JsonElement result, params string[] properties)
    {
        result = root;
        foreach (string property in properties) if (!TryGetPropertyIgnoreCase(result, property, out result)) return false;
        return true;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in element.EnumerateObject())
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static TimeSpan ReadAudioDuration(string path)
    {
        using WaveStream reader = CreateAudioReader(path);
        return reader.TotalTime;
    }

    private static WaveStream CreateAudioReader(string path) =>
        string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)
            ? new WaveFileReader(path)
            : new MediaFoundationReader(path);
}
