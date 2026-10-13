using System.Text.RegularExpressions;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using Npgsql;
using NpgsqlTypes;

namespace MeetingAssistant.Infrastructure.Storage.Postgres;

/// <summary>
/// Implementación Postgres de <see cref="IMeetingHistoryStore"/>. Hace lo mismo
/// que <c>SqliteMeetingHistoryStore</c> y pasa el mismo contrato
/// (<c>--verify-postgres</c> corre las mismas comprobaciones contra los dos).
///
/// Todo va con parámetros. La consulta de búsqueda además pasa por
/// <see cref="ToTsQuery"/> antes de llegar al servidor.
/// </summary>
public sealed partial class PostgresMeetingHistoryStore : IMeetingHistoryStore
{
    private const decimal MicroUsdPerUsd = 1_000_000m;

    /// <summary>Misma definición que en el store de SQLite: tokens y costo 0.</summary>
    private const string MissingPriceCondition =
        "cost_micro_usd = 0 and (coalesce(tokens_input, 0) > 0 or coalesce(tokens_output, 0) > 0)";

    private readonly PostgresDatabase _database;

    public PostgresMeetingHistoryStore(PostgresDatabase database) => _database = database;

    // -------------------------------------------------------------- escritura

    public async Task<long> CreateSessionAsync(
        DateTimeOffset startedAtUtc,
        string source,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "insert into session(started_at_utc, source) values (@started, @source) returning id;", connection);
        command.Parameters.Add("started", NpgsqlDbType.TimestampTz).Value = startedAtUtc.ToUniversalTime();
        command.Parameters.Add("source", NpgsqlDbType.Text).Value = source;

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task CompleteSessionAsync(
        long sessionId,
        DateTimeOffset endedAtUtc,
        string? audioPath,
        TimeSpan? duration,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update session
               set ended_at_utc = @ended, audio_path = @audio, duration_seconds = @duration
             where id = @id;
            """,
            connection);
        command.Parameters.Add("ended", NpgsqlDbType.TimestampTz).Value = endedAtUtc.ToUniversalTime();
        command.Parameters.Add("audio", NpgsqlDbType.Text).Value = (object?)audioPath ?? DBNull.Value;
        command.Parameters.Add("duration", NpgsqlDbType.Double).Value = (object?)duration?.TotalSeconds ?? DBNull.Value;
        command.Parameters.Add("id", NpgsqlDbType.Bigint).Value = sessionId;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveTranscriptAsync(TranscriptRecord transcript, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        // La columna search es generada: reemplazar el texto reindexa solo.
        await using var command = new NpgsqlCommand(
            """
            insert into transcript(session_id, text, provider, model, cost_micro_usd, created_at_utc)
            values (@session, @text, @provider, @model, @cost, @created)
            on conflict (session_id) do update set
                text = excluded.text,
                provider = excluded.provider,
                model = excluded.model,
                cost_micro_usd = excluded.cost_micro_usd,
                created_at_utc = excluded.created_at_utc;
            """,
            connection);
        command.Parameters.Add("session", NpgsqlDbType.Bigint).Value = transcript.SessionId;
        command.Parameters.Add("text", NpgsqlDbType.Text).Value = transcript.Text;
        command.Parameters.Add("provider", NpgsqlDbType.Text).Value = (object?)transcript.Provider ?? DBNull.Value;
        command.Parameters.Add("model", NpgsqlDbType.Text).Value = (object?)transcript.Model ?? DBNull.Value;
        command.Parameters.Add("cost", NpgsqlDbType.Bigint).Value = (object?)ToMicroUsd(transcript.CostUsd) ?? DBNull.Value;
        command.Parameters.Add("created", NpgsqlDbType.TimestampTz).Value = transcript.CreatedAtUtc.ToUniversalTime();

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> SaveReportAsync(NewReport report, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into report(session_id, prompt_id, prompt_version, markdown, structured_json,
                               llm_provider, llm_model, tokens_input, tokens_output,
                               cost_micro_usd, vault_path, created_at_utc)
            values (@session, @promptId, @promptVersion, @markdown, @structured,
                    @provider, @model, @tokensIn, @tokensOut, @cost, @vault, @created)
            returning id;
            """,
            connection);
        command.Parameters.Add("session", NpgsqlDbType.Bigint).Value = report.SessionId;
        command.Parameters.Add("promptId", NpgsqlDbType.Text).Value = report.PromptId;
        command.Parameters.Add("promptVersion", NpgsqlDbType.Text).Value = (object?)report.PromptVersion ?? DBNull.Value;
        command.Parameters.Add("markdown", NpgsqlDbType.Text).Value = report.Markdown;
        command.Parameters.Add("structured", NpgsqlDbType.Text).Value = (object?)report.StructuredJson ?? DBNull.Value;
        command.Parameters.Add("provider", NpgsqlDbType.Text).Value = (object?)report.LlmProvider ?? DBNull.Value;
        command.Parameters.Add("model", NpgsqlDbType.Text).Value = (object?)report.LlmModel ?? DBNull.Value;
        command.Parameters.Add("tokensIn", NpgsqlDbType.Integer).Value = (object?)report.InputTokens ?? DBNull.Value;
        command.Parameters.Add("tokensOut", NpgsqlDbType.Integer).Value = (object?)report.OutputTokens ?? DBNull.Value;
        command.Parameters.Add("cost", NpgsqlDbType.Bigint).Value = (object?)ToMicroUsd(report.CostUsd) ?? DBNull.Value;
        command.Parameters.Add("vault", NpgsqlDbType.Text).Value = (object?)report.VaultPath ?? DBNull.Value;
        command.Parameters.Add("created", NpgsqlDbType.TimestampTz).Value = report.CreatedAtUtc.ToUniversalTime();

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    // ---------------------------------------------------------------- lectura

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        int limit,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        // sum(bigint) devuelve numeric en Postgres; se vuelve a bigint para
        // leerlo como entero, que es lo que es.
        await using var command = new NpgsqlCommand(
            """
            select s.id,
                   s.started_at_utc,
                   s.duration_seconds,
                   s.source,
                   (select count(*) from report r where r.session_id = s.id)::integer,
                   (coalesce((select sum(r.cost_micro_usd) from report r where r.session_id = s.id), 0)
                     + coalesce((select t.cost_micro_usd from transcript t where t.session_id = s.id), 0))::bigint,
                   (select left(t.text, 180) from transcript t where t.session_id = s.id),
                   (select u.state from cloud_upload u
                     where u.session_id = s.id and u.kind = 'audio' order by u.id desc limit 1)
              from session s
             order by s.started_at_utc desc
             limit @limit offset @offset;
            """,
            connection);
        command.Parameters.Add("limit", NpgsqlDbType.Integer).Value = limit;
        command.Parameters.Add("offset", NpgsqlDbType.Integer).Value = offset;

        var results = new List<SessionSummary>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SessionSummary(
                SessionId: reader.GetInt64(0),
                StartedAtUtc: reader.GetFieldValue<DateTimeOffset>(1),
                Duration: reader.IsDBNull(2) ? null : TimeSpan.FromSeconds(reader.GetDouble(2)),
                Source: reader.GetString(3),
                ReportCount: reader.GetInt32(4),
                TotalCostUsd: reader.GetInt64(5) / MicroUsdPerUsd,
                TranscriptPreview: reader.IsDBNull(6) ? null : reader.GetString(6),
                AudioArchiveState: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return results;
    }

    public async Task<SessionRecord?> GetSessionAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select id, started_at_utc, ended_at_utc, audio_path, duration_seconds, source from session where id = @id;",
            connection);
        command.Parameters.Add("id", NpgsqlDbType.Bigint).Value = sessionId;

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new SessionRecord(
            Id: reader.GetInt64(0),
            StartedAtUtc: reader.GetFieldValue<DateTimeOffset>(1),
            EndedAtUtc: reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            AudioPath: reader.IsDBNull(3) ? null : reader.GetString(3),
            Duration: reader.IsDBNull(4) ? null : TimeSpan.FromSeconds(reader.GetDouble(4)),
            Source: reader.GetString(5));
    }

    public async Task<TranscriptRecord?> GetTranscriptAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select session_id, text, provider, model, cost_micro_usd, created_at_utc from transcript where session_id = @id;",
            connection);
        command.Parameters.Add("id", NpgsqlDbType.Bigint).Value = sessionId;

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new TranscriptRecord(
            SessionId: reader.GetInt64(0),
            Text: reader.GetString(1),
            Provider: reader.IsDBNull(2) ? null : reader.GetString(2),
            Model: reader.IsDBNull(3) ? null : reader.GetString(3),
            CostUsd: reader.IsDBNull(4) ? null : reader.GetInt64(4) / MicroUsdPerUsd,
            CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(5));
    }

    public async Task<IReadOnlyList<ReportRecord>> GetReportsAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select id, session_id, prompt_id, prompt_version, markdown, structured_json,
                   llm_provider, llm_model, tokens_input, tokens_output,
                   cost_micro_usd, vault_path, created_at_utc
              from report where session_id = @id order by created_at_utc, id;
            """,
            connection);
        command.Parameters.Add("id", NpgsqlDbType.Bigint).Value = sessionId;

        var results = new List<ReportRecord>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
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
                CostUsd: reader.IsDBNull(10) ? null : reader.GetInt64(10) / MicroUsdPerUsd,
                VaultPath: reader.IsDBNull(11) ? null : reader.GetString(11),
                CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(12)));
        }

        return results;
    }

    public async Task<IReadOnlyList<TranscriptSearchHit>> SearchTranscriptsAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        string tsQuery = ToTsQuery(query);
        // Antes de abrir la conexión: una consulta vacía no toca la base.
        if (tsQuery.Length == 0) return [];

        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        // ts_headline marca el término entre corchetes, igual que snippet() en
        // SQLite: la interfaz muestra el fragmento tal cual y no sabe de qué
        // motor viene.
        await using var command = new NpgsqlCommand(
            """
            select s.id,
                   s.started_at_utc,
                   ts_headline('ma_simple', t.text, q,
                       'StartSel="[", StopSel="]", MaxWords=14, MinWords=6, MaxFragments=1, FragmentDelimiter="…"')
              from transcript t
              join session s on s.id = t.session_id
             cross join to_tsquery('ma_simple', @query) q
             where t.search @@ q
             order by ts_rank(t.search, q) desc, s.started_at_utc desc
             limit @limit;
            """,
            connection);
        command.Parameters.Add("query", NpgsqlDbType.Text).Value = tsQuery;
        command.Parameters.Add("limit", NpgsqlDbType.Integer).Value = limit;

        var results = new List<TranscriptSearchHit>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TranscriptSearchHit(
                SessionId: reader.GetInt64(0),
                StartedAtUtc: reader.GetFieldValue<DateTimeOffset>(1),
                Snippet: reader.GetString(2)));
        }

        return results;
    }

    // -------------------------------------------------------------- analítica

    public async Task<CostSummary> GetCostSummaryAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            select (select count(*) from session)::integer,
                   (select count(distinct session_id) from report)::integer,
                   (select count(*) from report)::integer,
                   (select count(*) from transcript)::integer,
                   coalesce((select sum(cost_micro_usd) from report), 0)::bigint,
                   coalesce((select sum(cost_micro_usd) from transcript), 0)::bigint,
                   (select count(*) from report where {MissingPriceCondition})::integer,
                   (select count(*) from report where cost_micro_usd is null)::integer,
                   (select count(*) from transcript where cost_micro_usd is null)::integer,
                   (select min(created_at_utc) from report),
                   (select max(created_at_utc) from report);
            """,
            connection);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new CostSummary(
            SessionCount: reader.GetInt32(0),
            SessionsWithReportsCount: reader.GetInt32(1),
            ReportCount: reader.GetInt32(2),
            TranscriptCount: reader.GetInt32(3),
            LlmCostUsd: reader.GetInt64(4) / MicroUsdPerUsd,
            TranscriptionCostUsd: reader.GetInt64(5) / MicroUsdPerUsd,
            ReportsMissingPriceCount: reader.GetInt32(6),
            ReportsWithoutCostCount: reader.GetInt32(7),
            TranscriptsWithoutCostCount: reader.GetInt32(8),
            FirstReportUtc: reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            LastReportUtc: reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10));
    }

    public async Task<IReadOnlyList<PromptUsageSummary>> GetPromptUsageAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            select prompt_id,
                   prompt_version,
                   count(*)::integer,
                   coalesce(sum(cost_micro_usd), 0)::bigint,
                   coalesce(avg(tokens_output), 0)::double precision,
                   max(created_at_utc),
                   coalesce(sum(case when {MissingPriceCondition} then 1 else 0 end), 0)::integer
              from report
             group by prompt_id, prompt_version
             order by count(*) desc, prompt_id;
            """,
            connection);

        var results = new List<PromptUsageSummary>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PromptUsageSummary(
                PromptId: reader.GetString(0),
                PromptVersion: reader.IsDBNull(1) ? null : reader.GetString(1),
                ReportCount: reader.GetInt32(2),
                TotalCostUsd: reader.GetInt64(3) / MicroUsdPerUsd,
                AverageOutputTokens: reader.GetDouble(4),
                LastUsedUtc: reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                ReportsMissingPriceCount: reader.GetInt32(6)));
        }

        return results;
    }

    // ------------------------------------------------------------ conversiones

    private static long? ToMicroUsd(decimal? value) =>
        value is null ? null : (long)Math.Round(value.Value * MicroUsdPerUsd, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Convierte lo que escribe una persona en un <c>tsquery</c> válido. Es el
    /// equivalente de <c>SqliteValueConversions.ToFts5Query</c>, con el mismo
    /// trato: se pierde poder de consulta y se gana que <b>nunca lance</b>.
    ///
    /// Se queda sólo con letras y dígitos —todo lo demás separa palabras—, así
    /// que ningún operador de <c>tsquery</c> (<c>&amp;</c>, <c>|</c>, <c>!</c>,
    /// paréntesis, comillas, dos puntos) puede llegar al servidor. Cada palabra
    /// busca por prefijo y todas tienen que aparecer.
    /// </summary>
    internal static string ToTsQuery(string userInput) =>
        string.Join(
            " & ",
            NotWordCharacters().Split(userInput ?? string.Empty)
                .Where(term => term.Length > 0)
                .Select(term => $"{term}:*"));

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotWordCharacters();
}
