using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Autotests de la subida a la nube (Fase 6, paso 1).
///
/// <c>--verify-archive</c> corre con un archivo en la nube falso y una base
/// temporal: gratis, sin red, y capaz de provocar los fallos que con Azure de
/// verdad no se pueden pedir a demanda. <c>--verify-blob-upload</c> es el
/// complemento: una subida real, chica, contra la cuenta configurada.
/// </summary>
internal static class ArchiveSelfTests
{
    public static async Task<int> VerifyArchiveAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ma-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "meetings.db");
        Console.WriteLine($"=== Autotest de subida a la nube ==={Environment.NewLine}{directory}{Environment.NewLine}");

        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        string NewFile(string name)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllText(path, "contenido de prueba");
            return path;
        }

        try
        {
            var factory = new SqliteConnectionFactory(databasePath);
            new SqliteSchemaMigrator(factory).Migrate();
            IMeetingHistoryStore history = new SqliteMeetingHistoryStore(factory);
            IArchiveUploadStore uploads = new SqliteArchiveUploadStore(factory);
            TimeSpan[] noWait = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

            async Task<ArchiveUpload?> RowFor(string localPath) =>
                (await AllRowsAsync(factory)).SingleOrDefault(row => row.LocalPath == localPath);

            long sessionId = await history.CreateSessionAsync(DateTimeOffset.UtcNow, SessionSource.Hotkey);

            // --- El camino feliz ------------------------------------------------
            Console.WriteLine("--- Subida normal ---");
            var archive = new FakeCloudArchive();
            var failed = new List<ArchiveUploadFailedEventArgs>();
            var service = new ArchiveUploadService(archive, uploads, retryDelays: noWait);
            service.UploadFailed += (_, e) => failed.Add(e);

            string audio = NewFile("meeting-ok.wav");
            service.Enqueue(sessionId, ArchiveKind.Audio, audio);
            await service.WhenIdleAsync();

            ArchiveUpload? okRow = await RowFor(audio);
            Check("la subida queda registrada como subida", okRow?.State == ArchiveUploadState.Uploaded);
            Check("con la URL remota", okRow?.RemoteUrl == $"fake://audio/{okRow?.RemoteName}");
            Check("el nombre remoto lleva prefijo de anio y mes y el nombre del archivo",
                okRow is not null &&
                System.Text.RegularExpressions.Regex.IsMatch(okRow.RemoteName, @"^\d{4}/\d{2}/meeting-ok\.wav$"));
            Check("en un solo intento, sin aviso", okRow?.Attempts == 1 && failed.Count == 0);

            IReadOnlyList<SessionSummary> listed = await history.ListSessionsAsync(10);
            Check("el historial ve el audio como subido", listed.Single().AudioArchiveState == "uploaded");

            service.Enqueue(sessionId, ArchiveKind.Audio, audio);
            await service.WhenIdleAsync();
            Check("encolar dos veces el mismo archivo no lo sube dos veces ni duplica la fila",
                archive.Calls.Count(call => call == audio) == 1 && (await AllRowsAsync(factory)).Count == 1);

            // --- Falla un par de veces y despues sale ----------------------------
            Console.WriteLine($"{Environment.NewLine}--- Reintentos ---");
            archive.FailuresBeforeSuccess = 2;
            string flaky = NewFile("meeting-flaky.wav");
            service.Enqueue(sessionId, ArchiveKind.Audio, flaky);
            await service.WhenIdleAsync();

            ArchiveUpload? flakyRow = await RowFor(flaky);
            Check("tras dos fallos, el tercer intento la sube",
                flakyRow is { State: ArchiveUploadState.Uploaded, Attempts: 3 });
            Check("y no avisa: un fallo que se recupera solo no es noticia", failed.Count == 0);
            Check("el error de los intentos fallidos no queda pegado a una subida que salio bien",
                flakyRow?.LastError is null);

            // --- Falla siempre ---------------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Fallo definitivo ---");
            archive.FailuresBeforeSuccess = int.MaxValue;
            string doomed = NewFile("meeting-doomed.wav");
            service.Enqueue(sessionId, ArchiveKind.Audio, doomed);
            await service.WhenIdleAsync();

            ArchiveUpload? doomedRow = await RowFor(doomed);
            Check("agotados los intentos queda como fallida, con los cuatro contados",
                doomedRow is { State: ArchiveUploadState.Failed, Attempts: 4 });
            Check("guarda el error del ultimo intento", doomedRow?.LastError == "fallo simulado");
            Check("y avisa una sola vez", failed.Count == 1 && failed[0].LocalPath == doomed);
            Check("el archivo local sigue en su lugar", File.Exists(doomed));

            // --- Se retoma al arrancar -------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Retomar al arrancar ---");
            archive.FailuresBeforeSuccess = 0;
            var restarted = new ArchiveUploadService(archive, uploads, retryDelays: noWait);
            var restartedFailures = new List<ArchiveUploadFailedEventArgs>();
            restarted.UploadFailed += (_, e) => restartedFailures.Add(e);
            await restarted.ResumeUnfinishedAsync();

            Check("una instancia nueva retoma la subida fallida y la completa",
                (await RowFor(doomed))?.State == ArchiveUploadState.Uploaded);
            Check("no queda nada sin terminar", (await uploads.ListUnfinishedAsync()).Count == 0);

            string vanished = NewFile("meeting-vanished.wav");
            await uploads.EnqueueAsync(sessionId, ArchiveKind.Audio, vanished, "2026/10/meeting-vanished.wav");
            File.Delete(vanished);
            await restarted.ResumeUnfinishedAsync();
            ArchiveUpload? vanishedRow = await RowFor(vanished);
            Check("un archivo local que ya no existe queda como fallido, sin reintentos inutiles",
                vanishedRow is { State: ArchiveUploadState.Failed, Attempts: 1 });
            Check("retomar no avisa (si no, una credencial mala seria un toast por archivo en cada arranque)",
                restartedFailures.Count == 0);

            // --- Apagado -----------------------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Sin configurar ---");
            int rowsBefore = (await AllRowsAsync(factory)).Count;
            var off = new ArchiveUploadService(new FakeCloudArchive { IsConfigured = false }, uploads, retryDelays: noWait);
            off.Enqueue(sessionId, ArchiveKind.Audio, NewFile("meeting-off.wav"));
            await off.WhenIdleAsync();
            await off.ResumeUnfinishedAsync();
            Check("sin credencial no se encola ni se sube nada", (await AllRowsAsync(factory)).Count == rowsBefore);
            Check("el adaptador de Azure se declara apagado sin cadena de conexion",
                !new AzureBlobCloudArchive(new ConfigurationBuilder().Build()).IsConfigured);

            // --- La base rota no se propaga ---------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Base rota ---");
            var logged = new List<string>();
            var broken = new ArchiveUploadService(
                archive,
                new SqliteArchiveUploadStore(new SqliteConnectionFactory(@"\\?\Z:\no-existe\jamas\meetings.db")),
                (operation, _) => logged.Add(operation),
                noWait);
            bool threw = false;
            try
            {
                broken.Enqueue(sessionId, ArchiveKind.Audio, NewFile("meeting-broken.wav"));
                await broken.WhenIdleAsync();
                await broken.ResumeUnfinishedAsync();
            }
            catch (Exception) { threw = true; }
            Check("con la base rota, encolar y retomar no lanzan", !threw);
            Check("y el fallo queda registrado", logged.Count == 2);

            // --- El pipeline encola audio y reporte --------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Desde el pipeline ---");
            archive.Calls.Clear();
            var pipelineUploads = new ArchiveUploadService(archive, uploads, retryDelays: noWait);
            IMeetingPipeline pipeline = new MeetingPipeline(
                new FakeAudioCapture(directory),
                new FakeTranscriptionClient("Transcript de prueba."),
                new FakeReportExtractor(),
                new FakeReportStorage(directory),
                directory,
                history,
                archiveQueue: pipelineUploads);

            await pipeline.StartRecordingAsync(SessionSource.Hotkey);
            MeetingPipelineResult result = await pipeline.StopRecordingAndProcessAsync();
            await pipelineUploads.WhenIdleAsync();

            Check("una grabacion sube su audio",
                (await RowFor(result.Audio.AudioPath)) is { Kind: ArchiveKind.Audio, State: ArchiveUploadState.Uploaded });
            Check("y el .md de su reporte",
                (await RowFor(result.SavedReportPath)) is { Kind: ArchiveKind.Report, State: ArchiveUploadState.Uploaded });

            long recordedSession = (await history.ListSessionsAsync(1)).Single().SessionId;
            string transcriptText = (await history.GetTranscriptAsync(recordedSession))!.Text;
            ExtractionSaveResult regenerated = await pipeline.ExtractForSessionAsync(
                recordedSession, transcriptText, "feature-handoff");
            await pipelineUploads.WhenIdleAsync();
            Check("un reporte re-generado tambien sube su .md",
                (await RowFor(regenerated.SavedReportPath))?.State == ArchiveUploadState.Uploaded);

            // El caso que justifica el finally: sin transcripcion, el WAV es lo
            // unico con lo que esa reunion se puede recuperar.
            IMeetingPipeline failingPipeline = new MeetingPipeline(
                new FakeAudioCapture(directory),
                new ThrowingTranscriptionClient(),
                new FakeReportExtractor(),
                new FakeReportStorage(directory),
                directory,
                history,
                archiveQueue: pipelineUploads);
            await failingPipeline.StartRecordingAsync(SessionSource.Hotkey);
            bool transcriptionFailed = false;
            try { await failingPipeline.StopRecordingAndProcessAsync(); }
            catch (Exception) { transcriptionFailed = true; }
            await pipelineUploads.WhenIdleAsync();

            long failedSession = (await history.ListSessionsAsync(1)).Single().SessionId;
            Check("si la transcripcion falla, el audio se sube igual",
                transcriptionFailed &&
                (await AllRowsAsync(factory)).Any(row =>
                    row.SessionId == failedSession && row is { Kind: ArchiveKind.Audio, State: ArchiveUploadState.Uploaded }));

            // --- Migracion sobre una base v1 con datos ------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Migracion v1 a v2 ---");
            Check("el esquema del codigo es v2 o posterior", SqliteSchemaMigrator.LatestVersion >= 2);
            Check("borrar la sesion arrastra sus subidas (cascade)", await CascadeDeletesUploadsAsync(factory, sessionId));

            // Una base como las que ya existen: v1, con datos y sin la tabla
            // nueva. Se arma devolviendo una v2 a ese estado, porque el
            // migrador no sabe detenerse en una version intermedia.
            string legacyPath = Path.Combine(directory, "legacy.db");
            var legacyFactory = new SqliteConnectionFactory(legacyPath);
            var legacyMigrator = new SqliteSchemaMigrator(legacyFactory);
            legacyMigrator.Migrate();
            await using (SqliteConnection legacy = legacyFactory.Open())
            await using (SqliteCommand command = legacy.CreateCommand())
            {
                command.CommandText =
                    """
                    drop table cloud_upload;
                    drop table if exists prompt_version;
                    pragma user_version = 1;
                    insert into session(started_at_utc, source) values ('2026-08-27T09:00:00.000Z', 'hotkey');
                    insert into transcript(session_id, text, created_at_utc) values (1, 'texto viejo', '2026-08-27T09:05:00.000Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            string migration = legacyMigrator.Migrate();
            IMeetingHistoryStore legacyHistory = new SqliteMeetingHistoryStore(legacyFactory);
            IReadOnlyList<SessionSummary> legacySessions = await legacyHistory.ListSessionsAsync(10);
            Check("una base v1 con datos migra a la ultima version", migration.Contains($"de v1 a v{SqliteSchemaMigrator.LatestVersion}", StringComparison.Ordinal));
            Check("conserva sus filas, y el listado funciona con la tabla nueva vacia",
                legacySessions is [{ TranscriptPreview: "texto viejo", AudioArchiveState: null }]);
            Check("y despues acepta subidas",
                (await new SqliteArchiveUploadStore(legacyFactory)
                    .EnqueueAsync(1, ArchiveKind.Audio, @"C:\x\viejo.wav", "2026/08/viejo.wav")).State == ArchiveUploadState.Pending);
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

    /// <summary>
    /// Sube de verdad dos archivos chicos a la cuenta configurada, comprueba que
    /// llegaron leyendolos de vuelta, y los borra. Usa el mismo adaptador y los
    /// mismos contenedores que la app, bajo el prefijo <c>selftest/</c>.
    /// </summary>
    public static async Task<int> VerifyBlobUploadAsync(IConfiguration configuration)
    {
        Console.WriteLine("=== Subida real a Azure Blob ===");

        var archive = new AzureBlobCloudArchive(configuration);
        if (!archive.IsConfigured)
        {
            Console.Error.WriteLine("Falta AzureStorage:ConnectionString en la configuracion del harness. No se probo nada.");
            return 2;
        }

        int failures = 0;
        string directory = Path.Combine(Path.GetTempPath(), $"ma-blob-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            foreach ((ArchiveKind kind, string extension, int sizeBytes) in new[]
            {
                (ArchiveKind.Audio, ".wav", 20 * 1024 * 1024), // mas de un bloque de 8 MB
                (ArchiveKind.Report, ".md", 2 * 1024)
            })
            {
                string localPath = Path.Combine(directory, $"selftest-{Guid.NewGuid():N}{extension}");
                byte[] content = new byte[sizeBytes];
                Random.Shared.NextBytes(content);
                await File.WriteAllBytesAsync(localPath, content);
                string remoteName = $"selftest/{Path.GetFileName(localPath)}";

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                string url = await archive.UploadAsync(kind, localPath, remoteName);
                stopwatch.Stop();

                var blob = new Azure.Storage.Blobs.BlobClient(
                    configuration["AzureStorage:ConnectionString"],
                    new Uri(url).Segments[1].TrimEnd('/'),
                    remoteName);
                Azure.Storage.Blobs.Models.BlobProperties properties = await blob.GetPropertiesAsync();
                using var downloaded = new MemoryStream();
                await blob.DownloadToAsync(downloaded);
                bool identical = downloaded.ToArray().AsSpan().SequenceEqual(content);

                Console.WriteLine(
                    $"  [{(identical ? "OK " : "MAL")}] {kind}: {sizeBytes:N0} bytes en {stopwatch.Elapsed.TotalSeconds:F1} s, " +
                    $"contenedor '{blob.BlobContainerName}', tipo '{properties.ContentType}', contenido identico al leerlo de vuelta");
                if (!identical) failures++;

                await blob.DeleteAsync();
                Console.WriteLine("        borrado tras comprobar");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"La subida real fallo: {exception.Message}");
            failures++;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }

        Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<List<ArchiveUpload>> AllRowsAsync(SqliteConnectionFactory factory)
    {
        await using SqliteConnection connection = factory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "select id, session_id, kind, local_path, remote_name, state, attempts, remote_url, last_error from cloud_upload order by id;";

        var rows = new List<ArchiveUpload>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ArchiveUpload(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2) == "audio" ? ArchiveKind.Audio : ArchiveKind.Report,
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5) switch
                {
                    "uploaded" => ArchiveUploadState.Uploaded,
                    "failed" => ArchiveUploadState.Failed,
                    _ => ArchiveUploadState.Pending
                },
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return rows;
    }

    private static async Task<bool> CascadeDeletesUploadsAsync(SqliteConnectionFactory factory, long sessionId)
    {
        await using SqliteConnection connection = factory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            delete from session where id = $id;
            select count(*) from cloud_upload where session_id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 0;
    }
}

/// <summary>Archivo en la nube falso, que puede fallar las veces que se le pida.</summary>
internal sealed class FakeCloudArchive : ICloudArchive
{
    private int _failuresSoFar;

    public bool IsConfigured { get; init; } = true;

    /// <summary>Cuantas subidas seguidas fallan antes de que una salga bien.</summary>
    public int FailuresBeforeSuccess
    {
        get;
        set { field = value; _failuresSoFar = 0; }
    }

    /// <summary>Rutas locales de las subidas que salieron bien.</summary>
    public List<string> Calls { get; } = [];

    public Task<string> UploadAsync(
        ArchiveKind kind,
        string localPath,
        string remoteName,
        CancellationToken cancellationToken = default)
    {
        if (_failuresSoFar < FailuresBeforeSuccess)
        {
            _failuresSoFar++;
            throw new IOException("fallo simulado");
        }

        Calls.Add(localPath);
        RemoteSizes[remoteName] = new FileInfo(localPath).Length;
        return Task.FromResult($"fake://{(kind == ArchiveKind.Audio ? "audio" : "report")}/{remoteName}");
    }

    /// <summary>Lo que "hay en la nube": nombre remoto y tamano. Los tests lo alteran para simular perdidas.</summary>
    public Dictionary<string, long> RemoteSizes { get; } = [];

    /// <summary>Si es true, preguntar por un blob lanza, como con la red caida.</summary>
    public bool SizeLookupFails { get; set; }

    public Task<long?> GetRemoteSizeAsync(
        ArchiveKind kind,
        string remoteName,
        CancellationToken cancellationToken = default)
    {
        if (SizeLookupFails) throw new IOException("sin red (simulado)");
        return Task.FromResult(RemoteSizes.TryGetValue(remoteName, out long size) ? size : (long?)null);
    }
}

internal sealed class ThrowingTranscriptionClient : ITranscriptionClient
{
    public Task<TranscriptionResult> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("transcripcion caida (simulada)");
}
