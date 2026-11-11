using MeetingAssistant.App.Startup;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Centraliza las operaciones de grabación de todos los disparadores (RecordPage, bandeja, hotkey y,
/// vía <see cref="IRecordingTrigger"/>, el endpoint HTTP). Desde el paso B detener ya no espera el
/// reporte: convierte, deja la reunión en la cola de subidas y vuelve; el reporte llega después
/// (<see cref="MeetingUploadQueue.ReportReady"/>).
///
/// Los eventos se levantan en el hilo de quien llamó. Desde HTTP eso es un hilo del pool, así que
/// todo suscriptor que toque XAML tiene que volver al hilo de UI por su cuenta.
/// </summary>
public sealed class RecordingCoordinator : IRecordingTrigger
{
    private readonly MeetingRecorder _recorder;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private bool _isProcessing;

    public RecordingCoordinator(MeetingRecorder recorder)
    {
        _recorder = recorder;
    }

    public bool IsRecording => _recorder.IsRecording;

    /// <summary>Convirtiendo y encolando (segundos). El envío y el reporte no cuentan: van en segundo plano.</summary>
    public bool IsProcessing => _isProcessing;

    public event EventHandler? StateChanged;

    public event EventHandler<EventArgs>? RecordingStarted;

    /// <summary>La grabación quedó guardada y en la cola de subidas.</summary>
    public event EventHandler<QueuedMeeting>? RecordingQueued;

    public event EventHandler<RecordingFailedEventArgs>? RecordingFailed;

    public async Task StartRecordingAsync(string source, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (IsProcessing || _recorder.IsRecording)
            {
                throw new InvalidOperationException(IsProcessing
                    ? "Se está guardando la grabación anterior; espera un momento."
                    : "Ya hay una grabación en curso.");
            }

            await _recorder.StartRecordingAsync(source, cancellationToken);
            OnStateChanged();
            RaiseSafely(RecordingStarted, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            RaiseRecordingFailed(exception, "No se pudo iniciar la grabación");
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<QueuedMeeting> StopRecordingAndQueueAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsRecording) throw new NothingToStopException();

            _isProcessing = true;
            OnStateChanged();

            QueuedMeeting queued = await _recorder.StopRecordingAndQueueAsync(cancellationToken);
            RaiseSafely(RecordingQueued, queued);
            return queued;
        }
        catch (Exception exception)
        {
            RaiseRecordingFailed(exception, "No se pudo guardar la grabación");
            throw;
        }
        finally
        {
            _isProcessing = false;
            OnStateChanged();
            _operationLock.Release();
        }
    }

    /// <summary>Un audio que ya existe ("Procesar audio existente"): se convierte y se encola igual que una grabación.</summary>
    public async Task<QueuedMeeting> QueueExistingAudioAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRecording || IsProcessing)
            {
                throw new InvalidOperationException("No se puede procesar un archivo mientras hay una grabación en curso.");
            }

            _isProcessing = true;
            OnStateChanged();

            QueuedMeeting queued = await _recorder.QueueAudioFileAsync(audioPath, SessionSource.Import, cancellationToken);
            RaiseSafely(RecordingQueued, queued);
            return queued;
        }
        catch (Exception exception)
        {
            RaiseRecordingFailed(exception, "No se pudo encolar el audio");
            throw;
        }
        finally
        {
            _isProcessing = false;
            OnStateChanged();
            _operationLock.Release();
        }
    }

    private void OnStateChanged() => RaiseSafely(StateChanged, EventArgs.Empty);

    /// <summary>Detener sin nada grabando no es un fallo que haya que avisar (el endpoint contesta 409).</summary>
    private void RaiseRecordingFailed(Exception exception, string operation)
    {
        if (exception is NothingToStopException) return;
        RaiseSafely(RecordingFailed, new RecordingFailedEventArgs(exception, operation));
    }

    /// <summary>Sigue siendo una InvalidOperationException: el contrato hacia afuera no cambia.</summary>
    private sealed class NothingToStopException() : InvalidOperationException("No hay una grabación en curso.");

    private void RaiseSafely<TArgs>(EventHandler<TArgs>? handlers, TArgs args)
    {
        foreach (EventHandler<TArgs> handler in handlers?.GetInvocationList().Cast<EventHandler<TArgs>>() ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                AppLog.Failure($"RecordingCoordinator.{typeof(TArgs).Name} ({handler.Method.DeclaringType?.Name})", exception);
            }
        }
    }

    private void RaiseSafely(EventHandler? handlers, EventArgs args)
    {
        foreach (EventHandler handler in handlers?.GetInvocationList().Cast<EventHandler>() ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                AppLog.Failure($"RecordingCoordinator.StateChanged ({handler.Method.DeclaringType?.Name})", exception);
            }
        }
    }
}

public sealed class RecordingFailedEventArgs : EventArgs
{
    public RecordingFailedEventArgs(Exception exception, string operation)
    {
        Exception = exception;
        Operation = operation;
    }

    public Exception Exception { get; }

    /// <summary>Qué se estaba intentando, en texto ya presentable.</summary>
    public string Operation { get; }
}
