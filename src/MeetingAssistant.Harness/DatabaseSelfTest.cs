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

/// <summary>--verify-db-selftest: el esquema y el store de SQLite, sobre una base temporal. Offline.</summary>
internal static class DatabaseSelfTest
{
    /// <summary>
    /// Prueba funcional del esquema sobre una base <b>temporal</b>, que se borra al
    /// terminar. Nunca toca la base real.
    ///
    /// Que las tablas existan no prueba que el esquema funcione. Lo que se ejercita
    /// acá es justo lo que se rompe en silencio: los triggers que mantienen el
    /// índice FTS5, el borrado en cascada de las foreign keys, y el tokenizador con
    /// <c>remove_diacritics</c> — que importa porque el corpus es español mezclado
    /// con inglés y nadie escribe los acentos al buscar.
    /// </summary>
    public static int Run()
    {
        string temporaryPath = Path.Combine(Path.GetTempPath(), $"ma-selftest-{Guid.NewGuid():N}.db");
        Console.WriteLine($"=== Autotest de esquema ==={Environment.NewLine}{temporaryPath}");

        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        try
        {
            var factory = new SqliteConnectionFactory(temporaryPath);
            string migrationResult = new SqliteSchemaMigrator(factory).Migrate();
            Console.WriteLine($"Migracion: {migrationResult}{Environment.NewLine}");

            using Microsoft.Data.Sqlite.SqliteConnection connection = factory.Open();

            long Scalar(string sql)
            {
                using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
            }

            void Execute(string sql)
            {
                using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            Execute(
                """
                insert into session(id, started_at_utc, source) values (1, '2026-08-27T09:00:00Z', 'hotkey');
                insert into transcript(session_id, text, provider, model, cost_micro_usd, created_at_utc)
                    values (1, 'Hablamos de la migracion del stored procedure y la validacion de sesión.',
                            'Deepgram', 'nova-3', 434, '2026-08-27T09:05:00Z');
                insert into report(session_id, prompt_id, prompt_version, markdown, cost_micro_usd, created_at_utc)
                    values (1, 'assignment-meeting', 'v1', '# Reporte', 762, '2026-08-27T09:06:00Z');
                insert into report(session_id, prompt_id, prompt_version, markdown, cost_micro_usd, created_at_utc)
                    values (1, 'feature-handoff', 'v1', '# Handoff', 900, '2026-08-27T09:07:00Z');
                """);

            Check("una sesion admite varios reportes", Scalar("select count(*) from report where session_id = 1;") == 2);
            Check("el trigger de insert alimento el indice FTS5",
                Scalar("select count(*) from transcript_fts where transcript_fts match 'stored';") == 1);
            Check("busqueda sin acento encuentra texto acentuado ('sesion' -> 'sesión')",
                Scalar("select count(*) from transcript_fts where transcript_fts match 'sesion';") == 1);
            Check("busqueda con acento tambien encuentra ('sesión')",
                Scalar("select count(*) from transcript_fts where transcript_fts match 'sesión';") == 1);
            Check("una palabra ausente no da falsos positivos",
                Scalar("select count(*) from transcript_fts where transcript_fts match 'zanahoria';") == 0);

            Execute("update transcript set text = 'Ahora hablamos de facturacion electronica.' where session_id = 1;");
            Check("el trigger de update reindexo (lo viejo ya no aparece)",
                Scalar("select count(*) from transcript_fts where transcript_fts match 'stored';") == 0);
            Check("el trigger de update reindexo (lo nuevo si aparece)",
                Scalar("select count(*) from transcript_fts where transcript_fts match 'facturacion';") == 1);

            Check("el costo entero sobrevive el viaje sin deriva",
                Scalar("select sum(cost_micro_usd) from report;") == 1662L);

            Execute("delete from session where id = 1;");
            Check("borrar la sesion arrastra el transcript (cascade)", Scalar("select count(*) from transcript;") == 0);
            Check("borrar la sesion arrastra los reportes (cascade)", Scalar("select count(*) from report;") == 0);
            Check("el trigger de delete limpio el indice FTS5",
                Scalar("select count(*) from transcript_fts where transcript_fts match 'facturacion';") == 0);

            using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "insert into transcript_fts(transcript_fts) values('integrity-check');";
                try
                {
                    command.ExecuteNonQuery();
                    Check("el indice FTS5 queda consistente al final", true);
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    Check("el indice FTS5 queda consistente al final", false);
                }
            }

            // --- Ahora lo mismo, pero a traves de las interfaces de Core ---------
            // El SQL de arriba prueba el esquema; esto prueba los adaptadores, que
            // es donde viven las conversiones (dinero a micro-dolares, fechas a UTC)
            // y el saneado de la consulta FTS5.
            Console.WriteLine($"{Environment.NewLine}--- Store, via IMeetingHistoryStore ---");

            IMeetingHistoryStore store = new SqliteMeetingHistoryStore(factory);
            var startedAt = new DateTimeOffset(2026, 8, 27, 14, 30, 0, TimeSpan.FromHours(-7));

            long sessionId = store.CreateSessionAsync(startedAt, SessionSource.Hotkey).GetAwaiter().GetResult();
            store.CompleteSessionAsync(sessionId, startedAt.AddMinutes(12), @"C:\audio\x.wav", TimeSpan.FromMinutes(12))
                .GetAwaiter().GetResult();
            store.SaveTranscriptAsync(new TranscriptRecord(
                sessionId, "Revisamos la integracion con el módulo de facturación.",
                "Deepgram", "nova-3", 0.001234m, startedAt.AddMinutes(13))).GetAwaiter().GetResult();
            // Modelo inventado a propósito, igual que en FakeReportExtractor: el test
            // no mira el nombre, y uno real (fue DeepSeek-V4-Flash) quedaba viejo
            // cada vez que se cambiaba el deployment.
            store.SaveReportAsync(new NewReport(
                sessionId, "assignment-meeting", "v1", "# Reporte", null,
                "Azure AI Foundry", "modelo-de-prueba", 890, 520, 0.000434m,
                @"C:\vault\r.md", startedAt.AddMinutes(14))).GetAwaiter().GetResult();

            SessionRecord? roundTripped = store.GetSessionAsync(sessionId).GetAwaiter().GetResult();
            Check("la sesion vuelve con su id", roundTripped?.Id == sessionId);
            // El instante tiene que ser el mismo aunque se haya guardado en UTC y el
            // original viniera con offset -07:00. Es la garantia que hace que
            // ordenar por fecha no dependa del huso de quien grabo.
            Check("el instante sobrevive la ida y vuelta a UTC", roundTripped?.StartedAtUtc == startedAt);
            Check("la duracion sobrevive", roundTripped?.Duration == TimeSpan.FromMinutes(12));

            TranscriptRecord? storedTranscript = store.GetTranscriptAsync(sessionId).GetAwaiter().GetResult();
            Check("el costo decimal del transcript vuelve exacto", storedTranscript?.CostUsd == 0.001234m);

            IReadOnlyList<ReportRecord> storedReports = store.GetReportsAsync(sessionId).GetAwaiter().GetResult();
            Check("el costo decimal del reporte vuelve exacto", storedReports.Count == 1 && storedReports[0].CostUsd == 0.000434m);
            Check("los tokens vuelven", storedReports.Count == 1 && storedReports[0].InputTokens == 890);

            IReadOnlyList<SessionSummary> listed = store.ListSessionsAsync(10).GetAwaiter().GetResult();
            Check("el listado trae la sesion con su conteo de reportes",
                listed.Count == 1 && listed[0].ReportCount == 1);
            Check("el costo de la sesion suma transcripcion y reporte",
                listed.Count == 1 && listed[0].TotalCostUsd == 0.001668m);

            IReadOnlyList<TranscriptSearchHit> hits =
                store.SearchTranscriptsAsync("facturacion").GetAwaiter().GetResult();
            Check("la busqueda por interfaz encuentra sin acentos", hits.Count == 1);
            Check("el resultado trae un fragmento, no solo la fecha",
                hits.Count == 1 && hits[0].Snippet.Length > 0);

            // Lo que mas importa de todo el saneado: escribiendo en una caja de
            // busqueda uno pasa por estados invalidos de sintaxis FTS5, y ninguno
            // puede llegar al usuario como un error de SQL.
            bool survivedGarbage = true;
            foreach (string hostile in new[] { "\"", "AND", "NEAR(", "*", "a OR", "(((", "'" })
            {
                try { store.SearchTranscriptsAsync(hostile).GetAwaiter().GetResult(); }
                catch (Exception) { survivedGarbage = false; }
            }
            Check("una consulta a medias o con operadores sueltos no lanza", survivedGarbage);

            // --- Busqueda con varias sesiones (paso 7) --------------------------
            // Lo de arriba prueba una sola reunion. La caja de busqueda del
            // historial depende de cosas que con una sola fila no se pueden ver:
            // cuales vuelven, en que orden, y que una sesion sin transcript no
            // aparece nunca.
            long deploySession = store.CreateSessionAsync(startedAt.AddDays(1), SessionSource.Tray).GetAwaiter().GetResult();
            store.SaveTranscriptAsync(new TranscriptRecord(
                deploySession, "Hablamos de la migración de la base de datos y del despliegue del viernes.",
                "Deepgram", "nova-3", null, startedAt.AddDays(1))).GetAwaiter().GetResult();

            long billingSession = store.CreateSessionAsync(startedAt.AddDays(2), SessionSource.Http).GetAwaiter().GetResult();
            store.SaveTranscriptAsync(new TranscriptRecord(
                billingSession, "Facturación, facturación y más facturación.",
                "Deepgram", "nova-3", null, startedAt.AddDays(2))).GetAwaiter().GetResult();

            long silentSession = store.CreateSessionAsync(startedAt.AddDays(3), SessionSource.Hotkey).GetAwaiter().GetResult();

            IReadOnlyList<TranscriptSearchHit> billingHits =
                store.SearchTranscriptsAsync("facturacion").GetAwaiter().GetResult();
            Check("con varias sesiones, la busqueda devuelve solo las que mencionan el termino",
                billingHits.Count == 2 &&
                billingHits.Any(hit => hit.SessionId == sessionId) &&
                billingHits.Any(hit => hit.SessionId == billingSession));
            Check("y vienen por relevancia: la que mas lo repite va primero",
                billingHits.Count == 2 && billingHits[0].SessionId == billingSession);

            IReadOnlyList<TranscriptSearchHit> deployHits =
                store.SearchTranscriptsAsync("despliegue").GetAwaiter().GetResult();
            Check("un termino de una sola reunion devuelve esa y ninguna otra",
                deployHits.Count == 1 && deployHits[0].SessionId == deploySession);
            // La interfaz muestra el fragmento tal cual, con los corchetes. Si
            // alguien cambia los marcadores en el store, que se entere aca.
            Check("el fragmento marca el termino entre corchetes",
                deployHits.Count == 1 && deployHits[0].Snippet.Contains("[despliegue]", StringComparison.Ordinal));

            Check("una sesion sin transcript nunca aparece en resultados",
                new[] { "facturacion", "de", "la", "despliegue" }
                    .SelectMany(term => store.SearchTranscriptsAsync(term).GetAwaiter().GetResult())
                    .All(hit => hit.SessionId != silentSession));

            Check("una consulta vacia devuelve lista vacia",
                store.SearchTranscriptsAsync(string.Empty).GetAwaiter().GetResult().Count == 0);
            Check("una consulta de puros espacios devuelve lista vacia",
                store.SearchTranscriptsAsync("   \t ").GetAwaiter().GetResult().Count == 0);
            // Sin consultar: contra una base que no abre, una consulta vacia tiene
            // que volver igual, porque el store corta antes de abrir la conexion.
            var unreachableStore = new SqliteMeetingHistoryStore(
                new SqliteConnectionFactory(@"\\?\Z:\no-existe\jamas\meetings.db"));
            bool emptyQuerySkippedDatabase;
            try
            {
                emptyQuerySkippedDatabase = unreachableStore.SearchTranscriptsAsync("  ").GetAwaiter().GetResult().Count == 0;
            }
            catch (Exception) { emptyQuerySkippedDatabase = false; }
            Check("y lo hace sin tocar la base", emptyQuerySkippedDatabase);

            // El store SI lanza con la base rota: quien no puede lanzar es la
            // pagina. Queda fijado para que nadie asuma que el store se lo traga.
            bool brokenSearchThrows;
            try
            {
                unreachableStore.SearchTranscriptsAsync("facturacion").GetAwaiter().GetResult();
                brokenSearchThrows = false;
            }
            catch (Exception) { brokenSearchThrows = true; }
            Check("con la base rota la busqueda lanza (la pagina es quien lo atrapa)", brokenSearchThrows);

            Check("la busqueda es por prefijo: 'factura' encuentra 'facturación'",
                store.SearchTranscriptsAsync("factura").GetAwaiter().GetResult().Count == 2);
            Check("y solo por prefijo: 'facturacionx' no encuentra nada",
                store.SearchTranscriptsAsync("facturacionx").GetAwaiter().GetResult().Count == 0);
            Check("varias palabras se combinan con AND (las dos en la misma reunion)",
                store.SearchTranscriptsAsync("migracion despliegue").GetAwaiter().GetResult() is [{ } both] &&
                both.SessionId == deploySession);
            Check("AND de palabras de reuniones distintas no devuelve nada",
                store.SearchTranscriptsAsync("migracion facturacion").GetAwaiter().GetResult().Count == 0);
            Check("el limite se respeta",
                store.SearchTranscriptsAsync("facturacion", limit: 1).GetAwaiter().GetResult().Count == 1);

            CostSummary costs = store.GetCostSummaryAsync().GetAwaiter().GetResult();
            Check("el resumen de costo acumulado cuadra", costs.TotalCostUsd == 0.001668m && costs.ReportCount == 1);

            IReadOnlyList<PromptUsageSummary> usage = store.GetPromptUsageAsync().GetAwaiter().GetResult();
            Check("el uso por prompt agrupa por id y version",
                usage.Count == 1 && usage[0].PromptId == "assignment-meeting" && usage[0].ReportCount == 1);

            // --- Analitica con varios reportes (paso 8) -------------------------
            // Las dos comprobaciones de arriba corren sobre UN reporte, y con uno
            // solo no se puede equivocar ninguna agregacion. La pagina de costos
            // muestra dinero: aca se cazan los errores que a ojo siguen pareciendo
            // un numero.
            Console.WriteLine($"{Environment.NewLine}--- Costos y uso por prompt ---");

            NewReport Report(long session, string promptId, string? version, int? tokensIn, int? tokensOut, decimal? cost, int minute) =>
                new(session, promptId, version, "# Reporte", null, "Proveedor", "modelo", tokensIn, tokensOut, cost,
                    null, startedAt.AddDays(4).AddMinutes(minute));

            // Misma version que el reporte que ya existe: tiene que caer en su grupo.
            store.SaveReportAsync(Report(deploySession, "assignment-meeting", "v1", 100, 200, 0.000100m, 1)).GetAwaiter().GetResult();
            // Otra version del mismo prompt: grupo aparte. Uno sin tokens de salida.
            store.SaveReportAsync(Report(deploySession, "assignment-meeting", "v2", 10, null, 0.000001m, 2)).GetAwaiter().GetResult();
            store.SaveReportAsync(Report(billingSession, "assignment-meeting", "v2", 10, 300, 0.000002m, 3)).GetAwaiter().GetResult();
            // El caso del precio faltante: consumio tokens y quedo en cero.
            store.SaveReportAsync(Report(billingSession, "functional-spec", "v1", 500, 400, 0m, 4)).GetAwaiter().GetResult();
            // Costo desconocido de verdad (null), conviviendo con los demas.
            store.SaveReportAsync(Report(billingSession, "functional-spec", "v1", null, null, null, 5)).GetAwaiter().GetResult();
            // Cero sin tokens y sin version: NO es un precio faltante.
            store.SaveReportAsync(Report(sessionId, "feature-handoff", null, null, null, 0m, 6)).GetAwaiter().GetResult();

            CostSummary full = store.GetCostSummaryAsync().GetAwaiter().GetResult();
            Check("cuenta todos los reportes", full.ReportCount == 7);
            // 434 + 100 + 1 + 2 + 0 + 0 micro-dolares. Es el motivo entero de no
            // guardar dinero en punto flotante, probado con varios sumandos.
            Check("la suma de LLM cuadra exacta con varios sumandos", full.LlmCostUsd == 0.000537m);
            Check("la transcripcion va aparte del LLM", full.TranscriptionCostUsd == 0.001234m);
            Check("el total es LLM mas transcripcion", full.TotalCostUsd == 0.001771m);
            Check("un costo null no vuelve null el total ni se cuenta callado: se informa aparte",
                full.ReportsWithoutCostCount == 1);
            Check("detecta el reporte con tokens y costo 0 (precio faltante)", full.ReportsMissingPriceCount == 1);
            Check("cuenta las transcripciones sin costo calculado",
                full.TranscriptCount == 3 && full.TranscriptsWithoutCostCount == 2);
            Check("distingue reuniones con reporte del total de sesiones",
                full.SessionCount == 4 && full.SessionsWithReportsCount == 3);

            IReadOnlyList<PromptUsageSummary> groups = store.GetPromptUsageAsync().GetAwaiter().GetResult();
            PromptUsageSummary? Group(string promptId, string? version) =>
                groups.SingleOrDefault(group => group.PromptId == promptId && group.PromptVersion == version);

            Check("agrupa por prompt y version sin colapsar dos versiones en una", groups.Count == 4);
            Check("la v1 junta sus dos reportes y suma exacto",
                Group("assignment-meeting", "v1") is { ReportCount: 2, TotalCostUsd: 0.000534m, AverageOutputTokens: 360 });
            Check("el promedio de tokens ignora el null en vez de contarlo como cero",
                Group("assignment-meeting", "v2") is { ReportCount: 2, TotalCostUsd: 0.000003m, AverageOutputTokens: 300 });
            Check("el precio faltante se ve en su grupo",
                Group("functional-spec", "v1") is { ReportCount: 2, TotalCostUsd: 0m, ReportsMissingPriceCount: 1 });
            Check("un prompt sin version forma su grupo, y un cero sin tokens no es precio faltante",
                Group("feature-handoff", null) is { ReportCount: 1, ReportsMissingPriceCount: 0 });
            Check("la tabla por prompt suma lo mismo que la linea de LLM",
                groups.Sum(group => group.TotalCostUsd) == full.LlmCostUsd);

            // Una base recien creada: ceros y fechas null, sin lanzar y sin dividir.
            string emptyPath = Path.Combine(Path.GetTempPath(), $"ma-selftest-vacia-{Guid.NewGuid():N}.db");
            try
            {
                var emptyFactory = new SqliteConnectionFactory(emptyPath);
                new SqliteSchemaMigrator(emptyFactory).Migrate();
                IMeetingHistoryStore emptyStore = new SqliteMeetingHistoryStore(emptyFactory);

                CostSummary empty = emptyStore.GetCostSummaryAsync().GetAwaiter().GetResult();
                Check("una base sin reportes devuelve ceros y fechas null, sin lanzar",
                    empty is
                    {
                        SessionCount: 0, ReportCount: 0, TranscriptCount: 0, TotalCostUsd: 0m,
                        ReportsMissingPriceCount: 0, FirstReportUtc: null, LastReportUtc: null
                    });
                Check("y el uso por prompt viene vacio",
                    emptyStore.GetPromptUsageAsync().GetAwaiter().GetResult().Count == 0);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (string leftover in new[] { emptyPath, emptyPath + "-wal", emptyPath + "-shm" })
                {
                    try { if (File.Exists(leftover)) File.Delete(leftover); } catch { /* best effort */ }
                }
            }

            // El precio de transcripcion. Los valores de aca son de prueba, elegidos
            // para que la cuenta sea obvia: no son el precio de ningun proveedor.
            ITranscriptionCostEstimator Estimator(params (string Key, string Value)[] settings) =>
                new ConfigPricingCostEstimator(new ConfigurationBuilder()
                    .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
                    .Build());

            Check("sin precio de transcripcion el costo es desconocido (null), no cero",
                Estimator().EstimateTranscriptionCostUsd("Deepgram", TimeSpan.FromMinutes(10)) is null);
            Check("el 0 del archivo de ejemplo tambien cuenta como sin precio",
                Estimator(("Pricing:Deepgram:PerMinute", "0"))
                    .EstimateTranscriptionCostUsd("Deepgram", TimeSpan.FromMinutes(10)) is null);
            Check("con precio, el costo es minutos por precio",
                Estimator(("Pricing:Deepgram:PerMinute", "0.5"))
                    .EstimateTranscriptionCostUsd("Deepgram", TimeSpan.FromSeconds(90)) == 0.75m);

            // --- Ajustes y cifrado ---------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Ajustes, via ISettingsStore ---");

            ISettingsStore settings = new SqliteSettingsStore(factory, new DpapiSecretProtector());
            settings.SetAsync("Storage:VaultPath", @"C:\vault").GetAwaiter().GetResult();
            settings.SetAsync("Deepgram:ApiKey", "clave-secreta-de-prueba", isSecret: true).GetAwaiter().GetResult();

            Check("un ajuste normal vuelve tal cual",
                settings.GetAsync("Storage:VaultPath").GetAwaiter().GetResult() == @"C:\vault");
            Check("un secreto vuelve descifrado",
                settings.GetAsync("Deepgram:ApiKey").GetAwaiter().GetResult() == "clave-secreta-de-prueba");

            // La comprobacion que hace que valga la pena: en disco NO puede estar en
            // claro. Sin esto, mover las claves a la base no habria mejorado nada.
            using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "select value from setting where key = 'Deepgram:ApiKey';";
                string onDisk = command.ExecuteScalar()?.ToString() ?? string.Empty;
                Check("el secreto esta cifrado en disco, no en claro",
                    onDisk.Length > 0 && !onDisk.Contains("clave-secreta-de-prueba", StringComparison.Ordinal));
            }

            settings.SetAsync("Deepgram:ApiKey", "   ").GetAwaiter().GetResult();
            Check("guardar vacio borra el override (se vuelve al valor empaquetado)",
                settings.GetAsync("Deepgram:ApiKey").GetAwaiter().GetResult() is null);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"El autotest reviento: {exception}");
            failures++;
        }
        finally
        {
            // El pool mantiene el archivo abierto; sin esto el borrado falla en
            // Windows y quedan bases de prueba tiradas en %TEMP%.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (string leftover in new[] { temporaryPath, temporaryPath + "-wal", temporaryPath + "-shm" })
            {
                try { if (File.Exists(leftover)) File.Delete(leftover); } catch { /* best effort */ }
            }
        }

        Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
        return failures == 0 ? 0 : 1;
    }

}
