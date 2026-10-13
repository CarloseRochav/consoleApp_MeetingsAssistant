using MeetingAssistant.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace MeetingAssistant.Infrastructure.Storage.Postgres;

/// <summary>
/// Ajustes compartidos, en la tabla <c>setting</c> de Postgres.
///
/// <b>No guarda secretos.</b> La tabla ni siquiera tiene la columna
/// <c>is_secret</c>: acá no hay con qué cifrar de forma que otra máquina lo
/// pueda leer, así que una credencial escrita acá quedaría en texto plano en un
/// servidor. Qué clave puede entrar lo decide
/// <see cref="SettingKeyPolicy.CanLiveInCloud"/>, y se aplica en las dos
/// direcciones: al escribir se rechaza, y al leer se descarta lo que no debería
/// estar.
/// </summary>
public sealed class PostgresSettingsStore : ICloudSettingsStore
{
    private readonly PostgresDatabase _database;

    public PostgresSettingsStore(PostgresDatabase database) => _database = database;

    public async Task<IReadOnlyList<SettingEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select key, value, updated_at_utc from setting order by key;", connection);

        var results = new List<SettingEntry>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string key = reader.GetString(0);
            if (!SettingKeyPolicy.CanLiveInCloud(key)) continue;

            results.Add(new SettingEntry(
                key,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                IsSecret: false,
                reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return results;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!SettingKeyPolicy.CanLiveInCloud(key)) return null;

        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select value from setting where key = @key;", connection);
        command.Parameters.Add("key", NpgsqlDbType.Text).Value = key;

        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>
    /// Lanza si la clave no puede vivir en la nube. Lanzar, y no ignorar en
    /// silencio, es deliberado: quien llama tiene que enterarse de que ese valor
    /// <b>no</b> quedó compartido.
    /// </summary>
    public async Task SetAsync(
        string key,
        string? value,
        bool isSecret = false,
        CancellationToken cancellationToken = default)
    {
        if (isSecret || !SettingKeyPolicy.CanLiveInCloud(key))
        {
            throw new InvalidOperationException(
                $"'{key}' no se guarda en la nube: es un secreto, o un ajuste propio de esta máquina.");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            await RemoveAsync(key, cancellationToken);
            return;
        }

        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into setting(key, value, updated_at_utc) values (@key, @value, now())
            on conflict (key) do update set value = excluded.value, updated_at_utc = excluded.updated_at_utc;
            """,
            connection);
        command.Parameters.Add("key", NpgsqlDbType.Text).Value = key;
        command.Parameters.Add("value", NpgsqlDbType.Text).Value = value;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("delete from setting where key = @key;", connection);
        command.Parameters.Add("key", NpgsqlDbType.Text).Value = key;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// Los ajustes de la nube como una capa más de <c>IConfiguration</c>, entre el
/// almacén local y las variables de entorno:
///
/// empaquetado -> archivo de usuario -> SQLite local -> <b>nube</b> -> entorno.
///
/// O sea: <b>primero la base compartida, y si no responde, lo local</b>. Las
/// variables de entorno siguen arriba como vía de escape.
/// </summary>
public sealed class CloudSettingsConfigurationSource : IConfigurationSource
{
    private readonly ICloudSettingsStore _store;
    private readonly TimeSpan _timeout;
    private readonly Action<string, Exception>? _onFailure;

    public CloudSettingsConfigurationSource(
        ICloudSettingsStore store,
        TimeSpan timeout,
        Action<string, Exception>? onFailure = null)
    {
        _store = store;
        _timeout = timeout;
        _onFailure = onFailure;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new CloudSettingsConfigurationProvider(_store, _timeout, _onFailure);
}

public sealed class CloudSettingsConfigurationProvider : ConfigurationProvider
{
    private readonly ICloudSettingsStore _store;
    private readonly TimeSpan _timeout;
    private readonly Action<string, Exception>? _onFailure;

    public CloudSettingsConfigurationProvider(
        ICloudSettingsStore store,
        TimeSpan timeout,
        Action<string, Exception>? onFailure = null)
    {
        _store = store;
        _timeout = timeout;
        _onFailure = onFailure;
    }

    /// <summary><c>true</c> si la última carga leyó de la nube; <c>false</c> si cayó a lo local.</summary>
    public bool LoadedFromCloud { get; private set; }

    /// <summary>
    /// <b>Nunca lanza y nunca espera más que el plazo.</b> Esta carga corre en
    /// el arranque, antes de que exista la ventana, y del otro lado hay una red
    /// y una base que puede estar dormida. Si no contesta a tiempo, la capa
    /// queda vacía, se registra, y la app arranca con lo local.
    ///
    /// El filtro de <see cref="SettingKeyPolicy.CanLiveInCloud"/> se repite acá
    /// aunque el store ya lo aplique: es la última puerta antes de que un valor
    /// entre a la configuración, y no depende de qué store le hayan pasado.
    /// </summary>
    public override void Load()
    {
        var loaded = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        LoadedFromCloud = false;

        try
        {
            using var timeout = new CancellationTokenSource(_timeout);
            // Task.Run para que el plazo valga también si el store se queda
            // bloqueado antes de su primer await.
            Task<IReadOnlyList<SettingEntry>> read = Task.Run(() => _store.GetAllAsync(timeout.Token), timeout.Token);
            if (!read.Wait(_timeout))
            {
                throw new TimeoutException(
                    $"Los ajustes de la nube no respondieron en {_timeout.TotalSeconds:F0} s. Se usan los locales.");
            }

            foreach (SettingEntry entry in read.GetAwaiter().GetResult())
            {
                if (entry.Value is null || !SettingKeyPolicy.CanLiveInCloud(entry.Key)) continue;

                loaded[entry.Key] = entry.Value;
            }
        }
        catch (Exception exception)
        {
            _onFailure?.Invoke("CloudSettingsConfigurationProvider.Load", exception);
            return;
        }

        Data = loaded;
        LoadedFromCloud = true;
    }
}

public static class CloudSettingsConfigurationBuilderExtensions
{
    /// <summary>
    /// Hay que llamarlo <b>después</b> de los ajustes locales y <b>antes</b> de
    /// las variables de entorno.
    /// </summary>
    public static IConfigurationBuilder AddCloudSettings(
        this IConfigurationBuilder builder,
        ICloudSettingsStore store,
        TimeSpan timeout,
        Action<string, Exception>? onFailure = null) =>
        builder.Add(new CloudSettingsConfigurationSource(store, timeout, onFailure));
}
