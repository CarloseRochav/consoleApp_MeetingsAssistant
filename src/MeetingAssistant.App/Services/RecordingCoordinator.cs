using MeetingAssistant.Core.Abstractions;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Centraliza las operaciones de grabación iniciadas desde la interfaz de la
/// aplicación (RecordPage, tray, hotkey). Es intencionalmente App-local: expone
/// eventos de ciclo de vida propios de la UI, mientras que el pipeline de Core
/// permanece agnóstico a proveedor y a interfaz.
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
                throw new InvalidOperationException("Ya hay una grabación en curso.");
            }

            await _pipeline.StartRecordingAsync(cancellationToken);
            OnStateChanged();
        }
        catch (Exception exception)
        {
            RaiseRecordingFailed(exception);
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
                throw new InvalidOperationException("No hay una grabación en curso.");
            }

            _isProcessing = true;
            OnStateChanged();

            MeetingPipelineResult result = await _pipeline.StopRecordingAndProcessAsync(cancellationToken);
            RaiseRecordingCompleted(result);
            return result;
        }
        catch (Exception exception)
        {
            RaiseRecordingFailed(exception);
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
            RaiseRecordingCompleted(result);
            return result;
        }
        catch (Exception exception)
        {
            RaiseRecordingFailed(exception);
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
