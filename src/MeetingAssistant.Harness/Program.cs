using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Cost;
using MeetingAssistant.Infrastructure.Llm;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using MeetingAssistant.Infrastructure.Transcription;
using Microsoft.Data.Sqlite;
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

if (positional.Count == 1 && string.Equals(positional[0], "--verify-db", StringComparison.OrdinalIgnoreCase))
{
    return VerifyDatabase();
}

if (positional.Count == 1 && string.Equals(positional[0], "--verify-db-selftest", StringComparison.OrdinalIgnoreCase))
{
    return VerifyDatabaseSelfTest();
}

if (positional.Count == 2 &&
    string.Equals(positional[0], "--verify-pipeline-history", StringComparison.OrdinalIgnoreCase))
{
    return await VerifyPipelineHistoryAsync(positional[1], promptId);
}

if (positional.Count == 1 && string.Equals(positional[0], "--verify-settings-config", StringComparison.OrdinalIgnoreCase))
{
    return VerifySettingsConfiguration();
}

if (positional.Count is 2 or 3 && string.Equals(positional[0], "--set-setting", StringComparison.OrdinalIgnoreCase))
{
    return SetRealSetting(positional[1], positional.Count == 3 ? positional[2] : null);
}

if (positional.Count == 1 && string.Equals(positional[0], "--verify-reextraction", StringComparison.OrdinalIgnoreCase))
{
    return await VerifyReextractionAsync();
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
    await pipeline.StartRecordingAsync(SessionSource.Harness);
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

/// <summary>
/// Corre el pipeline completo sobre un audio existente con un almacen de
/// historial apuntando a una base <b>temporal</b>, y comprueba que quedaron
/// registradas la sesion, el transcript y el reporte — ademas del .md en el
/// vault.
///
/// Existe porque el autotest de esquema prueba el store, pero no el cableado:
/// que el pipeline cree la sesion, guarde el transcript antes de extraer y
/// registre el reporte con su vault_path. Y porque hacerlo sobre un audio
/// existente no necesita microfono, que es justo lo que puede estar bloqueado
/// por el consentimiento de Windows.
///
/// Al final repite la corrida con la base apuntada a una ruta imposible, para
/// comprobar la regla que manda en este paso: <b>un fallo de base no puede
/// tumbar una grabacion</b>.
/// </summary>
static async Task<int> VerifyPipelineHistoryAsync(string audioPath, string? requestedPromptId)
{
    if (!File.Exists(audioPath))
    {
        Console.Error.WriteLine($"No existe el audio indicado: {audioPath}");
        return 1;
    }

    int failures = 0;
    void Check(string description, bool passed)
    {
        Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
        if (!passed) failures++;
    }

    IConfiguration configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();

    IPromptCatalog catalog = new BuiltInPromptCatalog();
    ILlmReportExtractor extractor = new LlmReportExtractor(
        CreateLlmClient(configuration), new ConfigPricingCostEstimator(configuration), catalog);
    ITranscriptionClient transcription = new DeepgramTranscriptionClient(
        ReadRequiredSetting(configuration, "Deepgram", "ApiKey", "DEEPGRAM_API_KEY"));

    string temporaryPath = Path.Combine(Path.GetTempPath(), $"ma-pipeline-{Guid.NewGuid():N}.db");
    var failuresSeen = new List<string>();

    try
    {
        var factory = new SqliteConnectionFactory(temporaryPath);
        new SqliteSchemaMigrator(factory).Migrate();
        IMeetingHistoryStore history = new SqliteMeetingHistoryStore(factory);

        Console.WriteLine($"=== Pipeline con historial ==={Environment.NewLine}{temporaryPath}{Environment.NewLine}");

        IMeetingPipeline pipeline = new MeetingPipeline(
            new AudioCaptureService(),
            transcription,
            extractor,
            new MarkdownReportStorage(configuration),
            Path.Combine(AppContext.BaseDirectory, "meeting-output"),
            history,
            (operation, exception) => failuresSeen.Add($"{operation}: {exception.Message}"));

        MeetingPipelineResult result = await pipeline.ProcessAudioFileAsync(audioPath);

        Check("el .md llego al vault", File.Exists(result.SavedReportPath));
        Check("no hubo fallos de historial", failuresSeen.Count == 0);
        if (failuresSeen.Count > 0) failuresSeen.ForEach(f => Console.WriteLine($"        {f}"));

        IReadOnlyList<SessionSummary> sessions = await history.ListSessionsAsync(10);
        Check("quedo registrada exactamente una sesion", sessions.Count == 1);
        Check("la sesion se marco como importada",
            sessions.Count == 1 && (await history.GetSessionAsync(sessions[0].SessionId))?.Source == SessionSource.Import);

        long sessionId = sessions[0].SessionId;
        TranscriptRecord? storedTranscript = await history.GetTranscriptAsync(sessionId);
        Check("el transcript quedo guardado", storedTranscript is not null);
        Check("el transcript guardado es el mismo que devolvio el pipeline",
            storedTranscript?.Text == result.Transcription.Transcript);

        IReadOnlyList<ReportRecord> reports = await history.GetReportsAsync(sessionId);
        Check("quedo registrado un reporte", reports.Count == 1);
        Check("el reporte apunta al .md del vault",
            reports.Count == 1 && reports[0].VaultPath == result.SavedReportPath);
        Check("el markdown guardado coincide con el generado",
            reports.Count == 1 && reports[0].Markdown == result.ReportMarkdown);
        Check("el costo del reporte quedo registrado",
            reports.Count == 1 && reports[0].CostUsd is not null);
        // structured_json solo para assignment-meeting; los demas prompts dan
        // Markdown suelto y tienen que dejarlo en null.
        bool isAssignmentPrompt = reports.Count == 1 && reports[0].PromptId == ReportExtractionPrompt.Id;
        Check($"structured_json coherente con el prompt ({(isAssignmentPrompt ? "assignment-meeting: presente" : "otro prompt: null")})",
            reports.Count == 1 && (isAssignmentPrompt ? reports[0].StructuredJson is not null : reports[0].StructuredJson is null));

        Check("la busqueda encuentra la reunion recien guardada",
            (await history.SearchTranscriptsAsync(
                result.Transcription.Transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])).Count >= 1);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"El pipeline con historial reviento: {exception}");
        failures++;
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string leftover in new[] { temporaryPath, temporaryPath + "-wal", temporaryPath + "-shm" })
        {
            try { if (File.Exists(leftover)) File.Delete(leftover); } catch { /* best effort */ }
        }
    }

    // --- La regla que manda: una base rota no puede tumbar una grabacion -----
    Console.WriteLine($"{Environment.NewLine}--- Resiliencia: base apuntada a una ruta imposible ---");
    var brokenFailures = new List<string>();
    try
    {
        // Un directorio que no existe y no se puede crear.
        var brokenFactory = new SqliteConnectionFactory(@"\\?\Z:\no-existe\jamas\meetings.db");
        IMeetingHistoryStore brokenHistory = new SqliteMeetingHistoryStore(brokenFactory);

        IMeetingPipeline resilientPipeline = new MeetingPipeline(
            new AudioCaptureService(),
            transcription,
            extractor,
            new MarkdownReportStorage(configuration),
            Path.Combine(AppContext.BaseDirectory, "meeting-output"),
            brokenHistory,
            (operation, exception) => brokenFailures.Add(operation));

        MeetingPipelineResult resilientResult = await resilientPipeline.ProcessAudioFileAsync(audioPath);

        Check("la grabacion llego al vault pese a la base rota", File.Exists(resilientResult.SavedReportPath));
        Check("el transcript se produjo igual", resilientResult.Transcription.Transcript.Length > 0);
        Check("los fallos de base se registraron en vez de propagarse", brokenFailures.Count > 0);
        Console.WriteLine($"        operaciones que fallaron y se absorbieron: {string.Join(", ", brokenFailures.Distinct())}");
    }
    catch (Exception exception)
    {
        Check($"la grabacion llego al vault pese a la base rota (reviento: {exception.GetType().Name})", false);
    }

    Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
    return failures == 0 ? 0 : 1;
}

/// <summary>
/// Prueba a que sesion queda colgado un reporte re-extraido desde el historial.
///
/// Corre con dobles de prueba —sin Deepgram, sin LLM y sin microfono— sobre una
/// base temporal, asi que es gratis y deterministico. Eso importa: el defecto que
/// este test persigue es de <b>atribucion</b>, no de extraccion, y pagarle a dos
/// proveedores para comprobar a que fila apunta una foreign key seria absurdo.
///
/// El defecto, que la lectura de codigo si mostro pero solo mirandolo a proposito:
/// <c>MeetingPipeline.ExtractAndSaveAsync</c> deduce la sesion de
/// <c>_currentSessionId</c>, un campo de instancia de un singleton. Re-extraer una
/// reunion vieja por ese camino la habria colgado de la ultima grabacion —o
/// habria creado una sesion fantasma— y en los dos casos <b>sin sintoma</b>: base
/// consistente, .md en el vault, historial que miente.
/// </summary>
static async Task<int> VerifyReextractionAsync()
{
    string temporaryPath = Path.Combine(Path.GetTempPath(), $"ma-reextract-{Guid.NewGuid():N}.db");
    string temporaryVault = Path.Combine(Path.GetTempPath(), $"ma-reextract-vault-{Guid.NewGuid():N}");
    Console.WriteLine($"=== Autotest de re-extraccion ==={Environment.NewLine}{temporaryPath}");

    int failures = 0;
    void Check(string description, bool passed)
    {
        Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
        if (!passed) failures++;
    }

    try
    {
        Directory.CreateDirectory(temporaryVault);

        var factory = new SqliteConnectionFactory(temporaryPath);
        new SqliteSchemaMigrator(factory).Migrate();
        IMeetingHistoryStore history = new SqliteMeetingHistoryStore(factory);

        var capture = new FakeAudioCapture(temporaryVault);
        var historyFailures = new List<string>();
        IMeetingPipeline pipeline = new MeetingPipeline(
            capture,
            new FakeTranscriptionClient("Transcript de la grabacion nueva."),
            new FakeReportExtractor(),
            new FakeReportStorage(temporaryVault),
            temporaryVault,
            history,
            (operation, exception) => historyFailures.Add($"{operation}: {exception.Message}"));

        // --- Una reunion vieja, como la que estaria en el historial -----------
        long oldSessionId = await history.CreateSessionAsync(
            DateTimeOffset.UtcNow.AddDays(-7), SessionSource.Hotkey);
        await history.CompleteSessionAsync(
            oldSessionId, DateTimeOffset.UtcNow.AddDays(-7).AddMinutes(20),
            Path.Combine(temporaryVault, "vieja.wav"), TimeSpan.FromMinutes(20));
        await history.SaveTranscriptAsync(new TranscriptRecord(
            oldSessionId, "Transcript de la reunion de la semana pasada.",
            "Deepgram", "nova-3", null, DateTimeOffset.UtcNow.AddDays(-7)));

        // --- Y una grabacion de HOY, que es la que deja _currentSessionId ----
        // Esta es la condicion que hace visible el defecto. Sin grabar primero,
        // el bug se manifiesta de la otra forma (sesion fantasma), y se comprueba
        // mas abajo.
        await pipeline.StartRecordingAsync(SessionSource.Hotkey);
        MeetingPipelineResult todayResult = await pipeline.StopRecordingAndProcessAsync();

        IReadOnlyList<SessionSummary> afterRecording = await history.ListSessionsAsync(10);
        Check("la grabacion de hoy creo su propia sesion", afterRecording.Count == 2);
        Check("no hubo fallos de historial", historyFailures.Count == 0);
        if (historyFailures.Count > 0) historyFailures.ForEach(f => Console.WriteLine($"        {f}"));

        long todaySessionId = afterRecording
            .Where(session => session.SessionId != oldSessionId)
            .Select(session => session.SessionId)
            .Single();

        Check("el reporte de hoy quedo en la sesion de hoy",
            (await history.GetReportsAsync(todaySessionId)).Count == 1);
        Check("y el .md de hoy llego al vault", File.Exists(todayResult.SavedReportPath));

        // --- La re-extraccion de la reunion VIEJA, con la de hoy aun en curso -
        Console.WriteLine($"{Environment.NewLine}--- Re-extraer la reunion vieja, con una grabacion reciente hecha ---");

        TranscriptRecord? oldTranscript = await history.GetTranscriptAsync(oldSessionId);
        Check("la reunion vieja tiene transcript guardado", oldTranscript is not null);

        ExtractionSaveResult reextracted = await pipeline.ExtractForSessionAsync(
            oldSessionId, oldTranscript!.Text, "feature-handoff");

        // LA comprobacion. Con ExtractAndSaveAsync este reporte habria caido en
        // todaySessionId y nada habria fallado.
        IReadOnlyList<ReportRecord> oldReports = await history.GetReportsAsync(oldSessionId);
        Check("el reporte re-extraido quedo en la reunion VIEJA", oldReports.Count == 1);
        Check("y NO se colo en la grabacion de hoy",
            (await history.GetReportsAsync(todaySessionId)).Count == 1);
        Check("no se creo ninguna sesion fantasma",
            (await history.ListSessionsAsync(10)).Count == 2);
        Check("el reporte re-extraido guarda el prompt con el que se pidio",
            oldReports.Count == 1 && oldReports[0].PromptId == "feature-handoff");
        Check("el .md nuevo llego al vault", File.Exists(reextracted.SavedReportPath));
        Check("y no piso el .md del reporte de hoy",
            reextracted.SavedReportPath != todayResult.SavedReportPath &&
            File.Exists(todayResult.SavedReportPath));

        // --- La otra cara del defecto: sin grabacion previa -------------------
        // Un pipeline nuevo tiene _currentSessionId en null. Por el camino viejo
        // eso abria una sesion marcada como importacion; el camino nuevo no puede.
        Console.WriteLine($"{Environment.NewLine}--- Re-extraer sin ninguna grabacion previa ---");

        IMeetingPipeline freshPipeline = new MeetingPipeline(
            new FakeAudioCapture(temporaryVault),
            new FakeTranscriptionClient("no se usa"),
            new FakeReportExtractor(),
            new FakeReportStorage(temporaryVault),
            temporaryVault,
            history,
            (operation, exception) => historyFailures.Add($"{operation}: {exception.Message}"));

        await freshPipeline.ExtractForSessionAsync(oldSessionId, oldTranscript.Text, "functional-spec");

        Check("sigue sin crearse una sesion fantasma de importacion",
            (await history.ListSessionsAsync(10)).Count == 2);
        Check("la reunion vieja acumula sus dos reportes (una sesion admite varios)",
            (await history.GetReportsAsync(oldSessionId)).Count == 2);

        // --- El comportamiento viejo no cambio -------------------------------
        // ExtractAndSaveAsync es el flujo de dos pasos de la ventana y de
        // "Adjuntar transcripcion (.txt)". Este paso no debia tocarlo.
        Console.WriteLine($"{Environment.NewLine}--- ExtractAndSaveAsync sigue igual ---");

        await freshPipeline.ExtractAndSaveAsync("Un transcript pegado a mano.", "assignment-meeting");
        IReadOnlyList<SessionSummary> afterPaste = await history.ListSessionsAsync(10);
        Check("un transcript suelto sigue abriendo su sesion de importacion",
            afterPaste.Count == 3);

        long pastedSessionId = afterPaste
            .Where(s => s.SessionId != oldSessionId && s.SessionId != todaySessionId)
            .Select(s => s.SessionId)
            .Single();
        Check("y esa sesion queda marcada como importacion",
            (await history.GetSessionAsync(pastedSessionId))?.Source == SessionSource.Import);

        // --- Resiliencia: la regla que manda en toda la fase -----------------
        Console.WriteLine($"{Environment.NewLine}--- Base rota: la re-extraccion igual llega al vault ---");

        var brokenFailures = new List<string>();
        IMeetingPipeline brokenPipeline = new MeetingPipeline(
            new FakeAudioCapture(temporaryVault),
            new FakeTranscriptionClient("no se usa"),
            new FakeReportExtractor(),
            new FakeReportStorage(temporaryVault),
            temporaryVault,
            new SqliteMeetingHistoryStore(new SqliteConnectionFactory(@"\\?\Z:\no-existe\jamas\meetings.db")),
            (operation, _) => brokenFailures.Add(operation));

        ExtractionSaveResult degraded = await brokenPipeline.ExtractForSessionAsync(
            999, "Transcript cualquiera.", "assignment-meeting");

        Check("con la base rota, el .md igual llego al vault", File.Exists(degraded.SavedReportPath));
        Check("y el fallo se registro en vez de propagarse",
            brokenFailures.Contains("MeetingPipeline.SaveReport"));
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"El autotest reviento: {exception}");
        failures++;
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        foreach (string leftover in new[] { temporaryPath, temporaryPath + "-wal", temporaryPath + "-shm" })
        {
            try { if (File.Exists(leftover)) File.Delete(leftover); } catch { /* best effort */ }
        }

        try { if (Directory.Exists(temporaryVault)) Directory.Delete(temporaryVault, recursive: true); }
        catch { /* best effort */ }
    }

    Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
    return failures == 0 ? 0 : 1;
}

/// <summary>
/// Escribe (o borra, con el valor omitido) un ajuste en la base <b>real</b>, la
/// misma que usa la app instalada. El flag de secreto no se pasa por parametro:
/// lo decide <c>SettingKeyPolicy</c>, igual que en SettingsPage y en el
/// importador, para que no haya tres respuestas distintas a "esto se cifra".
///
/// Es el complemento de escritura de --verify-db, y existe por la misma razon:
/// poder tocar la base real sin depender de tener instalado un cliente de SQLite
/// — y, sobre todo, sin depender de que la app arranque. Un ajuste guardado mal
/// que impida el arranque se arregla desde aca o con una variable de entorno; la
/// UI no sirve si la app no abre.
/// </summary>
static int SetRealSetting(string key, string? value)
{
    string databasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingAssistant",
        "meetings.db");

    if (!File.Exists(databasePath))
    {
        Console.Error.WriteLine($"No existe la base: {databasePath}");
        return 1;
    }

    var factory = new SqliteConnectionFactory(databasePath);
    ISettingsStore store = new SqliteSettingsStore(factory, new DpapiSecretProtector());
    bool isSecret = SettingKeyPolicy.IsSecret(key);

    store.SetAsync(key, value, isSecret).GetAwaiter().GetResult();

    // Nunca se imprime el valor: esta salida termina en scrollback de terminal y
    // la clave puede ser una credencial.
    Console.WriteLine(value is null || value.Length == 0
        ? $"Borrado '{key}' (vuelve a mandar el valor empaquetado)."
        : $"Guardado '{key}' ({(isSecret ? "cifrado con DPAPI" : "en claro")}), {value.Length} caracter(es).");

    SqliteConnection.ClearAllPools();
    return 0;
}

/// <summary>
/// Prueba la configuracion en base (Fase 5, paso 5) sobre una base
/// <b>temporal</b>: la precedencia entre capas y la migracion de una sola vez del
/// appsettings.json de usuario que creo T9. Nunca toca la base real ni el
/// archivo real del usuario.
///
/// Lo que importa aca no es que el provider lea filas — eso es trivial — sino
/// dos cosas que se rompen sin hacer ruido:
///
/// - <b>Que las variables de entorno sigan mandando sobre la base.</b> Es la via
///   de escape que queda cuando un ajuste guardado mal impide arrancar. Si la
///   base pisara al entorno, un valor malo en la base seria irreparable desde
///   afuera de la app, y el sintoma seria una app que no abre y no se puede
///   arreglar.
/// - <b>Que un import a medias no destruya el archivo del usuario.</b> El
///   importador borra el original; si lo borrara sin haber verificado la
///   relectura, un fallo de DPAPI se llevaria la configuracion entera.
/// </summary>
static int VerifySettingsConfiguration()
{
    string temporaryPath = Path.Combine(Path.GetTempPath(), $"ma-config-{Guid.NewGuid():N}.db");
    string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ma-config-{Guid.NewGuid():N}");
    Console.WriteLine($"=== Autotest de configuracion en base ==={Environment.NewLine}{temporaryPath}");

    int failures = 0;
    void Check(string description, bool passed)
    {
        Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
        if (!passed) failures++;
    }

    // Nombre de variable de entorno con la forma Seccion__Clave. Se limpia en el
    // finally: dejarla puesta contaminaria cualquier corrida posterior del
    // harness en la misma sesion de shell.
    const string environmentVariableName = "Storage__SubFolder";

    try
    {
        Directory.CreateDirectory(temporaryDirectory);

        var factory = new SqliteConnectionFactory(temporaryPath);
        new SqliteSchemaMigrator(factory).Migrate();
        ISettingsStore store = new SqliteSettingsStore(factory, new DpapiSecretProtector());

        // La capa "empaquetada", en memoria: es el appsettings.json de fabrica.
        var packaged = new Dictionary<string, string?>
        {
            ["Storage:VaultPath"] = @"C:\empaquetado\vault",
            ["Storage:SubFolder"] = "EmpaquetadoSubFolder",
            ["Llm:Provider"] = "Gemini",
            ["Deepgram:ApiKey"] = "clave-empaquetada"
        };

        IConfigurationRoot Build(ISettingsStore settingsStore, Action<string, Exception>? onFailure = null) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(packaged)
                .AddSqliteSettings(settingsStore, onFailure)
                .AddEnvironmentVariables()
                .Build();

        Console.WriteLine($"{Environment.NewLine}--- Precedencia entre capas ---");

        Check("sin fila en la base, manda el valor empaquetado",
            Build(store)["Storage:VaultPath"] == @"C:\empaquetado\vault");

        store.SetAsync("Storage:VaultPath", @"D:\base\vault").GetAwaiter().GetResult();
        Check("una fila de la base pisa al empaquetado",
            Build(store)["Storage:VaultPath"] == @"D:\base\vault");

        Check("una clave que la base no tiene sigue cayendo al empaquetado",
            Build(store)["Llm:Provider"] == "Gemini");

        // El corazon del paso: el entorno queda ARRIBA de la base.
        store.SetAsync("Storage:SubFolder", "SubFolderDeLaBase").GetAwaiter().GetResult();
        Environment.SetEnvironmentVariable(environmentVariableName, "SubFolderDelEntorno");
        Check("una variable de entorno Seccion__Clave pisa a la base",
            Build(store)["Storage:SubFolder"] == "SubFolderDelEntorno");
        Environment.SetEnvironmentVariable(environmentVariableName, null);
        Check("sin la variable de entorno, vuelve a mandar la base",
            Build(store)["Storage:SubFolder"] == "SubFolderDeLaBase");

        Console.WriteLine($"{Environment.NewLine}--- Secretos a traves de IConfiguration ---");

        store.SetAsync("Deepgram:ApiKey", "clave-secreta-de-prueba", isSecret: true).GetAwaiter().GetResult();
        Check("un secreto se lee descifrado a traves de IConfiguration",
            Build(store)["Deepgram:ApiKey"] == "clave-secreta-de-prueba");

        using (SqliteConnection connection = factory.Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "select value from setting where key = 'Deepgram:ApiKey';";
            string onDisk = command.ExecuteScalar()?.ToString() ?? string.Empty;
            Check("el secreto esta cifrado en disco, no en claro",
                onDisk.Length > 0 && !onDisk.Contains("clave-secreta-de-prueba", StringComparison.Ordinal));
        }

        // Un secreto que no se puede descifrar es el caso real de "base copiada de
        // otro perfil". No puede lanzar en el arranque: tiene que desaparecer de
        // la capa y dejar ver la de abajo, para que el validador lo reporte como
        // faltante con un mensaje que se entiende.
        using (SqliteConnection connection = factory.Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "update setting set value = 'bm8tZXMtdW4tYmxvYi1kZS1EUEFQSQ==' where key = 'Deepgram:ApiKey';";
            command.ExecuteNonQuery();
        }

        bool undecryptableSurvived = true;
        string? fellThrough = null;
        try { fellThrough = Build(store)["Deepgram:ApiKey"]; }
        catch (Exception) { undecryptableSurvived = false; }

        Check("un secreto indescifrable no lanza al construir la configuracion", undecryptableSurvived);
        Check("y deja ver la capa de abajo en vez de un valor invalido",
            fellThrough == "clave-empaquetada");

        Console.WriteLine($"{Environment.NewLine}--- Base rota: la app tiene que seguir arrancando ---");

        var brokenFailures = new List<string>();
        IConfigurationRoot degraded = null!;
        bool builtAnyway = true;
        try
        {
            ISettingsStore brokenStore = new SqliteSettingsStore(
                new SqliteConnectionFactory(@"\\?\Z:\no-existe\jamas\meetings.db"),
                new DpapiSecretProtector());
            degraded = Build(brokenStore, (operation, _) => brokenFailures.Add(operation));
        }
        catch (Exception)
        {
            builtAnyway = false;
        }

        Check("una base en una ruta imposible no impide construir la configuracion", builtAnyway);
        Check("con la base rota, el empaquetado sigue visible",
            builtAnyway && degraded["Storage:VaultPath"] == @"C:\empaquetado\vault");
        Check("el fallo de base se reporto por el callback en vez de propagarse",
            brokenFailures.Contains("SqliteConfigurationProvider.Load"));

        Console.WriteLine($"{Environment.NewLine}--- Recarga ---");

        IConfigurationRoot reloadable = Build(store);
        store.SetAsync("Storage:VaultPath", @"E:\vault\despues").GetAwaiter().GetResult();
        Check("un valor escrito despues no aparece hasta recargar",
            reloadable["Storage:VaultPath"] == @"D:\base\vault");
        reloadable.Reload();
        Check("Reload() lo ve", reloadable["Storage:VaultPath"] == @"E:\vault\despues");

        // --- Migracion de una sola vez del archivo de T9 --------------------
        Console.WriteLine($"{Environment.NewLine}--- Import del appsettings.json de usuario (T9) ---");

        string userFilePath = Path.Combine(temporaryDirectory, "appsettings.json");
        // Anidamiento de tres niveles y una propiedad con ':' en el nombre a
        // proposito: es la forma que tiene la seccion Pricing del example real, y
        // es justo donde un recorrido del JSON escrito a mano se desviaria de lo
        // que produce AddJsonFile.
        File.WriteAllText(userFilePath,
            """
            {
              "Storage": { "VaultPath": "C:\\usuario\\vault", "SubFolder": "ReportesDeUsuario" },
              "Deepgram": { "ApiKey": "deepgram-de-usuario" },
              "Gemini": { "ApiKey": "gemini-de-usuario", "Model": "gemini-de-prueba" },
              "AzureFoundry": { "Endpoint": "https://ejemplo/openai/v1/", "ApiKey": "" },
              "Api": { "Port": 5757, "AuthToken": "token-de-usuario" },
              "Pricing": {
                "Gemini:gemini-de-prueba": { "InputPerMillion": 0.30, "OutputPerMillion": 2.50 }
              },
              "Hotkey": { "Modifiers": "Control+Alt", "Key": "F9" },
              "Marcador": { "ApiKey": "<pon-tu-clave-aca>" }
            }
            """);

        // Base limpia para el import: la de arriba ya tiene filas de las pruebas
        // de precedencia y ensuciaria los conteos.
        string importDatabasePath = Path.Combine(temporaryDirectory, "import.db");
        var importFactory = new SqliteConnectionFactory(importDatabasePath);
        new SqliteSchemaMigrator(importFactory).Migrate();
        ISettingsStore importStore = new SqliteSettingsStore(importFactory, new DpapiSecretProtector());

        UserSettingsImportResult result =
            new UserSettingsImporter(importStore).ImportOnceAsync(userFilePath).GetAwaiter().GetResult();
        Console.WriteLine($"        {result.Describe()}");

        Check("el import se declara hecho", result.Outcome == UserSettingsImportOutcome.Imported);
        Check("importo la clave anidada de tres niveles con ':' en el nombre",
            importStore.GetAsync("Pricing:Gemini:gemini-de-prueba:InputPerMillion").GetAwaiter().GetResult() == "0.30");
        Check("importo un valor numerico como texto",
            importStore.GetAsync("Api:Port").GetAwaiter().GetResult() == "5757");
        Check("importo las claves que la UI no sabe editar (Hotkey)",
            importStore.GetAsync("Hotkey:Key").GetAwaiter().GetResult() == "F9");
        Check("un valor no secreto vuelve tal cual",
            importStore.GetAsync("Storage:VaultPath").GetAwaiter().GetResult() == @"C:\usuario\vault");
        Check("un secreto vuelve descifrado",
            importStore.GetAsync("Deepgram:ApiKey").GetAwaiter().GetResult() == "deepgram-de-usuario");
        Check("omitio el marcador de posicion en vez de importarlo",
            importStore.GetAsync("Marcador:ApiKey").GetAwaiter().GetResult() is null &&
            result.SkippedKeys.Contains("Marcador:ApiKey"));
        Check("omitio la clave vacia (AzureFoundry:ApiKey)",
            importStore.GetAsync("AzureFoundry:ApiKey").GetAwaiter().GetResult() is null);

        // Lo que hace que el paso valga: ninguna credencial legible, ni en la base
        // ni en el archivo que queda. Api:AuthToken cuenta como credencial porque
        // enciende el microfono remotamente.
        using (SqliteConnection connection = importFactory.Open())
        {
            string RawValue(string key)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "select value from setting where key = $key;";
                command.Parameters.AddWithValue("$key", key);
                return command.ExecuteScalar()?.ToString() ?? string.Empty;
            }

            Check("la API key de Deepgram quedo cifrada en la base",
                !RawValue("Deepgram:ApiKey").Contains("deepgram-de-usuario", StringComparison.Ordinal));
            Check("la API key de Gemini quedo cifrada en la base",
                !RawValue("Gemini:ApiKey").Contains("gemini-de-usuario", StringComparison.Ordinal));
            Check("el token del endpoint local tambien se trato como secreto",
                RawValue("Api:AuthToken").Length > 0 &&
                !RawValue("Api:AuthToken").Contains("token-de-usuario", StringComparison.Ordinal));
            Check("un valor no secreto NO se cifro (sigue legible a ojo en la base)",
                RawValue("Storage:SubFolder") == "ReportesDeUsuario");
        }

        Check("el archivo original ya no esta en la ruta que lee IConfiguration",
            !File.Exists(userFilePath));

        string redactedPath = UserSettingsImporter.RedactedCopyPathFor(userFilePath);
        Check("quedo la copia redactada al lado", File.Exists(redactedPath));

        string redacted = File.Exists(redactedPath) ? File.ReadAllText(redactedPath) : string.Empty;
        Check("la copia redactada no contiene ninguna credencial en claro",
            !redacted.Contains("deepgram-de-usuario", StringComparison.Ordinal) &&
            !redacted.Contains("gemini-de-usuario", StringComparison.Ordinal) &&
            !redacted.Contains("token-de-usuario", StringComparison.Ordinal));
        Check("la copia redactada conserva los valores no secretos (para poder mirar que habia)",
            redacted.Contains("ReportesDeUsuario", StringComparison.Ordinal) &&
            redacted.Contains("Control+Alt", StringComparison.Ordinal));

        Check("quedo la marca de migrado en la base",
            importStore.GetAsync(UserSettingsImporter.MarkerKey).GetAwaiter().GetResult() is not null);

        // Idempotencia. Se vuelve a crear el archivo con OTRO valor: si el segundo
        // import lo leyera, el valor cambiaria — y eso significaria que cada
        // arranque puede volver a pisar lo que el usuario edito en la UI.
        File.WriteAllText(userFilePath, """{ "Storage": { "VaultPath": "C:\\no-deberia-importarse" } }""");
        UserSettingsImportResult second =
            new UserSettingsImporter(importStore).ImportOnceAsync(userFilePath).GetAwaiter().GetResult();

        Check("el segundo import no hace nada",
            second.Outcome == UserSettingsImportOutcome.AlreadyImported);
        Check("y no piso el valor que ya estaba",
            importStore.GetAsync("Storage:VaultPath").GetAwaiter().GetResult() == @"C:\usuario\vault");
        Check("el segundo import dejo el archivo intacto, no lo borro",
            File.Exists(userFilePath));

        // La comprobacion que justifica verificar la relectura antes de mover el
        // archivo: con el almacen roto, el import falla y el archivo del usuario
        // tiene que seguir ahi, porque mientras exista su capa sigue alimentando
        // a la app y no se perdio nada.
        Console.WriteLine($"{Environment.NewLine}--- Import contra una base rota ---");

        string survivorPath = Path.Combine(temporaryDirectory, "sobreviviente.json");
        File.WriteAllText(survivorPath, """{ "Storage": { "VaultPath": "C:\\intacto" } }""");
        ISettingsStore brokenImportStore = new SqliteSettingsStore(
            new SqliteConnectionFactory(@"\\?\Z:\no-existe\jamas\meetings.db"),
            new DpapiSecretProtector());
        UserSettingsImportResult failed =
            new UserSettingsImporter(brokenImportStore).ImportOnceAsync(survivorPath).GetAwaiter().GetResult();

        Check("un import contra una base rota se reporta como fallido, no lanza",
            failed.Outcome == UserSettingsImportOutcome.Failed);
        Check("y el archivo del usuario queda INTACTO", File.Exists(survivorPath));

        Check("una base limpia sin archivo de usuario no tiene nada que importar",
            new UserSettingsImporter(store)
                .ImportOnceAsync(Path.Combine(temporaryDirectory, "no-existe.json"))
                .GetAwaiter().GetResult().Outcome == UserSettingsImportOutcome.NothingToImport);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"El autotest reviento: {exception}");
        failures++;
    }
    finally
    {
        Environment.SetEnvironmentVariable(environmentVariableName, null);
        SqliteConnection.ClearAllPools();
        foreach (string leftover in new[] { temporaryPath, temporaryPath + "-wal", temporaryPath + "-shm" })
        {
            try { if (File.Exists(leftover)) File.Delete(leftover); } catch { /* best effort */ }
        }

        try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true); }
        catch { /* best effort */ }
    }

    Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
    return failures == 0 ? 0 : 1;
}

/// <summary>
/// Prueba funcional del esquema sobre una base <b>temporal</b>, que se borra al
/// terminar. Nunca toca la base real.
///
/// Que las tablas existan no prueba que el esquema funcione. Lo que se ejercita
/// acá es justo lo que se rompe en silencio: los triggers que mantienen el
/// índice FTS5, el borrado en cascada de las foreign keys, y el tokenizador con
/// <c>remove_diacritics</c> — que importa porque el corpus es español mezclado
/// con inglés y nadie escribe los acentos al buscar.
/// </summary>
static int VerifyDatabaseSelfTest()
{
    string temporaryPath = Path.Combine(Path.GetTempPath(), $"ma-selftest-{Guid.NewGuid():N}.db");
    Console.WriteLine($"=== Autotest de esquema ==={Environment.NewLine}{temporaryPath}");

    int failures = 0;
    void Check(string description, bool passed)
    {
        Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
        if (!passed) failures++;
    }

    try
    {
        var factory = new SqliteConnectionFactory(temporaryPath);
        string migrationResult = new SqliteSchemaMigrator(factory).Migrate();
        Console.WriteLine($"Migracion: {migrationResult}{Environment.NewLine}");

        using Microsoft.Data.Sqlite.SqliteConnection connection = factory.Open();

        long Scalar(string sql)
        {
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
        }

        void Execute(string sql)
        {
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        Execute(
            """
            insert into session(id, started_at_utc, source) values (1, '2026-08-27T09:00:00Z', 'hotkey');
            insert into transcript(session_id, text, provider, model, cost_micro_usd, created_at_utc)
                values (1, 'Hablamos de la migracion del stored procedure y la validacion de sesión.',
                        'Deepgram', 'nova-3', 434, '2026-08-27T09:05:00Z');
            insert into report(session_id, prompt_id, prompt_version, markdown, cost_micro_usd, created_at_utc)
                values (1, 'assignment-meeting', 'v1', '# Reporte', 762, '2026-08-27T09:06:00Z');
            insert into report(session_id, prompt_id, prompt_version, markdown, cost_micro_usd, created_at_utc)
                values (1, 'feature-handoff', 'v1', '# Handoff', 900, '2026-08-27T09:07:00Z');
            """);

        Check("una sesion admite varios reportes", Scalar("select count(*) from report where session_id = 1;") == 2);
        Check("el trigger de insert alimento el indice FTS5",
            Scalar("select count(*) from transcript_fts where transcript_fts match 'stored';") == 1);
        Check("busqueda sin acento encuentra texto acentuado ('sesion' -> 'sesión')",
            Scalar("select count(*) from transcript_fts where transcript_fts match 'sesion';") == 1);
        Check("busqueda con acento tambien encuentra ('sesión')",
            Scalar("select count(*) from transcript_fts where transcript_fts match 'sesión';") == 1);
        Check("una palabra ausente no da falsos positivos",
            Scalar("select count(*) from transcript_fts where transcript_fts match 'zanahoria';") == 0);

        Execute("update transcript set text = 'Ahora hablamos de facturacion electronica.' where session_id = 1;");
        Check("el trigger de update reindexo (lo viejo ya no aparece)",
            Scalar("select count(*) from transcript_fts where transcript_fts match 'stored';") == 0);
        Check("el trigger de update reindexo (lo nuevo si aparece)",
            Scalar("select count(*) from transcript_fts where transcript_fts match 'facturacion';") == 1);

        Check("el costo entero sobrevive el viaje sin deriva",
            Scalar("select sum(cost_micro_usd) from report;") == 1662L);

        Execute("delete from session where id = 1;");
        Check("borrar la sesion arrastra el transcript (cascade)", Scalar("select count(*) from transcript;") == 0);
        Check("borrar la sesion arrastra los reportes (cascade)", Scalar("select count(*) from report;") == 0);
        Check("el trigger de delete limpio el indice FTS5",
            Scalar("select count(*) from transcript_fts where transcript_fts match 'facturacion';") == 0);

        using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "insert into transcript_fts(transcript_fts) values('integrity-check');";
            try
            {
                command.ExecuteNonQuery();
                Check("el indice FTS5 queda consistente al final", true);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                Check("el indice FTS5 queda consistente al final", false);
            }
        }

        // --- Ahora lo mismo, pero a traves de las interfaces de Core ---------
        // El SQL de arriba prueba el esquema; esto prueba los adaptadores, que
        // es donde viven las conversiones (dinero a micro-dolares, fechas a UTC)
        // y el saneado de la consulta FTS5.
        Console.WriteLine($"{Environment.NewLine}--- Store, via IMeetingHistoryStore ---");

        IMeetingHistoryStore store = new SqliteMeetingHistoryStore(factory);
        var startedAt = new DateTimeOffset(2026, 8, 27, 14, 30, 0, TimeSpan.FromHours(-7));

        long sessionId = store.CreateSessionAsync(startedAt, SessionSource.Hotkey).GetAwaiter().GetResult();
        store.CompleteSessionAsync(sessionId, startedAt.AddMinutes(12), @"C:\audio\x.wav", TimeSpan.FromMinutes(12))
            .GetAwaiter().GetResult();
        store.SaveTranscriptAsync(new TranscriptRecord(
            sessionId, "Revisamos la integracion con el módulo de facturación.",
            "Deepgram", "nova-3", 0.001234m, startedAt.AddMinutes(13))).GetAwaiter().GetResult();
        store.SaveReportAsync(new NewReport(
            sessionId, "assignment-meeting", "v1", "# Reporte", null,
            "Azure AI Foundry", "DeepSeek-V4-Flash", 890, 520, 0.000434m,
            @"C:\vault\r.md", startedAt.AddMinutes(14))).GetAwaiter().GetResult();

        SessionRecord? roundTripped = store.GetSessionAsync(sessionId).GetAwaiter().GetResult();
        Check("la sesion vuelve con su id", roundTripped?.Id == sessionId);
        // El instante tiene que ser el mismo aunque se haya guardado en UTC y el
        // original viniera con offset -07:00. Es la garantia que hace que
        // ordenar por fecha no dependa del huso de quien grabo.
        Check("el instante sobrevive la ida y vuelta a UTC", roundTripped?.StartedAtUtc == startedAt);
        Check("la duracion sobrevive", roundTripped?.Duration == TimeSpan.FromMinutes(12));

        TranscriptRecord? storedTranscript = store.GetTranscriptAsync(sessionId).GetAwaiter().GetResult();
        Check("el costo decimal del transcript vuelve exacto", storedTranscript?.CostUsd == 0.001234m);

        IReadOnlyList<ReportRecord> storedReports = store.GetReportsAsync(sessionId).GetAwaiter().GetResult();
        Check("el costo decimal del reporte vuelve exacto", storedReports.Count == 1 && storedReports[0].CostUsd == 0.000434m);
        Check("los tokens vuelven", storedReports.Count == 1 && storedReports[0].InputTokens == 890);

        IReadOnlyList<SessionSummary> listed = store.ListSessionsAsync(10).GetAwaiter().GetResult();
        Check("el listado trae la sesion con su conteo de reportes",
            listed.Count == 1 && listed[0].ReportCount == 1);
        Check("el costo de la sesion suma transcripcion y reporte",
            listed.Count == 1 && listed[0].TotalCostUsd == 0.001668m);

        IReadOnlyList<TranscriptSearchHit> hits =
            store.SearchTranscriptsAsync("facturacion").GetAwaiter().GetResult();
        Check("la busqueda por interfaz encuentra sin acentos", hits.Count == 1);
        Check("el resultado trae un fragmento, no solo la fecha",
            hits.Count == 1 && hits[0].Snippet.Length > 0);

        // Lo que mas importa de todo el saneado: escribiendo en una caja de
        // busqueda uno pasa por estados invalidos de sintaxis FTS5, y ninguno
        // puede llegar al usuario como un error de SQL.
        bool survivedGarbage = true;
        foreach (string hostile in new[] { "\"", "AND", "NEAR(", "*", "a OR", "(((", "'" })
        {
            try { store.SearchTranscriptsAsync(hostile).GetAwaiter().GetResult(); }
            catch (Exception) { survivedGarbage = false; }
        }
        Check("una consulta a medias o con operadores sueltos no lanza", survivedGarbage);

        CostSummary costs = store.GetCostSummaryAsync().GetAwaiter().GetResult();
        Check("el resumen de costo acumulado cuadra", costs.TotalCostUsd == 0.001668m && costs.ReportCount == 1);

        IReadOnlyList<PromptUsageSummary> usage = store.GetPromptUsageAsync().GetAwaiter().GetResult();
        Check("el uso por prompt agrupa por id y version",
            usage.Count == 1 && usage[0].PromptId == "assignment-meeting" && usage[0].ReportCount == 1);

        // --- Ajustes y cifrado ---------------------------------------------
        Console.WriteLine($"{Environment.NewLine}--- Ajustes, via ISettingsStore ---");

        ISettingsStore settings = new SqliteSettingsStore(factory, new DpapiSecretProtector());
        settings.SetAsync("Storage:VaultPath", @"C:\vault").GetAwaiter().GetResult();
        settings.SetAsync("Deepgram:ApiKey", "clave-secreta-de-prueba", isSecret: true).GetAwaiter().GetResult();

        Check("un ajuste normal vuelve tal cual",
            settings.GetAsync("Storage:VaultPath").GetAwaiter().GetResult() == @"C:\vault");
        Check("un secreto vuelve descifrado",
            settings.GetAsync("Deepgram:ApiKey").GetAwaiter().GetResult() == "clave-secreta-de-prueba");

        // La comprobacion que hace que valga la pena: en disco NO puede estar en
        // claro. Sin esto, mover las claves a la base no habria mejorado nada.
        using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "select value from setting where key = 'Deepgram:ApiKey';";
            string onDisk = command.ExecuteScalar()?.ToString() ?? string.Empty;
            Check("el secreto esta cifrado en disco, no en claro",
                onDisk.Length > 0 && !onDisk.Contains("clave-secreta-de-prueba", StringComparison.Ordinal));
        }

        settings.SetAsync("Deepgram:ApiKey", "   ").GetAwaiter().GetResult();
        Check("guardar vacio borra el override (se vuelve al valor empaquetado)",
            settings.GetAsync("Deepgram:ApiKey").GetAwaiter().GetResult() is null);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"El autotest reviento: {exception}");
        failures++;
    }
    finally
    {
        // El pool mantiene el archivo abierto; sin esto el borrado falla en
        // Windows y quedan bases de prueba tiradas en %TEMP%.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string leftover in new[] { temporaryPath, temporaryPath + "-wal", temporaryPath + "-shm" })
        {
            try { if (File.Exists(leftover)) File.Delete(leftover); } catch { /* best effort */ }
        }
    }

    Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
    return failures == 0 ? 0 : 1;
}

/// <summary>
/// Inspecciona la base local de reuniones: versión de esquema, objetos creados y
/// filas por tabla. Existe para poder comprobar contra la máquina lo que una
/// migración dice haber hecho, sin depender de tener instalado un cliente de
/// SQLite. Mismo criterio que --verify-render.
///
/// Apunta a la misma ruta que usa la app instalada
/// (%LOCALAPPDATA%\MeetingAssistant\meetings.db), así que lee la base de verdad,
/// no una copia.
/// </summary>
static int VerifyDatabase()
{
    string databasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingAssistant",
        "meetings.db");

    Console.WriteLine($"=== Base de reuniones ==={Environment.NewLine}{databasePath}");
    if (!File.Exists(databasePath))
    {
        Console.Error.WriteLine("No existe todavia. La crea el primer arranque de la app.");
        return 1;
    }

    Console.WriteLine($"Tamano: {new FileInfo(databasePath).Length:N0} bytes");

    var factory = new SqliteConnectionFactory(databasePath);
    using Microsoft.Data.Sqlite.SqliteConnection connection = factory.Open();

    using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
    {
        command.CommandText = "pragma user_version;";
        Console.WriteLine($"Version de esquema: v{command.ExecuteScalar()} (el codigo espera v{SqliteSchemaMigrator.LatestVersion})");
    }

    using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
    {
        command.CommandText =
            "select type, name from sqlite_master where name not like 'sqlite_%' order by type, name;";
        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
        Console.WriteLine($"{Environment.NewLine}=== Objetos ===");
        while (reader.Read()) Console.WriteLine($"  {reader.GetString(0),-7} {reader.GetString(1)}");
    }

    Console.WriteLine($"{Environment.NewLine}=== Filas ===");
    foreach (string table in new[] { "session", "transcript", "report", "setting" })
    {
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"select count(*) from {table};";
        Console.WriteLine($"  {table,-12} {command.ExecuteScalar()}");
    }

    // Las ultimas sesiones, con su origen. Es lo primero que uno quiere ver al
    // mirar la base de verdad, y lo que dice si la columna source se esta
    // rellenando distinto segun el camino o siempre igual.
    using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
    {
        command.CommandText =
            """
            select s.id, s.started_at_utc, s.source, s.duration_seconds,
                   (select count(*) from transcript t where t.session_id = s.id),
                   (select count(*) from report r where r.session_id = s.id)
              from session s order by s.started_at_utc desc limit 10;
            """;
        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
        Console.WriteLine($"{Environment.NewLine}=== Ultimas sesiones ===");
        Console.WriteLine($"  {"id",-4} {"inicio (UTC)",-26} {"origen",-9} {"dur(s)",-8} {"trans",-6} reportes");
        while (reader.Read())
        {
            Console.WriteLine(
                $"  {reader.GetInt64(0),-4} {reader.GetString(1),-26} {reader.GetString(2),-9} " +
                $"{(reader.IsDBNull(3) ? "-" : reader.GetDouble(3).ToString("F1")),-8} " +
                $"{reader.GetInt32(4),-6} {reader.GetInt32(5)}");
        }
    }

    // Los ajustes, con los secretos enmascarados. Lo que hay que poder ver de un
    // secreto no es su valor sino tres cosas: que la fila existe, que esta
    // marcada como secreta, y que **se puede descifrar en este perfil** — DPAPI
    // ata el valor al usuario y la maquina, asi que una base copiada de otro
    // lado deja las credenciales ilegibles y eso tiene que verse aca, no
    // descubrirse cuando falle una transcripcion.
    using (SqliteCommand command = connection.CreateCommand())
    {
        command.CommandText = "select key, value, is_secret, updated_at_utc from setting order by key;";
        using SqliteDataReader reader = command.ExecuteReader();
        var protector = new DpapiSecretProtector();

        Console.WriteLine($"{Environment.NewLine}=== Ajustes ===");
        Console.WriteLine($"  {"clave",-46} {"tipo",-9} valor");
        while (reader.Read())
        {
            bool isSecret = reader.GetInt64(2) != 0;
            string? stored = reader.IsDBNull(1) ? null : reader.GetString(1);
            string shown = isSecret
                ? stored is null
                    ? "(nulo)"
                    : protector.TryUnprotect(stored) is { } clear
                        ? $"cifrado, descifrable ({clear.Length} chars)"
                        : "cifrado, NO DESCIFRABLE en este perfil"
                : stored ?? "(nulo)";

            Console.WriteLine($"  {reader.GetString(0),-46} {(isSecret ? "secreto" : "en claro"),-9} {shown}");
        }
    }

    // La integridad referencial y el indice FTS son justo lo que se rompe en
    // silencio: comprobarlos a mano es la unica forma de saber que los triggers
    // y las foreign keys quedaron como se creian.
    using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
    {
        command.CommandText = "pragma foreign_key_check;";
        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
        Console.WriteLine($"{Environment.NewLine}Integridad referencial: {(reader.Read() ? "FALLOS" : "sin violaciones")}");
    }

    using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
    {
        command.CommandText = "insert into transcript_fts(transcript_fts) values('integrity-check');";
        try
        {
            command.ExecuteNonQuery();
            Console.WriteLine("Indice FTS5: consistente");
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            Console.WriteLine($"Indice FTS5: INCONSISTENTE — {exception.Message}");
            return 1;
        }
    }

    return 0;
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

// ---------------------------------------------------------------------------
// Dobles de prueba para --verify-reextraction
//
// Existen para que el test de atribucion de sesion sea gratis y deterministico:
// lo que se comprueba es a que fila apunta una foreign key, y pagarle a Deepgram
// y al LLM para averiguarlo seria absurdo. Ademas el microfono puede estar
// bloqueado por el consentimiento de Windows, que ya bloqueo una verificacion
// del paso 4.
//
// Se declaran al final del archivo porque un programa de sentencias de nivel
// superior admite tipos despues de las sentencias, y sacarlos a archivos aparte
// los volveria parte de la superficie del harness sin necesidad.
// ---------------------------------------------------------------------------

/// <summary>Captura de audio que no toca el microfono: crea un .wav vacio.</summary>
internal sealed class FakeAudioCapture : IAudioCaptureService
{
    private readonly string _directory;

    public FakeAudioCapture(string directory) => _directory = directory;

    public bool IsCapturing { get; private set; }

    public Task StartAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        IsCapturing = true;
        return Task.CompletedTask;
    }

    public Task<AudioCaptureResult> StopAsync(CancellationToken cancellationToken = default)
    {
        IsCapturing = false;
        string path = Path.Combine(_directory, $"fake-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, []);
        return Task.FromResult(new AudioCaptureResult(path, TimeSpan.FromMinutes(3), "loopback falso", "mic falso"));
    }
}

internal sealed class FakeTranscriptionClient : ITranscriptionClient
{
    private readonly string _transcript;

    public FakeTranscriptionClient(string transcript) => _transcript = transcript;

    public Task<TranscriptionResult> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TranscriptionResult(
            _transcript, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(2), "es", []));
}

/// <summary>
/// Extractor que devuelve Markdown fijo. Respeta el <c>promptId</c> que recibe
/// —y por eso el test puede comprobar que el reporte guardado trae el prompt con
/// el que se pidio, no el por defecto.
/// </summary>
internal sealed class FakeReportExtractor : ILlmReportExtractor
{
    public Task<ExtractionResult> ExtractAsync(
        string transcript,
        string? promptId = null,
        CancellationToken cancellationToken = default)
    {
        string id = promptId ?? ReportExtractionPrompt.Id;
        var prompt = new PromptDefinition(
            id, $"Prompt {id}", "Doble de prueba", "vtest", "system prompt falso",
            PromptOutputKind.FunctionalSpecification);

        var metadata = new MeetingReportMetadata(
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            LlmProvider: "Proveedor falso",
            LlmModel: "modelo-falso",
            PromptVersion: prompt.Version,
            InputTokens: 100,
            OutputTokens: 200,
            EstimatedCostUsd: 0.000123m,
            PromptId: id);

        return Task.FromResult(new ExtractionResult(
            $"# Reporte falso ({id})\n\n{transcript}", null, metadata, prompt));
    }
}

/// <summary>
/// Storage que escribe en un directorio temporal con el mismo esquema de nombres
/// que <c>MarkdownReportStorage</c> (<c>{prompt}-{yyyyMMdd-HHmmss}</c> mas un
/// sufijo unico). El sufijo esta porque el test genera varios reportes dentro del
/// mismo segundo y hace falta poder comprobar que uno no pisa al otro.
/// </summary>
internal sealed class FakeReportStorage : IReportStorage
{
    private readonly string _directory;

    public FakeReportStorage(string directory) => _directory = directory;

    public Task<string> SaveAsync(MeetingReport report, CancellationToken cancellationToken = default) =>
        SaveMarkdownAsync("# sin usar", report.Metadata, cancellationToken);

    public Task<string> SaveMarkdownAsync(
        string markdown,
        MeetingReportMetadata? metadata,
        CancellationToken cancellationToken = default)
    {
        string prefix = metadata?.PromptId ?? "meeting-report";
        string path = Path.Combine(
            _directory,
            $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, markdown);
        return Task.FromResult(path);
    }
}
