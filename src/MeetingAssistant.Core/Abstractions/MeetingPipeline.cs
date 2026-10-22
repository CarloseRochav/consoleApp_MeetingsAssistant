using System.Text.Json;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Orquesta el pipeline completo: captura → transcripción → extracción → guardado.
/// Esto reemplaza el wiring manual que vivía en Harness/Program.cs — Program.cs
/// pasa a ser solo composición de dependencias (DI) + una llamada a RunAsync.
/// </summary>
public interface IMeetingPipeline
{
    bool IsRecording { get; }

    /// <summary>
    /// <paramref name="source"/> es obligatorio y sin valor por defecto a
    /// propósito: es un valor de <see cref="SessionSource"/> que sólo conoce
    /// quien llama (hotkey, bandeja, HTTP, ventana…). Con un default, cada
    /// llamador nuevo heredaría en silencio una etiqueta equivocada y la columna
    /// dejaría de servir para distinguirlos, que es su única razón de existir.
    /// </summary>
    Task StartRecordingAsync(string source, CancellationToken cancellationToken = default);
    Task<MeetingPipelineResult> StopRecordingAndProcessAsync(CancellationToken cancellationToken = default);
    Task<MeetingPipelineResult> ProcessAudioFileAsync(string audioPath, CancellationToken cancellationToken = default);
    Task<TranscriptionSession> StopRecordingAndTranscribeAsync(CancellationToken cancellationToken = default);
    Task<TranscriptionSession> TranscribeAudioFileAsync(string audioPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra un transcript que llegó sin audio ("Adjuntar transcripción
    /// (.txt)") como una sesión de importación nueva, y devuelve su id. Es el
    /// equivalente de <see cref="TranscribeAudioFileAsync"/> para un texto: la
    /// sesión nace al importar, no al extraer, así que reintentar una extracción
    /// fallida no abre una segunda sesión.
    ///
    /// Devuelve null si no hay historial o si la base falló: lo que sigue
    /// funciona igual, sólo que sin fila.
    /// </summary>
    Task<long?> ImportTranscriptAsync(string transcript, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transcript suelto, sin sesión conocida: lo importa como sesión nueva
    /// (<see cref="ImportTranscriptAsync"/>) y extrae. <b>Siempre</b> abre una
    /// sesión nueva; para colgar el reporte de una sesión que ya existe está
    /// <see cref="ExtractForSessionAsync"/>.
    ///
    /// Hasta 2026-10-14 este método deducía la sesión de un campo con la
    /// última grabación, y el campo nunca se limpiaba: con la app viva días en
    /// la bandeja, cada transcript adjuntado después de una grabación quedaba
    /// colgado de esa reunión y su texto no se guardaba.
    /// </summary>
    Task<ExtractionSaveResult> ExtractAndSaveAsync(
        string transcript,
        string promptId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extrae un reporte para una sesión <b>ya existente</b>, identificada
    /// explícitamente: la del flujo de dos pasos de la ventana (el id viene en
    /// <see cref="TranscriptionSession.SessionId"/>), la de un transcript ya
    /// importado, o una vieja del historial que se re-genera con otro prompt.
    /// </summary>
    Task<ExtractionSaveResult> ExtractForSessionAsync(
        long sessionId,
        string transcript,
        string promptId,
        CancellationToken cancellationToken = default);
}

public sealed record MeetingPipelineResult(
    MeetingReport? Report,
    string SavedReportPath,
    AudioCaptureResult Audio,
    TranscriptionResult Transcription,
    string ReportMarkdown,
    PromptDefinition Prompt,
    long? SessionId = null);

/// <param name="SessionId">
/// La fila del historial a la que pertenece esta transcripción, o null si no
/// hay historial o la base falló. Viaja con el resultado, y no en un campo del
/// pipeline, para que el reporte que se pida minutos después —tras elegir el
/// prompt— caiga en esta reunión aunque entre medio haya empezado otra.
/// </param>
public sealed record TranscriptionSession(
    AudioCaptureResult Audio,
    TranscriptionResult Transcription,
    long? SessionId = null);

public sealed record ExtractionSaveResult(
    string SavedReportPath,
    string ReportMarkdown,
    MeetingReport? StructuredReport,
    PromptDefinition Prompt,
    MeetingReportMetadata Metadata,
    long? SessionId = null);

public sealed class MeetingPipeline : IMeetingPipeline
{
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly ITranscriptionClient _transcriptionClient;
    private readonly ILlmReportExtractor _reportExtractor;
    private readonly IReportStorage _reportStorage;
    private readonly string _audioOutputDirectory;
    private readonly IMeetingHistoryStore? _historyStore;
    private readonly Action<string, Exception>? _onHistoryFailure;
    private readonly ITranscriptionCostEstimator? _transcriptionCostEstimator;
    private readonly IArchiveQueue? _archiveQueue;

    /// <summary>
    /// Con qué nombre queda registrado el proveedor de transcripción, y bajo qué
    /// nombre se busca su precio. Una sola constante para que no puedan
    /// discrepar: si lo hicieran, el costo quedaría en null sin que nada falle.
    /// </summary>
    public const string TranscriptionProviderName = "Deepgram";

    /// <summary>
    /// Id de la sesión de la grabación en curso, 0 si no hay. Es el único estado
    /// de sesión que guarda el pipeline, y sólo vive mientras el micrófono está
    /// abierto: <see cref="StopRecordingAndTranscribeAsync"/> lo toma y lo
    /// limpia de una vez, y a partir de ahí el id viaja en los resultados
    /// (<see cref="TranscriptionSession.SessionId"/>). Ninguna extracción lo lee.
    ///
    /// Tiene la misma vida que la captura de audio, que ya impide dos
    /// grabaciones a la vez. Que un start y un stop no se crucen lo garantiza
    /// <c>RecordingCoordinator</c>, por el que pasan todos los disparadores,
    /// HTTP incluido.
    /// </summary>
    private long _recordingSessionId;

    public MeetingPipeline(
        IAudioCaptureService audioCaptureService,
        ITranscriptionClient transcriptionClient,
        ILlmReportExtractor reportExtractor,
        IReportStorage reportStorage,
        string audioOutputDirectory,
        IMeetingHistoryStore? historyStore = null,
        Action<string, Exception>? onHistoryFailure = null,
        ITranscriptionCostEstimator? transcriptionCostEstimator = null,
        IArchiveQueue? archiveQueue = null)
    {
        _audioCaptureService = audioCaptureService;
        _transcriptionClient = transcriptionClient;
        _reportExtractor = reportExtractor;
        _reportStorage = reportStorage;
        _audioOutputDirectory = audioOutputDirectory;
        // Opcional a propósito: el harness corre el pipeline de verdad y no debe
        // ensuciar el historial del usuario con corridas de prueba.
        _historyStore = historyStore;
        // Delegado en vez de una llamada directa a App.LogStartupFailure porque
        // Core no puede referenciar App. Lo inyecta quien compone.
        _onHistoryFailure = onHistoryFailure;
        // Opcional: sin estimador el costo de transcripción queda en null, que
        // es lo mismo que pasa cuando el estimador no tiene precio configurado.
        _transcriptionCostEstimator = transcriptionCostEstimator;
        // Opcional: sin cola, nada se sube a la nube y el resto sigue igual.
        _archiveQueue = archiveQueue;
    }

    public bool IsRecording => _audioCaptureService.IsCapturing;

    public async Task StartRecordingAsync(string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        // El audio primero: si la captura no arranca, no hay reunión que
        // registrar. Al revés quedarían sesiones fantasma cada vez que falle el
        // micrófono.
        await _audioCaptureService.StartAsync(_audioOutputDirectory, cancellationToken);

        Interlocked.Exchange(ref _recordingSessionId, 0);
        long? sessionId = await RecordHistoryAsync(
            "CreateSession",
            store => store.CreateSessionAsync(DateTimeOffset.UtcNow, source, cancellationToken));
        Interlocked.Exchange(ref _recordingSessionId, sessionId ?? 0);
    }

    public async Task<MeetingPipelineResult> StopRecordingAndProcessAsync(CancellationToken cancellationToken = default)
    {
        TranscriptionSession session = await StopRecordingAndTranscribeAsync(cancellationToken);
        return await ExtractSessionAsync(session, promptId: null, cancellationToken);
    }

    public async Task<MeetingPipelineResult> ProcessAudioFileAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        TranscriptionSession session = await TranscribeAudioFileAsync(audioPath, cancellationToken);
        return await ExtractSessionAsync(session, promptId: null, cancellationToken);
    }

    public async Task<TranscriptionSession> StopRecordingAndTranscribeAsync(CancellationToken cancellationToken = default)
    {
        AudioCaptureResult audio = await _audioCaptureService.StopAsync(cancellationToken);

        // Se toma y se limpia en un solo paso: desde acá el id viaja en los
        // resultados, y la próxima grabación empieza sin rastro de ésta.
        long taken = Interlocked.Exchange(ref _recordingSessionId, 0);
        long? sessionId = taken == 0 ? null : taken;

        // Se cierra la sesión acá, ANTES de transcribir, y no al final con la
        // duración exacta que devuelve el proveedor. El motivo es concreto: si
        // la transcripción falla —credencial vencida, red caída, audio en
        // silencio—, lo que hay que no perder es **dónde quedó el .wav**, porque
        // con eso la reunión se puede re-transcribir más tarde. Una duración de
        // captura un poco menos precisa es un precio barato por eso.
        if (sessionId is { } id)
        {
            await RecordHistoryAsync(
                "CompleteSession",
                store => store.CompleteSessionAsync(id, DateTimeOffset.UtcNow, audio.AudioPath, audio.Duration, cancellationToken));
        }

        return await TranscribeThenArchiveAsync(audio, sessionId, cancellationToken);
    }

    private async Task<TranscriptionSession> TranscribeThenArchiveAsync(
        AudioCaptureResult audio,
        long? sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await TranscribeAudioAsync(audio, sessionId, cancellationToken);
        }
        finally
        {
            // Sin sesión no hay fila contra la cual registrar la subida: pasa
            // sólo si la base falló al abrir la sesión.
            if (sessionId is { } id)
            {
                _archiveQueue?.Enqueue(id, ArchiveKind.Audio, audio.AudioPath);
            }
        }
    }

    public async Task<TranscriptionSession> TranscribeAudioFileAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        if (!File.Exists(audioPath)) throw new FileNotFoundException("No existe el archivo de audio indicado.", audioPath);
        if (IsRecording) throw new InvalidOperationException("No se puede procesar un archivo mientras hay una grabación en curso.");

        var audio = new AudioCaptureResult(audioPath, TimeSpan.Zero, "Archivo importado", "Archivo importado");

        // Un audio importado no pasó por StartRecordingAsync, así que abre su
        // propia sesión. Sin esto, procesar un archivo existente no dejaría
        // ningún rastro en el historial.
        long? sessionId = await RecordHistoryAsync(
            "CreateSession(import)",
            async store =>
            {
                long id = await store.CreateSessionAsync(DateTimeOffset.UtcNow, SessionSource.Import, cancellationToken);
                await store.CompleteSessionAsync(id, DateTimeOffset.UtcNow, audioPath, null, cancellationToken);
                return id;
            });

        return await TranscribeThenArchiveAsync(audio, sessionId, cancellationToken);
    }

    public async Task<long?> ImportTranscriptAsync(string transcript, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);

        return await RecordHistoryAsync(
            "CreateSession(transcript suelto)",
            async store =>
            {
                long id = await store.CreateSessionAsync(DateTimeOffset.UtcNow, SessionSource.Import, cancellationToken);
                await store.SaveTranscriptAsync(
                    new TranscriptRecord(id, transcript, null, null, null, DateTimeOffset.UtcNow),
                    cancellationToken);
                return id;
            });
    }

    public async Task<ExtractionSaveResult> ExtractAndSaveAsync(
        string transcript,
        string promptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);

        long? sessionId = await ImportTranscriptAsync(transcript, cancellationToken);
        return await ExtractAndRecordAsync(sessionId, transcript, promptId, cancellationToken);
    }

    public async Task<ExtractionSaveResult> ExtractForSessionAsync(
        long sessionId,
        string transcript,
        string promptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);

        return await ExtractAndRecordAsync(sessionId, transcript, promptId, cancellationToken);
    }

    private async Task<ExtractionSaveResult> ExtractAndRecordAsync(
        long? sessionId,
        string transcript,
        string promptId,
        CancellationToken cancellationToken)
    {
        ExtractionResult extraction = await _reportExtractor.ExtractAsync(transcript, promptId, cancellationToken);
        string savedPath = await _reportStorage.SaveMarkdownAsync(
            extraction.MarkdownBody, extraction.Metadata, cancellationToken);

        await RecordReportAsync(sessionId, extraction, savedPath, cancellationToken);

        return new ExtractionSaveResult(
            savedPath,
            extraction.MarkdownBody,
            extraction.StructuredReport,
            extraction.Prompt,
            extraction.Metadata,
            sessionId);
    }

    private async Task<TranscriptionSession> TranscribeAudioAsync(
        AudioCaptureResult audio,
        long? sessionId,
        CancellationToken cancellationToken)
    {
        TranscriptionResult transcription = await _transcriptionClient.TranscribeAsync(
            audio.AudioPath, cancellationToken);

        // Falla rápido y con contexto claro, en vez de dejar que
        // LlmReportExtractor lance el ArgumentException genérico de "transcript
        // vacío" sin decir de dónde vino el audio que la causó.
        if (string.IsNullOrWhiteSpace(transcription.Transcript))
        {
            throw new InvalidOperationException(
                $"La transcripción del audio en '{audio.AudioPath}' vino vacía — no se detectó habla.");
        }

        // El transcript se guarda ACÁ, antes de extraer. Es lo único de toda la
        // cadena que no se puede regenerar sin volver a pagarle a Deepgram, así
        // que no puede depender de que la extracción con el LLM salga bien.
        //
        // CostUsd sale de los minutos de audio por el precio configurado. Si no
        // hay precio queda en null: preferible a un cero que parezca medido.
        if (sessionId is { } id)
        {
            await RecordHistoryAsync(
                "SaveTranscript",
                store => store.SaveTranscriptAsync(
                    new TranscriptRecord(
                        id,
                        transcription.Transcript,
                        Provider: TranscriptionProviderName,
                        Model: transcription.Model,
                        CostUsd: _transcriptionCostEstimator?.EstimateTranscriptionCostUsd(
                            TranscriptionProviderName, transcription.AudioDuration),
                        CreatedAtUtc: DateTimeOffset.UtcNow),
                    cancellationToken));
        }

        return new TranscriptionSession(audio with { Duration = transcription.AudioDuration }, transcription, sessionId);
    }

    private async Task RecordReportAsync(
        long? sessionId,
        ExtractionResult extraction,
        string savedVaultPath,
        CancellationToken cancellationToken)
    {
        if (sessionId is null) return;

        await RecordHistoryAsync(
            "SaveReport",
            store => store.SaveReportAsync(
                new NewReport(
                    SessionId: sessionId.Value,
                    PromptId: extraction.Prompt.Id,
                    PromptVersion: extraction.Prompt.Version,
                    Markdown: extraction.MarkdownBody,
                    // Sólo assignment-meeting devuelve un MeetingReport
                    // estructurado; el resto del catálogo da Markdown suelto.
                    // Se serializa con las mismas opciones que usa el parser
                    // para que lo guardado tenga la forma que produce el LLM.
                    StructuredJson: extraction.StructuredReport is null
                        ? null
                        : JsonSerializer.Serialize(extraction.StructuredReport, MeetingReportParser.SerializerOptions),
                    LlmProvider: extraction.Metadata.LlmProvider,
                    LlmModel: extraction.Metadata.LlmModel,
                    InputTokens: extraction.Metadata.InputTokens,
                    OutputTokens: extraction.Metadata.OutputTokens,
                    CostUsd: extraction.Metadata.EstimatedCostUsd,
                    // El .md del vault sigue siendo el producto; esto sólo
                    // apunta a dónde quedó.
                    VaultPath: savedVaultPath,
                    CreatedAtUtc: extraction.Metadata.GeneratedAtUtc),
                cancellationToken));

        // El .md también va a la nube. Se encola aunque la fila del reporte no
        // se haya podido guardar: el archivo existe en el vault igual.
        _archiveQueue?.Enqueue(sessionId.Value, ArchiveKind.Report, savedVaultPath);
    }

    /// <summary>
    /// Corre una operación de historial sin dejar que un fallo de la base
    /// tumbe la reunión: el error va a <c>_onHistoryFailure</c> y sigue todo.
    /// </summary>
    private async Task RecordHistoryAsync(string operation, Func<IMeetingHistoryStore, Task> action)
    {
        await RecordHistoryAsync<bool>(operation, async store =>
        {
            await action(store);
            return true;
        });
    }

    /// <returns>Lo que devolvió la operación, o null si no hay historial o falló.</returns>
    private async Task<T?> RecordHistoryAsync<T>(string operation, Func<IMeetingHistoryStore, Task<T>> action)
        where T : struct
    {
        if (_historyStore is null) return null;

        try
        {
            return await action(_historyStore);
        }
        catch (Exception exception)
        {
            _onHistoryFailure?.Invoke($"MeetingPipeline.{operation}", exception);
            return null;
        }
    }

    private async Task<MeetingPipelineResult> ExtractSessionAsync(
        TranscriptionSession session,
        string? promptId,
        CancellationToken cancellationToken)
    {
        ExtractionResult extraction = await _reportExtractor.ExtractAsync(
            session.Transcription.Transcript, promptId, cancellationToken);
        string savedPath = await _reportStorage.SaveMarkdownAsync(
            extraction.MarkdownBody, extraction.Metadata, cancellationToken);

        // Este es el camino de una sola pasada — hotkey, bandeja, HTTP e
        // importación —, distinto del de dos pasos de la ventana que pasa por
        // ExtractForSessionAsync. Los dos tienen que registrar el reporte: olvidar
        // éste dejaba sin fila justo a los caminos que más se usan.
        await RecordReportAsync(session.SessionId, extraction, savedPath, cancellationToken);

        return new MeetingPipelineResult(
            extraction.StructuredReport,
            savedPath,
            session.Audio,
            session.Transcription,
            extraction.MarkdownBody,
            extraction.Prompt,
            session.SessionId);
    }
}
