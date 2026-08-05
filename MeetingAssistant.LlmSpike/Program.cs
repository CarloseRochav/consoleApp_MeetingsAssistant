using System.Text;
using System.Text.Json;
using MeetingAssistant.LlmSpike.Services;

const string appSettingsFileName = "appsettings.json";
ILlmClient llmClient;
try
{
    llmClient = CreateLlmClient();
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Configuracion LLM invalida: {exception.Message}");
    return 1;
}

string transcriptPath;
try
{
    transcriptPath = ResolveTranscriptPath(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

string transcript = await File.ReadAllTextAsync(transcriptPath);
string promptTranscript = RepeatToMinimumLength(transcript, 16_000, out bool loadWasExtended);

Console.WriteLine($"Transcript: {transcriptPath}");
Console.WriteLine($"Carga de prompt: ~{promptTranscript.Length / 4:N0} tokens (estimacion por caracteres)." +
    (loadWasExtended ? " Se repitio el transcript corto solo para medir latencia con carga representativa." : string.Empty));

try
{
    var llmService = new LLMService(llmClient);
    Console.WriteLine($"Proveedor: {llmService.Provider}");
    Console.WriteLine($"Modelo: {llmService.Model}");
    LlmResponse result = await llmService.SummarizeAsync(promptTranscript);

    Console.WriteLine();
    Console.WriteLine("=== Respuesta ===");
    Console.WriteLine(result.Text);
    Console.WriteLine();
    Console.WriteLine($"Autenticacion: correcta (respuesta exitosa de {llmService.Provider}).");
    Console.WriteLine($"Latencia total: {result.Latency.TotalSeconds:F2} s");
    Console.WriteLine($"Uso reportado: {result.InputTokens} input tokens, {result.OutputTokens} output tokens, {result.ThinkingTokens} thinking tokens.");
    if (llmService.Provider == "Gemini")
    {
        double estimatedCost = result.InputTokens * 0.30 / 1_000_000d +
            (result.OutputTokens + result.ThinkingTokens) * 2.50 / 1_000_000d;
        Console.WriteLine($"Costo estimado: US${estimatedCost:F6} (precio publico de Gemini 3.5 Flash-Lite; no reportado por la API).");
    }
    else
    {
        Console.WriteLine("Costo estimado: no configurado para este proveedor.");
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Error al llamar al proveedor LLM: {exception.Message}");
    return 1;
}

static string ResolveTranscriptPath(string[] args)
{
    if (args.Length > 0)
    {
        string explicitPath = Path.GetFullPath(args[0]);
        return File.Exists(explicitPath)
            ? explicitPath
            : throw new FileNotFoundException("No existe el transcript indicado.", explicitPath);
    }

    string audioBinDirectory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "MeetingAssistant.AudioSpike", "bin"));
    string? latest = Directory.Exists(audioBinDirectory)
        ? Directory.EnumerateFiles(audioBinDirectory, "transcript-*.txt", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
        : null;
    return latest ?? throw new FileNotFoundException("No se encontro transcript-*.txt. Ejecuta primero MeetingAssistant.TranscriptionSpike o pasa su path como argumento.");
}

static string RepeatToMinimumLength(string text, int minimumLength, out bool extended)
{
    extended = text.Length < minimumLength;
    if (!extended) return text;

    var builder = new StringBuilder(minimumLength + text.Length);
    while (builder.Length < minimumLength)
    {
        builder.AppendLine(text);
    }
    return builder.ToString();
}

static ILlmClient CreateLlmClient()
{
    string provider = ReadSetting("Llm", "Provider") ?? "Gemini";
    return provider.ToLowerInvariant() switch
    {
        "gemini" => new GeminiLlmClient(
            ReadRequiredSetting("Gemini", "ApiKey")),
        "azurefoundry" => new AzureFoundryLlmClient(
            ReadRequiredSetting("AzureFoundry", "Endpoint"),
            ReadRequiredSetting("AzureFoundry", "Deployment"),
            ReadSetting("AzureFoundry", "ApiKey")),
        _ => throw new InvalidOperationException(
            $"Proveedor '{provider}' no soportado. Usa 'Gemini' o 'AzureFoundry'.")
    };
}

static string ReadRequiredSetting(string section, string property)
{
    return ReadSetting(section, property) ?? throw new InvalidOperationException(
        $"Falta configurar {section}:{property} en {appSettingsFileName}.");
}

static string? ReadSetting(string section, string property)
{
    string path = Path.Combine(AppContext.BaseDirectory, appSettingsFileName);
    if (!File.Exists(path)) return null;

    using FileStream stream = File.OpenRead(path);
    using JsonDocument document = JsonDocument.Parse(stream);
    JsonElement root = document.RootElement;
    if (!root.TryGetProperty(section, out JsonElement configuration)) return null;
    if (!configuration.TryGetProperty(property, out JsonElement value)) return null;

    string? text = value.GetString();
    return string.IsNullOrWhiteSpace(text) ||
        text.StartsWith("PUT_", StringComparison.Ordinal) ||
        text.StartsWith("<", StringComparison.Ordinal)
        ? null
        : text;
}
