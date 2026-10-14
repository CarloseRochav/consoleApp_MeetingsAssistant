using MeetingAssistant.Infrastructure.Storage.Postgres;
using Microsoft.Extensions.Configuration;
using Npgsql;

/// <summary>
/// La parte de <c>--verify-db</c> que mira Postgres. Desde que la app guarda el
/// historial en Neon, el SQLite local dejó de ser lo que muestran Historial y
/// Costos, y <c>--verify-db</c> sólo miraba ese archivo.
///
/// <b>Sólo lee:</b> no pasa por los stores (abrir uno migra el esquema si está
/// atrasado) y la sesión se pone en modo de sólo lectura, así que cualquier
/// escritura que se colara acá lanzaría en vez de tocar los datos reales.
/// </summary>
internal static class PostgresInspection
{
    public static async Task<int> RunAsync(IConfiguration configuration)
    {
        Console.WriteLine($"{Environment.NewLine}=== Historial en Postgres ===");

        string? connectionString = configuration["Postgres:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.StartsWith('<'))
        {
            Console.WriteLine("Sin Postgres:ConnectionString en la configuracion del harness: no se mira.");
            return 0;
        }

        try
        {
            await using var database = new PostgresDatabase(connectionString);
            Console.WriteLine(database.Description);

            if (!await database.HasSchemaAsync())
            {
                Console.WriteLine("La base no tiene el esquema de la app todavia (nunca se uso para el historial).");
                return 0;
            }

            await using NpgsqlConnection connection = await database.OpenWithoutMigratingAsync();
            await using (var readOnly = new NpgsqlCommand("set session characteristics as transaction read only;", connection))
            {
                await readOnly.ExecuteNonQueryAsync();
            }

            Console.WriteLine(
                $"Version de esquema: v{await ScalarAsync(connection, "select coalesce(max(version), 0) from schema_version;")} " +
                $"(el codigo espera v{PostgresDatabase.LatestVersion})");

            Console.WriteLine($"{Environment.NewLine}=== Filas (Postgres) ===");
            foreach (string table in new[] { "session", "transcript", "report", "prompt_version", "cloud_upload" })
            {
                Console.WriteLine($"  {table,-15} {await ScalarAsync(connection, $"select count(*) from {table};")}");
            }

            await using (var command = new NpgsqlCommand(
                """
                select s.id, s.started_at_utc, s.source, s.duration_seconds,
                       (select count(*) from transcript t where t.session_id = s.id),
                       (select count(*) from report r where r.session_id = s.id)
                  from session s order by s.started_at_utc desc limit 10;
                """,
                connection))
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
            {
                Console.WriteLine($"{Environment.NewLine}=== Ultimas sesiones (Postgres) ===");
                Console.WriteLine($"  {"id",-4} {"inicio (UTC)",-26} {"origen",-9} {"dur(s)",-8} {"trans",-6} reportes");
                while (await reader.ReadAsync())
                {
                    Console.WriteLine(
                        $"  {reader.GetInt64(0),-4} {reader.GetFieldValue<DateTimeOffset>(1).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}  " +
                        $"{reader.GetString(2),-9} {(reader.IsDBNull(3) ? "-" : reader.GetDouble(3).ToString("F1")),-8} " +
                        $"{reader.GetInt64(4),-6} {reader.GetInt64(5)}");
                }
            }

            await using (var command = new NpgsqlCommand(
                """
                select coalesce((select sum(cost_micro_usd) from report), 0),
                       coalesce((select sum(cost_micro_usd) from transcript), 0),
                       (select count(*) from transcript where cost_micro_usd is null),
                       (select count(*) from report where cost_micro_usd is null);
                """,
                connection))
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
            {
                await reader.ReadAsync();
                Console.WriteLine($"{Environment.NewLine}=== Costos (Postgres, micro-dolares) ===");
                Console.WriteLine($"  LLM total: {reader.GetInt64(0)}   transcripcion total: {reader.GetInt64(1)}");
                Console.WriteLine($"  transcripts sin costo: {reader.GetInt64(2)}   reportes sin costo: {reader.GetInt64(3)}");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"No se pudo leer Postgres: {exception.Message}");
            return 1;
        }
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
