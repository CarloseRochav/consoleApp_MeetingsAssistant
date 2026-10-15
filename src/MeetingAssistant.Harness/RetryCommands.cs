using System.Diagnostics;
using System.Net.Sockets;
using MeetingAssistant.Infrastructure.Storage.Postgres;
using Npgsql;

/// <summary>
/// Autotest de los reintentos al abrir conexiones con Postgres. Offline: los
/// errores se fabrican, y la prueba de punta a punta apunta a un puerto local
/// cerrado, que rechaza la conexion al instante.
/// </summary>
internal static class RetryCommands
{
    private static readonly IReadOnlyList<TimeSpan> FastDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)];

    public static async Task<int> VerifyAsync()
    {
        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        Console.WriteLine("=== Reintentos al abrir conexiones con Postgres ===");

        NpgsqlException Dropped() => new("Conexion cortada.", new SocketException((int)SocketError.ConnectionReset));
        NpgsqlException TimedOut() => new("Plazo vencido.", new TimeoutException());

        // --- Que se reintenta --------------------------------------------------
        Console.WriteLine($"{Environment.NewLine}--- Que se reintenta ---");
        Check("una conexion cortada es reintentable", ConnectionRetry.IsRetryable(Dropped()));
        Check("una base que se esta levantando (57P03) es reintentable",
            ConnectionRetry.IsRetryable(new PostgresException("starting up", "FATAL", "FATAL", "57P03")));
        Check("un plazo vencido NO: ya es una caida, repetirlo solo hace esperar",
            !ConnectionRetry.IsRetryable(TimedOut()));
        Check("una clave mal puesta (28P01) NO",
            !ConnectionRetry.IsRetryable(new PostgresException("password authentication failed", "FATAL", "FATAL", "28P01")));
        Check("un error que no es de Npgsql NO", !ConnectionRetry.IsRetryable(new InvalidOperationException()));

        // --- Como se reintenta ------------------------------------------------
        Console.WriteLine($"{Environment.NewLine}--- Como se reintenta ---");
        int calls = 0;
        string result = await ConnectionRetry.ExecuteAsync(
            _ => ++calls < 3 ? throw Dropped() : Task.FromResult("abierta"), FastDelays);
        Check("dos cortes y despues abre: devuelve la conexion al tercer intento", result == "abierta" && calls == 3);

        calls = 0;
        Exception? last = await CaptureAsync(() => ConnectionRetry.ExecuteAsync<string>(
            _ => { calls++; throw Dropped(); }, FastDelays));
        Check("siempre cortada: tres intentos y lanza el ultimo error", calls == 3 && last is NpgsqlException);

        calls = 0;
        last = await CaptureAsync(() => ConnectionRetry.ExecuteAsync<string>(
            _ => { calls++; throw new PostgresException("password authentication failed", "FATAL", "FATAL", "28P01"); }, FastDelays));
        Check("clave mal puesta: un solo intento", calls == 1 && last is PostgresException { SqlState: "28P01" });

        calls = 0;
        last = await CaptureAsync(() => ConnectionRetry.ExecuteAsync<string>(
            _ => { calls++; throw TimedOut(); }, FastDelays));
        Check("plazo vencido: un solo intento", calls == 1);

        calls = 0;
        using (var cancellation = new CancellationTokenSource())
        {
            last = await CaptureAsync(() => ConnectionRetry.ExecuteAsync<string>(
                _ => { calls++; cancellation.Cancel(); throw Dropped(); },
                [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)],
                cancellation.Token));
        }
        Check("cancelado: no espera ni vuelve a intentar", calls == 1 && last is NpgsqlException);

        // --- De punta a punta, contra un puerto cerrado ---------------------------
        Console.WriteLine($"{Environment.NewLine}--- PostgresDatabase contra un puerto local cerrado ---");
        await using (var database = new PostgresDatabase("Host=127.0.0.1;Port=1;Username=nadie;Password=nada;Database=nada"))
        {
            var clock = Stopwatch.StartNew();
            last = await CaptureAsync(() => database.OpenWithoutMigratingAsync());
            clock.Stop();
            double expected = ConnectionRetry.DefaultDelays.Sum(delay => delay.TotalSeconds);
            Check($"rechazada: lanza despues de esperar los reintentos (~{expected:0.#} s; tardo {clock.Elapsed.TotalSeconds:0.0} s)",
                last is NpgsqlException && clock.Elapsed.TotalSeconds >= expected && clock.Elapsed.TotalSeconds < expected + 10);
        }

        Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception exception) { return exception; }
    }
}
