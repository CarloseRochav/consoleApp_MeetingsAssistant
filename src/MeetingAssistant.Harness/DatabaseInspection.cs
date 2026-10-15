using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;
using MeetingAssistant.Infrastructure;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Cost;
using MeetingAssistant.Infrastructure.Llm;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using MeetingAssistant.Infrastructure.Transcription;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

/// <summary>--verify-db: mira la base local REAL, sin cambiarla.</summary>
internal static class DatabaseInspection
{
    /// <summary>
    /// Inspecciona la base local de reuniones: versión de esquema, objetos creados y
    /// filas por tabla. Existe para poder comprobar contra la máquina lo que una
    /// migración dice haber hecho, sin depender de tener instalado un cliente de
    /// SQLite. Mismo criterio que --verify-render.
    ///
    /// Apunta a la misma ruta que usa la app instalada
    /// (%LOCALAPPDATA%\MeetingAssistant\meetings.db), así que lee la base de verdad,
    /// no una copia.
    /// </summary>
    public static int InspectLocal()
    {
        string databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetingAssistant",
            "meetings.db");

        Console.WriteLine($"=== Base de reuniones ==={Environment.NewLine}{databasePath}");
        if (!File.Exists(databasePath))
        {
            Console.Error.WriteLine("No existe todavia. La crea el primer arranque de la app.");
            return 1;
        }

        Console.WriteLine($"Tamano: {new FileInfo(databasePath).Length:N0} bytes");
        Console.WriteLine(
            "Ojo: los ajustes viven siempre aca, pero si la app tiene Database:Provider = Postgres su historial " +
            "es el de Postgres (mas abajo), no las sesiones de este archivo.");

        var factory = new SqliteConnectionFactory(databasePath);
        using Microsoft.Data.Sqlite.SqliteConnection connection = factory.Open();

        using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "pragma user_version;";
            Console.WriteLine($"Version de esquema: v{command.ExecuteScalar()} (el codigo espera v{SqliteSchemaMigrator.LatestVersion})");
        }

        using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "select type, name from sqlite_master where name not like 'sqlite_%' order by type, name;";
            using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
            Console.WriteLine($"{Environment.NewLine}=== Objetos ===");
            while (reader.Read()) Console.WriteLine($"  {reader.GetString(0),-7} {reader.GetString(1)}");
        }

        Console.WriteLine($"{Environment.NewLine}=== Filas ===");
        foreach (string table in new[] { "session", "transcript", "report", "setting" })
        {
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"select count(*) from {table};";
            Console.WriteLine($"  {table,-12} {command.ExecuteScalar()}");
        }

        // Las ultimas sesiones, con su origen. Es lo primero que uno quiere ver al
        // mirar la base de verdad, y lo que dice si la columna source se esta
        // rellenando distinto segun el camino o siempre igual.
        using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                select s.id, s.started_at_utc, s.source, s.duration_seconds,
                       (select count(*) from transcript t where t.session_id = s.id),
                       (select count(*) from report r where r.session_id = s.id)
                  from session s order by s.started_at_utc desc limit 10;
                """;
            using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
            Console.WriteLine($"{Environment.NewLine}=== Ultimas sesiones ===");
            Console.WriteLine($"  {"id",-4} {"inicio (UTC)",-26} {"origen",-9} {"dur(s)",-8} {"trans",-6} reportes");
            while (reader.Read())
            {
                Console.WriteLine(
                    $"  {reader.GetInt64(0),-4} {reader.GetString(1),-26} {reader.GetString(2),-9} " +
                    $"{(reader.IsDBNull(3) ? "-" : reader.GetDouble(3).ToString("F1")),-8} " +
                    $"{reader.GetInt32(4),-6} {reader.GetInt32(5)}");
            }
        }

        // Los costos, en micro-dolares crudos y con SQL propio, sin pasar por el
        // store. Es contra lo que se compara la pagina Costos: si esto se calculara
        // con las mismas consultas que la pagina, un join de mas las duplicaria a
        // las dos por igual y la comparacion no probaria nada.
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                select prompt_id, coalesce(prompt_version, '-'), count(*), coalesce(sum(cost_micro_usd), 0)
                  from report group by prompt_id, prompt_version order by prompt_id, prompt_version;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            Console.WriteLine($"{Environment.NewLine}=== Costos (micro-USD: 1.000.000 = US$1) ===");
            Console.WriteLine($"  {"prompt",-24} {"version",-9} {"reportes",-9} micro-USD");
            while (reader.Read())
            {
                Console.WriteLine(
                    $"  {reader.GetString(0),-24} {reader.GetString(1),-9} {reader.GetInt32(2),-9} {reader.GetInt64(3)}");
            }
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                select coalesce((select sum(cost_micro_usd) from report), 0),
                       coalesce((select sum(cost_micro_usd) from transcript), 0),
                       (select count(*) from transcript where cost_micro_usd is null),
                       (select count(*) from report where cost_micro_usd is null),
                       (select count(*) from report where cost_micro_usd = 0
                           and (coalesce(tokens_input, 0) > 0 or coalesce(tokens_output, 0) > 0));
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            reader.Read();
            Console.WriteLine($"  LLM total: {reader.GetInt64(0)}   transcripcion total: {reader.GetInt64(1)}");
            Console.WriteLine(
                $"  transcripts sin costo: {reader.GetInt32(2)}   reportes sin costo: {reader.GetInt32(3)}   " +
                $"reportes con tokens y costo 0: {reader.GetInt32(4)}");
        }

        // Los ajustes, con los secretos enmascarados. Lo que hay que poder ver de un
        // secreto no es su valor sino tres cosas: que la fila existe, que esta
        // marcada como secreta, y que **se puede descifrar en este perfil** — DPAPI
        // ata el valor al usuario y la maquina, asi que una base copiada de otro
        // lado deja las credenciales ilegibles y eso tiene que verse aca, no
        // descubrirse cuando falle una transcripcion.
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "select key, value, is_secret, updated_at_utc from setting order by key;";
            using SqliteDataReader reader = command.ExecuteReader();
            var protector = new DpapiSecretProtector();

            Console.WriteLine($"{Environment.NewLine}=== Ajustes ===");
            Console.WriteLine($"  {"clave",-46} {"tipo",-9} valor");
            while (reader.Read())
            {
                bool isSecret = reader.GetInt64(2) != 0;
                string? stored = reader.IsDBNull(1) ? null : reader.GetString(1);
                string shown = isSecret
                    ? stored is null
                        ? "(nulo)"
                        : protector.TryUnprotect(stored) is { } clear
                            ? $"cifrado, descifrable ({clear.Length} chars)"
                            : "cifrado, NO DESCIFRABLE en este perfil"
                    : stored ?? "(nulo)";

                Console.WriteLine($"  {reader.GetString(0),-46} {(isSecret ? "secreto" : "en claro"),-9} {shown}");
            }
        }

        // La integridad referencial y el indice FTS son justo lo que se rompe en
        // silencio: comprobarlos a mano es la unica forma de saber que los triggers
        // y las foreign keys quedaron como se creian.
        using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "pragma foreign_key_check;";
            using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
            Console.WriteLine($"{Environment.NewLine}Integridad referencial: {(reader.Read() ? "FALLOS" : "sin violaciones")}");
        }

        using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "insert into transcript_fts(transcript_fts) values('integrity-check');";
            try
            {
                command.ExecuteNonQuery();
                Console.WriteLine("Indice FTS5: consistente");
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception)
            {
                Console.WriteLine($"Indice FTS5: INCONSISTENTE — {exception.Message}");
                return 1;
            }
        }

        return 0;
    }

}
