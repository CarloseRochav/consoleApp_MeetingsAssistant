using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Orquesta el pipeline completo: captura → transcripción → extracción → guardado.
/// Esto reemplaza el wiring manual que vivía en Harness/Program.cs — Program.cs
/// pasa a ser solo composición de dependencias (DI) + una llamada a RunAsync.
/// </summary>
public interface IMeetingPipeline
{
    bool IsRecording { get; }
    Task StartRecordingAsync(CancellationToken cancellationToken = default);
    Task<MeetingPipelineResult> StopRecordingAndProcessAsync(CancellationToken cancellationToken = default);
    Task<MeetingPipelineResult> ProcessAudioFileAsync(string audioPath, CancellationToken cancellationToken = default);
}

public sealed record MeetingPipelineResult(
    MeetingReport Report,
    string SavedReportPath,
    AudioCaptureResult Audio,
    TranscriptionResult Transcription);

public sealed class MeetingPipeline : IMeetingPipeline
{
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly ITranscriptionClient _transcriptionClient;
    private readonly ILlmReportExtractor _reportExtractor;
    private readonly IReportStorage _reportStorage;
    private readonly string _audioOutputDirectory;

    public MeetingPipeline(
        IAudioCaptureService audioCaptureService,
        ITranscriptionClient transcriptionClient,
        ILlmReportExtractor reportExtractor,
        IReportStorage reportStorage,
        string audioOutputDirectory)
    {
        _audioCaptureService = audioCaptureService;
        _transcriptionClient = transcriptionClient;
        _reportExtractor = reportExtractor;
        _reportStorage = reportStorage;
        _audioOutputDirectory = audioOutputDirectory;
    }

    public bool IsRecording => _audioCaptureService.IsCapturing;

    public Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        return _audioCaptureService.StartAsync(_audioOutputDirectory, cancellationToken);
    }

    public async Task<MeetingPipelineResult> StopRecordingAndProcessAsync(CancellationToken cancellationToken = default)
    {
        AudioCaptureResult audio = await _audioCaptureService.StopAsync(cancellationToken);
        return await ProcessAudioAsync(audio, cancellationToken);
    }

    public async Task<MeetingPipelineResult> ProcessAudioFileAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        if (!File.Exists(audioPath)) throw new FileNotFoundException("No existe el archivo de audio indicado.", audioPath);
        if (IsRecording) throw new InvalidOperationException("No se puede procesar un archivo mientras hay una grabación en curso.");

        var audio = new AudioCaptureResult(audioPath, TimeSpan.Zero, "Archivo importado", "Archivo importado");
        return await ProcessAudioAsync(audio, cancellationToken);
    }

    private async Task<MeetingPipelineResult> ProcessAudioAsync(AudioCaptureResult audio, CancellationToken cancellationToken)
    {

        TranscriptionResult transcription = await _transcriptionClient.TranscribeAsync(
            audio.AudioPath, cancellationToken);

        // Falla rápido y con contexto claro, en vez de dejar que
        // LlmReportExtractor lance el ArgumentException genérico de "transcript
        // vacío" sin decir de dónde vino el audio que la causó.
        if (string.IsNullOrWhiteSpace(transcription.Transcript))
        {
            throw new InvalidOperationException(
                $"La transcripción del audio en '{audio.AudioPath}' vino vacía — no se detectó habla.");
        }

        MeetingReport report = await _reportExtractor.ExtractAsync(transcription.Transcript, cancellationToken);
        string savedPath = await _reportStorage.SaveAsync(report, cancellationToken);

        return new MeetingPipelineResult(report, savedPath, audio with { Duration = transcription.AudioDuration }, transcription);
    }
}
