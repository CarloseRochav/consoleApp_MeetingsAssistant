using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Infrastructure;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Cost;
using MeetingAssistant.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Lo que hace el harness sin un comando <c>--verify-*</c>: el pipeline de
/// verdad, con el microfono o con un archivo. <b>Cobra</b>: llama a Deepgram y
/// al LLM, y escribe en el vault configurado.
///
///   &lt;segundos&gt;                    graba y procesa (15 si no se indica)
///   --transcribe-file &lt;wav&gt;        solo transcribe
///   --process-file &lt;wav&gt;           transcribe, extrae y guarda
///   --extract-transcript &lt;txt&gt;     extrae y guarda desde un transcript
/// </summary>
internal static class PipelineRunCommand
{
    public static readonly HarnessCommand TranscribeFile = new(
        "--transcribe-file", "--transcribe-file <wav>", "Transcribe un audio existente. Cobra (Deepgram).", 1, 1,
        context => RunAsync(context, audioPath: context.Arguments[0], process: false, transcriptPath: null, durationSeconds: 0));

    public static readonly HarnessCommand ProcessFile = new(
        "--process-file", "--process-file <wav> [--prompt <id>]", "Transcribe, extrae y guarda un audio existente. Cobra.", 1, 1,
        context => RunAsync(context, audioPath: context.Arguments[0], process: true, transcriptPath: null, durationSeconds: 0));

    public static readonly HarnessCommand ExtractTranscript = new(
        "--extract-transcript", "--extract-transcript <txt> [--prompt <id>]", "Extrae y guarda un reporte desde un transcript. Cobra (LLM).", 1, 1,
        context => RunAsync(context, audioPath: null, process: false, transcriptPath: context.Arguments[0], durationSeconds: 0));

    /// <summary>Sin comando: el primer argumento, si hay, son los segundos de grabacion.</summary>
    public static Task<int> RecordAsync(CommandContext context)
    {
        int durationSeconds = context.Arguments.Count > 0 && int.TryParse(context.Arguments[0], out int parsed) ? parsed : 15;
        return RunAsync(context, audioPath: null, process: false, transcriptPath: null, durationSeconds);
    }

    private static async Task<int> RunAsync(
        CommandContext context,
        string? audioPath,
        bool process,
        string? transcriptPath,
        int durationSeconds)
    {
        if (audioPath is null && transcriptPath is null && durationSeconds <= 0)
        {
            Console.Error.WriteLine("La duracion de captura debe ser mayor que cero.");
            return 1;
        }

        if (audioPath is not null && !File.Exists(audioPath))
        {
            Console.Error.WriteLine($"No existe el audio indicado: {audioPath}");
            return 1;
        }

        if (transcriptPath is not null && !File.Exists(transcriptPath))
        {
            Console.Error.WriteLine($"No existe el transcript indicado: {transcriptPath}");
            return 1;
        }

        try
        {
            IConfiguration configuration = context.Configuration;
            IPromptCatalog promptCatalog = new BuiltInPromptCatalog();

            ITranscriptionClient? transcriptionClient = null;
            ITranscriptionClient GetTranscriptionClient() =>
                transcriptionClient ??= ProviderClientFactory.CreateTranscriptionClient(configuration);

            if (audioPath is not null && !process)
            {
                Console.WriteLine($"=== Transcribiendo archivo existente ==={Environment.NewLine}{audioPath}");
                TranscriptionResult transcription = await GetTranscriptionClient().TranscribeAsync(audioPath);
                Console.WriteLine($"Transcripción recibida: {transcription.Transcript.Length:N0} caracteres; {transcription.Utterances.Count:N0} intervenciones.");
                Console.WriteLine($"Duracion: {transcription.AudioDuration.TotalSeconds:F2} s; transcripcion: {transcription.Latency.TotalSeconds:F2} s.");
                return 0;
            }

            ILlmReportExtractor reportExtractor = new LlmReportExtractor(
                ProviderClientFactory.CreateLlmClient(configuration),
                new ConfigPricingCostEstimator(configuration),
                promptCatalog);

            if (transcriptPath is not null)
            {
                string chosenPrompt = context.PromptId ?? promptCatalog.Default.Id;
                Console.WriteLine($"=== Extrayendo reporte de transcript ==={Environment.NewLine}{transcriptPath}");
                Console.WriteLine($"Prompt: {chosenPrompt}");
                string transcript = await File.ReadAllTextAsync(transcriptPath);
                ExtractionResult extracted = await reportExtractor.ExtractAsync(transcript, chosenPrompt);
                IReportStorage transcriptStorage = new MarkdownReportStorage(configuration);
                string savedPath = await transcriptStorage.SaveMarkdownAsync(extracted.MarkdownBody, extracted.Metadata);
                Console.WriteLine();
                Console.WriteLine("=== Prompt usado ===");
                Console.WriteLine(extracted.Prompt.SystemPrompt);
                Console.WriteLine();
                Console.WriteLine("=== Reporte generado ===");
                Console.WriteLine(extracted.MarkdownBody);
                if (extracted.StructuredReport is not null)
                {
                    PrintReport(extracted.StructuredReport);
                }
                Console.WriteLine($"Reporte guardado: {savedPath}");
                return 0;
            }

            IAudioCaptureService audioCapture = new AudioCaptureService();
            IReportStorage reportStorage = new MarkdownReportStorage(configuration);
            string outputDirectory = Path.Combine(AppContext.BaseDirectory, "meeting-output");
            IMeetingPipeline pipeline = new MeetingPipeline(
                audioCapture,
                GetTranscriptionClient(),
                reportExtractor,
                reportStorage,
                outputDirectory);

            if (audioPath is not null)
            {
                Console.WriteLine($"=== Procesando archivo existente ==={Environment.NewLine}{audioPath}");
                MeetingPipelineResult importedResult = await pipeline.ProcessAudioFileAsync(audioPath);
                Console.WriteLine($"Audio: {importedResult.Audio.AudioPath}");
                Console.WriteLine($"Duración: {importedResult.Transcription.AudioDuration.TotalSeconds:F2} s; transcripción: {importedResult.Transcription.Latency.TotalSeconds:F2} s.");
                Console.WriteLine($"Reporte guardado: {importedResult.SavedReportPath}");
                return 0;
            }

            Console.WriteLine("=== Meeting Assistant Harness ===");
            await pipeline.StartRecordingAsync(SessionSource.Harness);
            Console.WriteLine($"Grabando {durationSeconds} segundos...");
            await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
            MeetingPipelineResult result = await pipeline.StopRecordingAndProcessAsync();
            Console.WriteLine($"Audio: {result.Audio.AudioPath}");
            Console.WriteLine($"Loopback: {result.Audio.LoopbackDevice}");
            Console.WriteLine($"Mic: {result.Audio.MicrophoneDevice}");
            Console.WriteLine();
            Console.WriteLine("=== Transcript completo ===");
            Console.WriteLine(result.Transcription.Transcript);
            foreach (DiarizedUtterance utterance in result.Transcription.Utterances)
                Console.WriteLine($"Speaker {utterance.Speaker}: {utterance.Transcript}");
            Console.WriteLine($"Duracion: {result.Transcription.AudioDuration.TotalSeconds:F2} s; transcripcion: {result.Transcription.Latency.TotalSeconds:F2} s.");
            if (result.Report is not null)
            {
                PrintReport(result.Report);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("=== Reporte generado ===");
                Console.WriteLine(result.ReportMarkdown);
            }
            Console.WriteLine($"Reporte guardado: {result.SavedReportPath}");
            return 0;
        }
        catch (MeetingReportParseException exception)
        {
            Console.Error.WriteLine($"Error al interpretar el reporte: {exception.Message}");
            Console.Error.WriteLine("=== Output LLM que no pudo interpretarse ===");
            Console.Error.WriteLine(exception.RawOutput);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Error en el pipeline: {exception.Message}");
            return 1;
        }
    }

    private static void PrintReport(MeetingReport report)
    {
        Console.WriteLine();
        Console.WriteLine("=== Reporte de reunion ===");
        Console.WriteLine($"Resumen: {report.Summary}");
        PrintList("Insights", report.Insights);
        PrintList("Requirements", report.Requirements);
        PrintList("Indications", report.Indications);
        PrintList("Open questions", report.OpenQuestions);
        Console.WriteLine("Task list:");
        foreach (TaskItem task in report.TaskList)
            Console.WriteLine($"- [{task.Priority}] {task.Task}\n  Context: {task.Context}");

        if (report.Metadata is not null)
        {
            Console.WriteLine("Metadata:");
            Console.WriteLine($"Provider: {report.Metadata.LlmProvider}");
            Console.WriteLine($"Model: {report.Metadata.LlmModel}");
            Console.WriteLine($"Prompt version: {report.Metadata.PromptVersion}");
            Console.WriteLine($"Tokens: {report.Metadata.InputTokens} input, {report.Metadata.OutputTokens} output");
            Console.WriteLine($"Estimated cost: US${report.Metadata.EstimatedCostUsd:F6}");
        }
    }

    private static void PrintList(string title, IReadOnlyList<string> values)
    {
        Console.WriteLine($"{title}:");
        foreach (string value in values) Console.WriteLine($"- {value}");
    }
}
