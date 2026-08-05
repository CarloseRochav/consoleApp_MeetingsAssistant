using System.Text;
using System.Text.Json;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Llm;
using MeetingAssistant.Infrastructure.Transcription;

const string appSettingsFileName = "appsettings.json";
int durationSeconds = args.Length > 0 && int.TryParse(args[0], out int parsedDuration) ? parsedDuration : 15;
if (durationSeconds <= 0)
{
    Console.Error.WriteLine("La duracion de captura debe ser mayor que cero.");
    return 1;
}

try
{
    ITranscriptionClient transcriptionClient = new DeepgramTranscriptionClient(ReadRequiredSetting("Deepgram", "ApiKey", "DEEPGRAM_API_KEY"));
    ILlmClient llmClient = CreateLlmClient();
    IAudioCaptureService audioCapture = new AudioCaptureService();

    Console.WriteLine("=== Meeting Assistant Harness ===");
    Console.WriteLine($"Grabando {durationSeconds} segundos...");
    string outputDirectory = Path.Combine(AppContext.BaseDirectory, "meeting-output");
    AudioCaptureResult capture = await audioCapture.CaptureAsync(TimeSpan.FromSeconds(durationSeconds), outputDirectory);
    Console.WriteLine($"Audio: {capture.AudioPath}");
    Console.WriteLine($"Loopback: {capture.LoopbackDevice}");
    Console.WriteLine($"Mic: {capture.MicrophoneDevice}");

    Console.WriteLine("Transcribiendo con Deepgram Nova-3...");
    TranscriptionResult transcription = await transcriptionClient.TranscribeAsync(capture.AudioPath);
    Console.WriteLine();
    Console.WriteLine("=== Transcript completo ===");
    Console.WriteLine(transcription.Transcript);
    foreach (DiarizedUtterance utterance in transcription.Utterances)
        Console.WriteLine($"Speaker {utterance.Speaker}: {utterance.Transcript}");
    Console.WriteLine($"Duracion: {transcription.AudioDuration.TotalSeconds:F2} s; transcripcion: {transcription.Latency.TotalSeconds:F2} s.");

    const string instruction = "Resume la siguiente transcripcion de reunion en exactamente 3 puntos breves. " +
        "Conserva los nombres, decisiones y pendientes cuando existan.\n\n";
    string promptTranscript = RepeatToMinimumLength(transcription.Transcript, 16_000, out bool extended);
    Console.WriteLine($"Llamando {llmClient.Provider} ({llmClient.Model})..." +
        (extended ? " Transcript repetido para conservar la carga de latencia del spike LLM." : string.Empty));
    LlmProviderResponse response = await llmClient.GenerateAsync(new LlmRequest(instruction + promptTranscript, 300));
    if (string.IsNullOrWhiteSpace(response.Text)) throw new InvalidOperationException($"{llmClient.Provider} no devolvio texto.");

    Console.WriteLine();
    Console.WriteLine("=== Respuesta LLM ===");
    Console.WriteLine(response.Text);
    Console.WriteLine($"Uso reportado: {response.InputTokens} input, {response.OutputTokens} output, {response.ThinkingTokens} reasoning tokens.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Error en el pipeline: {exception.Message}");
    return 1;
}

static ILlmClient CreateLlmClient()
{
    string provider = ReadSetting("Llm", "Provider") ?? "Gemini";
    return provider.ToLowerInvariant() switch
    {
        "gemini" => new GeminiLlmClient(ReadRequiredSetting("Gemini", "ApiKey", "GEMINI_API_KEY")),
        "azurefoundry" => new AzureFoundryLlmClient(
            ReadRequiredSetting("AzureFoundry", "Endpoint"),
            ReadRequiredSetting("AzureFoundry", "Deployment"),
            ReadSetting("AzureFoundry", "ApiKey")),
        _ => throw new InvalidOperationException($"Proveedor '{provider}' no soportado. Usa 'Gemini' o 'AzureFoundry'.")
    };
}

static string ReadRequiredSetting(string section, string property, string? environmentVariable = null) =>
    ReadSetting(section, property, environmentVariable) ?? throw new InvalidOperationException(
        $"Falta configurar {section}:{property} en {appSettingsFileName} o la variable de entorno {environmentVariable}.");

static string? ReadSetting(string section, string property, string? environmentVariable = null)
{
    string? environmentValue = environmentVariable is null ? null : Environment.GetEnvironmentVariable(environmentVariable);
    if (!string.IsNullOrWhiteSpace(environmentValue)) return environmentValue;
    string path = Path.Combine(AppContext.BaseDirectory, appSettingsFileName);
    if (!File.Exists(path)) return null;
    using FileStream stream = File.OpenRead(path);
    using JsonDocument document = JsonDocument.Parse(stream);
    if (!document.RootElement.TryGetProperty(section, out JsonElement configuration) ||
        !configuration.TryGetProperty(property, out JsonElement value)) return null;
    string? text = value.GetString();
    return string.IsNullOrWhiteSpace(text) || text.StartsWith("<", StringComparison.Ordinal) ? null : text;
}

static string RepeatToMinimumLength(string text, int minimumLength, out bool extended)
{
    extended = text.Length < minimumLength;
    if (!extended) return text;
    var builder = new StringBuilder(minimumLength + text.Length);
    while (builder.Length < minimumLength) builder.AppendLine(text);
    return builder.ToString();
}
