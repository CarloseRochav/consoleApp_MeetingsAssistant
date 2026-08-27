using MeetingAssistant.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace MeetingAssistant.Infrastructure.Storage.Sqlite;

/// <summary>
/// Implementación SQLite de <see cref="ISettingsStore"/>. Los valores marcados
/// como secretos se cifran con el <see cref="ISecretProtector"/> inyectado
/// <b>antes</b> de tocar el disco, nunca después.
/// </summary>
public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ISecretProtector _secretProtector;

    public SqliteSettingsStore(SqliteConnectionFactory connectionFactory, ISecretProtector secretProtector)
    {
        _connectionFactory = connectionFactory;
        _secretProtector = secretProtector;
    }

    public async Task<IReadOnlyList<SettingEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "select key, value, is_secret, updated_at_utc from setting order by key;";

        var results = new List<SettingEntry>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bool isSecret = reader.GetInt64(2) != 0;
            string? stored = reader.IsDBNull(1) ? null : reader.GetString(1);

            results.Add(new SettingEntry(
                Key: reader.GetString(0),
                // Un secreto indescifrable viene como null, no revienta: el
                // usuario tiene que poder abrir Configuración y reescribirlo.
                Value: isSecret && stored is not null ? _secretProtector.TryUnprotect(stored) : stored,
                IsSecret: isSecret,
                UpdatedAtUtc: SqliteValueConversions.ToTimestamp(reader.GetString(3))));
        }

        return results;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "select value, is_secret from setting where key = $key;";
        command.Parameters.AddWithValue("$key", key);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0)) return null;

        string stored = reader.GetString(0);
        return reader.GetInt64(1) != 0 ? _secretProtector.TryUnprotect(stored) : stored;
    }

    public async Task SetAsync(
        string key,
        string? value,
        bool isSecret = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // Vaciar un campo borra el override y devuelve el valor empaquetado.
        // Mismo criterio que ya usa el archivo de usuario de T9: si guardar un
        // vacío dejara una cadena vacía, no habría forma de volver atrás desde
        // la UI.
        if (string.IsNullOrWhiteSpace(value))
        {
            await RemoveAsync(key, cancellationToken);
            return;
        }

        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            insert into setting(key, value, is_secret, updated_at_utc)
            values ($key, $value, $isSecret, $updated)
            on conflict(key) do update set
                value = excluded.value,
                is_secret = excluded.is_secret,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", isSecret ? _secretProtector.Protect(value) : value);
        command.Parameters.AddWithValue("$isSecret", isSecret ? 1 : 0);
        command.Parameters.AddWithValue("$updated", SqliteValueConversions.ToText(DateTimeOffset.UtcNow));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "delete from setting where key = $key;";
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
