using System.Diagnostics;
using System.Text.Json;
using Deepgram;
using Deepgram.Models.Listen.v1.REST;
using Microsoft.Extensions.Configuration;

const string apiKeyVariable = "DEEPGRAM_API_KEY";
IConfiguration configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();
string? apiKey = configuration[apiKeyVariable];
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine($"Falta {apiKeyVariable} en appsettings.json/appsettings.Development.json o variable de entorno.");
    return 1;
}

string audioPath;
try
{
    audioPath = ResolveAudioPath(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

Console.WriteLine($"Audio: {audioPath}");
Console.WriteLine("Transcribiendo con Deepgram Nova-3 (multilingual + diarization)...");

Library.Initialize();
try
{
    var stopwatch = Stopwatch.StartNew();
    var client = ClientFactory.CreateListenRESTClient(apiKey);
    var response = await client.TranscribeFile(
        await File.ReadAllBytesAsync(audioPath),
        new PreRecordedSchema
        {
            Model = "nova-3",
            Language = "multi",
            SmartFormat = true,
            DiarizeModel = "latest",
            Utterances = true
        });
    stopwatch.Stop();

    using var document = JsonDocument.Parse(JsonSerializer.Serialize(response));
    JsonElement root = document.RootElement;
    string transcript = ReadString(root, "results", "channels", 0, "alternatives", 0, "transcript") ?? string.Empty;
    string? detectedLanguage = ReadString(root, "results", "channels", 0, "detected_language");
    string diarization = FormatDiarization(root);

    Console.WriteLine();
    Console.WriteLine("=== Transcript completo ===");
    Console.WriteLine(transcript);
    if (!string.IsNullOrWhiteSpace(diarization))
    {
        Console.WriteLine();
        Console.WriteLine("=== Diarización básica ===");
        Console.WriteLine(diarization);
    }

    string outputDirectory = Path.GetDirectoryName(audioPath)!;
    string transcriptPath = Path.Combine(outputDirectory, $"transcript-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
    string output = $"Transcript\n{transcript}\n";
    if (!string.IsNullOrWhiteSpace(diarization))
    {
        output += $"\nDiarización básica\n{diarization}\n";
    }
    await File.WriteAllTextAsync(transcriptPath, output);

    double durationSeconds = ReadWaveDuration(audioPath);
    Console.WriteLine();
    Console.WriteLine($"Transcript guardado: {transcriptPath}");
    Console.WriteLine($"Duración de audio: {durationSeconds:F2} s");
    Console.WriteLine($"Tiempo de transcripción: {stopwatch.Elapsed.TotalSeconds:F2} s");
    Console.WriteLine($"Idioma reportado: {detectedLanguage ?? "multi (configurado para español/inglés)"}");
    Console.WriteLine("Coherencia español/inglés: Nova-3 se solicitó con language=multi; valida el texto impreso para la evaluación semántica.");
    return 0;
}
finally
{
    Library.Terminate();
}

static string ResolveAudioPath(string[] args)
{
    if (args.Length > 0)
    {
        string explicitPath = Path.GetFullPath(args[0]);
        return File.Exists(explicitPath)
            ? explicitPath
            : throw new FileNotFoundException("No existe el archivo WAV indicado.", explicitPath);
    }

    string audioBinDirectory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "MeetingAssistant.AudioSpike", "bin"));
    string? latest = Directory.Exists(audioBinDirectory)
        ? Directory.EnumerateFiles(audioBinDirectory, "*.wav", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
        : null;

    return latest ?? throw new FileNotFoundException("No se encontró ningún WAV en MeetingAssistant.AudioSpike/spike-output. Pasa su path como argumento.");
}

static string? ReadString(JsonElement root, params object[] path)
{
    JsonElement current = root;
    foreach (object segment in path)
    {
        if (segment is string property)
        {
            if (!current.TryGetProperty(property, out current) && !TryGetPropertyIgnoreCase(current, property, out current))
            {
                return null;
            }
        }
        else if (segment is int index && (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength()))
        {
            return null;
        }
        else if (segment is int arrayIndex)
        {
            current = current[arrayIndex];
        }
    }
    return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
}

static string FormatDiarization(JsonElement root)
{
    if (!TryGetPath(root, out JsonElement utterances, "results", "utterances") || utterances.ValueKind != JsonValueKind.Array)
    {
        return string.Empty;
    }

    return string.Join(Environment.NewLine, utterances.EnumerateArray()
        .Select(utterance =>
        {
            string speaker = ReadString(utterance, "speaker") ?? "?";
            string text = ReadString(utterance, "transcript") ?? string.Empty;
            return $"Speaker {speaker}: {text}";
        })
        .Where(line => !line.EndsWith(": ", StringComparison.Ordinal)));
}

static bool TryGetPath(JsonElement root, out JsonElement result, params string[] properties)
{
    result = root;
    foreach (string property in properties)
    {
        if (!TryGetPropertyIgnoreCase(result, property, out result)) return false;
    }
    return true;
}

static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
{
    if (element.ValueKind != JsonValueKind.Object)
    {
        value = default;
        return false;
    }

    foreach (JsonProperty property in element.EnumerateObject())
    {
        if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
        {
            value = property.Value;
            return true;
        }
    }
    value = default;
    return false;
}

static double ReadWaveDuration(string path)
{
    using var reader = new BinaryReader(File.OpenRead(path));
    if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("El archivo no es WAV RIFF.");
    reader.ReadInt32();
    if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("El archivo no es WAV.");

    int byteRate = 0;
    int dataLength = 0;
    while (reader.BaseStream.Position < reader.BaseStream.Length)
    {
        string chunkId = new(reader.ReadChars(4));
        int chunkLength = reader.ReadInt32();
        if (chunkId == "fmt ")
        {
            reader.ReadInt16();
            reader.ReadInt16();
            reader.ReadInt32();
            byteRate = reader.ReadInt32();
            reader.BaseStream.Position += chunkLength - 12;
        }
        else if (chunkId == "data")
        {
            dataLength = chunkLength;
            break;
        }
        else
        {
            reader.BaseStream.Position += chunkLength;
        }
    }
    return byteRate == 0 ? 0 : (double)dataLength / byteRate;
}
