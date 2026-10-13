using System.Text.RegularExpressions;
using Npgsql;

namespace MeetingAssistant.Infrastructure.Storage.Postgres;

/// <summary>
/// La conexión a Postgres (Neon) y su esquema. Es el equivalente de
/// <c>SqliteConnectionFactory</c> más <c>SqliteSchemaMigrator</c>, juntos por
/// una razón: acá la migración <b>no corre al arrancar la app</b>.
///
/// Una base en la nube puede tardar segundos en despertar o no estar, y el
/// arranque no puede depender de la red. Así que el esquema se asegura la
/// primera vez que alguien pide una conexión, una sola vez, y si falla se
/// vuelve a intentar en el próximo pedido en vez de quedar roto para siempre.
/// </summary>
public sealed partial class PostgresDatabase : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string? _schema;
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private bool _migrated;

    /// <param name="connectionString">
    /// Cadena de Npgsql (<c>Host=...;Username=...</c>) o la URL que da Neon
    /// (<c>postgresql://usuario:clave@host/base?sslmode=require</c>).
    /// </param>
    /// <param name="schema">
    /// Esquema donde vive todo. Null usa el por defecto (<c>public</c>). Los
    /// autotests pasan uno propio para no tocar los datos reales.
    /// </param>
    public PostgresDatabase(string connectionString, string? schema = null)
    {
        if (schema is not null && !SafeIdentifier().IsMatch(schema))
        {
            throw new ArgumentException("El nombre de esquema sólo admite minúsculas, dígitos y guion bajo.", nameof(schema));
        }

        var builder = ToBuilder(connectionString);
        builder.Timeout = 15;
        builder.CommandTimeout = 30;
        builder.ApplicationName = "MeetingAssistant";
        _schema = schema;
        _dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        Description = $"{builder.Host}/{builder.Database}{(schema is null ? string.Empty : $" (esquema {schema})")}";
    }

    /// <summary>Host y base, sin usuario ni clave. Para el log.</summary>
    public string Description { get; }

    public static int LatestVersion => Migrations[^1].Version;

    /// <summary>Devuelve una conexión abierta, con el esquema ya al día.</summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (!_migrated) await MigrateAsync(cancellationToken);
        return await OpenInSchemaAsync(requireSchema: true, cancellationToken);
    }

    /// <summary>
    /// Abre una conexión y, si hay esquema propio, la apunta a él <b>en cada
    /// apertura</b> y comprueba que quedó apuntada.
    ///
    /// No se usa el parámetro de arranque <c>search_path</c> de la cadena de
    /// conexión porque <b>el proxy de Neon lo descarta en silencio</b>: la
    /// conexión abre sin error y trabaja contra <c>public</c>. Así fue como la
    /// primera versión del autotest escribió sus datos de prueba en el esquema
    /// real, creyendo estar aislada. La comprobación de abajo existe para que
    /// eso no pueda volver a pasar sin que algo lance.
    /// </summary>
    private async Task<NpgsqlConnection> OpenInSchemaAsync(bool requireSchema, CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        if (_schema is null) return connection;

        try
        {
            // public sigue en la ruta porque ahí vive la extensión unaccent. El
            // nombre ya pasó por SafeIdentifier; un identificador no admite
            // parámetros.
            await ExecuteAsync(connection, null, $"set search_path to {_schema}, public;", cancellationToken);

            if (requireSchema)
            {
                await using var check = new NpgsqlCommand("select current_schema();", connection);
                string? actual = (await check.ExecuteScalarAsync(cancellationToken))?.ToString();
                if (!string.Equals(actual, _schema, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"La conexión debía trabajar en el esquema '{_schema}' y quedó en '{actual}'. No se ejecuta nada.");
                }
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Aplica las migraciones que falten y devuelve una línea para el log. Es
    /// idempotente y se puede llamar en cualquier momento.
    /// </summary>
    public async Task<string> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await _migrationLock.WaitAsync(cancellationToken);
        try
        {
            if (_schema is not null)
            {
                // Primero el esquema, con una conexión que todavía no lo exige.
                await using NpgsqlConnection bootstrap = await OpenInSchemaAsync(requireSchema: false, cancellationToken);
                await ExecuteAsync(bootstrap, null, $"create schema if not exists {_schema};", cancellationToken);
            }

            await using NpgsqlConnection connection = await OpenInSchemaAsync(requireSchema: true, cancellationToken);

            await ExecuteAsync(
                connection,
                null,
                "create table if not exists schema_version (version integer primary key, applied_at_utc timestamptz not null default now());",
                cancellationToken);

            int current;
            await using (var read = new NpgsqlCommand("select coalesce(max(version), 0) from schema_version;", connection))
            {
                current = Convert.ToInt32(await read.ExecuteScalarAsync(cancellationToken));
            }

            int applied = 0;
            foreach ((int version, string sql) in Migrations)
            {
                if (version <= current) continue;

                // Cada paso con su registro de versión en la misma transacción:
                // un fallo a mitad deja la base en la versión anterior, entera.
                await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
                await ExecuteAsync(connection, transaction, sql, cancellationToken);
                await ExecuteAsync(
                    connection, transaction, $"insert into schema_version(version) values ({version});", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                applied++;
            }

            _migrated = true;
            return applied == 0
                ? $"esquema ya en v{current}, sin cambios"
                : $"esquema migrado de v{current} a v{LatestVersion} ({applied} paso(s))";
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    /// <summary>
    /// <c>true</c> si el esquema ya fue creado alguna vez. Sólo lee: es lo que
    /// permite simular una migración sin que preguntar cree las tablas.
    /// </summary>
    public async Task<bool> HasSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenInSchemaAsync(requireSchema: false, cancellationToken);
        await using var command = new NpgsqlCommand(
            _schema is null
                ? "select to_regclass('public.schema_version') is not null;"
                : $"select to_regclass('{_schema}.schema_version') is not null;",
            connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    /// <summary>Borra el esquema entero. Sólo para los autotests, y sólo con esquema propio.</summary>
    public async Task DropSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schema is null) throw new InvalidOperationException("No se borra el esquema por defecto.");

        await using NpgsqlConnection connection = await OpenInSchemaAsync(requireSchema: false, cancellationToken);
        await ExecuteAsync(connection, null, $"drop schema if exists {_schema} cascade;", cancellationToken);
        _migrated = false;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Neon entrega una URL; Npgsql quiere pares clave=valor. Se aceptan las dos
    /// formas para que lo que se copia del panel de Neon sirva tal cual.
    /// </summary>
    internal static NpgsqlConnectionStringBuilder ToBuilder(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        connectionString = connectionString.Trim();

        if (!connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return new NpgsqlConnectionStringBuilder(connectionString);
        }

        var uri = new Uri(connectionString);
        string[] userInfo = uri.UserInfo.Split(':', 2);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
        };

        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;

            switch (parts[0].ToLowerInvariant())
            {
                case "sslmode":
                    builder.SslMode = Enum.Parse<SslMode>(value.Replace("-", string.Empty), ignoreCase: true);
                    break;
                case "channel_binding":
                    builder.ChannelBinding = Enum.Parse<ChannelBinding>(value, ignoreCase: true);
                    break;
            }
        }

        return builder;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SafeIdentifier();

    /// <summary>
    /// Los pasos, en orden. <b>Nunca se edita uno ya publicado</b> — se agrega
    /// el siguiente. Misma regla que en <c>SqliteSchemaMigrator</c>.
    /// </summary>
    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, Version1Sql)
    ];

    /// <summary>
    /// v1 — lo mismo que el esquema v2 de SQLite, con tipos de Postgres.
    ///
    /// Lo que cambia de verdad es la búsqueda. FTS5 no existe acá, así que:
    ///
    /// - <c>ma_simple</c> es una configuración de búsqueda propia: la <c>simple</c>
    ///   (sin stemming ni palabras vacías, que con un corpus ES/EN mezclado harían
    ///   más daño que bien) con <c>unaccent</c> adelante. Es el equivalente del
    ///   tokenizador <c>unicode61 remove_diacritics 2</c>: <c>sesion</c> encuentra
    ///   <c>sesión</c> y al revés.
    /// - <c>transcript.search</c> es una columna generada, así que el índice se
    ///   mantiene solo, como hacían los tres triggers de SQLite.
    ///
    /// Y una columna que SQLite no necesita: <c>cloud_upload.machine_name</c>.
    /// Las rutas locales son de una máquina; con la base compartida, sin esto una
    /// máquina intentaría retomar las subidas pendientes de la otra y las
    /// marcaría como fallidas por no encontrar el archivo.
    ///
    /// Las fechas son <c>timestamptz</c> y el dinero sigue en micro-dólares
    /// enteros, por los mismos motivos que en SQLite.
    /// </summary>
    private const string Version1Sql =
        """
        create extension if not exists unaccent;

        create text search configuration ma_simple (copy = simple);
        alter text search configuration ma_simple
            alter mapping for asciiword, asciihword, hword_asciipart, word, hword, hword_part
            with unaccent, simple;

        create table session (
            id                bigint generated always as identity primary key,
            started_at_utc    timestamptz not null,
            ended_at_utc      timestamptz,
            audio_path        text,
            duration_seconds  double precision,
            source            text not null
        );

        create index ix_session_started on session(started_at_utc desc);

        create table transcript (
            session_id      bigint primary key references session(id) on delete cascade,
            text            text not null,
            provider        text,
            model           text,
            cost_micro_usd  bigint,
            created_at_utc  timestamptz not null,
            search          tsvector generated always as (to_tsvector('ma_simple', text)) stored
        );

        create index ix_transcript_search on transcript using gin(search);

        create table report (
            id               bigint generated always as identity primary key,
            session_id       bigint not null references session(id) on delete cascade,
            prompt_id        text not null,
            prompt_version   text,
            markdown         text not null,
            structured_json  text,
            llm_provider     text,
            llm_model        text,
            tokens_input     integer,
            tokens_output    integer,
            cost_micro_usd   bigint,
            vault_path       text,
            created_at_utc   timestamptz not null
        );

        create index ix_report_session on report(session_id);
        create index ix_report_created on report(created_at_utc desc);
        create index ix_report_prompt  on report(prompt_id, prompt_version);

        create table setting (
            key             text primary key,
            value           text,
            updated_at_utc  timestamptz not null
        );

        create table cloud_upload (
            id              bigint generated always as identity primary key,
            session_id      bigint not null references session(id) on delete cascade,
            machine_name    text not null,
            kind            text not null,
            local_path      text not null,
            remote_name     text not null,
            state           text not null,
            attempts        integer not null default 0,
            remote_url      text,
            last_error      text,
            created_at_utc  timestamptz not null,
            updated_at_utc  timestamptz not null,
            unique(machine_name, kind, local_path)
        );

        create index ix_cloud_upload_session on cloud_upload(session_id);
        create index ix_cloud_upload_state   on cloud_upload(state);
        """;
}
