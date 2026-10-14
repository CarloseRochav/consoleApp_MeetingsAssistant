using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Centraliza las operaciones de grabación de todos los disparadores
/// (RecordPage, tray, hotkey y, vía <see cref="IRecordingTrigger"/>, el endpoint
/// HTTP). Es intencionalmente App-local: expone eventos de ciclo de vida propios
/// de la UI, mientras que el pipeline de Core permanece agnóstico a proveedor y
/// a interfaz.
///
/// Los eventos se levantan en el hilo de quien llamó. Desde HTTP eso es un hilo
/// del pool, así que todo suscriptor que toque XAML tiene que volver al hilo de
/// UI por su cuenta (RecordViewModel y TrayIconService lo hacen).
/// </summary>
public sealed class RecordingCoordinator : IRecordingTrigger
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

    /// <summary>
    /// La captura arrancó. Existe además de <see cref="StateChanged"/> porque
    /// aquel se dispara en cada transición (incluidas las de procesamiento) y
    /// no distingue el arranque de una grabación de un refresco cualquiera.
    /// </summary>
    public event EventHandler<EventArgs>? RecordingStarted;

    /// <summary>
    /// Hay transcripción y falta elegir prompt. Es un estado terminal del flujo
    /// de dos pasos: la app queda esperando al usuario, no procesando.
    /// </summary>
    public event EventHandler<TranscriptReadyEventArgs>? TranscriptReady;

    /// <summary>
    /// Se guardó un reporte en el vault. Lo levantan los tres caminos que
    /// guardan — el de un paso, el de archivo existente y el de extracción tras
    /// elegir prompt — a diferencia de <see cref="RecordingCompleted"/>, que
    /// solo cubre los dos primeros porque su payload exige audio y transcripción.
    /// </summary>
    public event EventHandler<ReportSavedEventArgs>? ReportSaved;

    public async Task StartRecordingAsync(string source, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (IsProcessing || _pipeline.IsRecording)
            {
                throw new InvalidOperationException(IsProcessing
                    ? "Hay una reunión procesándose; espera a que termine para grabar otra."
                    : "Ya hay una grabación en curso.");
            }

            await _pipeline.StartRecordingAsync(source, cancellationToken);
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
            RaiseReportSaved(result.SavedReportPath, result.Prompt);
            return result;
        }
        catch (Exception exception)
        {
            RaiseRecordingFailed(exception, "No se pudo crear el reporte");
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
            RaiseReportSaved(result.SavedReportPath, result.Prompt);
            return result;
        }
        catch (Exception exception)
        {
            RaiseRecordingFailed(exception, "No se pudo procesar el audio");
            throw;
        }
        finally
        {
            _isProcessing = false;
            OnStateChanged();
            _operationLock.Release();
        }
    }

    public async Task<TranscriptionSession> StopRecordingAndTranscribeAsync(CancellationToken cancellationToken = default)
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

            TranscriptionSession session = await _pipeline.StopRecordingAndTranscribeAsync(cancellationToken);
            RaiseSafely(TranscriptReady, new TranscriptReadyEventArgs(session));
            return session;
        }
        catch (Exception exception)
        {
            RaiseRecordingFailed(exception, "No se pudo transcribir la reunión");
            throw;
        }
        finally
        {
            _isProcessing = false;
            OnStateChanged();
            _operationLock.Release();
        }
    }

    public async Task<TranscriptionSession> TranscribeExistingAudioAsync(string audioPath, CancellationToken cancellationToken = default)
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

            TranscriptionSession session = await _pipeline.TranscribeAudioFileAsync(audioPath, cancellationToken);
            RaiseSafely(TranscriptReady, new TranscriptReadyEventArgs(session));
            return session;
        }
        catch (Exception exception)
        {
            RaiseRecordingFailed(exception, "No se pudo transcribir el audio");
            throw;
        }
        finally
        {
            _isProcessing = false;
            OnStateChanged();
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Registra un transcript adjuntado (.txt) como sesión de importación. Sin
    /// candado: sólo escribe una fila nueva y no toca la captura ni la sesión de
    /// ninguna otra operación.
    /// </summary>
    public Task<long?> ImportTranscriptAsync(string transcript, CancellationToken cancellationToken = default) =>
        _pipeline.ImportTranscriptAsync(transcript, cancellationToken);

    /// <summary>
    /// Genera el reporte del transcript que muestra la ventana.
    /// <paramref name="sessionId"/> es la reunión a la que pertenece —la que
    /// devolvió la transcripción o la importación—; null sólo si no hay fila
    /// (historial apagado o base caída), y entonces el pipeline intenta abrir
    /// una nueva.
    /// </summary>
    public async Task<ExtractionSaveResult> ExtractAndSaveAsync(
        string transcript,
        string promptId,
        long? sessionId,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRecording)
            {
                throw new InvalidOperationException("No se puede extraer un reporte mientras hay una grabación en curso.");
            }

            _isProcessing = true;
            OnStateChanged();

            ExtractionSaveResult result = sessionId is { } id
                ? await _pipeline.ExtractForSessionAsync(id, transcript, promptId, cancellationToken)
                : await _pipeline.ExtractAndSaveAsync(transcript, promptId, cancellationToken);
            RaiseReportSaved(result.SavedReportPath, result.Prompt);
            return result;
        }
        catch (Exception exception)
        {
            RaiseRecordingFailed(exception, "No se pudo crear el reporte");
            throw;
        }
        finally
        {
            _isProcessing = false;
            OnStateChanged();
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Re-genera un reporte para una sesión del historial con otro prompt.
    ///
    /// Pasa por el coordinador, y no directo al pipeline, por dos razones que no
    /// son de estilo: el <c>_operationLock</c> es lo que <b>de verdad</b>
    /// serializa las operaciones —así una re-extracción desde Historial no puede
    /// solaparse con una grabación— y <c>RaiseReportSaved</c> es lo que dispara
    /// el toast. Un reporte que aparece en el vault sin avisar sería una
    /// regresión de T4.2.
    /// </summary>
    public async Task<ExtractionSaveResult> ExtractForSessionAsync(
        long sessionId,
        string transcript,
        string promptId,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRecording)
            {
                throw new InvalidOperationException("No se puede extraer un reporte mientras hay una grabación en curso.");
            }

            _isProcessing = true;
            OnStateChanged();

            ExtractionSaveResult result = await _pipeline.ExtractForSessionAsync(
                sessionId, transcript, promptId, cancellationToken);
            RaiseReportSaved(result.SavedReportPath, result.Prompt);
            return result;
        }
        catch (Exception exception)
        {
            RaiseRecordingFailed(exception, "No se pudo re-generar el reporte");
            throw;
        }
        finally
        {
            _isProcessing = false;
            OnStateChanged();
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Con el mismo resguardo que el resto de los eventos. Antes se invocaba
    /// directo, y un suscriptor que lanzara —p. ej. la bandeja tocando XAML desde
    /// el hilo de una petición HTTP— convertía una grabación correcta en un
    /// error.
    /// </summary>
    private void OnStateChanged() => RaiseSafely(StateChanged, EventArgs.Empty);

    private void RaiseRecordingCompleted(MeetingPipelineResult result) =>
        RaiseSafely(RecordingCompleted, new RecordingCompletedEventArgs(result));

    private void RaiseRecordingFailed(Exception exception, string operation) =>
        RaiseSafely(RecordingFailed, new RecordingFailedEventArgs(exception, operation));

    private void RaiseReportSaved(string savedReportPath, PromptDefinition prompt) =>
        RaiseSafely(ReportSaved, new ReportSavedEventArgs(savedReportPath, prompt));

    /// <summary>
    /// Invoca a cada suscriptor por separado y traga sus excepciones: un
    /// suscriptor no debe convertir un procesamiento exitoso en un fallo, ni
    /// ocultar el error original cuando lo que se está propagando ya es una
    /// excepción.
    /// </summary>
    private void RaiseSafely<TArgs>(EventHandler<TArgs>? handlers, TArgs args)
    {
        foreach (EventHandler<TArgs> handler in
                 handlers?.GetInvocationList().Cast<EventHandler<TArgs>>() ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                // Se traga, pero no en silencio: un suscriptor que falla en cada
                // evento (p. ej. por tocar XAML fuera del hilo de UI) tiene que
                // poder encontrarse en el log.
                App.LogStartupFailure($"RecordingCoordinator.{typeof(TArgs).Name} ({handler.Method.DeclaringType?.Name})", exception);
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
                App.LogStartupFailure($"RecordingCoordinator.StateChanged ({handler.Method.DeclaringType?.Name})", exception);
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
    public RecordingFailedEventArgs(Exception exception, string operation)
    {
        Exception = exception;
        Operation = operation;
    }

    public Exception Exception { get; }

    /// <summary>
    /// Qué se estaba intentando, en texto ya presentable. El evento cubre los
    /// seis caminos del coordinador, así que sin esto un aviso fuera de la
    /// ventana no puede decir si falló la grabación, la transcripción o el
    /// reporte.
    /// </summary>
    public string Operation { get; }
}

public sealed class TranscriptReadyEventArgs : EventArgs
{
    public TranscriptReadyEventArgs(TranscriptionSession session) => Session = session;

    public TranscriptionSession Session { get; }
}

public sealed class ReportSavedEventArgs : EventArgs
{
    public ReportSavedEventArgs(string savedReportPath, PromptDefinition prompt)
    {
        SavedReportPath = savedReportPath;
        Prompt = prompt;
    }

    public string SavedReportPath { get; }

    public PromptDefinition Prompt { get; }
}
