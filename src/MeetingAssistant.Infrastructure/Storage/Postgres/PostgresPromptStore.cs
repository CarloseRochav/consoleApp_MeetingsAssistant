using MeetingAssistant.Core.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace MeetingAssistant.Infrastructure.Storage.Postgres;

/// <summary>
/// Implementación Postgres de <see cref="IPromptStore"/>, sobre la tabla
/// <c>prompt_version</c> (esquema v2). Al estar en la base compartida, una
/// edición hecha en una máquina la ven las demás al reiniciar.
/// </summary>
public sealed class PostgresPromptStore : IPromptStore
{
    private readonly PostgresDatabase _database;

    public PostgresPromptStore(PostgresDatabase database) => _database = database;

    public async Task<IReadOnlyList<PromptEdit>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select prompt_id, base_version, version, system_prompt, note, created_at_utc from prompt_version order by id;",
            connection);

        var results = new List<PromptEdit>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PromptEdit(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return results;
    }

    public async Task SaveAsync(PromptEdit edit, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into prompt_version(prompt_id, base_version, version, system_prompt, note, created_at_utc)
            values (@prompt, @base, @version, @text, @note, @created);
            """,
            connection);
        command.Parameters.Add("prompt", NpgsqlDbType.Text).Value = edit.PromptId;
        command.Parameters.Add("base", NpgsqlDbType.Text).Value = edit.BaseVersion;
        command.Parameters.Add("version", NpgsqlDbType.Text).Value = (object?)edit.Version ?? DBNull.Value;
        command.Parameters.Add("text", NpgsqlDbType.Text).Value = (object?)edit.SystemPrompt ?? DBNull.Value;
        command.Parameters.Add("note", NpgsqlDbType.Text).Value = (object?)edit.Note ?? DBNull.Value;
        command.Parameters.Add("created", NpgsqlDbType.TimestampTz).Value = edit.CreatedAtUtc.ToUniversalTime();

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
