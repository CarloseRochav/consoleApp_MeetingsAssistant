using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Cost;
using MeetingAssistant.Infrastructure.Llm;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Transcription;
using Microsoft.Extensions.Configuration;

const string appSettingsFileName = "appsettings.json";
string? promptId = null;
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (string.Equals(args[i], "--prompt", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        promptId = args[++i];
        continue;
    }

    positional.Add(args[i]);
}

bool processExistingAudio = positional.Count == 2 && string.Equals(positional[0], "--process-file", StringComparison.OrdinalIgnoreCase);
bool extractExistingTranscript = positional.Count == 2 && string.Equals(positional[0], "--extract-transcript", StringComparison.OrdinalIgnoreCase);
string? existingAudioPath = positional.Count == 2 &&
    (string.Equals(positional[0], "--transcribe-file", StringComparison.OrdinalIgnoreCase) || processExistingAudio)
    ? positional[1]
    : null;
string? existingTranscriptPath = extractExistingTranscript ? positional[1] : null;
int durationSeconds = existingAudioPath is null && existingTranscriptPath is null && positional.Count > 0 && int.TryParse(positional[0], out int parsedDuration)
    ? parsedDuration
    : 15;
if (existingAudioPath is null && existingTranscriptPath is null && durationSeconds <= 0)
{
    Console.Error.WriteLine("La duracion de captura debe ser mayor que cero.");
    return 1;
}

if (existingAudioPath is not null && !File.Exists(existingAudioPath))
{
    Console.Error.WriteLine($"No existe el audio indicado: {existingAudioPath}");
    return 1;
}

if (existingTranscriptPath is not null && !File.Exists(existingTranscriptPath))
{
    Console.Error.WriteLine($"No existe el transcript indicado: {existingTranscriptPath}");
    return 1;
}

if (positional.Count == 1 && string.Equals(positional[0], "--verify-render", StringComparison.OrdinalIgnoreCase))
{
    return VerifyCatalogAndRender();
}

try
{
    IConfiguration configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile(appSettingsFileName, optional: false, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();

    IPromptCatalog promptCatalog = new BuiltInPromptCatalog();

    ITranscriptionClient? transcriptionClient = null;
    ITranscriptionClient GetTranscriptionClient() =>
        transcriptionClient ??= new DeepgramTranscriptionClient(ReadRequiredSetting(configuration, "Deepgram", "ApiKey", "DEEPGRAM_API_KEY"));

    if (existingAudioPath is not null && !processExistingAudio)
    {
        Console.WriteLine($"=== Transcribiendo archivo existente ==={Environment.NewLine}{existingAudioPath}");
        TranscriptionResult transcription = await GetTranscriptionClient().TranscribeAsync(existingAudioPath);
        Console.WriteLine($"Transcripción recibida: {transcription.Transcript.Length:N0} caracteres; {transcription.Utterances.Count:N0} intervenciones.");
        Console.WriteLine($"Duracion: {transcription.AudioDuration.TotalSeconds:F2} s; transcripcion: {transcription.Latency.TotalSeconds:F2} s.");
        return 0;
    }

    ILlmClient llmClient = CreateLlmClient(configuration);
    ILlmReportExtractor reportExtractor = new LlmReportExtractor(
        llmClient,
        new ConfigPricingCostEstimator(configuration),
        promptCatalog);

    if (existingTranscriptPath is not null)
    {
        string chosenPrompt = promptId ?? promptCatalog.Default.Id;
        Console.WriteLine($"=== Extrayendo reporte de transcript ==={Environment.NewLine}{existingTranscriptPath}");
        Console.WriteLine($"Prompt: {chosenPrompt}");
        string transcript = await File.ReadAllTextAsync(existingTranscriptPath);
        ExtractionResult extracted = await reportExtractor.ExtractAsync(transcript, chosenPrompt);
        IReportStorage transcriptStorage = new MarkdownReportStorage(configuration);
        string savedPath = await transcriptStorage.SaveMarkdownAsync(extracted.MarkdownBody, extracted.Metadata);
        Console.WriteLine();
        Console.WriteLine("=== Prompt usado ===");
        Console.WriteLine(extracted.Prompt.SystemPrompt);
        Console.WriteLine();
        Console.WriteLine("=== Reporte generado ===");
        Console.WriteLine(extracted.MarkdownBody);
        if (extracted.StructuredReport is not null)
        {
            PrintReport(extracted.StructuredReport);
        }
        Console.WriteLine($"Reporte guardado: {savedPath}");
        return 0;
    }
    IAudioCaptureService audioCapture = new AudioCaptureService();
    IReportStorage reportStorage = new MarkdownReportStorage(configuration);
    string outputDirectory = Path.Combine(AppContext.BaseDirectory, "meeting-output");
    IMeetingPipeline pipeline = new MeetingPipeline(
        audioCapture,
        GetTranscriptionClient(),
        reportExtractor,
        reportStorage,
        outputDirectory);

    if (existingAudioPath is not null)
    {
        Console.WriteLine($"=== Procesando archivo existente ==={Environment.NewLine}{existingAudioPath}");
        MeetingPipelineResult importedResult = await pipeline.ProcessAudioFileAsync(existingAudioPath);
        Console.WriteLine($"Audio: {importedResult.Audio.AudioPath}");
        Console.WriteLine($"Duración: {importedResult.Transcription.AudioDuration.TotalSeconds:F2} s; transcripción: {importedResult.Transcription.Latency.TotalSeconds:F2} s.");
        Console.WriteLine($"Reporte guardado: {importedResult.SavedReportPath}");
        return 0;
    }

    Console.WriteLine("=== Meeting Assistant Harness ===");
    await pipeline.StartRecordingAsync();
    Console.WriteLine($"Grabando {durationSeconds} segundos...");
    await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
    MeetingPipelineResult result = await pipeline.StopRecordingAndProcessAsync();
    Console.WriteLine($"Audio: {result.Audio.AudioPath}");
    Console.WriteLine($"Loopback: {result.Audio.LoopbackDevice}");
    Console.WriteLine($"Mic: {result.Audio.MicrophoneDevice}");
    Console.WriteLine();
    Console.WriteLine("=== Transcript completo ===");
    Console.WriteLine(result.Transcription.Transcript);
    foreach (DiarizedUtterance utterance in result.Transcription.Utterances)
        Console.WriteLine($"Speaker {utterance.Speaker}: {utterance.Transcript}");
    Console.WriteLine($"Duracion: {result.Transcription.AudioDuration.TotalSeconds:F2} s; transcripcion: {result.Transcription.Latency.TotalSeconds:F2} s.");
    if (result.Report is not null)
    {
        PrintReport(result.Report);
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine("=== Reporte generado ===");
        Console.WriteLine(result.ReportMarkdown);
    }
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

static int VerifyCatalogAndRender()
{
    IPromptCatalog catalog = new BuiltInPromptCatalog();
    Console.WriteLine("=== Catalogo de prompts ===");
    foreach (PromptDefinition prompt in catalog.GetAll())
    {
        Console.WriteLine($"- {prompt.Id} @{prompt.Version}: {prompt.DisplayName} ({prompt.OutputKind})");
    }

    PromptDefinition functional = catalog.GetById("functional-spec");
    Console.WriteLine();
    Console.WriteLine($"=== Prompt functional-spec @{functional.Version} ===");
    Console.WriteLine(functional.SystemPrompt);

    string[] requiredFragments =
    [
        "same language as the transcript",
        "Executive summary",
        "Identified entities and states",
        "Text-based flow diagram",
        "Business rules and conditions",
        "Ambiguous or pending points to confirm",
        "Agreed actions/decisions"
    ];

    string[] missing = requiredFragments
        .Where(fragment => !functional.SystemPrompt.Contains(fragment, StringComparison.Ordinal))
        .ToArray();

    if (missing.Length > 0)
    {
        Console.Error.WriteLine("El prompt functional-spec no contiene: " + string.Join(", ", missing));
        return 1;
    }

    if (functional.SystemPrompt.Contains("CFS", StringComparison.Ordinal) ||
        functional.Version != FunctionalSpecPrompt.Version)
    {
        Console.Error.WriteLine("El prompt functional-spec no es la versión esperada o todavía menciona un dominio ajeno.");
        return 1;
    }

    Console.WriteLine("OK: catalogo y prompt de especificación funcional.");
    return 0;
}
