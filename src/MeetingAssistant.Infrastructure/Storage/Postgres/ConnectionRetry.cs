using Npgsql;

namespace MeetingAssistant.Infrastructure.Storage.Postgres;

/// <summary>
/// Reintentos al <b>abrir</b> una conexión con Postgres, y sólo ahí.
///
/// Abrir es lo único que se puede repetir sin riesgo: si falla, no llegó
/// ningún comando a la base. Reintentar un insert sí sería peligroso: si la
/// base lo guardó y se cortó la respuesta, el segundo intento duplica la fila.
///
/// Qué se reintenta: lo que Npgsql marca como transitorio (Neon despertando,
/// una conexión cortada), <b>salvo los plazos vencidos</b>. Un plazo de
/// conexión de 15 s que se vence ya es una caída, no un parpadeo, y repetirlo
/// tres veces sólo haría esperar un minuto para fallar igual. Una clave mal
/// puesta (28P01) no es transitoria y falla de una.
/// </summary>
public static class ConnectionRetry
{
    /// <summary>Esperas entre intentos: tres intentos en total, unos 3 s de margen.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultDelays = [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2)];

    /// <summary>
    /// Incluye los errores del servidor que Npgsql da por transitorios, como
    /// 57P03 ("the database system is starting up", lo que contesta una base
    /// que se está levantando). Al abrir no se mandó nada todavía, así que
    /// también esos son seguros de repetir.
    /// </summary>
    public static bool IsRetryable(Exception exception) =>
        exception is NpgsqlException { IsTransient: true } npgsql &&
        npgsql.InnerException is not TimeoutException;

    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> open,
        IReadOnlyList<TimeSpan> delays,
        CancellationToken cancellationToken = default,
        Func<Exception, bool>? isRetryable = null)
    {
        isRetryable ??= IsRetryable;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await open(cancellationToken);
            }
            catch (Exception exception) when (
                attempt < delays.Count &&
                !cancellationToken.IsCancellationRequested &&
                isRetryable(exception))
            {
                await Task.Delay(delays[attempt], cancellationToken);
            }
        }
    }
}
