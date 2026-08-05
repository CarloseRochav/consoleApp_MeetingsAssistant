using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

const string apiKeyVariable = "GEMINI_API_KEY";
const string appSettingsFileName = "appsettings.json";
const string model = "gemini-3.5-flash-lite";
string? apiKey = ReadApiKeyFromAppSettings() ?? Environment.GetEnvironmentVariable(apiKeyVariable);
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine($"Falta la clave de Gemini. Ponla en {appSettingsFileName} o en la variable de entorno {apiKeyVariable}.");
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
string prompt = "Resume la siguiente transcripción de reunión en exactamente 3 puntos breves. " +
    "Conserva los nombres, decisiones y pendientes cuando existan.\n\n" + promptTranscript;

Console.WriteLine($"Transcript: {transcriptPath}");
Console.WriteLine($"Modelo: {model}");
Console.WriteLine($"Carga de prompt: ~{prompt.Length / 4:N0} tokens (estimación por caracteres)." +
    (loadWasExtended ? " Se repitió el transcript corto solo para medir latencia con carga representativa." : string.Empty));

using var httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);
var requestBody = new
{
    contents = new[] { new { parts = new[] { new { text = prompt } } } },
    generationConfig = new { maxOutputTokens = 300 }
};

var stopwatch = Stopwatch.StartNew();
using HttpResponseMessage response = await httpClient.PostAsync(
    $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent",
    new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));
string responseJson = await response.Content.ReadAsStringAsync();
stopwatch.Stop();

if (!response.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"Gemini respondió {(int)response.StatusCode} {response.ReasonPhrase}: {responseJson}");
    return 1;
}

using var document = JsonDocument.Parse(responseJson);
JsonElement root = document.RootElement;
string answer = root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? string.Empty;
int inputTokens = ReadInt(root, "usageMetadata", "promptTokenCount");
int outputTokens = ReadInt(root, "usageMetadata", "candidatesTokenCount");
double estimatedCost = inputTokens * 0.30 / 1_000_000d + outputTokens * 2.50 / 1_000_000d;

Console.WriteLine();
Console.WriteLine("=== Respuesta ===");
Console.WriteLine(answer);
Console.WriteLine();
Console.WriteLine("Autenticación: correcta (respuesta HTTP exitosa de Gemini).");
Console.WriteLine($"Latencia total: {stopwatch.Elapsed.TotalSeconds:F2} s");
Console.WriteLine($"Uso reportado: {inputTokens} input tokens, {outputTokens} output tokens.");
Console.WriteLine($"Costo estimado: US${estimatedCost:F6} (precios públicos de Gemini 3.5 Flash-Lite; no reportado por la API).");
return 0;

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
    return latest ?? throw new FileNotFoundException("No se encontró transcript-*.txt. Ejecuta primero MeetingAssistant.TranscriptionSpike o pasa su path como argumento.");
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

static int ReadInt(JsonElement root, params string[] path)
{
    JsonElement current = root;
    foreach (string property in path)
    {
        if (!current.TryGetProperty(property, out current)) return 0;
    }
    return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out int value) ? value : 0;
}

static string? ReadApiKeyFromAppSettings()
{
    string path = Path.Combine(AppContext.BaseDirectory, appSettingsFileName);
    if (!File.Exists(path)) return null;

    using FileStream stream = File.OpenRead(path);
    using JsonDocument document = JsonDocument.Parse(stream);
    JsonElement root = document.RootElement;
    if (!root.TryGetProperty("Gemini", out JsonElement gemini)) return null;
    if (!gemini.TryGetProperty("ApiKey", out JsonElement apiKey)) return null;

    string? value = apiKey.GetString();
    return string.IsNullOrWhiteSpace(value) || value == "PUT_YOUR_API_KEY_HERE" ? null : value;
}
