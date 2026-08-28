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
    Task<ExtractionSaveResult> ExtractAndSaveAsync(
        string transcript,
        string promptId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extrae un reporte para una sesión <b>ya existente</b>, identificada
    /// explícitamente. Es lo que permite re-generar un reporte desde un
    /// transcript viejo del historial con otro prompt.
    ///
    /// Existe como método aparte, y no como un parámetro opcional de
    /// <see cref="ExtractAndSaveAsync"/>, porque la diferencia no es cosmética:
    /// <c>ExtractAndSaveAsync</c> deduce la sesión del estado mutable de la
    /// última grabación, y para el historial <b>eso está mal de dos formas
    /// distintas y silenciosas</b> — si hubo una grabación en esta sesión de la
    /// app, el reporte queda colgado de la reunión equivocada; si no hubo,
    /// se abre una sesión fantasma marcada como importación. En los dos casos la
    /// base queda consistente, el <c>.md</c> llega al vault, y el historial
    /// miente.
    ///
    /// Este método <b>no lee ni escribe</b> la sesión en curso. Es lo que lo
    /// hace seguro frente a una grabación simultánea por HTTP, que sí puede
    /// pasar: el endpoint llama al pipeline directo, sin pasar por
    /// <c>RecordingCoordinator</c>.
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
    PromptDefinition Prompt);

public sealed record TranscriptionSession(
    AudioCaptureResult Audio,
    TranscriptionResult Transcription);

public sealed record ExtractionSaveResult(
    string SavedReportPath,
    string ReportMarkdown,
    MeetingReport? StructuredReport,
    PromptDefinition Prompt,
    MeetingReportMetadata Metadata);

public sealed class MeetingPipeline : IMeetingPipeline
{
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly ITranscriptionClient _transcriptionClient;
    private readonly ILlmReportExtractor _reportExtractor;
    private readonly IReportStorage _reportStorage;
    private readonly string _audioOutputDirectory;
    private readonly IMeetingHistoryStore? _historyStore;
    private readonly Action<string, Exception>? _onHistoryFailure;

    /// <summary>
    /// Id de la sesión en curso. Es estado mutable, igual que ya lo es la
    /// captura de audio (<see cref="IsRecording"/> consulta al servicio), y por
    /// la misma razón: el flujo de dos pasos de la ventana transcribe primero y
    /// extrae después, con minutos de por medio mientras se elige el prompt, así
    /// que algo tiene que recordar a qué reunión pertenece ese reporte.
    ///
    /// Vale para un solo flujo a la vez, que es lo que hay:
    /// <c>RecordingCoordinator</c> serializa las operaciones y la app es de
    /// instancia única desde T4.1.
    /// </summary>
    private long? _currentSessionId;

    public MeetingPipeline(
        IAudioCaptureService audioCaptureService,
        ITranscriptionClient transcriptionClient,
        ILlmReportExtractor reportExtractor,
        IReportStorage reportStorage,
        string audioOutputDirectory,
        IMeetingHistoryStore? historyStore = null,
        Action<string, Exception>? onHistoryFailure = null)
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
    }

    public bool IsRecording => _audioCaptureService.IsCapturing;

    public async Task StartRecordingAsync(string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        // El audio primero: si la captura no arranca, no hay reunión que
        // registrar. Al revés quedarían sesiones fantasma cada vez que falle el
        // micrófono.
        await _audioCaptureService.StartAsync(_audioOutputDirectory, cancellationToken);

        _currentSessionId = null;
        await RecordHistoryAsync(
            "CreateSession",
            async store => _currentSessionId =
                await store.CreateSessionAsync(DateTimeOffset.UtcNow, source, cancellationToken));
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

        // Se cierra la sesión acá, ANTES de transcribir, y no al final con la
        // duración exacta que devuelve el proveedor. El motivo es concreto: si
        // la transcripción falla —credencial vencida, red caída, audio en
        // silencio—, lo que hay que no perder es **dónde quedó el .wav**, porque
        // con eso la reunión se puede re-transcribir más tarde. Una duración de
        // captura un poco menos precisa es un precio barato por eso.
        await RecordHistoryAsync(
            "CompleteSession",
            store => store.CompleteSessionAsync(
                _currentSessionId!.Value, DateTimeOffset.UtcNow, audio.AudioPath, audio.Duration, cancellationToken),
            requiresSession: true);

        return await TranscribeAudioAsync(audio, cancellationToken);
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
        _currentSessionId = null;
        await RecordHistoryAsync(
            "CreateSession(import)",
            async store =>
            {
                _currentSessionId = await store.CreateSessionAsync(
                    DateTimeOffset.UtcNow, SessionSource.Import, cancellationToken);
                await store.CompleteSessionAsync(
                    _currentSessionId.Value, DateTimeOffset.UtcNow, audioPath, null, cancellationToken);
            });

        return await TranscribeAudioAsync(audio, cancellationToken);
    }

    public async Task<ExtractionSaveResult> ExtractAndSaveAsync(
        string transcript,
        string promptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);

        // Un transcript pegado a mano ("Adjuntar transcripción (.txt)") nunca
        // pasó por el pipeline, así que no hay sesión. Se abre una para que el
        // reporte igual quede registrado.
        if (_currentSessionId is null)
        {
            await RecordHistoryAsync(
                "CreateSession(transcript suelto)",
                async store =>
                {
                    _currentSessionId = await store.CreateSessionAsync(
                        DateTimeOffset.UtcNow, SessionSource.Import, cancellationToken);
                    await store.SaveTranscriptAsync(
                        new TranscriptRecord(_currentSessionId.Value, transcript, null, null, null, DateTimeOffset.UtcNow),
                        cancellationToken);
                });
        }

        return await ExtractAndRecordAsync(_currentSessionId, transcript, promptId, cancellationToken);
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

        // Ni una línea que toque _currentSessionId. Ver el comentario de la
        // interfaz: es justo lo que hace correcto a este camino.
        return await ExtractAndRecordAsync(sessionId, transcript, promptId, cancellationToken);
    }

    /// <summary>
    /// El tramo común de las dos extracciones: extraer, escribir el
    /// <c>.md</c> en el vault y registrar el reporte contra la sesión que le
    /// corresponda. La sesión entra <b>por parámetro</b>, y ese es todo el
    /// punto: quien llama declara a qué reunión pertenece el reporte, en vez de
    /// heredarlo de un campo.
    /// </summary>
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
            extraction.Metadata);
    }

    private async Task<TranscriptionSession> TranscribeAudioAsync(
        AudioCaptureResult audio,
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
        // CostUsd va en null porque hoy nadie calcula el costo de transcripción:
        // ICostEstimator sólo cubre el LLM. Preferible null a un cero que
        // parezca medido.
        await RecordHistoryAsync(
            "SaveTranscript",
            store => store.SaveTranscriptAsync(
                new TranscriptRecord(
                    _currentSessionId!.Value,
                    transcription.Transcript,
                    Provider: "Deepgram",
                    Model: null,
                    CostUsd: null,
                    CreatedAtUtc: DateTimeOffset.UtcNow),
                cancellationToken),
            requiresSession: true);

        return new TranscriptionSession(audio with { Duration = transcription.AudioDuration }, transcription);
    }

    /// <summary>
    /// <paramref name="sessionId"/> nulo significa que la sesión nunca llegó a
    /// crearse porque la base falló antes. Se sale sin hacer nada: el registro
    /// de esa reunión ya estaba perdido y encadenar un segundo error sólo
    /// ensucia el log. Es el mismo criterio que <c>requiresSession</c> en
    /// <see cref="RecordHistoryAsync"/>, pero decidido acá porque ahora la
    /// sesión es un parámetro y no un campo.
    /// </summary>
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
        // Sin requiresSession a propósito: ese flag consulta _currentSessionId, y
        // en la re-extracción desde el historial ese campo puede estar en null
        // con un sessionId perfectamente válido en el parámetro. Dejarlo habría
        // hecho que el reporte NO se registrara, en silencio y sólo por el camino
        // nuevo. El guard equivalente es el `if (sessionId is null)` de arriba.
    }

    /// <summary>
    /// Ejecuta una escritura de historial de forma que <b>nunca</b> pueda
    /// tumbar una grabación.
    ///
    /// Es la regla que manda en este paso, y viene de un precedente caro: en
    /// T4.4 una excepción no atrapada se llevó puesta la app entera y costó
    /// nueve días encontrarla. Si la base falla, la reunión tiene que seguir
    /// hasta el vault igual — se pierde el historial de esa reunión, no la
    /// reunión.
    ///
    /// <paramref name="requiresSession"/> marca las operaciones que necesitan
    /// una sesión abierta. Si la creación de la sesión falló antes, se saltan en
    /// silencio en vez de reventar con un null: el registro de esa reunión ya
    /// estaba perdido, y encadenar un segundo error sólo ensucia el log.
    /// </summary>
    private async Task RecordHistoryAsync(
        string operation,
        Func<IMeetingHistoryStore, Task> action,
        bool requiresSession = false)
    {
        if (_historyStore is null) return;
        if (requiresSession && _currentSessionId is null) return;

        try
        {
            await action(_historyStore);
        }
        catch (Exception exception)
        {
            _onHistoryFailure?.Invoke($"MeetingPipeline.{operation}", exception);
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
        // ExtractAndSaveAsync. Los dos tienen que registrar el reporte: olvidar
        // éste dejaba sin fila justo a los caminos que más se usan.
        await RecordReportAsync(_currentSessionId, extraction, savedPath, cancellationToken);

        return new MeetingPipelineResult(
            extraction.StructuredReport,
            savedPath,
            session.Audio,
            session.Transcription,
            extraction.MarkdownBody,
            extraction.Prompt);
    }
}
