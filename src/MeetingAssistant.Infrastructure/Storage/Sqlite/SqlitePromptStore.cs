using MeetingAssistant.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace MeetingAssistant.Infrastructure.Storage.Sqlite;

/// <summary>Implementación SQLite de <see cref="IPromptStore"/>, sobre la tabla <c>prompt_version</c> (esquema v3).</summary>
public sealed class SqlitePromptStore : IPromptStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqlitePromptStore(SqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<PromptEdit>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "select prompt_id, base_version, version, system_prompt, note, created_at_utc from prompt_version order by id;";

        var results = new List<PromptEdit>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PromptEdit(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                SqliteValueConversions.ToTimestamp(reader.GetString(5))));
        }

        return results;
    }

    public async Task SaveAsync(PromptEdit edit, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            insert into prompt_version(prompt_id, base_version, version, system_prompt, note, created_at_utc)
            values ($prompt, $base, $version, $text, $note, $created);
            """;
        command.Parameters.AddWithValue("$prompt", edit.PromptId);
        command.Parameters.AddWithValue("$base", edit.BaseVersion);
        command.Parameters.AddWithValue("$version", (object?)edit.Version ?? DBNull.Value);
        command.Parameters.AddWithValue("$text", (object?)edit.SystemPrompt ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", (object?)edit.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", SqliteValueConversions.ToText(edit.CreatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
