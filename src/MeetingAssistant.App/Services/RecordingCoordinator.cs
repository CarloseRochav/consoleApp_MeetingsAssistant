using MeetingAssistant.Core.Abstractions;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Coordinates every UI-facing recording request against the shared pipeline.
/// This is intentionally App-local: it exposes application lifecycle events,
/// while the Core pipeline remains provider- and UI-agnostic.
/// </summary>
public sealed class RecordingCoordinator
{
    private readonly IMeetingPipeline _pipeline;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private bool _isProcessing;

    public RecordingCoordinator(IMeetingPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    public bool IsRecording => _pipeline.IsRecording;

    public bool IsProcessing => _isProcessing;

    public event EventHandler? StateChanged;

    public event EventHandler<RecordingCompletedEventArgs>? RecordingCompleted;

    public event EventHandler<RecordingFailedEventArgs>? RecordingFailed;

    public async Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (IsProcessing || _pipeline.IsRecording)
            {
                throw new InvalidOperationException("Ya hay una grabaci\u00f3n en curso.");
            }

            await _pipeline.StartRecordingAsync(cancellationToken);
            OnStateChanged();
        }
        catch (Exception exception)
        {
            OnRecordingFailed(exception);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<MeetingPipelineResult> StopRecordingAndProcessAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsRecording)
            {
                throw new InvalidOperationException("No hay una grabaci\u00f3n en curso.");
            }

            _isProcessing = true;
            OnStateChanged();

            MeetingPipelineResult result = await _pipeline.StopRecordingAndProcessAsync(cancellationToken);
            OnRecordingCompleted(result);
            return result;
        }
        catch (Exception exception)
        {
            OnRecordingFailed(exception);
            throw;
        }
        finally
        {
            _isProcessing = false;
            OnStateChanged();
            _operationLock.Release();
        }
    }

    public async Task<MeetingPipelineResult> ProcessExistingAudioAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRecording || IsProcessing)
            {
                throw new InvalidOperationException("No se puede procesar un archivo mientras hay una grabación o proceso en curso.");
            }

            _isProcessing = true;
            OnStateChanged();

            MeetingPipelineResult result = await _pipeline.ProcessAudioFileAsync(audioPath, cancellationToken);
            OnRecordingCompleted(result);
            return result;
        }
        catch (Exception exception)
        {
            OnRecordingFailed(exception);
            throw;
        }
        finally
        {
            _isProcessing = false;
            OnStateChanged();
            _operationLock.Release();
        }
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnRecordingCompleted(MeetingPipelineResult result) =>
        RecordingCompleted?.Invoke(this, new RecordingCompletedEventArgs(result));

    private void OnRecordingFailed(Exception exception) =>
        RecordingFailed?.Invoke(this, new RecordingFailedEventArgs(exception));
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
