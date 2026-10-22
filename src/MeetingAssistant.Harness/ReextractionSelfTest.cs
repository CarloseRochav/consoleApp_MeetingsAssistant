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

/// <summary>--verify-reextraction: cada reporte cae en la reunion que corresponde. Offline.</summary>
internal static class ReextractionSelfTest
{
    /// <summary>
    /// Prueba a que sesion queda colgado un reporte re-extraido desde el historial.
    ///
    /// Corre con dobles de prueba —sin Deepgram, sin LLM y sin microfono— sobre una
    /// base temporal, asi que es gratis y deterministico. Eso importa: el defecto que
    /// este test persigue es de <b>atribucion</b>, no de extraccion, y pagarle a dos
    /// proveedores para comprobar a que fila apunta una foreign key seria absurdo.
    ///
    /// El defecto, que la lectura de codigo si mostro pero solo mirandolo a proposito:
    /// <c>MeetingPipeline.ExtractAndSaveAsync</c> deducia la sesion de
    /// <c>_currentSessionId</c>, un campo de instancia de un singleton (retirado el 2026-10-14). Re-extraer una
    /// reunion vieja por ese camino la habria colgado de la ultima grabacion —o
    /// habria creado una sesion fantasma— y en los dos casos <b>sin sintoma</b>: base
    /// consistente, .md en el vault, historial que miente.
    /// </summary>
    public static async Task<int> RunAsync()
    {
        string temporaryPath = Path.Combine(Path.GetTempPath(), $"ma-reextract-{Guid.NewGuid():N}.db");
        string temporaryVault = Path.Combine(Path.GetTempPath(), $"ma-reextract-vault-{Guid.NewGuid():N}");
        Console.WriteLine($"=== Autotest de re-extraccion ==={Environment.NewLine}{temporaryPath}");

        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        try
        {
            Directory.CreateDirectory(temporaryVault);

            var factory = new SqliteConnectionFactory(temporaryPath);
            new SqliteSchemaMigrator(factory).Migrate();
            IMeetingHistoryStore history = new SqliteMeetingHistoryStore(factory);

            var capture = new FakeAudioCapture(temporaryVault);
            var historyFailures = new List<string>();
            IMeetingPipeline pipeline = new MeetingPipeline(
                capture,
                new FakeTranscriptionClient("Transcript de la grabacion nueva."),
                new FakeReportExtractor(),
                new FakeReportStorage(temporaryVault),
                temporaryVault,
                history,
                (operation, exception) => historyFailures.Add($"{operation}: {exception.Message}"),
                // Precio de prueba, no el de ningun proveedor: el transcript falso
                // dura 3 minutos, asi que el costo guardado tiene que ser 0.03.
                new ConfigPricingCostEstimator(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Pricing:Deepgram:PerMinute"] = "0.01" })
                    .Build()));

            // --- Una reunion vieja, como la que estaria en el historial -----------
            long oldSessionId = await history.CreateSessionAsync(
                DateTimeOffset.UtcNow.AddDays(-7), SessionSource.Hotkey);
            await history.CompleteSessionAsync(
                oldSessionId, DateTimeOffset.UtcNow.AddDays(-7).AddMinutes(20),
                Path.Combine(temporaryVault, "vieja.wav"), TimeSpan.FromMinutes(20));
            await history.SaveTranscriptAsync(new TranscriptRecord(
                oldSessionId, "Transcript de la reunion de la semana pasada.",
                "Deepgram", "nova-3", null, DateTimeOffset.UtcNow.AddDays(-7)));

            // --- Y una grabacion de HOY, la que dejaba el campo de sesion ---
            // Esta es la condicion que hace visible el defecto. Sin grabar primero,
            // el bug se manifiesta de la otra forma (sesion fantasma), y se comprueba
            // mas abajo.
            await pipeline.StartRecordingAsync(SessionSource.Hotkey);
            MeetingPipelineResult todayResult = await pipeline.StopRecordingAndProcessAsync();

            IReadOnlyList<SessionSummary> afterRecording = await history.ListSessionsAsync(10);
            Check("la grabacion de hoy creo su propia sesion", afterRecording.Count == 2);
            TranscriptRecord? todayTranscript = await history.GetTranscriptAsync(afterRecording[0].SessionId);
            Check("el pipeline guarda el costo de transcripcion cuando hay precio (3 min x 0.01)",
                todayTranscript?.CostUsd == 0.03m);
            Check("y el modelo con que se transcribio (antes quedaba vacio)", todayTranscript?.Model == "fake-nova");
            Check("no hubo fallos de historial", historyFailures.Count == 0);
            if (historyFailures.Count > 0) historyFailures.ForEach(f => Console.WriteLine($"        {f}"));

            long todaySessionId = afterRecording
                .Where(session => session.SessionId != oldSessionId)
                .Select(session => session.SessionId)
                .Single();

            Check("el reporte de hoy quedo en la sesion de hoy",
                (await history.GetReportsAsync(todaySessionId)).Count == 1);
            Check("y el .md de hoy llego al vault", File.Exists(todayResult.SavedReportPath));

            // --- La re-extraccion de la reunion VIEJA, con la de hoy aun en curso -
            Console.WriteLine($"{Environment.NewLine}--- Re-extraer la reunion vieja, con una grabacion reciente hecha ---");

            TranscriptRecord? oldTranscript = await history.GetTranscriptAsync(oldSessionId);
            Check("la reunion vieja tiene transcript guardado", oldTranscript is not null);

            ExtractionSaveResult reextracted = await pipeline.ExtractForSessionAsync(
                oldSessionId, oldTranscript!.Text, "feature-handoff");

            // LA comprobacion. Con ExtractAndSaveAsync este reporte habria caido en
            // todaySessionId y nada habria fallado.
            IReadOnlyList<ReportRecord> oldReports = await history.GetReportsAsync(oldSessionId);
            Check("el reporte re-extraido quedo en la reunion VIEJA", oldReports.Count == 1);
            Check("y NO se colo en la grabacion de hoy",
                (await history.GetReportsAsync(todaySessionId)).Count == 1);
            Check("no se creo ninguna sesion fantasma",
                (await history.ListSessionsAsync(10)).Count == 2);
            Check("el reporte re-extraido guarda el prompt con el que se pidio",
                oldReports.Count == 1 && oldReports[0].PromptId == "feature-handoff");
            Check("el .md nuevo llego al vault", File.Exists(reextracted.SavedReportPath));
            Check("y no piso el .md del reporte de hoy",
                reextracted.SavedReportPath != todayResult.SavedReportPath &&
                File.Exists(todayResult.SavedReportPath));

            // --- La otra cara del defecto: sin grabacion previa -------------------
            // Un pipeline nuevo, sin grabacion previa. Por el camino viejo
            // eso abria una sesion marcada como importacion; el camino nuevo no puede.
            Console.WriteLine($"{Environment.NewLine}--- Re-extraer sin ninguna grabacion previa ---");

            IMeetingPipeline freshPipeline = new MeetingPipeline(
                new FakeAudioCapture(temporaryVault),
                new FakeTranscriptionClient("no se usa"),
                new FakeReportExtractor(),
                new FakeReportStorage(temporaryVault),
                temporaryVault,
                history,
                (operation, exception) => historyFailures.Add($"{operation}: {exception.Message}"));

            await freshPipeline.ExtractForSessionAsync(oldSessionId, oldTranscript.Text, "functional-spec");

            Check("sigue sin crearse una sesion fantasma de importacion",
                (await history.ListSessionsAsync(10)).Count == 2);
            Check("la reunion vieja acumula sus dos reportes (una sesion admite varios)",
                (await history.GetReportsAsync(oldSessionId)).Count == 2);

            // --- El comportamiento viejo no cambio -------------------------------
            // ExtractAndSaveAsync es el flujo de dos pasos de la ventana y de
            // "Adjuntar transcripcion (.txt)". Este paso no debia tocarlo.
            Console.WriteLine($"{Environment.NewLine}--- ExtractAndSaveAsync sigue igual ---");

            await freshPipeline.ExtractAndSaveAsync("Un transcript pegado a mano.", "assignment-meeting");
            IReadOnlyList<SessionSummary> afterPaste = await history.ListSessionsAsync(10);
            Check("un transcript suelto sigue abriendo su sesion de importacion",
                afterPaste.Count == 3);

            long pastedSessionId = afterPaste
                .Where(s => s.SessionId != oldSessionId && s.SessionId != todaySessionId)
                .Select(s => s.SessionId)
                .Single();
            Check("y esa sesion queda marcada como importacion",
                (await history.GetSessionAsync(pastedSessionId))?.Source == SessionSource.Import);

            // --- Transcript adjuntado DESPUES de una grabacion (audit 2026-10-14) -
            // El caso que el bloque anterior no cubria: `pipeline` ya grabo hoy.
            // Antes del arreglo este reporte caia en todaySessionId y el texto
            // adjuntado no se guardaba en ningun lado.
            Console.WriteLine($"{Environment.NewLine}--- Transcript adjuntado despues de grabar ---");

            const string attachedText = "Transcript adjuntado despues de grabar.";
            ExtractionSaveResult attached = await pipeline.ExtractAndSaveAsync(attachedText, "assignment-meeting");
            Check("el transcript adjuntado abrio su propia sesion",
                attached.SessionId is { } attachedId && attachedId != todaySessionId && attachedId != oldSessionId);
            Check("y NO se colgo de la grabacion de hoy",
                (await history.GetReportsAsync(todaySessionId)).Count == 1);
            Check("el texto adjuntado quedo guardado en su sesion",
                attached.SessionId is { } withText &&
                (await history.GetTranscriptAsync(withText))?.Text == attachedText);
            Check("y su reporte tambien",
                attached.SessionId is { } withReport && (await history.GetReportsAsync(withReport)).Count == 1);

            // Importar primero y extraer despues (lo que hace la ventana): un
            // reintento tras una extraccion fallida no abre otra sesion.
            int sessionsBeforeImport = (await history.ListSessionsAsync(50)).Count;
            long? importedId = await pipeline.ImportTranscriptAsync("Otro transcript adjuntado.");
            await pipeline.ExtractForSessionAsync(importedId!.Value, "Otro transcript adjuntado.", "assignment-meeting");
            await pipeline.ExtractForSessionAsync(importedId.Value, "Otro transcript adjuntado.", "feature-handoff");
            Check("importar y extraer dos veces deja UNA sesion con dos reportes",
                (await history.ListSessionsAsync(50)).Count == sessionsBeforeImport + 1 &&
                (await history.GetReportsAsync(importedId.Value)).Count == 2);

            // --- Dos pasos con una grabacion HTTP en medio -------------------------
            // La ventana transcribe; mientras se elige el prompt, empieza otra
            // grabacion. El reporte tiene que ir a la reunion de la ventana.
            Console.WriteLine($"{Environment.NewLine}--- Flujo de dos pasos con otra grabacion en medio ---");

            await pipeline.StartRecordingAsync(SessionSource.Window);
            TranscriptionSession windowSession = await pipeline.StopRecordingAndTranscribeAsync();
            Check("la transcripcion de la ventana trae su sesion", windowSession.SessionId is not null);

            await pipeline.StartRecordingAsync(SessionSource.Http);
            await pipeline.ExtractForSessionAsync(
                windowSession.SessionId!.Value, windowSession.Transcription.Transcript, "assignment-meeting");
            MeetingPipelineResult httpResult = await pipeline.StopRecordingAndProcessAsync();

            Check("el reporte de la ventana quedo en la reunion de la ventana",
                (await history.GetReportsAsync(windowSession.SessionId.Value)).Count == 1);
            Check("la grabacion HTTP tiene su propia sesion con su propio reporte",
                httpResult.SessionId is { } httpId && httpId != windowSession.SessionId &&
                (await history.GetReportsAsync(httpId)).Count == 1);

            // --- Una transcripcion lenta cruzada con una grabacion nueva -----------
            // Lo que podia pasar cuando HTTP llamaba al pipeline sin el candado del
            // coordinador: mientras Deepgram tarda, empieza otra reunion. Antes, el
            // transcript de la primera se guardaba en la sesion de la segunda.
            Console.WriteLine($"{Environment.NewLine}--- Transcripcion lenta cruzada con otra grabacion ---");

            var gate = new GatedTranscriptionClient("Transcript de la reunion lenta.");
            IMeetingPipeline slowPipeline = new MeetingPipeline(
                new FakeAudioCapture(temporaryVault),
                gate,
                new FakeReportExtractor(),
                new FakeReportStorage(temporaryVault),
                temporaryVault,
                history,
                (operation, exception) => historyFailures.Add($"{operation}: {exception.Message}"));

            await slowPipeline.StartRecordingAsync(SessionSource.Window);
            Task<TranscriptionSession> slowTranscription = slowPipeline.StopRecordingAndTranscribeAsync();
            await gate.Entered;
            await slowPipeline.StartRecordingAsync(SessionSource.Http);
            gate.Release();
            TranscriptionSession slow = await slowTranscription;
            TranscriptionSession second = await slowPipeline.StopRecordingAndTranscribeAsync();

            Check("las dos reuniones cruzadas tienen sesiones distintas",
                slow.SessionId is not null && second.SessionId is not null && slow.SessionId != second.SessionId);
            Check("el transcript lento quedo en SU sesion",
                (await history.GetTranscriptAsync(slow.SessionId!.Value))?.Text == "Transcript de la reunion lenta.");
            Check("no hubo fallos de historial en todo el recorrido", historyFailures.Count == 0);
            if (historyFailures.Count > 0) historyFailures.ForEach(f => Console.WriteLine($"        {f}"));

            // --- Resiliencia: la regla que manda en toda la fase -----------------
            Console.WriteLine($"{Environment.NewLine}--- Base rota: la re-extraccion igual llega al vault ---");

            var brokenFailures = new List<string>();
            IMeetingPipeline brokenPipeline = new MeetingPipeline(
                new FakeAudioCapture(temporaryVault),
                new FakeTranscriptionClient("no se usa"),
                new FakeReportExtractor(),
                new FakeReportStorage(temporaryVault),
                temporaryVault,
                new SqliteMeetingHistoryStore(new SqliteConnectionFactory(@"\\?\Z:\no-existe\jamas\meetings.db")),
                (operation, _) => brokenFailures.Add(operation));

            ExtractionSaveResult degraded = await brokenPipeline.ExtractForSessionAsync(
                999, "Transcript cualquiera.", "assignment-meeting");

            Check("con la base rota, el .md igual llego al vault", File.Exists(degraded.SavedReportPath));
            Check("y el fallo se registro en vez de propagarse",
                brokenFailures.Contains("MeetingPipeline.SaveReport"));

            // --- Dos reportes en el mismo segundo no se pisan ----------------------
            // El almacenamiento real, no el doble: el nombre del archivo lleva la
            // hora al segundo, y re-extraer dos veces seguidas daba el mismo nombre.
            Console.WriteLine($"{Environment.NewLine}--- Dos reportes del mismo prompt en el mismo segundo ---");
            var markdownStorage = new MarkdownReportStorage(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:VaultPath"] = temporaryVault,
                    ["Storage:SubFolder"] = "mismo-segundo"
                })
                .Build());
            var sameSecond = new MeetingReportMetadata(
                new DateTimeOffset(2026, 10, 14, 15, 0, 0, TimeSpan.Zero), "Prueba", "modelo-de-prueba", "v1", 1, 1, 0m, "feature-handoff");
            string firstPath = await markdownStorage.SaveMarkdownAsync("# Primero", sameSecond);
            string secondPath = await markdownStorage.SaveMarkdownAsync("# Segundo", sameSecond);
            string thirdPath = await markdownStorage.SaveMarkdownAsync("# Tercero", sameSecond);
            Check("tres reportes en el mismo segundo dan tres archivos distintos",
                new[] { firstPath, secondPath, thirdPath }.Distinct().Count() == 3 &&
                Directory.GetFiles(Path.GetDirectoryName(firstPath)!, "*.md").Length == 3);
            Check("el primero conserva el nombre de siempre y su contenido",
                Path.GetFileName(firstPath) == "feature-handoff-20261014-150000.md" &&
                File.ReadAllText(firstPath).Contains("# Primero", StringComparison.Ordinal));
            Check("los siguientes llevan -2 y -3",
                Path.GetFileName(secondPath) == "feature-handoff-20261014-150000-2.md" &&
                Path.GetFileName(thirdPath) == "feature-handoff-20261014-150000-3.md" &&
                File.ReadAllText(thirdPath).Contains("# Tercero", StringComparison.Ordinal));
            Check("el archivo sigue saliendo en UTF-8 con BOM, como antes",
                File.ReadAllBytes(firstPath).AsSpan().StartsWith(System.Text.Encoding.UTF8.GetPreamble()));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"El autotest reviento: {exception}");
            failures++;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string leftover in new[] { temporaryPath, temporaryPath + "-wal", temporaryPath + "-shm" })
            {
                try { if (File.Exists(leftover)) File.Delete(leftover); } catch { /* best effort */ }
            }

            try { if (Directory.Exists(temporaryVault)) Directory.Delete(temporaryVault, recursive: true); }
            catch { /* best effort */ }
        }

        Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
        return failures == 0 ? 0 : 1;
    }

}
