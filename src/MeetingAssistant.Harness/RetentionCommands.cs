using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Storage.Postgres;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Retención del audio local (Fase 6, paso 5).
///
/// <c>--verify-retention</c> es el autotest, sin red. <c>--audio-retention
/// &lt;dias&gt;</c> muestra qué borraría en esta máquina con ese plazo, contra la
/// nube de verdad; sólo con <c>--apply</c> borra.
/// </summary>
internal static class RetentionCommands
{
    /// <summary>Valida <c>&lt;dias&gt; [--apply]</c> y corre <see cref="RunAsync"/>.</summary>
    public static Task<int> RunFromArgumentsAsync(IConfiguration configuration, IReadOnlyList<string> arguments)
    {
        if (!int.TryParse(arguments[0], out int days) || days <= 0 ||
            (arguments.Count == 2 && !string.Equals(arguments[1], "--apply", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("Uso: --audio-retention <dias> [--apply]   (dias mayor que cero)");
            return Task.FromResult(1);
        }

        return RunAsync(configuration, days, apply: arguments.Count == 2);
    }

    public static async Task<int> RunAsync(IConfiguration configuration, int days, bool apply)
    {
        string localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingAssistant");
        string audioDirectory = Path.Combine(localRoot, "meeting-output");

        Console.WriteLine(apply
            ? $"=== Retencion de audio local, {days} dias: BORRANDO ==="
            : $"=== Retencion de audio local, {days} dias: simulacion (agrega --apply para borrar) ===");
        Console.WriteLine($"Carpeta: {audioDirectory}");

        var archive = new AzureBlobCloudArchive(configuration);
        if (!archive.IsConfigured)
        {
            Console.Error.WriteLine("Falta AzureStorage:ConnectionString: sin nube contra la cual comprobar, no se borra nada.");
            return 1;
        }

        // El registro de subidas esta donde este el historial. Con cadena de
        // Postgres se usa Postgres, que es donde escribe la app conmutada.
        string? connectionString = configuration["Postgres:ConnectionString"];
        PostgresDatabase? database = string.IsNullOrWhiteSpace(connectionString) ? null : new PostgresDatabase(connectionString);
        IArchiveUploadStore store = database is not null
            ? new PostgresArchiveUploadStore(database)
            : new SqliteArchiveUploadStore(new SqliteConnectionFactory(Path.Combine(localRoot, "meetings.db")));
        Console.WriteLine($"Registro de subidas: {(database is not null ? $"Postgres ({database.Description}), maquina {Environment.MachineName}" : "SQLite local")}");

        try
        {
            var failures = new List<string>();
            var retention = new LocalAudioRetentionService(
                archive, store, audioDirectory, (operation, exception) => failures.Add($"{operation}: {exception.Message}"));
            AudioRetentionResult result = await retention.RunAsync(TimeSpan.FromDays(days), apply);

            int onDisk = Directory.Exists(audioDirectory) ? Directory.GetFiles(audioDirectory, "*.wav").Length : 0;
            Console.WriteLine($"{Environment.NewLine}  {(apply ? "Borrados" : "Se borrarian")}: {result.Deleted.Count} ({result.BytesFreed / 1048576.0:F1} MB)");
            foreach (string path in result.Deleted) Console.WriteLine($"      {Path.GetFileName(path)}");
            Console.WriteLine($"  Subidos pero todavia dentro del plazo: {result.StillWithinRetention}");
            Console.WriteLine($"  Cumplieron el plazo y NO se borran: {result.Kept.Count}");
            foreach (RetainedAudio kept in result.Kept) Console.WriteLine($"      {Path.GetFileName(kept.LocalPath)}: {kept.Reason}");
            Console.WriteLine($"  WAV en la carpeta {(apply ? "despues" : "ahora")}: {onDisk}");
            Console.WriteLine("  (Los audios sin subida registrada no se tocan nunca, tengan la edad que tengan.)");
            foreach (string failure in failures) Console.WriteLine($"  FALLO: {failure}");

            return failures.Count == 0 ? 0 : 1;
        }
        finally
        {
            if (database is not null) await database.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }

    public static async Task<int> VerifyAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ma-retention-{Guid.NewGuid():N}");
        string audioDirectory = Path.Combine(directory, "meeting-output");
        string elsewhere = Path.Combine(directory, "musica-del-usuario");
        Directory.CreateDirectory(audioDirectory);
        Directory.CreateDirectory(elsewhere);
        Console.WriteLine($"=== Autotest de retencion de audio local ==={Environment.NewLine}{directory}{Environment.NewLine}");

        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        try
        {
            var factory = new SqliteConnectionFactory(Path.Combine(directory, "meetings.db"));
            new SqliteSchemaMigrator(factory).Migrate();
            IMeetingHistoryStore history = new SqliteMeetingHistoryStore(factory);
            IArchiveUploadStore store = new SqliteArchiveUploadStore(factory);
            long session = await history.CreateSessionAsync(DateTimeOffset.UtcNow, SessionSource.Hotkey);

            var archive = new FakeCloudArchive();
            var uploads = new ArchiveUploadService(archive, store, retryDelays: [TimeSpan.Zero]);

            DateTime old = DateTime.UtcNow.AddDays(-40);
            async Task<string> NewFileAsync(string folder, string name, ArchiveKind? uploadAs, DateTime lastWriteUtc)
            {
                string path = Path.Combine(folder, name);
                await File.WriteAllTextAsync(path, $"contenido de {name}");
                if (uploadAs is { } kind)
                {
                    uploads.Enqueue(session, kind, path);
                    await uploads.WhenIdleAsync();
                }
                File.SetLastWriteTimeUtc(path, lastWriteUtc);
                return path;
            }

            string RemoteNameOf(string path) => archive.RemoteSizes.Keys.Single(name => name.EndsWith(Path.GetFileName(path), StringComparison.Ordinal));

            string oldUploaded = await NewFileAsync(audioDirectory, "vieja-subida.wav", ArchiveKind.Audio, old);
            string recent = await NewFileAsync(audioDirectory, "reciente-subida.wav", ArchiveKind.Audio, DateTime.UtcNow.AddDays(-2));
            string outside = await NewFileAsync(elsewhere, "importada.wav", ArchiveKind.Audio, old);
            string neverUploaded = await NewFileAsync(audioDirectory, "vieja-sin-subir.wav", null, old);
            string lost = await NewFileAsync(audioDirectory, "perdida-en-la-nube.wav", ArchiveKind.Audio, old);
            string truncated = await NewFileAsync(audioDirectory, "subida-a-medias.wav", ArchiveKind.Audio, old);
            string report = await NewFileAsync(audioDirectory, "reporte-viejo.md", ArchiveKind.Report, old);

            archive.FailuresBeforeSuccess = int.MaxValue;
            string failedUpload = await NewFileAsync(audioDirectory, "subida-fallida.wav", ArchiveKind.Audio, old);
            archive.FailuresBeforeSuccess = 0;

            archive.RemoteSizes.Remove(RemoteNameOf(lost));
            archive.RemoteSizes[RemoteNameOf(truncated)] = 3;

            long oldSize = new FileInfo(oldUploaded).Length;
            string[] everything = [oldUploaded, recent, outside, neverUploaded, lost, truncated, report, failedUpload];
            var logged = new List<string>();
            var retention = new LocalAudioRetentionService(archive, store, audioDirectory, (operation, _) => logged.Add(operation));
            TimeSpan thirtyDays = TimeSpan.FromDays(30);

            // --- Cuando no debe hacer nada ---------------------------------------
            Console.WriteLine("--- Cuando no debe borrar nada ---");
            AudioRetentionResult noRetention = await retention.RunAsync(TimeSpan.Zero, apply: true);
            Check("sin plazo configurado no borra nada", noRetention.Deleted.Count == 0 && everything.All(File.Exists));

            var unconfigured = new LocalAudioRetentionService(new FakeCloudArchive { IsConfigured = false }, store, audioDirectory);
            Check("sin nube configurada no borra nada",
                (await unconfigured.RunAsync(thirtyDays, apply: true)).Deleted.Count == 0 && everything.All(File.Exists));

            archive.SizeLookupFails = true;
            AudioRetentionResult offline = await retention.RunAsync(thirtyDays, apply: true);
            archive.SizeLookupFails = false;
            Check("si no puede preguntarle a la nube, no borra: no poder preguntar no es 'esta subido'",
                offline.Deleted.Count == 0 && everything.All(File.Exists));
            Check("y lo deja anotado, sin lanzar", offline.Kept.Count == 3 && logged.Count == 3);

            bool brokenThrew = false;
            try
            {
                var broken = new LocalAudioRetentionService(
                    archive,
                    new SqliteArchiveUploadStore(new SqliteConnectionFactory(@"\\?\Z:\no-existe\jamas\meetings.db")),
                    audioDirectory);
                brokenThrew = (await broken.RunAsync(thirtyDays, apply: true)).Deleted.Count != 0;
            }
            catch (Exception) { brokenThrew = true; }
            Check("con la base rota no lanza ni borra", !brokenThrew && everything.All(File.Exists));

            // --- Simulacion ------------------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Simulacion ---");
            AudioRetentionResult plan = await retention.RunAsync(thirtyDays, apply: false);
            Check("identifica el unico audio que se puede borrar", plan.Deleted is [var only] && only == oldUploaded && plan.BytesFreed == oldSize);
            Check("y no borra nada", everything.All(File.Exists));

            // --- De verdad -------------------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Borrado ---");
            AudioRetentionResult done = await retention.RunAsync(thirtyDays, apply: true);
            Check("borra el audio viejo, subido y comprobado en la nube",
                done.Deleted is [var gone] && gone == oldUploaded && !File.Exists(oldUploaded) && done.BytesFreed == oldSize);
            Check("no toca el reciente, aunque este subido", File.Exists(recent) && done.StillWithinRetention == 1);
            Check("no toca un audio importado desde otra carpeta, aunque sea viejo y este subido", File.Exists(outside));
            Check("no toca un audio viejo que nunca se subio", File.Exists(neverUploaded));
            Check("no toca un audio cuya subida fallo", File.Exists(failedUpload));
            Check("no toca uno que la base da por subido pero no esta en la nube",
                File.Exists(lost) && done.Kept.Any(kept => kept.LocalPath == lost && kept.Reason.Contains("no está en la nube")));
            Check("no toca uno cuyo tamano en la nube no coincide",
                File.Exists(truncated) && done.Kept.Any(kept => kept.LocalPath == truncated && kept.Reason.Contains("no coincide")));
            Check("no toca los reportes: solo audio", File.Exists(report));

            AudioRetentionResult again = await retention.RunAsync(thirtyDays, apply: true);
            Check("una segunda pasada no encuentra nada mas que borrar", again.Deleted.Count == 0);

            // El plazo se mide de verdad: el reciente cae cuando pasa el tiempo.
            AudioRetentionResult later = await retention.RunAsync(thirtyDays, apply: true, now: DateTimeOffset.UtcNow.AddDays(40));
            Check("cuando el reciente cumple el plazo, se borra", later.Deleted is [var aged] && aged == recent && !File.Exists(recent));
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
}
