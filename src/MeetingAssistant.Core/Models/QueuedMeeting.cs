namespace MeetingAssistant.Core.Models;

/// <summary>Dónde está una reunión en su camino al reporte.</summary>
public enum QueuedMeetingStatus
{
    /// <summary>Grabada; espera su turno o la red para subir.</summary>
    Waiting,

    /// <summary>Subiendo el audio a Storage, bloque a bloque.</summary>
    Uploading,

    /// <summary>Subida; el servidor transcribe y escribe el reporte.</summary>
    Processing,

    /// <summary>El reporte está listo en el servidor; se escribe el .md en el vault.</summary>
    WritingReport,

    /// <summary>El .md está en el vault.</summary>
    Ready,

    /// <summary>Se detuvo por algo que reintentar solo no arregla; ver <see cref="QueuedMeeting.Error"/>.</summary>
    Failed
}

public enum QueuedMeetingKind
{
    /// <summary>Un audio que se sube y el servidor transcribe.</summary>
    Transcribe,

    /// <summary>Una reunión ya transcrita que el servidor vuelve a extraer con otro prompt (Jobs opción 2).</summary>
    Reextract
}

/// <summary>
/// Una reunión en la cola de subidas y dónde está. Se guarda después de cada paso, así un cierre de
/// la app, un reinicio o una caída de red siguen donde quedaron (decisión del dueño, 2026-11-09).
/// </summary>
public sealed record QueuedMeeting
{
    /// <summary>También el nombre de su archivo en la cola: "20261110-213000-a1b2".</summary>
    public required string Id { get; init; }

    public QueuedMeetingKind Kind { get; init; } = QueuedMeetingKind.Transcribe;

    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Qué la disparó en esta máquina (hotkey, bandeja, ventana, HTTP, importación). Sólo para mostrar: a la API va "desktop".</summary>
    public string Trigger { get; init; } = string.Empty;

    /// <summary>El archivo que se sube (el convertido a 16 kHz, o el original si no se pudo convertir).</summary>
    public string? AudioPath { get; init; }

    /// <summary>La grabación tal cual salió de la captura. Se conserva, como antes del paso B.</summary>
    public string? OriginalAudioPath { get; init; }

    public double? DurationSeconds { get; init; }

    public long TotalBytes { get; init; }

    public QueuedMeetingStatus Status { get; init; } = QueuedMeetingStatus.Waiting;

    public long? IdJob { get; init; }

    /// <summary>El enlace de sólo escritura mientras dura la subida. Una credencial: nunca se muestra ni se loguea.</summary>
    public string? UploadUrl { get; init; }

    public DateTimeOffset? UploadExpiresAt { get; init; }

    /// <summary>El tamaño de bloque con que empezó esta subida (cambiarlo a mitad rompería la lista de bloques).</summary>
    public int BlockBytes { get; init; }

    /// <summary>Los bloques que ya llegaron a Storage para el trabajo actual.</summary>
    public IReadOnlyList<int> SentBlocks { get; init; } = [];

    public long? IdSession { get; init; }

    /// <summary>El prompt de una re-extracción; null en una transcripción (el servidor usa el de por defecto).</summary>
    public string? PromptId { get; init; }

    /// <summary>El reporte que se escribió (o se va a escribir) en el vault.</summary>
    public long? IdReport { get; init; }

    /// <summary>Dónde quedó el .md en el vault.</summary>
    public string? ReportPath { get; init; }

    /// <summary>Cuándo llegó el .md al vault. De ahí cuentan los días para borrar el WAV original (decisión del dueño, 2026-11-11).</summary>
    public DateTimeOffset? ReadyAtUtc { get; init; }

    /// <summary>Por qué falló, en palabras para el usuario (con el motivo del servidor si lo dio).</summary>
    public string? Error { get; init; }

    /// <summary>Por qué espera ahora (sin red, el 429 con sus números...). Se borra al avanzar.</summary>
    public string? Notice { get; init; }

    /// <summary>
    /// Reintentos seguidos sin avanzar, para espaciar el siguiente. La hora del próximo intento vive
    /// sólo en memoria: al arrancar la app todo lo pendiente se intenta en seguida.
    /// </summary>
    public int RetryCount { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public long SentBytes => BlockBytes <= 0
        ? 0
        : SentBlocks.Sum(index => Math.Min((long)BlockBytes, TotalBytes - (long)index * BlockBytes));

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFinished => Status is QueuedMeetingStatus.Ready or QueuedMeetingStatus.Failed;

    /// <summary>Nunca el enlace.</summary>
    public override string ToString() =>
        $"QueuedMeeting(id={Id}, kind={Kind}, status={Status}, idJob={IdJob}, idSession={IdSession}, sentBlocks={SentBlocks.Count})";
}

/// <summary>Lo que va en el frontmatter del .md: los datos del reporte que dio el servidor.</summary>
public sealed record ReportFileMetadata(
    DateTimeOffset GeneratedAtUtc,
    string? PromptId,
    string? PromptVersion,
    string? LlmProvider,
    string? LlmModel,
    int? InputTokens,
    int? OutputTokens,
    decimal? CostUsd,
    long? IdSession,
    long? IdReport)
{
    public static ReportFileMetadata From(ApiReport report) => new(
        report.CreatedAt,
        report.PromptId,
        report.PromptVersion,
        report.LlmProvider,
        report.LlmModel,
        report.TokensInput,
        report.TokensOutput,
        report.CostMicroUsd is { } micro ? micro / 1_000_000m : null,
        report.IdSession,
        report.IdReport);
}
