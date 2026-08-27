using Microsoft.Data.Sqlite;

namespace MeetingAssistant.Infrastructure.Storage.Sqlite;

/// <summary>
/// Abre conexiones a la base local de reuniones y les aplica los PRAGMA que
/// SQLite <b>no</b> recuerda entre conexiones.
///
/// La ruta no se decide acá: la impone quien construye la fábrica, porque la
/// política de "dónde escribe la app" ya vive en un solo lugar
/// (<c>App.StartupErrorLogPath</c>, <c>App.MeetingOutputDirectory</c>) y
/// duplicarla en Infrastructure es cómo se terminan teniendo dos verdades.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // El acceso es de un solo proceso (la app es de instancia única
            // desde T4.1), pero el pool igual evita reabrir el archivo en cada
            // consulta.
            Pooling = true
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Devuelve una conexión ya abierta y configurada. Los tres PRAGMA de abajo
    /// tienen que aplicarse <b>por conexión</b>: SQLite no los persiste, y
    /// olvidarlos no da error — simplemente deja de haber integridad
    /// referencial, que es la peor forma de fallar.
    /// </summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using SqliteCommand pragmas = connection.CreateCommand();
        pragmas.CommandText =
            """
            pragma journal_mode = wal;
            pragma foreign_keys = on;
            pragma busy_timeout = 5000;
            """;
        pragmas.ExecuteNonQuery();

        return connection;
    }

    /// <summary>
    /// Crea el directorio contenedor si hace falta. Se separa de
    /// <see cref="Open"/> porque el primer arranque tiene que poder crear la
    /// base, y porque es el mismo problema que ya bloqueó T6b: escribir en un
    /// directorio que no existe bajo una instalación de sólo lectura.
    /// </summary>
    public void EnsureDirectoryExists()
    {
        string? directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }
}
