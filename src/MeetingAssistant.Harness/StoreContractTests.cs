using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Infrastructure.Storage.Postgres;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

/// <summary>
/// El contrato de los stores, escrito una vez y corrido contra los dos
/// proveedores (<c>--verify-postgres</c>).
///
/// Es lo que da derecho a decir que Postgres "hace lo mismo" que SQLite: no que
/// cada uno pasa sus propios tests, sino que <b>los dos pasan los mismos</b>. La
/// búsqueda es donde más importa, porque FTS5 y <c>tsvector</c> son motores
/// distintos y la interfaz no sabe cuál tiene debajo.
///
/// Contra Postgres corre en un <b>esquema propio y descartable</b> de la base
/// configurada, que se borra al terminar. Los datos reales no se tocan.
/// </summary>
internal static class StoreContractTests
{
    public static async Task<int> VerifyPostgresAsync(IConfiguration configuration)
    {
        int failures = 0;

        // --- SQLite primero: es la referencia, y corre sin red ---------------
        string sqlitePath = Path.Combine(Path.GetTempPath(), $"ma-contract-{Guid.NewGuid():N}.db");
        Console.WriteLine($"=== Contrato de los stores: SQLite ==={Environment.NewLine}{sqlitePath}");
        try
        {
            var factory = new SqliteConnectionFactory(sqlitePath);
            new SqliteSchemaMigrator(factory).Migrate();
            var unreachable = new SqliteConnectionFactory(@"\\?\Z:\no-existe\jamas\meetings.db");

            failures += await RunAsync(
                new SqliteMeetingHistoryStore(factory),
                new SqliteArchiveUploadStore(factory),
                new SqliteMeetingHistoryStore(unreachable));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string leftover in new[] { sqlitePath, sqlitePath + "-wal", sqlitePath + "-shm" })
            {
                try { if (File.Exists(leftover)) File.Delete(leftover); } catch { /* best effort */ }
            }
        }

        // --- Postgres ---------------------------------------------------------
        string? connectionString = configuration["Postgres:ConnectionString"];
        Console.WriteLine($"{Environment.NewLine}=== Contrato de los stores: Postgres ===");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Falta Postgres:ConnectionString en la configuracion del harness. Postgres NO se probo.");
            return 2;
        }

        string schema = $"ma_selftest_{Guid.NewGuid():N}";
        await using var database = new PostgresDatabase(connectionString, schema);
        Console.WriteLine($"{database.Description}");
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            string migration = await database.MigrateAsync();
            Console.WriteLine($"Migracion: {migration} ({stopwatch.Elapsed.TotalSeconds:F1} s, incluye despertar la base)");
            Console.WriteLine($"  [{(await database.MigrateAsync() is var again && again.Contains("sin cambios") ? "OK " : "MAL")}] migrar dos veces no hace nada la segunda");

            // Puerto 1 de la propia maquina: rechaza la conexion al instante.
            await using var unreachable = new PostgresDatabase("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x");

            failures += await RunAsync(
                new PostgresMeetingHistoryStore(database),
                new PostgresArchiveUploadStore(database, "maquina-a"),
                new PostgresMeetingHistoryStore(unreachable));

            failures += await RunSharedDatabaseChecksAsync(database);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"El contrato contra Postgres reviento: {exception}");
            failures++;
        }
        finally
        {
            try
            {
                await database.DropSchemaAsync();
                Console.WriteLine($"{Environment.NewLine}Esquema de prueba {schema} borrado.");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"NO se pudo borrar el esquema de prueba {schema}: {exception.Message}");
                failures++;
            }
        }

        Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Lo que sólo tiene sentido cuando varias máquinas comparten la base.</summary>
    private static async Task<int> RunSharedDatabaseChecksAsync(PostgresDatabase database)
    {
        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        Console.WriteLine($"{Environment.NewLine}--- Base compartida entre maquinas ---");
        var history = new PostgresMeetingHistoryStore(database);
        var machineA = new PostgresArchiveUploadStore(database, "maquina-a");
        var machineB = new PostgresArchiveUploadStore(database, "maquina-b");

        long session = await history.CreateSessionAsync(DateTimeOffset.UtcNow, SessionSource.Hotkey);
        const string samePath = @"C:\audio\misma-ruta.wav";
        ArchiveUpload fromA = await machineA.EnqueueAsync(session, ArchiveKind.Audio, samePath, "2026/10/a.wav");
        ArchiveUpload fromB = await machineB.EnqueueAsync(session, ArchiveKind.Audio, samePath, "2026/10/b.wav");

        Check("la misma ruta local en dos maquinas son dos subidas distintas", fromA.Id != fromB.Id);
        Check("cada maquina solo retoma sus propias subidas pendientes",
            (await machineA.ListUnfinishedAsync()).All(upload => upload.Id != fromB.Id) &&
            (await machineB.ListUnfinishedAsync()).Single().Id == fromB.Id);

        return failures;
    }

    private static async Task<int> RunAsync(
        IMeetingHistoryStore store,
        IArchiveUploadStore uploads,
        IMeetingHistoryStore unreachableStore)
    {
        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        try
        {
            // --- Base vacia ----------------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Base vacia ---");
            CostSummary empty = await store.GetCostSummaryAsync();
            Check("sin datos, el resumen de costos son ceros y fechas null",
                empty is { SessionCount: 0, ReportCount: 0, TranscriptCount: 0, TotalCostUsd: 0m, FirstReportUtc: null, LastReportUtc: null });
            Check("y el uso por prompt viene vacio", (await store.GetPromptUsageAsync()).Count == 0);
            Check("el listado viene vacio", (await store.ListSessionsAsync(10)).Count == 0);
            Check("una sesion que no existe es null", await store.GetSessionAsync(987654) is null);

            // --- Ida y vuelta ---------------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Ida y vuelta ---");
            var startedAt = new DateTimeOffset(2026, 8, 27, 14, 30, 0, TimeSpan.FromHours(-7));

            long sessionId = await store.CreateSessionAsync(startedAt, SessionSource.Hotkey);
            await store.CompleteSessionAsync(sessionId, startedAt.AddMinutes(12), @"C:\audio\x.wav", TimeSpan.FromMinutes(12));
            await store.SaveTranscriptAsync(new TranscriptRecord(
                sessionId, "Revisamos la integracion con el módulo de facturación.",
                "Deepgram", "nova-3", 0.001234m, startedAt.AddMinutes(13)));
            await store.SaveReportAsync(new NewReport(
                sessionId, "assignment-meeting", "v1", "# Reporte", "{\"a\":1}",
                "Proveedor", "modelo", 890, 520, 0.000434m, @"C:\vault\r.md", startedAt.AddMinutes(14)));

            SessionRecord? session = await store.GetSessionAsync(sessionId);
            Check("la sesion vuelve con su id y su origen", session is not null && session.Id == sessionId && session.Source == SessionSource.Hotkey);
            Check("el instante sobrevive la ida y vuelta a UTC", session?.StartedAtUtc == startedAt);
            Check("la duracion y la ruta del audio sobreviven",
                session?.Duration == TimeSpan.FromMinutes(12) && session?.AudioPath == @"C:\audio\x.wav");

            TranscriptRecord? transcript = await store.GetTranscriptAsync(sessionId);
            Check("el transcript vuelve con sus acentos", transcript?.Text == "Revisamos la integracion con el módulo de facturación.");
            Check("el costo decimal del transcript vuelve exacto", transcript?.CostUsd == 0.001234m);

            IReadOnlyList<ReportRecord> reports = await store.GetReportsAsync(sessionId);
            Check("el reporte vuelve con costo, tokens, json y ruta",
                reports is [{ CostUsd: 0.000434m, InputTokens: 890, OutputTokens: 520, StructuredJson: "{\"a\":1}", VaultPath: @"C:\vault\r.md", PromptVersion: "v1" }]);

            IReadOnlyList<SessionSummary> listed = await store.ListSessionsAsync(10);
            Check("el listado trae la sesion con conteo, costo sumado y previsualizacion",
                listed is [{ ReportCount: 1, TotalCostUsd: 0.001668m, AudioArchiveState: null }] &&
                listed[0].TranscriptPreview!.StartsWith("Revisamos", StringComparison.Ordinal));

            await store.SaveTranscriptAsync(new TranscriptRecord(
                sessionId, "Revisamos la integracion con el módulo de facturación. Y la sesión de login.",
                "Deepgram", "nova-3", 0.001234m, startedAt.AddMinutes(13)));
            Check("guardar otra vez el transcript lo reemplaza, no lo duplica",
                (await store.GetTranscriptAsync(sessionId))!.Text.EndsWith("login.", StringComparison.Ordinal) &&
                (await store.GetCostSummaryAsync()).TranscriptCount == 1);

            // --- Busqueda -------------------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Busqueda ---");
            long deploySession = await store.CreateSessionAsync(startedAt.AddDays(1), SessionSource.Tray);
            await store.SaveTranscriptAsync(new TranscriptRecord(
                deploySession, "Hablamos de la migración de la base de datos y del despliegue del viernes.",
                "Deepgram", "nova-3", null, startedAt.AddDays(1)));
            long billingSession = await store.CreateSessionAsync(startedAt.AddDays(2), SessionSource.Http);
            await store.SaveTranscriptAsync(new TranscriptRecord(
                billingSession, "Facturación, facturación y más facturación.",
                "Deepgram", "nova-3", null, startedAt.AddDays(2)));
            long silentSession = await store.CreateSessionAsync(startedAt.AddDays(3), SessionSource.Hotkey);

            IReadOnlyList<TranscriptSearchHit> billing = await store.SearchTranscriptsAsync("facturacion");
            Check("sin acento encuentra texto acentuado, solo en las reuniones que lo mencionan",
                billing.Count == 2 && billing.All(hit => hit.SessionId == sessionId || hit.SessionId == billingSession));
            Check("por relevancia: la que mas lo repite va primero",
                billing.Count == 2 && billing[0].SessionId == billingSession);
            Check("con acento encuentra lo mismo", (await store.SearchTranscriptsAsync("facturación")).Count == 2);
            Check("y al reves: con acento encuentra texto sin acento ('integración' -> 'integracion')",
                (await store.SearchTranscriptsAsync("integración")) is [{ } accented] && accented.SessionId == sessionId);
            Check("el texto reemplazado quedo indexado ('sesion' -> 'sesión')",
                (await store.SearchTranscriptsAsync("sesion")) is [{ } replaced] && replaced.SessionId == sessionId);

            IReadOnlyList<TranscriptSearchHit> deploy = await store.SearchTranscriptsAsync("despliegue");
            Check("un termino de una sola reunion devuelve esa y ninguna otra",
                deploy is [{ } only] && only.SessionId == deploySession);
            Check("el fragmento marca el termino entre corchetes",
                deploy.Count == 1 && deploy[0].Snippet.Contains("[despliegue]", StringComparison.Ordinal));
            Check("el fragmento marca tambien cuando se busco sin el acento",
                (await store.SearchTranscriptsAsync("migracion")) is [{ } marked] &&
                marked.Snippet.Contains("[migración]", StringComparison.Ordinal));

            Check("una palabra ausente no da falsos positivos", (await store.SearchTranscriptsAsync("zanahoria")).Count == 0);
            Check("por prefijo: 'factura' encuentra 'facturación'", (await store.SearchTranscriptsAsync("factura")).Count == 2);
            Check("solo por prefijo: 'facturacionx' no encuentra nada", (await store.SearchTranscriptsAsync("facturacionx")).Count == 0);
            Check("no distingue mayusculas", (await store.SearchTranscriptsAsync("DESPLIEGUE")).Count == 1);
            Check("varias palabras se combinan con AND",
                (await store.SearchTranscriptsAsync("migracion despliegue")) is [{ } both] && both.SessionId == deploySession);
            Check("AND de palabras de reuniones distintas no devuelve nada",
                (await store.SearchTranscriptsAsync("migracion facturacion")).Count == 0);
            Check("el limite se respeta", (await store.SearchTranscriptsAsync("facturacion", limit: 1)).Count == 1);

            bool silentNeverAppears = true;
            foreach (string term in new[] { "facturacion", "de", "la", "despliegue" })
            {
                silentNeverAppears &= (await store.SearchTranscriptsAsync(term)).All(hit => hit.SessionId != silentSession);
            }
            Check("una sesion sin transcript nunca aparece", silentNeverAppears);

            // Escribiendo en la caja se pasa por estados que son sintaxis a
            // medias del motor de busqueda. Ninguno puede lanzar.
            bool survivedGarbage = true;
            foreach (string hostile in new[] { "\"", "AND", "NEAR(", "*", "a OR", "(((", "'", "a & b", "!x", "a:*", "<->", "\\", "a | b", "ñ'; drop table session; --" })
            {
                try { await store.SearchTranscriptsAsync(hostile); }
                catch (Exception) { survivedGarbage = false; }
            }
            Check("una consulta a medias o con operadores sueltos no lanza", survivedGarbage);
            Check("y la tabla sigue ahi despues de la entrada hostil", (await store.ListSessionsAsync(10)).Count == 4);

            Check("una consulta vacia o de espacios devuelve lista vacia",
                (await store.SearchTranscriptsAsync(string.Empty)).Count == 0 &&
                (await store.SearchTranscriptsAsync("   \t ")).Count == 0);

            bool emptySkipsDatabase;
            try { emptySkipsDatabase = (await unreachableStore.SearchTranscriptsAsync("  ")).Count == 0; }
            catch (Exception) { emptySkipsDatabase = false; }
            Check("y lo hace sin tocar la base", emptySkipsDatabase);

            bool brokenThrows;
            try { await unreachableStore.SearchTranscriptsAsync("facturacion"); brokenThrows = false; }
            catch (Exception) { brokenThrows = true; }
            Check("con la base inalcanzable la busqueda lanza (quien lo atrapa es la pagina)", brokenThrows);

            // --- Costos y uso por prompt -----------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Costos y uso por prompt ---");
            NewReport Report(long forSession, string promptId, string? version, int? tokensIn, int? tokensOut, decimal? cost, int minute) =>
                new(forSession, promptId, version, "# Reporte", null, "Proveedor", "modelo", tokensIn, tokensOut, cost,
                    null, startedAt.AddDays(4).AddMinutes(minute));

            await store.SaveReportAsync(Report(deploySession, "assignment-meeting", "v1", 100, 200, 0.000100m, 1));
            await store.SaveReportAsync(Report(deploySession, "assignment-meeting", "v2", 10, null, 0.000001m, 2));
            await store.SaveReportAsync(Report(billingSession, "assignment-meeting", "v2", 10, 300, 0.000002m, 3));
            await store.SaveReportAsync(Report(billingSession, "functional-spec", "v1", 500, 400, 0m, 4));
            await store.SaveReportAsync(Report(billingSession, "functional-spec", "v1", null, null, null, 5));
            await store.SaveReportAsync(Report(sessionId, "feature-handoff", null, null, null, 0m, 6));

            CostSummary full = await store.GetCostSummaryAsync();
            Check("cuenta todos los reportes", full.ReportCount == 7);
            Check("la suma de LLM cuadra exacta con varios sumandos", full.LlmCostUsd == 0.000537m);
            Check("la transcripcion va aparte, y el total es la suma",
                full.TranscriptionCostUsd == 0.001234m && full.TotalCostUsd == 0.001771m);
            Check("informa aparte el costo null y el precio faltante",
                full.ReportsWithoutCostCount == 1 && full.ReportsMissingPriceCount == 1);
            Check("cuenta transcripciones sin costo, y reuniones con reporte",
                full is { TranscriptCount: 3, TranscriptsWithoutCostCount: 2, SessionCount: 4, SessionsWithReportsCount: 3 });
            Check("las fechas del primer y ultimo reporte",
                full.FirstReportUtc == startedAt.AddMinutes(14) && full.LastReportUtc == startedAt.AddDays(4).AddMinutes(6));

            IReadOnlyList<PromptUsageSummary> groups = await store.GetPromptUsageAsync();
            PromptUsageSummary? Group(string promptId, string? version) =>
                groups.SingleOrDefault(group => group.PromptId == promptId && group.PromptVersion == version);

            Check("agrupa por prompt y version sin colapsar dos versiones", groups.Count == 4);
            Check("la v1 junta sus dos reportes y suma exacto",
                Group("assignment-meeting", "v1") is { ReportCount: 2, TotalCostUsd: 0.000534m, AverageOutputTokens: 360 });
            Check("el promedio de tokens ignora el null",
                Group("assignment-meeting", "v2") is { ReportCount: 2, TotalCostUsd: 0.000003m, AverageOutputTokens: 300 });
            Check("el precio faltante se ve en su grupo",
                Group("functional-spec", "v1") is { ReportCount: 2, TotalCostUsd: 0m, ReportsMissingPriceCount: 1 });
            Check("un prompt sin version forma su grupo",
                Group("feature-handoff", null) is { ReportCount: 1, ReportsMissingPriceCount: 0 });
            Check("la tabla por prompt suma lo mismo que la linea de LLM",
                groups.Sum(group => group.TotalCostUsd) == full.LlmCostUsd);

            IReadOnlyList<ReportRecord> ordered = await store.GetReportsAsync(billingSession);
            Check("los reportes de una reunion vienen en orden de creacion",
                ordered.Select(report => report.PromptId).SequenceEqual(["assignment-meeting", "functional-spec", "functional-spec"]));

            IReadOnlyList<SessionSummary> page = await store.ListSessionsAsync(2, offset: 1);
            Check("el listado va de la mas nueva a la mas vieja, y pagina",
                page.Select(item => item.SessionId).SequenceEqual([billingSession, deploySession]));

            // --- Cola de subidas -------------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Cola de subidas ---");
            ArchiveUpload queued = await uploads.EnqueueAsync(sessionId, ArchiveKind.Audio, @"C:\audio\x.wav", "2026/08/x.wav");
            Check("encolar deja la subida pendiente", queued is { State: ArchiveUploadState.Pending, Attempts: 0, Kind: ArchiveKind.Audio });
            Check("el historial la ve pendiente",
                (await store.ListSessionsAsync(10)).Single(item => item.SessionId == sessionId).AudioArchiveState == "pending");

            ArchiveUpload again = await uploads.EnqueueAsync(sessionId, ArchiveKind.Audio, @"C:\audio\x.wav", "otro/nombre.wav");
            Check("encolar de nuevo el mismo archivo devuelve la misma fila, con su nombre remoto original",
                again.Id == queued.Id && again.RemoteName == "2026/08/x.wav");

            ArchiveUpload reportUpload = await uploads.EnqueueAsync(sessionId, ArchiveKind.Report, @"C:\audio\x.wav", "2026/08/x.md");
            Check("la misma ruta con otro tipo es otra subida", reportUpload.Id != queued.Id && reportUpload.Kind == ArchiveKind.Report);

            await uploads.MarkFailedAsync(queued.Id, "sin red");
            ArchiveUpload failed = (await uploads.ListUnfinishedAsync()).Single(upload => upload.Id == queued.Id);
            Check("un fallo suma un intento y guarda el error",
                failed is { State: ArchiveUploadState.Failed, Attempts: 1, LastError: "sin red" });

            await uploads.MarkUploadedAsync(queued.Id, "https://ejemplo/x.wav");
            Check("al subirse sale de lo pendiente", (await uploads.ListUnfinishedAsync()).All(upload => upload.Id != queued.Id));
            ArchiveUpload done = await uploads.EnqueueAsync(sessionId, ArchiveKind.Audio, @"C:\audio\x.wav", "x");
            Check("queda subida, con su URL y sin el error viejo",
                done is { State: ArchiveUploadState.Uploaded, Attempts: 2, RemoteUrl: "https://ejemplo/x.wav", LastError: null });
            Check("el historial la ve subida",
                (await store.ListSessionsAsync(10)).Single(item => item.SessionId == sessionId).AudioArchiveState == "uploaded");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"El contrato reviento: {exception}");
            failures++;
        }

        return failures;
    }
}
