using System.Diagnostics;
using System.Text.Json;
using Deepgram;
using Deepgram.Models.Listen.v1.REST;
using MeetingAssistant.Core.Abstractions;

namespace MeetingAssistant.Infrastructure.Transcription;

public sealed class DeepgramTranscriptionClient : ITranscriptionClient
{
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

        Library.Initialize();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var client = ClientFactory.CreateListenRESTClient(apiKey);
            var response = await client.TranscribeFile(
                await File.ReadAllBytesAsync(audioPath, cancellationToken),
                new PreRecordedSchema { Model = "nova-3", Language = "multi", SmartFormat = true, DiarizeModel = "latest", Utterances = true });
            stopwatch.Stop();

            using var document = JsonDocument.Parse(JsonSerializer.Serialize(response));
            JsonElement root = document.RootElement;
            string transcript = ReadString(root, "results", "channels", 0, "alternatives", 0, "transcript") ?? string.Empty;
            string? language = ReadString(root, "results", "channels", 0, "detected_language");
            return new TranscriptionResult(transcript, ReadWaveDuration(audioPath), stopwatch.Elapsed, language, ReadUtterances(root));
        }
        finally { Library.Terminate(); }
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

    private static TimeSpan ReadWaveDuration(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("El archivo no es WAV RIFF.");
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("El archivo no es WAV.");
        int byteRate = 0, dataLength = 0;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            string chunkId = new(reader.ReadChars(4)); int chunkLength = reader.ReadInt32();
            if (chunkId == "fmt ") { reader.ReadInt16(); reader.ReadInt16(); reader.ReadInt32(); byteRate = reader.ReadInt32(); reader.BaseStream.Position += chunkLength - 12; }
            else if (chunkId == "data") { dataLength = chunkLength; break; }
            else reader.BaseStream.Position += chunkLength;
        }
        return byteRate == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)dataLength / byteRate);
    }
}
