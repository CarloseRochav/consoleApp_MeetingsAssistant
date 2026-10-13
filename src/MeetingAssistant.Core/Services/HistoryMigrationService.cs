using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Services;

/// <summary>Lo que hizo, o haría, una migración.</summary>
public sealed record HistoryMigrationResult(
    int SourceSessions,
    int SessionsCopied,
    int SessionsAlreadyThere,
    int TranscriptsCopied,
    int ReportsCopied,
    IReadOnlyList<string> FilesToArchive,
    IReadOnlyList<string> FilesMissing,
    IReadOnlyList<string> Problems);

/// <summary>
/// Copia el historial de un store a otro: sesiones, transcripts y reportes. Es
/// la migración de una sola vez de SQLite a Postgres (Fase 6, paso 4), pero no
/// sabe de ninguno de los dos: sólo compone <see cref="IMeetingHistoryStore"/>.
///
/// Tres propiedades que importan:
/// <list type="bullet">
/// <item><b>No toca el origen.</b> Sólo lo lee.</item>
/// <item><b>Se puede volver a correr.</b> Una reunión se reconoce en el destino
/// por su instante de inicio, y un reporte por su prompt y su instante de
/// creación. Lo que ya está no se duplica; lo que quedó a medias de una corrida
/// interrumpida se completa.</item>
/// <item><b>El destino puede no estar vacío.</b> Si ya se grabó algo ahí, se
/// conserva.</item>
/// </list>
///
/// Los ids <b>cambian</b>: los asigna el destino. Se copia de la más vieja a la
/// más nueva para que queden en orden cronológico.
/// </summary>
public sealed class HistoryMigrationService
{
    private const int PageSize = 200;

    private readonly IMeetingHistoryStore _source;
    private readonly IMeetingHistoryStore _target;
    private readonly IArchiveQueue? _archiveQueue;

    /// <param name="archiveQueue">
    /// Si se pasa, el audio y los <c>.md</c> de cada reunión se mandan a
    /// archivar contra la sesión <b>del destino</b>. Encolar es idempotente.
    /// </param>
    public HistoryMigrationService(
        IMeetingHistoryStore source,
        IMeetingHistoryStore target,
        IArchiveQueue? archiveQueue = null)
    {
        _source = source;
        _target = target;
        _archiveQueue = archiveQueue;
    }

    /// <param name="apply">
    /// <c>false</c> sólo cuenta lo que haría: no escribe ni encola nada.
    /// </param>
    public async Task<HistoryMigrationResult> RunAsync(bool apply, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SessionSummary> sourceSessions = await ListAllAsync(_source, cancellationToken);
        Dictionary<DateTimeOffset, long> targetByStart = (await ListAllAsync(_target, cancellationToken))
            .GroupBy(session => session.StartedAtUtc)
            .ToDictionary(group => group.Key, group => group.First().SessionId);

        int sessionsCopied = 0, sessionsAlreadyThere = 0, transcriptsCopied = 0, reportsCopied = 0;
        var filesToArchive = new List<string>();
        var filesMissing = new List<string>();
        var problems = new List<string>();

        // De la más vieja a la más nueva: ListSessionsAsync las da al revés.
        foreach (SessionSummary summary in sourceSessions.Reverse())
        {
            try
            {
                SessionRecord? session = await _source.GetSessionAsync(summary.SessionId, cancellationToken);
                if (session is null) continue;

                TranscriptRecord? transcript = await _source.GetTranscriptAsync(session.Id, cancellationToken);
                IReadOnlyList<ReportRecord> reports = await _source.GetReportsAsync(session.Id, cancellationToken);

                bool exists = targetByStart.TryGetValue(session.StartedAtUtc, out long targetId);
                if (exists) sessionsAlreadyThere++;
                else sessionsCopied++;

                bool targetHasTranscript = false;
                IReadOnlyList<ReportRecord> targetReports = [];
                if (exists)
                {
                    targetHasTranscript = await _target.GetTranscriptAsync(targetId, cancellationToken) is not null;
                    targetReports = await _target.GetReportsAsync(targetId, cancellationToken);
                }

                List<ReportRecord> missingReports = reports
                    .Where(report => !targetReports.Any(existing =>
                        existing.PromptId == report.PromptId && existing.CreatedAtUtc == report.CreatedAtUtc))
                    .ToList();

                if (transcript is not null && !targetHasTranscript) transcriptsCopied++;
                reportsCopied += missingReports.Count;

                foreach (string? path in reports.Select(report => report.VaultPath).Prepend(session.AudioPath))
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    (File.Exists(path) ? filesToArchive : filesMissing).Add(path);
                }

                if (!apply) continue;

                if (!exists)
                {
                    targetId = await _target.CreateSessionAsync(session.StartedAtUtc, session.Source, cancellationToken);
                    targetByStart[session.StartedAtUtc] = targetId;
                }

                // Una sesión que nunca se cerró (grabación que falló antes de
                // detenerse) se deja igual de abierta en el destino. Y se mira
                // también cuando la sesión ya existía: una corrida interrumpida
                // justo después de crearla la habría dejado sin cerrar.
                if (session.EndedAtUtc is { } endedAt &&
                    (!exists || (await _target.GetSessionAsync(targetId, cancellationToken))?.EndedAtUtc is null))
                {
                    await _target.CompleteSessionAsync(
                        targetId, endedAt, session.AudioPath, session.Duration, cancellationToken);
                }

                if (transcript is not null && !targetHasTranscript)
                {
                    await _target.SaveTranscriptAsync(transcript with { SessionId = targetId }, cancellationToken);
                }

                foreach (ReportRecord report in missingReports)
                {
                    await _target.SaveReportAsync(
                        new NewReport(
                            targetId, report.PromptId, report.PromptVersion, report.Markdown, report.StructuredJson,
                            report.LlmProvider, report.LlmModel, report.InputTokens, report.OutputTokens,
                            report.CostUsd, report.VaultPath, report.CreatedAtUtc),
                        cancellationToken);
                }

                if (_archiveQueue is not null)
                {
                    if (!string.IsNullOrWhiteSpace(session.AudioPath) && File.Exists(session.AudioPath))
                    {
                        _archiveQueue.Enqueue(targetId, ArchiveKind.Audio, session.AudioPath);
                    }

                    foreach (ReportRecord report in reports)
                    {
                        if (!string.IsNullOrWhiteSpace(report.VaultPath) && File.Exists(report.VaultPath))
                        {
                            _archiveQueue.Enqueue(targetId, ArchiveKind.Report, report.VaultPath);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                // Una reunión que falla no detiene a las demás: se anota y se
                // sigue. Volver a correr completa lo que haya quedado a medias.
                problems.Add($"Sesión {summary.SessionId} ({summary.StartedAtUtc:u}): {exception.Message}");
            }
        }

        return new HistoryMigrationResult(
            sourceSessions.Count, sessionsCopied, sessionsAlreadyThere, transcriptsCopied, reportsCopied,
            filesToArchive, filesMissing, problems);
    }

    /// <summary>
    /// Comprueba, después de migrar, que todo lo del origen está en el destino:
    /// cada reunión, con el mismo texto de transcript y con todos sus reportes.
    /// Devuelve las diferencias; vacío significa que no falta nada.
    /// </summary>
    public async Task<IReadOnlyList<string>> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var differences = new List<string>();
        Dictionary<DateTimeOffset, long> targetByStart = (await ListAllAsync(_target, cancellationToken))
            .GroupBy(session => session.StartedAtUtc)
            .ToDictionary(group => group.Key, group => group.First().SessionId);

        foreach (SessionSummary summary in await ListAllAsync(_source, cancellationToken))
        {
            string label = $"Sesión {summary.SessionId} ({summary.StartedAtUtc:u})";

            if (!targetByStart.TryGetValue(summary.StartedAtUtc, out long targetId))
            {
                differences.Add($"{label}: no está en el destino.");
                continue;
            }

            SessionRecord? source = await _source.GetSessionAsync(summary.SessionId, cancellationToken);
            SessionRecord? target = await _target.GetSessionAsync(targetId, cancellationToken);
            if (source is not null && target is not null &&
                (source.Source != target.Source || source.AudioPath != target.AudioPath ||
                 source.EndedAtUtc != target.EndedAtUtc || !SameDuration(source.Duration, target.Duration)))
            {
                differences.Add($"{label}: los datos de la sesión no coinciden.");
            }

            TranscriptRecord? sourceTranscript = await _source.GetTranscriptAsync(summary.SessionId, cancellationToken);
            TranscriptRecord? targetTranscript = await _target.GetTranscriptAsync(targetId, cancellationToken);
            if (sourceTranscript is not null &&
                (targetTranscript is null || targetTranscript.Text != sourceTranscript.Text ||
                 targetTranscript.CostUsd != sourceTranscript.CostUsd))
            {
                differences.Add($"{label}: el transcript falta o no coincide.");
            }

            IReadOnlyList<ReportRecord> targetReports = await _target.GetReportsAsync(targetId, cancellationToken);
            foreach (ReportRecord report in await _source.GetReportsAsync(summary.SessionId, cancellationToken))
            {
                bool found = targetReports.Any(existing =>
                    existing.PromptId == report.PromptId &&
                    existing.CreatedAtUtc == report.CreatedAtUtc &&
                    existing.Markdown == report.Markdown &&
                    existing.CostUsd == report.CostUsd &&
                    existing.InputTokens == report.InputTokens &&
                    existing.OutputTokens == report.OutputTokens);

                if (!found) differences.Add($"{label}: falta o no coincide el reporte {report.PromptId} de {report.CreatedAtUtc:u}.");
            }
        }

        return differences;
    }

    /// <summary>La duración viaja como segundos en coma flotante; se compara al milisegundo.</summary>
    private static bool SameDuration(TimeSpan? left, TimeSpan? right) =>
        left is null || right is null
            ? left is null && right is null
            : Math.Abs((left.Value - right.Value).TotalMilliseconds) < 1;

    private static async Task<IReadOnlyList<SessionSummary>> ListAllAsync(
        IMeetingHistoryStore store,
        CancellationToken cancellationToken)
    {
        var all = new List<SessionSummary>();
        while (true)
        {
            IReadOnlyList<SessionSummary> page = await store.ListSessionsAsync(PageSize, all.Count, cancellationToken);
            all.AddRange(page);
            if (page.Count < PageSize) return all;
        }
    }
}
