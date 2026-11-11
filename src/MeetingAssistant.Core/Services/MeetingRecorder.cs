using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Services;

/// <summary>
/// Grabar y encolar (Fase 8, paso B): reemplaza al pipeline que transcribía y extraía en esta
/// máquina. Al detener, convierte la grabación a 16 kHz 16 bits mono y la deja en la
/// <see cref="MeetingUploadQueue"/>; el servidor hace el resto. Grabar no depende de la red ni de
/// la sesión: la cola espera (decisión del dueño, 2026-11-09).
/// </summary>
public sealed class MeetingRecorder : IRecordingTrigger
{
    private readonly IAudioCaptureService _capture;
    private readonly IAudioConverter _converter;
    private readonly MeetingUploadQueue _queue;
    private readonly string _outputDirectory;
    private readonly string _uploadDirectory;
    private readonly Action<string> _diagnostic;
    private readonly Action<string, Exception> _failure;
    private string _trigger = string.Empty;

    /// <param name="outputDirectory">Donde la captura deja el WAV original (se conserva, como antes).</param>
    /// <param name="uploadDirectory">Donde va la copia convertida que se sube (se borra al terminar).</param>
    public MeetingRecorder(
        IAudioCaptureService capture,
        IAudioConverter converter,
        MeetingUploadQueue queue,
        string outputDirectory,
        string uploadDirectory,
        Action<string>? diagnostic = null,
        Action<string, Exception>? failure = null)
    {
        _capture = capture;
        _converter = converter;
        _queue = queue;
        _outputDirectory = outputDirectory;
        _uploadDirectory = uploadDirectory;
        _diagnostic = diagnostic ?? (_ => { });
        _failure = failure ?? ((_, _) => { });
    }

    public bool IsRecording => _capture.IsCapturing;

    public async Task StartRecordingAsync(string source, CancellationToken cancellationToken = default)
    {
        await _capture.StartAsync(_outputDirectory, cancellationToken);
        _trigger = source;
    }

    public async Task<QueuedMeeting> StopRecordingAndQueueAsync(CancellationToken cancellationToken = default)
    {
        AudioCaptureResult capture = await _capture.StopAsync(cancellationToken);
        return await QueueAsync(capture.AudioPath, capture.Duration, _trigger, cancellationToken);
    }

    /// <summary>Un WAV que ya existe ("Procesar audio existente"): el original queda donde está.</summary>
    public Task<QueuedMeeting> QueueAudioFileAsync(string audioPath, string trigger, CancellationToken cancellationToken = default) =>
        QueueAsync(audioPath, null, trigger, cancellationToken);

    private async Task<QueuedMeeting> QueueAsync(string originalPath, TimeSpan? duration, string trigger, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_uploadDirectory);
        string target = Path.Combine(_uploadDirectory, $"{Path.GetFileNameWithoutExtension(originalPath)}-{Guid.NewGuid().ToString("N")[..6]}-16k.wav");

        try
        {
            AudioConversion conversion = await _converter.ConvertForUploadAsync(originalPath, target, cancellationToken);
            _diagnostic(
                $"Audio convertido para subir: {conversion.SourceFormat}, {conversion.SourceBytes / 1048576.0:F1} MB → " +
                $"16000 Hz 16 bits mono, {conversion.TargetBytes / 1048576.0:F1} MB, {conversion.Duration.TotalMinutes:F1} min.");
            return _queue.EnqueueAudio(conversion.TargetPath, originalPath, conversion.Duration.TotalSeconds, trigger);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // La reunión no se puede perder por la conversión: se sube el original tal cual (pesa más,
            // y si pasa el límite del servidor la cola lo dice).
            _failure("MeetingRecorder.Convert", exception);
            TryDelete(target);
            return _queue.EnqueueAudio(originalPath, originalPath, duration?.TotalSeconds, trigger);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Una copia a medias que no se pudo borrar no cambia nada: no está en la cola.
        }
    }
}
