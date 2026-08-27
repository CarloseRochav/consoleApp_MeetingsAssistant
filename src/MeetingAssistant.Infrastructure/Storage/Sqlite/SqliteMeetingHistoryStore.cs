using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using Microsoft.Data.Sqlite;

namespace MeetingAssistant.Infrastructure.Storage.Sqlite;

/// <summary>
/// Implementación SQLite de <see cref="IMeetingHistoryStore"/>. ADO.NET a mano,
/// sin ORM, igual que el resto de los adaptadores de este proyecto.
///
/// Todo va con parámetros, nunca interpolado — incluida la consulta de búsqueda,
/// que además pasa por <see cref="SqliteValueConversions.ToFts5Query"/> antes de
/// llegar acá.
/// </summary>
public sealed class SqliteMeetingHistoryStore : IMeetingHistoryStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteMeetingHistoryStore(SqliteConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    // -------------------------------------------------------------- escritura

    public async Task<long> CreateSessionAsync(
        DateTimeOffset startedAtUtc,
        string source,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            insert into session(started_at_utc, source) values ($started, $source);
            select last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$started", SqliteValueConversions.ToText(startedAtUtc));
        command.Parameters.AddWithValue("$source", source);

        object? id = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(id);
    }

    public async Task CompleteSessionAsync(
        long sessionId,
        DateTimeOffset endedAtUtc,
        string? audioPath,
        TimeSpan? duration,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            update session
               set ended_at_utc = $ended,
                   audio_path = $audio,
                   duration_seconds = $duration
             where id = $id;
            """;
        command.Parameters.AddWithValue("$ended", SqliteValueConversions.ToText(endedAtUtc));
        command.Parameters.AddWithValue("$audio", (object?)audioPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)duration?.TotalSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", sessionId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveTranscriptAsync(
        TranscriptRecord transcript,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        // insert-or-replace para poder re-transcribir el mismo audio. Los
        // triggers de FTS5 se encargan del índice: por eso se dejó que SQLite
        // los mantenga en vez de reindexar desde C#, donde alguien terminaría
        // olvidándolo.
        command.CommandText =
            """
            insert into transcript(session_id, text, provider, model, cost_micro_usd, created_at_utc)
            values ($session, $text, $provider, $model, $cost, $created)
            on conflict(session_id) do update set
                text = excluded.text,
                provider = excluded.provider,
                model = excluded.model,
                cost_micro_usd = excluded.cost_micro_usd,
                created_at_utc = excluded.created_at_utc;
            """;
        command.Parameters.AddWithValue("$session", transcript.SessionId);
        command.Parameters.AddWithValue("$text", transcript.Text);
        command.Parameters.AddWithValue("$provider", (object?)transcript.Provider ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)transcript.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$cost",
            (object?)SqliteValueConversions.ToMicroUsd(transcript.CostUsd) ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", SqliteValueConversions.ToText(transcript.CreatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> SaveReportAsync(NewReport report, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            insert into report(session_id, prompt_id, prompt_version, markdown, structured_json,
                               llm_provider, llm_model, tokens_input, tokens_output,
                               cost_micro_usd, vault_path, created_at_utc)
            values ($session, $promptId, $promptVersion, $markdown, $structured,
                    $provider, $model, $tokensIn, $tokensOut, $cost, $vault, $created);
            select last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$session", report.SessionId);
        command.Parameters.AddWithValue("$promptId", report.PromptId);
        command.Parameters.AddWithValue("$promptVersion", (object?)report.PromptVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$markdown", report.Markdown);
        command.Parameters.AddWithValue("$structured", (object?)report.StructuredJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$provider", (object?)report.LlmProvider ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)report.LlmModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$tokensIn", (object?)report.InputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$tokensOut", (object?)report.OutputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$cost",
            (object?)SqliteValueConversions.ToMicroUsd(report.CostUsd) ?? DBNull.Value);
        command.Parameters.AddWithValue("$vault", (object?)report.VaultPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", SqliteValueConversions.ToText(report.CreatedAtUtc));

        object? id = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(id);
    }

    // ---------------------------------------------------------------- lectura

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        int limit,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        // El costo de la sesión suma transcripción y reportes: es lo que
        // realmente costó esa reunión, no sólo la parte del LLM.
        command.CommandText =
            """
            select s.id,
                   s.started_at_utc,
                   s.duration_seconds,
                   s.source,
                   (select count(*) from report r where r.session_id = s.id),
                   coalesce((select sum(r.cost_micro_usd) from report r where r.session_id = s.id), 0)
                     + coalesce((select t.cost_micro_usd from transcript t where t.session_id = s.id), 0),
                   (select substr(t.text, 1, 180) from transcript t where t.session_id = s.id)
              from session s
             order by s.started_at_utc desc
             limit $limit offset $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var results = new List<SessionSummary>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SessionSummary(
                SessionId: reader.GetInt64(0),
                StartedAtUtc: SqliteValueConversions.ToTimestamp(reader.GetString(1)),
                Duration: reader.IsDBNull(2) ? null : TimeSpan.FromSeconds(reader.GetDouble(2)),
                Source: reader.GetString(3),
                ReportCount: reader.GetInt32(4),
                TotalCostUsd: SqliteValueConversions.ToUsd(reader.GetInt64(5)),
                TranscriptPreview: reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return results;
    }

    public async Task<SessionRecord?> GetSessionAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            select id, started_at_utc, ended_at_utc, audio_path, duration_seconds, source
              from session where id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new SessionRecord(
            Id: reader.GetInt64(0),
            StartedAtUtc: SqliteValueConversions.ToTimestamp(reader.GetString(1)),
            EndedAtUtc: reader.IsDBNull(2) ? null : SqliteValueConversions.ToTimestamp(reader.GetString(2)),
            AudioPath: reader.IsDBNull(3) ? null : reader.GetString(3),
            Duration: reader.IsDBNull(4) ? null : TimeSpan.FromSeconds(reader.GetDouble(4)),
            Source: reader.GetString(5));
    }

    public async Task<TranscriptRecord?> GetTranscriptAsync(
        long sessionId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            select session_id, text, provider, model, cost_micro_usd, created_at_utc
              from transcript where session_id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new TranscriptRecord(
            SessionId: reader.GetInt64(0),
            Text: reader.GetString(1),
            Provider: reader.IsDBNull(2) ? null : reader.GetString(2),
            Model: reader.IsDBNull(3) ? null : reader.GetString(3),
            CostUsd: reader.IsDBNull(4) ? null : SqliteValueConversions.ToUsd(reader.GetInt64(4)),
            CreatedAtUtc: SqliteValueConversions.ToTimestamp(reader.GetString(5)));
    }

    public async Task<IReadOnlyList<ReportRecord>> GetReportsAsync(
        long sessionId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            select id, session_id, prompt_id, prompt_version, markdown, structured_json,
                   llm_provider, llm_model, tokens_input, tokens_output,
                   cost_micro_usd, vault_path, created_at_utc
              from report where session_id = $id order by created_at_utc;
            """;
        command.Parameters.AddWithValue("$id", sessionId);

        var results = new List<ReportRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ReportRecord(
                Id: reader.GetInt64(0),
                SessionId: reader.GetInt64(1),
                PromptId: reader.GetString(2),
                PromptVersion: reader.IsDBNull(3) ? null : reader.GetString(3),
                Markdown: reader.GetString(4),
                StructuredJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                LlmProvider: reader.IsDBNull(6) ? null : reader.GetString(6),
                LlmModel: reader.IsDBNull(7) ? null : reader.GetString(7),
                InputTokens: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                OutputTokens: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                CostUsd: reader.IsDBNull(10) ? null : SqliteValueConversions.ToUsd(reader.GetInt64(10)),
                VaultPath: reader.IsDBNull(11) ? null : reader.GetString(11),
                CreatedAtUtc: SqliteValueConversions.ToTimestamp(reader.GetString(12))));
        }

        return results;
    }

    public async Task<IReadOnlyList<TranscriptSearchHit>> SearchTranscriptsAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        string ftsQuery = SqliteValueConversions.ToFts5Query(query);
        if (ftsQuery.Length == 0) return [];

        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        // snippet() devuelve el fragmento con el término marcado. Sin esto, un
        // resultado de búsqueda es sólo una fecha y no dice por qué salió.
        command.CommandText =
            """
            select s.id,
                   s.started_at_utc,
                   snippet(transcript_fts, 0, '[', ']', '…', 12)
              from transcript_fts
              join session s on s.id = transcript_fts.rowid
             where transcript_fts match $query
             order by rank
             limit $limit;
            """;
        command.Parameters.AddWithValue("$query", ftsQuery);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<TranscriptSearchHit>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TranscriptSearchHit(
                SessionId: reader.GetInt64(0),
                StartedAtUtc: SqliteValueConversions.ToTimestamp(reader.GetString(1)),
                Snippet: reader.GetString(2)));
        }

        return results;
    }

    // -------------------------------------------------------------- analítica

    public async Task<CostSummary> GetCostSummaryAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            select (select count(*) from session),
                   (select count(*) from report),
                   coalesce((select sum(cost_micro_usd) from report), 0)
                     + coalesce((select sum(cost_micro_usd) from transcript), 0),
                   (select min(created_at_utc) from report),
                   (select max(created_at_utc) from report);
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new CostSummary(
            SessionCount: reader.GetInt32(0),
            ReportCount: reader.GetInt32(1),
            TotalCostUsd: SqliteValueConversions.ToUsd(reader.GetInt64(2)),
            FirstReportUtc: reader.IsDBNull(3) ? null : SqliteValueConversions.ToTimestamp(reader.GetString(3)),
            LastReportUtc: reader.IsDBNull(4) ? null : SqliteValueConversions.ToTimestamp(reader.GetString(4)));
    }

    public async Task<IReadOnlyList<PromptUsageSummary>> GetPromptUsageAsync(
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            select prompt_id,
                   prompt_version,
                   count(*),
                   coalesce(sum(cost_micro_usd), 0),
                   coalesce(avg(tokens_output), 0),
                   max(created_at_utc)
              from report
             group by prompt_id, prompt_version
             order by count(*) desc, prompt_id;
            """;

        var results = new List<PromptUsageSummary>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PromptUsageSummary(
                PromptId: reader.GetString(0),
                PromptVersion: reader.IsDBNull(1) ? null : reader.GetString(1),
                ReportCount: reader.GetInt32(2),
                TotalCostUsd: SqliteValueConversions.ToUsd(reader.GetInt64(3)),
                AverageOutputTokens: reader.GetDouble(4),
                LastUsedUtc: reader.IsDBNull(5) ? null : SqliteValueConversions.ToTimestamp(reader.GetString(5))));
        }

        return results;
    }
}
