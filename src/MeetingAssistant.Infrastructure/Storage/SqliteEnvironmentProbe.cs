using Microsoft.Data.Sqlite;

namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// Diagnóstico de arranque para SQLite. Contesta una sola pregunta, y es la que
/// bloquea toda la Fase 5: <b>¿carga el binario nativo <c>e_sqlite3.dll</c>
/// cuando la app corre instalada desde <c>C:\Program Files\WindowsApps</c>?</b>
///
/// Existe porque este proyecto ya fue mordido dos veces por lo mismo: código que
/// funcionaba con <c>dotnet run</c> y fallaba bajo el paquete instalado — la
/// ruta del log (T7/T6a) y el directorio de audio (T6b, paso 0). Un paquete
/// nativo por RID es exactamente esa forma de riesgo, así que se mide en vez de
/// suponerse.
///
/// Comprueba también FTS5, que no es un detalle: la búsqueda full-text sobre
/// transcripts es la razón por la que se eligió SQLite sobre LiteDB. Si el
/// binario embebido viniera sin FTS5, esa decisión habría que revisarla.
///
/// Es barato — una base en memoria que se abre y se cierra — y sobrevive a cada
/// reempaquetado, que es cuando el riesgo puede reaparecer (por ejemplo si
/// alguna vez se empaqueta en Release, donde el trimming todavía no se ejerció).
/// </summary>
public static class SqliteEnvironmentProbe
{
    /// <summary>
    /// Devuelve una línea lista para el log de diagnóstico. Nunca lanza: un
    /// diagnóstico que tumba el arranque sería peor que el problema que
    /// diagnostica — la lección de T4.4, donde una excepción de arranque se
    /// llevó puesta la app entera.
    /// </summary>
    public static string Describe()
    {
        try
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();

            string version = ScalarText(connection, "select sqlite_version();");
            string library = ScalarText(connection, "select sqlite_source_id();");
            bool hasFts5 = HasFts5(connection);

            return $"SQLite OK — version {version}, FTS5 {(hasFts5 ? "disponible" : "NO DISPONIBLE")}, " +
                   $"source {library}";
        }
        catch (Exception exception)
        {
            // El caso que más importa: DllNotFoundException / BadImageFormatException
            // significan que el nativo no viajó dentro del paquete o no es de la
            // arquitectura correcta.
            return $"SQLite FALLO — {exception.GetType().Name}: {exception.Message}";
        }
    }

    private static bool HasFts5(SqliteConnection connection)
    {
        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "create virtual table fts5_probe using fts5(content);";
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            // Crear la tabla virtual es la única prueba que vale: compilar con
            // FTS5 y tenerlo registrado son cosas distintas.
            return false;
        }
    }

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString() ?? "(desconocido)";
    }
}
