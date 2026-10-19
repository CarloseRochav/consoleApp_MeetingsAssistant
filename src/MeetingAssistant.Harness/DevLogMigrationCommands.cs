using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Copia el historial de Postgres (la base de esta app) a la base de DevLog_Backend, esquema
/// <c>mtg</c> (Fase 8: una sola base, dueña la API). Origen: <c>Postgres:ConnectionString</c>.
/// Destino: <c>DevLog:ConnectionString</c>. Todas las sesiones quedan a nombre de un usuario de
/// DevLog (<c>idUser</c>).
///
/// Del lado de DevLog todo entra por sus funciones <c>mtg.mtg_import_*</c> (regla de DevLog: nada
/// de SQL de tablas desde C#); este comando sólo lee cada tabla de origen como JSON y se la pasa.
/// Conserva los ids, así que es un upsert: se puede correr de nuevo mientras el escritorio siga
/// escribiendo en la base vieja, y sólo mueve lo que cambió. Lo borrado en el origen NO se borra
/// en DevLog.
///
/// Sin <c>--apply</c> sólo cuenta y comprueba, sin escribir nada. Con <c>--apply</c> escribe todo
/// en UNA transacción del lado de DevLog: si algo falla, no queda nada a medias.
/// </summary>
internal static class DevLogMigrationCommands
{
    private const int PageSize = 100;

    /// <summary>
    /// Orden de carga: las sesiones primero, porque todo lo demás las referencia. Cada tabla lista
    /// sus columnas a mano para no mandar <c>transcript.search</c>, que DevLog genera solo.
    /// </summary>
    private static readonly (string Table, string Columns, string OrderBy, string ImportFunction, bool TakesUser)[] Tables =
    [
        ("session", "id, started_at_utc, ended_at_utc, audio_path, duration_seconds, source", "id", "mtg.mtg_import_sessions", true),
        ("transcript", "session_id, text, provider, model, cost_micro_usd, created_at_utc", "session_id", "mtg.mtg_import_transcripts", false),
        ("report", "id, session_id, prompt_id, prompt_version, markdown, structured_json, llm_provider, llm_model, tokens_input, tokens_output, cost_micro_usd, vault_path, created_at_utc", "id", "mtg.mtg_import_reports", false),
        ("prompt_version", "id, prompt_id, base_version, version, system_prompt, note, created_at_utc", "id", "mtg.mtg_import_prompt_versions", false),
        ("setting", "key, value, updated_at_utc", "key", "mtg.mtg_import_settings", false),
        ("cloud_upload", "id, session_id, machine_name, kind, local_path, remote_name, state, attempts, remote_url, last_error, created_at_utc, updated_at_utc", "id", "mtg.mtg_import_cloud_uploads", false),
    ];

    public static async Task<int> MigrateFromArgumentsAsync(IConfiguration configuration, IReadOnlyList<string> arguments)
    {
        if (!int.TryParse(arguments[0], out int idUser) || idUser <= 0)
        {
            Console.Error.WriteLine("El primer argumento es el idUser de DevLog dueño de las reuniones (un entero > 0).");
            return 1;
        }

        bool apply = arguments.Skip(1).Contains("--apply", StringComparer.OrdinalIgnoreCase);
        string? source = configuration["Postgres:ConnectionString"];
        string? target = configuration["DevLog:ConnectionString"];
        if (string.IsNullOrWhiteSpace(source) || source.StartsWith('<') || string.IsNullOrWhiteSpace(target) || target.StartsWith('<'))
        {
            Console.Error.WriteLine("Faltan Postgres:ConnectionString (origen) o DevLog:ConnectionString (destino; mejor la variable DevLog__ConnectionString).");
            return 1;
        }

        // Las dos formas que da Neon (URL postgresql:// o clave=valor), por el mismo camino que la
        // app. Si no se puede leer, el error NO lleva la cadena: lleva la clave.
        if (TryParse(source, "Postgres:ConnectionString") is not { } sourceBuilder ||
            TryParse(target, "DevLog:ConnectionString") is not { } targetBuilder)
        {
            return 1;
        }

        Console.WriteLine("=== Migrar el historial a DevLog_Backend ===");
        Console.WriteLine($"Origen:  {sourceBuilder.Host}/{sourceBuilder.Database}");
        Console.WriteLine($"Destino: {targetBuilder.Host}/{targetBuilder.Database}, esquema mtg, dueño idUser {idUser}");

        await using var sourceConnection = new NpgsqlConnection(sourceBuilder.ConnectionString);
        await using var targetConnection = new NpgsqlConnection(targetBuilder.ConnectionString);
        await sourceConnection.OpenAsync();
        await targetConnection.OpenAsync();

        if (!await ScalarAsync<bool>(targetConnection, "select to_regproc('mtg.mtg_import_sessions') is not null"))
        {
            Console.Error.WriteLine("El destino no tiene el esquema mtg: aplicar antes db/migrations/004_meetings.sql de DevLog.");
            return 1;
        }

        var user = new NpgsqlCommand("""select exists(select 1 from per.per_users where "idUser" = @id and is_active)""", targetConnection);
        user.Parameters.AddWithValue("id", idUser);
        if (!(bool)(await user.ExecuteScalarAsync())!)
        {
            Console.Error.WriteLine($"En DevLog no hay un usuario activo con idUser {idUser}.");
            return 1;
        }

        Console.WriteLine();
        foreach ((string table, _, _, _, _) in Tables)
        {
            long count = await ScalarAsync<long>(sourceConnection, $"select count(*) from public.{table}");
            Console.WriteLine($"  {table,-15} {count,6} filas en el origen");
        }

        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("Simulacion: no se escribio nada. Agrega --apply para copiar.");
            return 0;
        }

        await using NpgsqlTransaction transaction = await targetConnection.BeginTransactionAsync();
        Console.WriteLine();
        foreach ((string table, string columns, string orderBy, string importFunction, bool takesUser) in Tables)
        {
            int written = 0;
            for (int offset = 0; ; offset += PageSize)
            {
                string? page = await ScalarAsync<string?>(sourceConnection,
                    $"select jsonb_agg(to_jsonb(t))::text from (select {columns} from public.{table} order by {orderBy} limit {PageSize} offset {offset}) t");
                if (page is null) break;

                var import = new NpgsqlCommand(
                    takesUser ? $"select {importFunction}(p_iduser => @user, p_rows => @rows)" : $"select {importFunction}(p_rows => @rows)",
                    targetConnection, transaction);
                if (takesUser) import.Parameters.AddWithValue("user", idUser);
                import.Parameters.Add(new NpgsqlParameter("rows", NpgsqlDbType.Jsonb) { Value = page });
                written += (int)(await import.ExecuteScalarAsync())!;
            }

            Console.WriteLine($"  {table,-15} {written,6} filas escritas");
        }

        await transaction.CommitAsync();
        Console.WriteLine();
        Console.WriteLine("Copiado. Volver a correrlo actualiza lo que haya cambiado; no borra nada en DevLog.");
        return 0;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        object? result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default! : (T)result;
    }

    /// <summary>
    /// Null, con un aviso que nombra la clave de configuración y nada más, si la cadena no se puede
    /// leer. El mensaje de la excepción NO se muestra: Npgsql mete la cadena entera, clave incluida
    /// (pasó el 2026-10-19 con una URL de Neon y la clave de la base quedó en la salida).
    /// </summary>
    private static NpgsqlConnectionStringBuilder? TryParse(string connectionString, string settingKey)
    {
        try
        {
            return MeetingAssistant.Infrastructure.Storage.Postgres.PostgresDatabase.ToBuilder(connectionString);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"No se pudo leer {settingKey} ({exception.GetType().Name}). No se muestra su contenido.");
            return null;
        }
    }
}
