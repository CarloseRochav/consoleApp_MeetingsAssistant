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

// ---------------------------------------------------------------------------
// Dobles de prueba para --verify-reextraction
//
// Existen para que el test de atribucion de sesion sea gratis y deterministico:
// lo que se comprueba es a que fila apunta una foreign key, y pagarle a Deepgram
// y al LLM para averiguarlo seria absurdo. Ademas el microfono puede estar
// bloqueado por el consentimiento de Windows, que ya bloqueo una verificacion
// del paso 4.
//
// Se declaran al final del archivo porque un programa de sentencias de nivel
// superior admite tipos despues de las sentencias, y sacarlos a archivos aparte
// los volveria parte de la superficie del harness sin necesidad.
// ---------------------------------------------------------------------------

/// <summary>Captura de audio que no toca el microfono: crea un .wav vacio.</summary>
internal sealed class FakeAudioCapture : IAudioCaptureService
{
    private readonly string _directory;

    public FakeAudioCapture(string directory) => _directory = directory;

    public bool IsCapturing { get; private set; }

    public Task StartAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        IsCapturing = true;
        return Task.CompletedTask;
    }

    public Task<AudioCaptureResult> StopAsync(CancellationToken cancellationToken = default)
    {
        IsCapturing = false;
        string path = Path.Combine(_directory, $"fake-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, []);
        return Task.FromResult(new AudioCaptureResult(path, TimeSpan.FromMinutes(3), "loopback falso", "mic falso"));
    }
}

/// <summary>
/// Transcripcion que se queda esperando hasta que el test la suelta, para
/// cruzar a proposito una transcripcion lenta con una grabacion nueva. La
/// primera llamada espera; las siguientes contestan en el acto.
/// </summary>
internal sealed class GatedTranscriptionClient : ITranscriptionClient
{
    private readonly string _transcript;
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public GatedTranscriptionClient(string transcript) => _transcript = transcript;

    public Task Entered => _entered.Task;

    public void Release() => _released.TrySetResult();

    public async Task<TranscriptionResult> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _calls) == 1)
        {
            _entered.TrySetResult();
            await _released.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return new TranscriptionResult(_transcript, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(2), "es", []);
        }

        return new TranscriptionResult("Transcript de la otra reunion.", TimeSpan.FromMinutes(1), TimeSpan.Zero, "es", []);
    }
}

internal sealed class FakeTranscriptionClient : ITranscriptionClient
{
    private readonly string _transcript;

    public FakeTranscriptionClient(string transcript) => _transcript = transcript;

    public Task<TranscriptionResult> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TranscriptionResult(
            _transcript, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(2), "es", [], "fake-nova"));
}

/// <summary>
/// Extractor que devuelve Markdown fijo. Respeta el <c>promptId</c> que recibe
/// —y por eso el test puede comprobar que el reporte guardado trae el prompt con
/// el que se pidio, no el por defecto.
/// </summary>
internal sealed class FakeReportExtractor : ILlmReportExtractor
{
    public Task<ExtractionResult> ExtractAsync(
        string transcript,
        string? promptId = null,
        CancellationToken cancellationToken = default)
    {
        string id = promptId ?? ReportExtractionPrompt.Id;
        var prompt = new PromptDefinition(
            id, $"Prompt {id}", "Doble de prueba", "vtest", "system prompt falso",
            PromptOutputKind.FunctionalSpecification);

        var metadata = new MeetingReportMetadata(
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            LlmProvider: "Proveedor falso",
            LlmModel: "modelo-falso",
            PromptVersion: prompt.Version,
            InputTokens: 100,
            OutputTokens: 200,
            EstimatedCostUsd: 0.000123m,
            PromptId: id);

        return Task.FromResult(new ExtractionResult(
            $"# Reporte falso ({id})\n\n{transcript}", null, metadata, prompt));
    }
}

/// <summary>
/// Storage que escribe en un directorio temporal con el mismo esquema de nombres
/// que <c>MarkdownReportStorage</c> (<c>{prompt}-{yyyyMMdd-HHmmss}</c> mas un
/// sufijo unico). El sufijo esta porque el test genera varios reportes dentro del
/// mismo segundo y hace falta poder comprobar que uno no pisa al otro.
/// </summary>
internal sealed class FakeReportStorage : IReportStorage
{
    private readonly string _directory;

    public FakeReportStorage(string directory) => _directory = directory;

    public Task<string> SaveAsync(MeetingReport report, CancellationToken cancellationToken = default) =>
        SaveMarkdownAsync("# sin usar", report.Metadata, cancellationToken);

    public Task<string> SaveMarkdownAsync(
        string markdown,
        MeetingReportMetadata? metadata,
        CancellationToken cancellationToken = default)
    {
        string prefix = metadata?.PromptId ?? "meeting-report";
        string path = Path.Combine(
            _directory,
            $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, markdown);
        return Task.FromResult(path);
    }
}
