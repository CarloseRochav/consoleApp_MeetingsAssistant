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

/// <summary>--verify-pipeline-history: el pipeline completo contra una base temporal. Llama a Deepgram y al LLM de verdad.</summary>
internal static class PipelineHistorySelfTest
{
    /// <summary>
    /// Corre el pipeline completo sobre un audio existente con un almacen de
    /// historial apuntando a una base <b>temporal</b>, y comprueba que quedaron
    /// registradas la sesion, el transcript y el reporte — ademas del .md en el
    /// vault.
    ///
    /// Existe porque el autotest de esquema prueba el store, pero no el cableado:
    /// que el pipeline cree la sesion, guarde el transcript antes de extraer y
    /// registre el reporte con su vault_path. Y porque hacerlo sobre un audio
    /// existente no necesita microfono, que es justo lo que puede estar bloqueado
    /// por el consentimiento de Windows.
    ///
    /// Al final repite la corrida con la base apuntada a una ruta imposible, para
    /// comprobar la regla que manda en este paso: <b>un fallo de base no puede
    /// tumbar una grabacion</b>.
    /// </summary>
    public static async Task<int> RunAsync(string audioPath, string? requestedPromptId)
    {
        if (!File.Exists(audioPath))
        {
            Console.Error.WriteLine($"No existe el audio indicado: {audioPath}");
            return 1;
        }

        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        IConfiguration configuration = HarnessConfiguration.Load();

        IPromptCatalog catalog = new BuiltInPromptCatalog();
        ILlmReportExtractor extractor = new LlmReportExtractor(
            ProviderClientFactory.CreateLlmClient(configuration), new ConfigPricingCostEstimator(configuration), catalog);
        ITranscriptionClient transcription = ProviderClientFactory.CreateTranscriptionClient(configuration);

        string temporaryPath = Path.Combine(Path.GetTempPath(), $"ma-pipeline-{Guid.NewGuid():N}.db");
        var failuresSeen = new List<string>();

        try
        {
            var factory = new SqliteConnectionFactory(temporaryPath);
            new SqliteSchemaMigrator(factory).Migrate();
            IMeetingHistoryStore history = new SqliteMeetingHistoryStore(factory);

            Console.WriteLine($"=== Pipeline con historial ==={Environment.NewLine}{temporaryPath}{Environment.NewLine}");

            IMeetingPipeline pipeline = new MeetingPipeline(
                new AudioCaptureService(),
                transcription,
                extractor,
                new MarkdownReportStorage(configuration),
                Path.Combine(AppContext.BaseDirectory, "meeting-output"),
                history,
                (operation, exception) => failuresSeen.Add($"{operation}: {exception.Message}"));

            MeetingPipelineResult result = await pipeline.ProcessAudioFileAsync(audioPath);

            Check("el .md llego al vault", File.Exists(result.SavedReportPath));
            Check("no hubo fallos de historial", failuresSeen.Count == 0);
            if (failuresSeen.Count > 0) failuresSeen.ForEach(f => Console.WriteLine($"        {f}"));

            IReadOnlyList<SessionSummary> sessions = await history.ListSessionsAsync(10);
            Check("quedo registrada exactamente una sesion", sessions.Count == 1);
            Check("la sesion se marco como importada",
                sessions.Count == 1 && (await history.GetSessionAsync(sessions[0].SessionId))?.Source == SessionSource.Import);

            long sessionId = sessions[0].SessionId;
            TranscriptRecord? storedTranscript = await history.GetTranscriptAsync(sessionId);
            Check("el transcript quedo guardado", storedTranscript is not null);
            Check("el transcript guardado es el mismo que devolvio el pipeline",
                storedTranscript?.Text == result.Transcription.Transcript);

            IReadOnlyList<ReportRecord> reports = await history.GetReportsAsync(sessionId);
            Check("quedo registrado un reporte", reports.Count == 1);
            Check("el reporte apunta al .md del vault",
                reports.Count == 1 && reports[0].VaultPath == result.SavedReportPath);
            Check("el markdown guardado coincide con el generado",
                reports.Count == 1 && reports[0].Markdown == result.ReportMarkdown);
            Check("el costo del reporte quedo registrado",
                reports.Count == 1 && reports[0].CostUsd is not null);
            // structured_json solo para assignment-meeting; los demas prompts dan
            // Markdown suelto y tienen que dejarlo en null.
            bool isAssignmentPrompt = reports.Count == 1 && reports[0].PromptId == ReportExtractionPrompt.Id;
            Check($"structured_json coherente con el prompt ({(isAssignmentPrompt ? "assignment-meeting: presente" : "otro prompt: null")})",
                reports.Count == 1 && (isAssignmentPrompt ? reports[0].StructuredJson is not null : reports[0].StructuredJson is null));

            Check("la busqueda encuentra la reunion recien guardada",
                (await history.SearchTranscriptsAsync(
                    result.Transcription.Transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])).Count >= 1);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"El pipeline con historial reviento: {exception}");
            failures++;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (string leftover in new[] { temporaryPath, temporaryPath + "-wal", temporaryPath + "-shm" })
            {
                try { if (File.Exists(leftover)) File.Delete(leftover); } catch { /* best effort */ }
            }
        }

        // --- La regla que manda: una base rota no puede tumbar una grabacion -----
        Console.WriteLine($"{Environment.NewLine}--- Resiliencia: base apuntada a una ruta imposible ---");
        var brokenFailures = new List<string>();
        try
        {
            // Un directorio que no existe y no se puede crear.
            var brokenFactory = new SqliteConnectionFactory(@"\\?\Z:\no-existe\jamas\meetings.db");
            IMeetingHistoryStore brokenHistory = new SqliteMeetingHistoryStore(brokenFactory);

            IMeetingPipeline resilientPipeline = new MeetingPipeline(
                new AudioCaptureService(),
                transcription,
                extractor,
                new MarkdownReportStorage(configuration),
                Path.Combine(AppContext.BaseDirectory, "meeting-output"),
                brokenHistory,
                (operation, exception) => brokenFailures.Add(operation));

            MeetingPipelineResult resilientResult = await resilientPipeline.ProcessAudioFileAsync(audioPath);

            Check("la grabacion llego al vault pese a la base rota", File.Exists(resilientResult.SavedReportPath));
            Check("el transcript se produjo igual", resilientResult.Transcription.Transcript.Length > 0);
            Check("los fallos de base se registraron en vez de propagarse", brokenFailures.Count > 0);
            Console.WriteLine($"        operaciones que fallaron y se absorbieron: {string.Join(", ", brokenFailures.Distinct())}");
        }
        catch (Exception exception)
        {
            Check($"la grabacion llego al vault pese a la base rota (reviento: {exception.GetType().Name})", false);
        }

        Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
        return failures == 0 ? 0 : 1;
    }

}
