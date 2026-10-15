using Azure.Storage.Blobs;
using MeetingAssistant.Infrastructure.Storage.Postgres;

namespace MeetingAssistant.Infrastructure;

/// <summary>
/// Prueba una cadena de conexión antes de guardarla. Existe porque en una
/// máquina nueva estas cadenas se pegan a mano, y una mal copiada no falla al
/// guardar sino al arrancar, en silencio: la app cae a lo local y el historial
/// compartido simplemente no aparece.
///
/// Cada método devuelve null si conectó, o el motivo en una línea. Nunca lanza,
/// y nunca devuelve la cadena: el motivo se muestra en pantalla.
/// </summary>
public static class CloudConnectionCheck
{
    /// <summary>Abre una conexión y no toca el esquema: probar no puede migrar nada.</summary>
    public static async Task<string?> CheckPostgresAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var database = new PostgresDatabase(connectionString);
            await using var connection = await database.OpenWithoutMigratingAsync(cancellationToken);
            return null;
        }
        catch (Exception exception)
        {
            return $"Postgres: {FirstLine(exception)}";
        }
    }

    /// <summary>Lee las propiedades de la cuenta: necesita la credencial, no crea nada.</summary>
    public static async Task<string?> CheckAzureStorageAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            await new BlobServiceClient(connectionString).GetPropertiesAsync(cancellationToken);
            return null;
        }
        catch (Exception exception)
        {
            return $"Azure Storage: {FirstLine(exception)}";
        }
    }

    // Los errores de Azure traen los encabezados de la respuesta en varias líneas.
    private static string FirstLine(Exception exception) =>
        exception.GetBaseException().Message.Split('\n')[0].Trim();
}
