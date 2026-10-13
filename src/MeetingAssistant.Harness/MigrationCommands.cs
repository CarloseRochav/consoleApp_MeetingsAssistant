using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Storage.Postgres;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

/// <summary>
/// La migración de una sola vez del historial local a la nube (Fase 6, paso 4).
///
/// <c>--migrate-to-postgres</c> sin más <b>sólo muestra lo que haría</b>. Con
/// <c>--apply</c> lo hace: copia sesiones, transcripts y reportes del SQLite real
/// a Postgres, y sube a Azure Blob el audio y los <c>.md</c>. No borra ni
/// modifica nada local, y se puede volver a correr.
///
/// <c>--verify-migration</c> prueba la misma lógica entre dos SQLite temporales
/// y una nube falsa: gratis y sin red.
/// </summary>
internal static class MigrationCommands
{
    public static async Task<int> MigrateToPostgresAsync(IConfiguration configuration, bool apply)
    {
        string localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingAssistant");
        string sqlitePath = Path.Combine(localRoot, "meetings.db");

        Console.WriteLine(apply
            ? "=== Migracion a Postgres: APLICANDO ==="
            : "=== Migracion a Postgres: simulacion (nada se escribe; agrega --apply para hacerla) ===");

        if (!File.Exists(sqlitePath))
        {
            Console.Error.WriteLine($"No existe la base local: {sqlitePath}");
            return 1;
        }

        string? connectionString = configuration["Postgres:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Falta Postgres:ConnectionString en la configuracion del harness.");
            return 1;
        }

        var sourceFactory = new SqliteConnectionFactory(sqlitePath);
        IMeetingHistoryStore source = new SqliteMeetingHistoryStore(sourceFactory);
        await using var database = new PostgresDatabase(connectionString);
        IMeetingHistoryStore target = new PostgresMeetingHistoryStore(database);

        var archive = new AzureBlobCloudArchive(configuration);
        var uploadStore = new PostgresArchiveUploadStore(database);
        var uploadFailures = new List<string>();
        ArchiveUploadService? uploads = archive.IsConfigured
            ? new ArchiveUploadService(archive, uploadStore, (operation, exception) => uploadFailures.Add($"{operation}: {exception.Message}"))
            : null;

        Console.WriteLine($"Origen:  {sqlitePath}");
        Console.WriteLine($"Destino: {database.Description}");
        Console.WriteLine(archive.IsConfigured
            ? "Archivos: se suben a Azure Blob."
            : "Archivos: AzureStorage:ConnectionString no esta configurado; NO se sube ningun archivo.");

        try
        {
            if (apply) Console.WriteLine($"Esquema de destino: {await database.MigrateAsync()}");

            // En la simulacion el destino puede no tener esquema todavia: se
            // compara contra un destino vacio en vez de crearlo, porque abrir el
            // store lo crearia y la simulacion no escribe nada.
            IMeetingHistoryStore effectiveTarget = apply || await database.HasSchemaAsync() ? target : new EmptyHistoryStore();

            var migration = new HistoryMigrationService(source, effectiveTarget, apply ? uploads : null);
            HistoryMigrationResult result = await migration.RunAsync(apply);

            string verb = apply ? "copiadas" : "por copiar";
            Console.WriteLine($"{Environment.NewLine}--- Historial ---");
            Console.WriteLine($"  Reuniones en el origen:          {result.SourceSessions}");
            Console.WriteLine($"  Reuniones {verb + ":",-22} {result.SessionsCopied}");
            Console.WriteLine($"  Reuniones que ya estaban:        {result.SessionsAlreadyThere}");
            Console.WriteLine($"  Transcripts {verb + ":",-20} {result.TranscriptsCopied}");
            Console.WriteLine($"  Reportes {verb + ":",-23} {result.ReportsCopied}");
            foreach (string problem in result.Problems) Console.WriteLine($"  PROBLEMA: {problem}");

            // --- Archivos que el historial no conoce ---------------------------
            // Audios y reportes de antes de que existiera la base. No tienen
            // sesion a la que colgarse, asi que se suben directo, sin fila.
            var knownFiles = new HashSet<string>(
                result.FilesToArchive.Concat(result.FilesMissing), StringComparer.OrdinalIgnoreCase);

            var reportDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in knownFiles.Where(path => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            {
                if (Path.GetDirectoryName(path) is { } directory) reportDirectories.Add(directory);
            }
            if (configuration["Storage:VaultPath"] is { Length: > 0 } vault)
            {
                reportDirectories.Add(Path.Combine(vault, configuration["Storage:SubFolder"] ?? "Meetings"));
            }

            List<(ArchiveKind Kind, string Path)> orphans = [];
            string audioDirectory = Path.Combine(localRoot, "meeting-output");
            if (Directory.Exists(audioDirectory))
            {
                orphans.AddRange(Directory.EnumerateFiles(audioDirectory, "*.wav")
                    .Where(path => !knownFiles.Contains(path))
                    .Select(path => (ArchiveKind.Audio, path)));
            }
            foreach (string directory in reportDirectories.Where(Directory.Exists))
            {
                orphans.AddRange(Directory.EnumerateFiles(directory, "*.md")
                    .Where(path => !knownFiles.Contains(path))
                    .Select(path => (ArchiveKind.Report, path)));
            }

            long Size(IEnumerable<string> paths) => paths.Sum(path => new FileInfo(path).Length);
            Console.WriteLine($"{Environment.NewLine}--- Archivos ---");
            Console.WriteLine($"  De reuniones del historial:      {result.FilesToArchive.Count} ({Size(result.FilesToArchive) / 1048576.0:F1} MB)");
            Console.WriteLine($"  Que el historial nombra y ya no existen en disco: {result.FilesMissing.Count}");
            foreach (string missing in result.FilesMissing) Console.WriteLine($"      {missing}");
            Console.WriteLine($"  Anteriores al historial (sin reunion asociada): {orphans.Count} ({Size(orphans.Select(o => o.Path)) / 1048576.0:F1} MB)");
            Console.WriteLine($"      audios: {orphans.Count(o => o.Kind == ArchiveKind.Audio)}, reportes: {orphans.Count(o => o.Kind == ArchiveKind.Report)}");
            foreach (string directory in reportDirectories) Console.WriteLine($"      carpeta de reportes revisada: {directory}");

            if (!apply)
            {
                Console.WriteLine($"{Environment.NewLine}Simulacion terminada. Nada se escribio.");
                return result.Problems.Count == 0 ? 0 : 1;
            }

            int failures = result.Problems.Count;

            if (uploads is not null)
            {
                Console.WriteLine($"{Environment.NewLine}Subiendo archivos de las reuniones...");
                await uploads.WhenIdleAsync();

                int orphanFailures = 0;
                Console.WriteLine("Subiendo archivos anteriores al historial...");
                foreach ((ArchiveKind kind, string path) in orphans)
                {
                    try
                    {
                        await archive.UploadAsync(
                            kind, path, ArchiveUploadService.BuildRemoteName(path, File.GetLastWriteTimeUtc(path)));
                    }
                    catch (Exception exception)
                    {
                        orphanFailures++;
                        Console.WriteLine($"  NO se pudo subir {path}: {exception.Message}");
                    }
                }

                IReadOnlyList<ArchiveUpload> unfinished = await uploadStore.ListUnfinishedAsync();
                Console.WriteLine($"  Subidas de reuniones sin terminar: {unfinished.Count}");
                foreach (ArchiveUpload upload in unfinished)
                {
                    Console.WriteLine($"      {upload.State}: {upload.LocalPath} — {upload.LastError}");
                }
                Console.WriteLine($"  Archivos anteriores al historial subidos: {orphans.Count - orphanFailures} de {orphans.Count}");
                failures += unfinished.Count + orphanFailures;
            }

            // --- Comprobacion: todo lo del origen esta en el destino ------------
            Console.WriteLine($"{Environment.NewLine}--- Comprobacion ---");
            IReadOnlyList<string> differences = await migration.VerifyAsync();
            foreach (string difference in differences) Console.WriteLine($"  DIFERENCIA: {difference}");
            failures += differences.Count;

            CostSummary sourceTotals = await source.GetCostSummaryAsync();
            CostSummary targetTotals = await target.GetCostSummaryAsync();
            Console.WriteLine($"  {"",-14} {"origen",-10} destino");
            Console.WriteLine($"  {"reuniones",-14} {sourceTotals.SessionCount,-10} {targetTotals.SessionCount}");
            Console.WriteLine($"  {"transcripts",-14} {sourceTotals.TranscriptCount,-10} {targetTotals.TranscriptCount}");
            Console.WriteLine($"  {"reportes",-14} {sourceTotals.ReportCount,-10} {targetTotals.ReportCount}");
            Console.WriteLine($"  {"costo LLM",-14} {sourceTotals.LlmCostUsd,-10:F6} {targetTotals.LlmCostUsd:F6}");
            Console.WriteLine(differences.Count == 0
                ? "  Cada reunion del origen esta en el destino, con su transcript y sus reportes identicos."
                : $"  {differences.Count} diferencia(s).");

            Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Migracion completa." : $"Migracion con {failures} pendiente(s). Se puede volver a correr.")}");
            return failures == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"La migracion fallo: {exception.Message}");
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    public static async Task<int> VerifyMigrationAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ma-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Console.WriteLine($"=== Autotest de migracion del historial ==={Environment.NewLine}{directory}{Environment.NewLine}");

        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        try
        {
            SqliteConnectionFactory NewDatabase(string name)
            {
                var factory = new SqliteConnectionFactory(Path.Combine(directory, name));
                new SqliteSchemaMigrator(factory).Migrate();
                return factory;
            }

            string NewFile(string name)
            {
                string path = Path.Combine(directory, name);
                File.WriteAllText(path, "contenido");
                return path;
            }

            SqliteConnectionFactory sourceFactory = NewDatabase("origen.db");
            SqliteConnectionFactory targetFactory = NewDatabase("destino.db");
            IMeetingHistoryStore source = new SqliteMeetingHistoryStore(sourceFactory);
            IMeetingHistoryStore target = new SqliteMeetingHistoryStore(targetFactory);

            // --- Un origen con las formas que existen de verdad -----------------
            var day = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
            string audio = NewFile("meeting-1.wav");
            string reportFile = NewFile("reporte-1.md");

            long complete = await source.CreateSessionAsync(day, SessionSource.Hotkey);
            await source.CompleteSessionAsync(complete, day.AddMinutes(30), audio, TimeSpan.FromSeconds(1800.25));
            await source.SaveTranscriptAsync(new TranscriptRecord(complete, "Texto con acentos: sesión.", "Deepgram", null, 0.0052m, day.AddMinutes(31)));
            await source.SaveReportAsync(new NewReport(complete, "assignment-meeting", "v1", "# Uno", "{}", "P", "m", 10, 20, 0.000123m, reportFile, day.AddMinutes(32)));
            await source.SaveReportAsync(new NewReport(complete, "feature-handoff", "v1", "# Dos", null, "P", "m", 30, 40, 0m, Path.Combine(directory, "ya-no-existe.md"), day.AddMinutes(40)));

            // Grabacion que fallo antes de transcribir: sesion cerrada, sin nada mas.
            long failedRecording = await source.CreateSessionAsync(day.AddDays(1), SessionSource.Http);
            await source.CompleteSessionAsync(failedRecording, day.AddDays(1).AddMinutes(1), Path.Combine(directory, "borrado.wav"), TimeSpan.FromSeconds(60));

            // Sesion que nunca se cerro.
            await source.CreateSessionAsync(day.AddDays(2), SessionSource.Tray);

            // Y en el destino, algo que ya estaba: no se puede perder.
            long alreadyInTarget = await target.CreateSessionAsync(day.AddDays(10), SessionSource.Window);
            await target.SaveTranscriptAsync(new TranscriptRecord(alreadyInTarget, "Grabada directo en el destino.", null, null, null, day.AddDays(10)));

            var archive = new FakeCloudArchive();
            var uploads = new ArchiveUploadService(archive, new SqliteArchiveUploadStore(targetFactory), retryDelays: [TimeSpan.Zero]);
            var migration = new HistoryMigrationService(source, target, uploads);

            // --- Simulacion -----------------------------------------------------
            Console.WriteLine("--- Simulacion ---");
            HistoryMigrationResult plan = await migration.RunAsync(apply: false);
            await uploads.WhenIdleAsync();
            Check("cuenta lo que haria",
                plan is { SourceSessions: 3, SessionsCopied: 3, SessionsAlreadyThere: 0, TranscriptsCopied: 1, ReportsCopied: 2 });
            Check("distingue los archivos que existen de los que ya no",
                plan.FilesToArchive.Count == 2 && plan.FilesMissing.Count == 2);
            Check("y no escribe ni sube nada",
                (await target.ListSessionsAsync(10)).Count == 1 && archive.Calls.Count == 0);

            // --- Migracion ------------------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Migracion ---");
            HistoryMigrationResult done = await migration.RunAsync(apply: true);
            await uploads.WhenIdleAsync();

            IReadOnlyList<SessionSummary> migrated = await target.ListSessionsAsync(10);
            Check("copia las tres reuniones sin problemas", done is { SessionsCopied: 3, Problems.Count: 0 } && migrated.Count == 4);
            Check("lo que ya estaba en el destino sigue ahi",
                (await target.GetTranscriptAsync(alreadyInTarget))?.Text == "Grabada directo en el destino.");
            Check("la comprobacion no encuentra diferencias", (await migration.VerifyAsync()).Count == 0);

            SessionSummary copied = migrated.Single(item => item.StartedAtUtc == day);
            SessionRecord? copiedSession = await target.GetSessionAsync(copied.SessionId);
            Check("los ids cambian pero la reunion es la misma: origen, audio, fin y duracion",
                copied.SessionId != complete &&
                copiedSession is { Source: SessionSource.Hotkey } &&
                copiedSession.AudioPath == audio && copiedSession.EndedAtUtc == day.AddMinutes(30) &&
                copiedSession.Duration == TimeSpan.FromSeconds(1800.25));
            Check("el transcript llega con su texto y su costo",
                (await target.GetTranscriptAsync(copied.SessionId)) is { Text: "Texto con acentos: sesión.", CostUsd: 0.0052m });
            Check("los reportes cuelgan de la sesion nueva, con fecha y costo exactos",
                (await target.GetReportsAsync(copied.SessionId)) is [{ PromptId: "assignment-meeting", CostUsd: 0.000123m, StructuredJson: "{}" } first, { PromptId: "feature-handoff", CostUsd: 0m }] &&
                first.CreatedAtUtc == day.AddMinutes(32));
            Check("una sesion que nunca se cerro queda igual de abierta",
                (await target.GetSessionAsync(migrated.Single(item => item.StartedAtUtc == day.AddDays(2)).SessionId)) is { EndedAtUtc: null, AudioPath: null });
            Check("el transcript migrado se puede buscar en el destino",
                (await target.SearchTranscriptsAsync("sesion")) is [{ } hit] && hit.SessionId == copied.SessionId);
            Check("el orden cronologico se conserva en los ids nuevos",
                migrated.OrderBy(item => item.StartedAtUtc).Where(item => item.SessionId != alreadyInTarget)
                    .Select(item => item.SessionId).SequenceEqual(
                        migrated.Where(item => item.SessionId != alreadyInTarget).Select(item => item.SessionId).Order()));

            Check("sube el audio y el reporte que existen, y nada mas",
                archive.Calls.Order().SequenceEqual(new[] { audio, reportFile }.Order()));
            Check("el historial del destino ve el audio como subido", copied.AudioArchiveState == "uploaded");

            CostSummary sourceTotals = await source.GetCostSummaryAsync();
            CostSummary targetTotals = await target.GetCostSummaryAsync();
            Check("los costos del destino son los del origen",
                targetTotals.LlmCostUsd == sourceTotals.LlmCostUsd &&
                targetTotals.TranscriptionCostUsd == sourceTotals.TranscriptionCostUsd &&
                targetTotals.ReportCount == sourceTotals.ReportCount);

            // --- Volver a correr ------------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Volver a correr ---");
            HistoryMigrationResult again = await migration.RunAsync(apply: true);
            await uploads.WhenIdleAsync();
            Check("la segunda corrida no copia nada",
                again is { SessionsCopied: 0, SessionsAlreadyThere: 3, TranscriptsCopied: 0, ReportsCopied: 0 });
            Check("ni duplica filas", (await target.ListSessionsAsync(10)).Count == 4 && (await target.GetCostSummaryAsync()).ReportCount == 2);
            Check("ni vuelve a subir", archive.Calls.Count == 2);

            // Una corrida interrumpida: la sesion llego, su contenido no.
            SqliteConnectionFactory partialFactory = NewDatabase("parcial.db");
            IMeetingHistoryStore partial = new SqliteMeetingHistoryStore(partialFactory);
            await partial.CreateSessionAsync(day, SessionSource.Hotkey);
            var resumed = new HistoryMigrationService(source, partial);
            HistoryMigrationResult completed = await resumed.RunAsync(apply: true);
            Check("una migracion interrumpida se completa: agrega el transcript y los reportes que faltaban",
                completed is { SessionsCopied: 2, SessionsAlreadyThere: 1, TranscriptsCopied: 1, ReportsCopied: 2 } &&
                (await resumed.VerifyAsync()).Count == 0);

            // La comprobacion tiene que poder fallar, o no prueba nada.
            IMeetingHistoryStore emptyTarget = new SqliteMeetingHistoryStore(NewDatabase("vacio.db"));
            Check("contra un destino vacio, la comprobacion SI encuentra diferencias",
                (await new HistoryMigrationService(source, emptyTarget).VerifyAsync()).Count == 3);

            await using (SqliteConnection tamper = targetFactory.Open())
            await using (SqliteCommand command = tamper.CreateCommand())
            {
                command.CommandText = "update report set markdown = '# Alterado' where prompt_id = 'assignment-meeting';";
                await command.ExecuteNonQueryAsync();
            }
            Check("y detecta un reporte cuyo contenido no coincide", (await migration.VerifyAsync()).Count == 1);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"El autotest reviento: {exception}");
            failures++;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }

        Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Destino vacio para la simulacion: no abre ni crea nada.</summary>
    private sealed class EmptyHistoryStore : IMeetingHistoryStore
    {
        public Task<long> CreateSessionAsync(DateTimeOffset startedAtUtc, string source, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CompleteSessionAsync(long sessionId, DateTimeOffset endedAtUtc, string? audioPath, TimeSpan? duration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveTranscriptAsync(TranscriptRecord transcript, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> SaveReportAsync(NewReport report, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit, int offset = 0, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>([]);

        public Task<SessionRecord?> GetSessionAsync(long sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionRecord?>(null);

        public Task<TranscriptRecord?> GetTranscriptAsync(long sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<TranscriptRecord?>(null);

        public Task<IReadOnlyList<ReportRecord>> GetReportsAsync(long sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReportRecord>>([]);

        public Task<IReadOnlyList<TranscriptSearchHit>> SearchTranscriptsAsync(string query, int limit = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TranscriptSearchHit>>([]);

        public Task<CostSummary> GetCostSummaryAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PromptUsageSummary>> GetPromptUsageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PromptUsageSummary>>([]);
    }
}
