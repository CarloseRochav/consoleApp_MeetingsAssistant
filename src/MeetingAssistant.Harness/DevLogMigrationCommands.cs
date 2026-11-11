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

        int overwrites = await CompareAsync(sourceConnection, targetConnection, idUser);

        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("Simulacion: no se escribio nada. Agrega --apply para copiar.");
            return 0;
        }

        if (overwrites > 0)
        {
            // Paso B (2026-11-10): la importación es un upsert por id de TODAS las filas. Desde que
            // la API crea filas propias, una fila distinta con el mismo id es una fila de la API que
            // se pisaría. Nunca se aplica así: primero un plan para esas filas.
            Console.Error.WriteLine();
            Console.Error.WriteLine($"No se aplica: {overwrites} filas de DevLog se pisarian (ver arriba). Hace falta un plan para esas filas.");
            return 1;
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

    /// <summary>Fecha de la importación real (roadmap, Fase 8, tanda 2). Lo creado después en el origen es nuevo.</summary>
    private static readonly DateTimeOffset ImportDate = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Paso B (2026-11-10): compara origen y destino fila por fila antes de importar. Por tabla, el
    /// origen y el destino devuelven las mismas columnas con los mismos nombres (las que escribe la
    /// importación); se compara el md5 de cada fila como JSON, en UTC en las dos bases. Sólo ids,
    /// fechas y conteos salen a la consola: nunca el contenido. Devuelve cuántas filas de DevLog
    /// se pisarían (mismo id, contenido distinto).
    /// </summary>
    private static async Task<int> CompareAsync(NpgsqlConnection source, NpgsqlConnection target, int idUser)
    {
        await ExecuteAsync(source, "set time zone 'UTC'");
        await ExecuteAsync(target, "set time zone 'UTC'");

        // Cada tabla: (select del origen, select del destino) con las mismas columnas, la clave
        // "k" y la fecha "d" aparte. El hash cubre sólo las columnas que la importación escribe.
        (string Table, string Columns, string Key, string Date, string From)[] sourceSide =
        [
            ("session", $"id, started_at_utc, ended_at_utc, audio_path, duration_seconds, source, {idUser} as id_user", "id::text", "started_at_utc", "public.session"),
            ("transcript", "session_id, text, provider, model, cost_micro_usd, created_at_utc", "session_id::text", "created_at_utc", "public.transcript"),
            ("report", "id, session_id, prompt_id, prompt_version, markdown, structured_json, llm_provider, llm_model, tokens_input, tokens_output, cost_micro_usd, vault_path, created_at_utc", "id::text", "created_at_utc", "public.report"),
            ("prompt_version", "id, prompt_id, base_version, version, system_prompt, note, created_at_utc, null::int as id_user", "id::text", "created_at_utc", "public.prompt_version"),
            ("setting", "key, value, updated_at_utc", "key", "updated_at_utc", "public.setting"),
            ("cloud_upload", "id, session_id, machine_name, kind, local_path, remote_name, state, attempts, remote_url, last_error, created_at_utc, updated_at_utc", "id::text", "created_at_utc", "public.cloud_upload"),
        ];
        (string Columns, string Key, string Date, string From)[] targetSide =
        [
            ("\"idSession\" as id, started_at as started_at_utc, ended_at as ended_at_utc, audio_path, duration_seconds, source, \"idUser\" as id_user", "\"idSession\"::text", "started_at", "mtg.mtg_sessions"),
            ("\"idSession\" as session_id, text, provider, model, cost_micro_usd, created_at as created_at_utc", "\"idSession\"::text", "created_at", "mtg.mtg_transcripts"),
            ("\"idReport\" as id, \"idSession\" as session_id, prompt_id, prompt_version, markdown, structured_json, llm_provider, llm_model, tokens_input, tokens_output, cost_micro_usd, vault_path, created_at as created_at_utc", "\"idReport\"::text", "created_at", "mtg.mtg_reports"),
            ("\"idPromptVersion\" as id, prompt_id, base_version, version, system_prompt, note, created_at as created_at_utc, \"idUser\" as id_user", "\"idPromptVersion\"::text", "created_at", "mtg.mtg_prompt_versions"),
            ("key, value, updated_at as updated_at_utc", "key", "updated_at", "mtg.mtg_settings where \"idUser\" is null"),
            ("\"idCloudUpload\" as id, \"idSession\" as session_id, machine_name, kind, local_path, remote_name, state, attempts, remote_url, last_error, created_at as created_at_utc, updated_at as updated_at_utc", "\"idCloudUpload\"::text", "created_at", "mtg.mtg_cloud_uploads"),
        ];

        Console.WriteLine();
        Console.WriteLine($"Comparacion fila por fila (md5 de las columnas que escribe la importacion; 'nuevo' = fecha >= {ImportDate:yyyy-MM-dd}):");
        int overwrites = 0;
        for (int i = 0; i < sourceSide.Length; i++)
        {
            var s = sourceSide[i];
            var t = targetSide[i];
            Dictionary<string, (DateTimeOffset Date, string Hash)> sourceRows = await RowsAsync(source, s.Columns, s.Key, s.Date, s.From);
            Dictionary<string, (DateTimeOffset Date, string Hash)> targetRows = await RowsAsync(target, t.Columns, t.Key, t.Date, t.From);

            var onlySource = sourceRows.Keys.Except(targetRows.Keys).OrderBy(SortKey).ToList();
            var onlyTarget = targetRows.Keys.Except(sourceRows.Keys).OrderBy(SortKey).ToList();
            var differ = sourceRows.Keys.Intersect(targetRows.Keys).Where(k => sourceRows[k].Hash != targetRows[k].Hash).OrderBy(SortKey).ToList();
            int same = sourceRows.Count - onlySource.Count - differ.Count;
            int newInSource = sourceRows.Values.Count(r => r.Date >= ImportDate);
            DateTimeOffset? newestSource = sourceRows.Count == 0 ? null : sourceRows.Values.Max(r => r.Date);

            Console.WriteLine($"  {s.Table,-15} origen {sourceRows.Count,4} | destino {targetRows.Count,4} | iguales {same,4} | sólo en origen {onlySource.Count,3} | sólo en DevLog {onlyTarget.Count,3} | distintas {differ.Count,3} | origen nuevo {newInSource,3} | más reciente del origen {newestSource:yyyy-MM-dd HH:mm}Z");
            if (onlySource.Count > 0) Console.WriteLine($"      se insertarian: {Describe(onlySource, sourceRows)}");
            if (onlyTarget.Count > 0) Console.WriteLine($"      creadas en DevLog (la importacion no las toca): {Describe(onlyTarget, targetRows)}");
            if (differ.Count > 0)
            {
                Console.WriteLine($"      SE PISARIAN (mismo id, contenido distinto; fecha origen -> DevLog): {string.Join(", ", differ.Select(k => $"{k} ({sourceRows[k].Date:yyyy-MM-dd} -> {targetRows[k].Date:yyyy-MM-dd})"))}");
                overwrites += differ.Count;
            }
        }

        return overwrites;
    }

    private static string SortKey(string key) => long.TryParse(key, out long n) ? n.ToString("D20") : key;

    private static string Describe(List<string> keys, Dictionary<string, (DateTimeOffset Date, string Hash)> rows) =>
        string.Join(", ", keys.Select(k => $"{k} ({rows[k].Date:yyyy-MM-dd})"));

    private static async Task<Dictionary<string, (DateTimeOffset Date, string Hash)>> RowsAsync(
        NpgsqlConnection connection, string columns, string key, string date, string from)
    {
        var rows = new Dictionary<string, (DateTimeOffset, string)>();
        string sql = $"select k_, d_, md5((to_jsonb(t0) - 'k_' - 'd_')::text) from (select {key} as k_, {date} as d_, {columns} from {from}) t0";
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows[reader.GetString(0)] = (reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2));
        }

        return rows;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Las dos formas que da Neon: URL <c>postgresql://</c> o clave=valor. Era
    /// <c>PostgresDatabase.ToBuilder</c> en Infrastructure; desde el paso B (sin Postgres directo en la
    /// app) vive aquí, el único que todavía lee la base vieja, para la importación final.
    /// </summary>
    private static NpgsqlConnectionStringBuilder ToBuilder(string connectionString)
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
            return ToBuilder(connectionString);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"No se pudo leer {settingKey} ({exception.GetType().Name}). No se muestra su contenido.");
            return null;
        }
    }
}
