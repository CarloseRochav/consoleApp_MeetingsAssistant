using MeetingAssistant.Core.Abstractions;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Centraliza las operaciones de grabación iniciadas desde la interfaz de la
/// aplicación. El pipeline sigue siendo la autoridad para sus guard clauses,
/// de modo que los demás disparadores que lo usan directamente comparten el
/// mismo estado de captura.
/// </summary>
public sealed class RecordingCoordinator
{
    private readonly IMeetingPipeline _pipeline;
    private int _processingOperationCount;

    public RecordingCoordinator(IMeetingPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    public bool IsRecording => _pipeline.IsRecording;

    public bool IsProcessing => Volatile.Read(ref _processingOperationCount) > 0;

    public event EventHandler<RecordingCompletedEventArgs>? RecordingCompleted;

    public event EventHandler<RecordingFailedEventArgs>? RecordingFailed;

    public Task StartRecordingAsync(CancellationToken cancellationToken = default) =>
        _pipeline.StartRecordingAsync(cancellationToken);

    public async Task<MeetingPipelineResult> StopRecordingAndProcessAsync(
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _processingOperationCount);

        MeetingPipelineResult result;
        try
        {
            result = await _pipeline.StopRecordingAndProcessAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            Interlocked.Decrement(ref _processingOperationCount);
            RaiseRecordingFailed(exception);
            throw;
        }

        Interlocked.Decrement(ref _processingOperationCount);
        RaiseRecordingCompleted(result);
        return result;
    }

    private void RaiseRecordingCompleted(MeetingPipelineResult result)
    {
        var args = new RecordingCompletedEventArgs(result);
        foreach (EventHandler<RecordingCompletedEventArgs> handler in
                 RecordingCompleted?.GetInvocationList().Cast<EventHandler<RecordingCompletedEventArgs>>() ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Un suscriptor no debe convertir un procesamiento exitoso en un fallo.
            }
        }
    }

    private void RaiseRecordingFailed(Exception exception)
    {
        var args = new RecordingFailedEventArgs(exception);
        foreach (EventHandler<RecordingFailedEventArgs> handler in
                 RecordingFailed?.GetInvocationList().Cast<EventHandler<RecordingFailedEventArgs>>() ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Los fallos de notificación no deben ocultar el fallo original.
            }
        }
    }
}

public sealed class RecordingCompletedEventArgs : EventArgs
{
    public RecordingCompletedEventArgs(MeetingPipelineResult result) => Result = result;

    public MeetingPipelineResult Result { get; }
}

public sealed class RecordingFailedEventArgs : EventArgs
{
    public RecordingFailedEventArgs(Exception exception) => Exception = exception;

    public Exception Exception { get; }
}
