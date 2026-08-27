using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Registro persistente de reuniones: sesiones, transcripts y reportes.
///
/// Es el <b>lado de lectura que hoy no existe</b>. <see cref="IReportStorage"/>
/// sólo sabe guardar, y por eso `HistoryPage` nunca tuvo de dónde leer: ese, y no
/// el XAML, era el trabajo real detrás de la página.
///
/// No reemplaza a <see cref="IReportStorage"/>, lo acompaña. El <c>.md</c> del
/// vault sigue escribiéndose igual porque el vault es donde el usuario realmente
/// lee sus reportes; esto es el sistema de registro y el índice.
///
/// La implementación concreta (SQLite, o lo que sea) vive en Infrastructure.
/// </summary>
public interface IMeetingHistoryStore
{
    // -------------------------------------------------------------- escritura

    /// <summary>
    /// Abre una sesión al empezar a grabar y devuelve su id. Se crea al
    /// principio, y no al final con todo resuelto, para que una grabación que
    /// falla a mitad <b>igual deje rastro</b>: hoy, si el pipeline revienta, no
    /// queda constancia de que la reunión existió.
    /// </summary>
    Task<long> CreateSessionAsync(
        DateTimeOffset startedAtUtc,
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>Cierra la sesión con su audio y duración reales.</summary>
    Task CompleteSessionAsync(
        long sessionId,
        DateTimeOffset endedAtUtc,
        string? audioPath,
        TimeSpan? duration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Guarda o reemplaza el transcript de una sesión. Reemplazar tiene sentido
    /// al re-transcribir el mismo audio; los triggers de FTS5 mantienen el
    /// índice al día solos.
    /// </summary>
    Task SaveTranscriptAsync(TranscriptRecord transcript, CancellationToken cancellationToken = default);

    /// <summary>
    /// Guarda un reporte y devuelve su id. Una sesión admite <b>varios</b>: el
    /// catálogo ya permite re-correr el mismo transcript con otro prompt, y hoy
    /// esa comparación se pierde.
    /// </summary>
    Task<long> SaveReportAsync(NewReport report, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------- lectura

    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        int limit,
        int offset = 0,
        CancellationToken cancellationToken = default);

    Task<SessionRecord?> GetSessionAsync(long sessionId, CancellationToken cancellationToken = default);

    Task<TranscriptRecord?> GetTranscriptAsync(long sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReportRecord>> GetReportsAsync(long sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Búsqueda full-text sobre los transcripts. La consulta es sintaxis de
    /// FTS5, así que la implementación tiene que <b>sanearla</b>: un apóstrofo
    /// suelto o un operador a medias lanza, y eso no puede llegarle al usuario
    /// como un error de SQL mientras escribe.
    /// </summary>
    Task<IReadOnlyList<TranscriptSearchHit>> SearchTranscriptsAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default);

    // -------------------------------------------------------------- analítica

    Task<CostSummary> GetCostSummaryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PromptUsageSummary>> GetPromptUsageAsync(CancellationToken cancellationToken = default);
}
