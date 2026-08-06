using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Cost;
using MeetingAssistant.Infrastructure.Llm;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Transcription;
using Microsoft.Extensions.Configuration;

const string appSettingsFileName = "appsettings.json";
int durationSeconds = args.Length > 0 && int.TryParse(args[0], out int parsedDuration) ? parsedDuration : 15;
if (durationSeconds <= 0)
{
    Console.Error.WriteLine("La duracion de captura debe ser mayor que cero.");
    return 1;
}

try
{
    IConfiguration configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile(appSettingsFileName, optional: false, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();

    ITranscriptionClient transcriptionClient = new DeepgramTranscriptionClient(ReadRequiredSetting(configuration, "Deepgram", "ApiKey", "DEEPGRAM_API_KEY"));
    ILlmClient llmClient = CreateLlmClient(configuration);
    ILlmReportExtractor reportExtractor = new LlmReportExtractor(
        llmClient,
        new ConfigPricingCostEstimator(configuration));
    IAudioCaptureService audioCapture = new AudioCaptureService();
    IReportStorage reportStorage = new MarkdownReportStorage(configuration);
    string outputDirectory = Path.Combine(AppContext.BaseDirectory, "meeting-output");
    IMeetingPipeline pipeline = new MeetingPipeline(
        audioCapture,
        transcriptionClient,
        reportExtractor,
        reportStorage,
        outputDirectory);

    Console.WriteLine("=== Meeting Assistant Harness ===");
    Console.WriteLine($"Ejecutando pipeline de {durationSeconds} segundos...");
    MeetingPipelineResult result = await pipeline.RunAsync(TimeSpan.FromSeconds(durationSeconds));
    Console.WriteLine($"Audio: {result.Audio.AudioPath}");
    Console.WriteLine($"Loopback: {result.Audio.LoopbackDevice}");
    Console.WriteLine($"Mic: {result.Audio.MicrophoneDevice}");
    Console.WriteLine();
    Console.WriteLine("=== Transcript completo ===");
    Console.WriteLine(result.Transcription.Transcript);
    foreach (DiarizedUtterance utterance in result.Transcription.Utterances)
        Console.WriteLine($"Speaker {utterance.Speaker}: {utterance.Transcript}");
    Console.WriteLine($"Duracion: {result.Transcription.AudioDuration.TotalSeconds:F2} s; transcripcion: {result.Transcription.Latency.TotalSeconds:F2} s.");
    PrintReport(result.Report);
    Console.WriteLine($"Reporte guardado: {result.SavedReportPath}");
    return 0;
}
catch (MeetingReportParseException exception)
{
    Console.Error.WriteLine($"Error al interpretar el reporte: {exception.Message}");
    Console.Error.WriteLine("=== Output LLM que no pudo interpretarse ===");
    Console.Error.WriteLine(exception.RawOutput);
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Error en el pipeline: {exception.Message}");
    return 1;
}

static ILlmClient CreateLlmClient(IConfiguration configuration)
{
    string provider = ReadSetting(configuration, "Llm", "Provider") ?? "Gemini";
    return provider.ToLowerInvariant() switch
    {
        "gemini" => new GeminiLlmClient(ReadRequiredSetting(configuration, "Gemini", "ApiKey", "GEMINI_API_KEY")),
        "azurefoundry" => new AzureFoundryLlmClient(
            ReadRequiredSetting(configuration, "AzureFoundry", "Endpoint"),
            ReadRequiredSetting(configuration, "AzureFoundry", "Deployment"),
            ReadSetting(configuration, "AzureFoundry", "ApiKey")),
        _ => throw new InvalidOperationException($"Proveedor '{provider}' no soportado. Usa 'Gemini' o 'AzureFoundry'.")
    };
}

static string ReadRequiredSetting(IConfiguration configuration, string section, string property, string? environmentVariable = null) =>
    ReadSetting(configuration, section, property, environmentVariable) ?? throw new InvalidOperationException(
        $"Falta configurar {section}:{property} en {appSettingsFileName} o la variable de entorno {environmentVariable}.");

static string? ReadSetting(IConfiguration configuration, string section, string property, string? environmentVariable = null)
{
    string? text = configuration[$"{section}:{property}"] ??
        (environmentVariable is null ? null : configuration[environmentVariable]);
    return string.IsNullOrWhiteSpace(text) || text.StartsWith("<", StringComparison.Ordinal) ? null : text;
}

static void PrintReport(MeetingReport report)
{
    Console.WriteLine();
    Console.WriteLine("=== Reporte de reunion ===");
    Console.WriteLine($"Resumen: {report.Summary}");
    PrintList("Insights", report.Insights);
    PrintList("Requirements", report.Requirements);
    PrintList("Indications", report.Indications);
    PrintList("Open questions", report.OpenQuestions);
    Console.WriteLine("Task list:");
    foreach (TaskItem task in report.TaskList)
        Console.WriteLine($"- [{task.Priority}] {task.Task}\n  Context: {task.Context}");

    if (report.Metadata is not null)
    {
        Console.WriteLine("Metadata:");
        Console.WriteLine($"Provider: {report.Metadata.LlmProvider}");
        Console.WriteLine($"Model: {report.Metadata.LlmModel}");
        Console.WriteLine($"Prompt version: {report.Metadata.PromptVersion}");
        Console.WriteLine($"Tokens: {report.Metadata.InputTokens} input, {report.Metadata.OutputTokens} output");
        Console.WriteLine($"Estimated cost: US${report.Metadata.EstimatedCostUsd:F6}");
    }
}

static void PrintList(string title, IReadOnlyList<string> values)
{
    Console.WriteLine($"{title}:");
    foreach (string value in values) Console.WriteLine($"- {value}");
}
