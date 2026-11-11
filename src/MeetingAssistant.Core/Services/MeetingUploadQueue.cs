using System.Collections.Concurrent;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Services;

/// <summary>Hasta dónde llegó una corrida de <see cref="MeetingUploadQueue.AdvanceAsync"/>.</summary>
public enum QueueStep
{
    /// <summary>El .md está en el vault.</summary>
    Done,

    /// <summary>Subida; el servidor sigue trabajando. Se vuelve a mirar en un rato.</summary>
    Waiting,

    /// <summary>Algo pasajero (red, tope, enlace vencido): se reintenta más tarde, solo.</summary>
    Retry,

    /// <summary>Quedó en <see cref="QueuedMeetingStatus.Failed"/>; el usuario decide si reintenta.</summary>
    Failed,

    /// <summary>Sin sesión de dispositivo: espera a que el usuario vuelva a iniciar sesión.</summary>
    SignedOut
}

/// <summary>Bytes en Storage de una reunión que se está subiendo.</summary>
public sealed record QueueProgress(string Id, long SentBytes, long TotalBytes);

/// <summary>
/// La cola de subidas del escritorio (Fase 8, paso B; decisión del dueño 2026-11-09: sin API se
/// graba igual, se encola y se envía después). El camino de cada reunión es el del teléfono
/// (<c>MeetingPipeline</c> de <c>:core</c>): trabajo y enlace → el WAV a Storage bloque a bloque →
/// confirmar → el servidor transcribe y escribe el reporte → el .md al vault. Cada paso se guarda
/// antes del siguiente, así que una corrida sigue donde paró la anterior (app cerrada, reinicio,
/// red caída). Una re-extracción (Jobs opción 2) usa la misma cola, sin audio.
///
/// Un solo bucle en segundo plano procesa las reuniones de a una. Se despierta con
/// <see cref="Kick"/> (reunión nueva, red de vuelta, sesión iniciada), con el sondeo mientras el
/// servidor procesa, y con el reintento espaciado de lo que falló por algo pasajero.
/// </summary>
public sealed class MeetingUploadQueue : IDisposable
{
    /// <summary>Lo que va a la API como origen del trabajo (handoff del paso B).</summary>
    public const string Source = "desktop";

    /// <summary>4 MiB: unos 2 minutos de WAV de 16 kHz mono por bloque; un corte pierde como mucho eso.</summary>
    public const int DefaultBlockBytes = 4 << 20;

    private static readonly TimeSpan ProcessingPoll = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);

    private readonly IDevLogApi _api;
    private readonly DeviceAuthService _auth;
    private readonly IAudioUploader _uploader;
    private readonly IQueuedMeetingStore _store;
    private readonly IReportStorage _reports;
    private readonly TimeProvider _time;
    private readonly Action<string> _diagnostic;
    private readonly Action<string, Exception> _failure;
    private readonly int _blockBytes;

    /// <summary>No antes de esta hora, por reunión. Sólo en memoria: al arrancar todo está vencido.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _notBefore = new();
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _storeLock = new();
    private CancellationTokenSource? _loop;

    public MeetingUploadQueue(
        IDevLogApi api,
        DeviceAuthService auth,
        IAudioUploader uploader,
        IQueuedMeetingStore store,
        IReportStorage reports,
        TimeProvider? time = null,
        Action<string>? diagnostic = null,
        Action<string, Exception>? failure = null,
        int blockBytes = DefaultBlockBytes)
    {
        if (blockBytes <= 0) throw new ArgumentOutOfRangeException(nameof(blockBytes));
        _api = api;
        _auth = auth;
        _uploader = uploader;
        _store = store;
        _reports = reports;
        _time = time ?? TimeProvider.System;
        _diagnostic = diagnostic ?? (_ => { });
        _failure = failure ?? ((_, _) => { });
        _blockBytes = blockBytes;
    }

    /// <summary>Se guardó un cambio de estado. Llega en el hilo de la cola: quien toque XAML vuelve al de UI.</summary>
    public event EventHandler<QueuedMeeting>? Changed;

    /// <summary>Avance de bytes de la subida en curso.</summary>
    public event EventHandler<QueueProgress>? Progress;

    /// <summary>El .md de una reunión llegó al vault.</summary>
    public event EventHandler<QueuedMeeting>? ReportReady;

    /// <summary>Una reunión quedó en <see cref="QueuedMeetingStatus.Failed"/>.</summary>
    public event EventHandler<QueuedMeeting>? MeetingFailed;

    /// <summary>Todas, la más nueva primero.</summary>
    public IReadOnlyList<QueuedMeeting> Items
    {
        get
        {
            lock (_storeLock) return _store.All().OrderByDescending(item => item.CreatedAtUtc).ToList();
        }
    }

    public QueuedMeeting EnqueueAudio(string audioPath, string? originalAudioPath, double? durationSeconds, string trigger)
    {
        var meeting = new QueuedMeeting
        {
            Id = NewId(),
            CreatedAtUtc = _time.GetUtcNow(),
            Trigger = trigger,
            AudioPath = audioPath,
            OriginalAudioPath = originalAudioPath,
            DurationSeconds = durationSeconds,
            TotalBytes = new FileInfo(audioPath).Length
        };
        Save(meeting);
        _diagnostic($"Cola: {meeting.Id} encolada ({meeting.TotalBytes / 1048576.0:F1} MB, {trigger}).");
        Kick();
        return meeting;
    }

    public QueuedMeeting EnqueueReextract(long idSession, string promptId, string trigger)
    {
        var meeting = new QueuedMeeting
        {
            Id = NewId(),
            Kind = QueuedMeetingKind.Reextract,
            CreatedAtUtc = _time.GetUtcNow(),
            Trigger = trigger,
            IdSession = idSession,
            PromptId = promptId
        };
        Save(meeting);
        _diagnostic($"Cola: re-extracción {meeting.Id} de la sesión {idSession} con {promptId}.");
        Kick();
        return meeting;
    }

    /// <summary>
    /// Reintento pedido por el usuario. Una fallida vuelve a empezar: a buscar el reporte si la
    /// sesión ya existe, si no a subir de nuevo. Una pendiente se intenta ya, sin esperar su turno.
    /// </summary>
    public bool Retry(string id)
    {
        QueuedMeeting? meeting = Get(id);
        if (meeting is null || meeting.Status == QueuedMeetingStatus.Ready) return false;

        if (meeting.Status == QueuedMeetingStatus.Failed)
        {
            QueuedMeeting next = meeting.IdSession is not null && meeting.Kind == QueuedMeetingKind.Transcribe && meeting.IdJob is not null
                ? meeting with { Status = QueuedMeetingStatus.Processing, Error = null, Notice = null, RetryCount = 0 }
                : meeting with
                {
                    Status = QueuedMeetingStatus.Waiting, IdJob = null, UploadUrl = null, UploadExpiresAt = null,
                    SentBlocks = [], Error = null, Notice = null, RetryCount = 0
                };
            Save(next);
        }

        _notBefore.TryRemove(id, out _);
        Kick();
        return true;
    }

    /// <summary>Saca de la lista una reunión terminada (lista o fallida). El audio queda donde estaba.</summary>
    public bool Remove(string id)
    {
        QueuedMeeting? meeting = Get(id);
        if (meeting is null || !meeting.IsFinished) return false;
        lock (_storeLock) _store.Delete(id);
        _notBefore.TryRemove(id, out _);
        return true;
    }

    /// <summary>Arranca el bucle. Todo lo pendiente de una corrida anterior se intenta en seguida.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        _loop = new CancellationTokenSource();
        CancellationToken token = _loop.Token;
        _ = Task.Run(() => LoopAsync(token));
    }

    /// <summary>
    /// Despierta el bucle. Con <paramref name="resetBackoff"/> además olvida las esperas (la red
    /// volvió, o se inició sesión): lo que esperaba por eso se intenta ya.
    /// </summary>
    public void Kick(bool resetBackoff = false)
    {
        if (resetBackoff) _notBefore.Clear();
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public void Dispose()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
    }

    /// <summary>Una pasada por todo lo pendiente cuyo turno llegó. Público para los autotests.</summary>
    public async Task RunPendingAsync(CancellationToken cancellationToken = default)
    {
        foreach (QueuedMeeting meeting in Items.Where(item => !item.IsFinished).OrderBy(item => item.CreatedAtUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_notBefore.TryGetValue(meeting.Id, out DateTimeOffset notBefore) && notBefore > _time.GetUtcNow()) continue;

            QueueStep step = await AdvanceAsync(meeting.Id, cancellationToken);
            if (step == QueueStep.SignedOut) return;
        }
    }

    /// <summary>
    /// Lleva una reunión tan lejos como se pueda en esta corrida. Nunca lanza por un fallo de red o
    /// del servidor: lo guarda y lo dice con el <see cref="QueueStep"/>.
    /// </summary>
    public async Task<QueueStep> AdvanceAsync(string id, CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken);
        try
        {
            QueuedMeeting? meeting = Get(id);
            if (meeting is null) return QueueStep.Failed;

            QueueStep step;
            try
            {
                step = await AdvanceCoreAsync(meeting, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Un archivo que no se pudo leer, el vault que no se pudo escribir... Se reintenta
                // espaciado: casi siempre es pasajero (un disco ocupado, un antivirus).
                _failure($"MeetingUploadQueue.Advance({id})", exception);
                step = ScheduleRetry(Get(id) ?? meeting, $"Error en esta máquina: {exception.Message}");
            }

            if (step == QueueStep.Waiting) _notBefore[id] = _time.GetUtcNow() + ProcessingPoll;
            else if (step is QueueStep.Done or QueueStep.Failed) _notBefore.TryRemove(id, out _);
            return step;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<QueueStep> AdvanceCoreAsync(QueuedMeeting meeting, CancellationToken cancellationToken)
    {
        switch (meeting.Status)
        {
            case QueuedMeetingStatus.Ready:
                return QueueStep.Done;
            case QueuedMeetingStatus.Failed:
                return QueueStep.Failed;
        }

        if (_auth.Current is not { } session)
        {
            if (meeting.Notice != SignedOutNotice) Save(meeting with { Notice = SignedOutNotice });
            return QueueStep.SignedOut;
        }

        string token = session.Token;

        // 1. El trabajo: una re-extracción nace en cola; un audio pide su enlace de subida.
        if (meeting.Kind == QueuedMeetingKind.Reextract && meeting.Status == QueuedMeetingStatus.Waiting)
        {
            switch (await _api.ReextractAsync(token, meeting.IdSession!.Value, meeting.PromptId!, Source, cancellationToken))
            {
                case ApiResult<JobRow>.Success success:
                    meeting = Save(meeting with { Status = QueuedMeetingStatus.Processing, IdJob = success.Value.IdJob, Notice = null, RetryCount = 0 });
                    break;
                case ApiResult<JobRow>.Failure { Error: ApiError.NotFound }:
                    return Fail(meeting, "El servidor ya no tiene esta reunión.");
                case ApiResult<JobRow>.Failure { Error: ApiError.Rejected { Code: 409 } }:
                    return Fail(meeting, "Esta reunión no tiene transcripción en el servidor: no se puede volver a extraer.");
                case ApiResult<JobRow>.Failure { Error: ApiError.Rejected { Code: 400 } rejected }:
                    return Fail(meeting, $"El servidor rechazó el prompt: {Clean(rejected.Message)}");
                case ApiResult<JobRow>.Failure failure:
                    return FailureOf(meeting, session, failure.Error);
            }
        }

        if (meeting.Kind == QueuedMeetingKind.Transcribe &&
            (meeting.Status == QueuedMeetingStatus.Waiting || meeting.IdJob is null ||
             (meeting.Status == QueuedMeetingStatus.Uploading && (meeting.UploadUrl is null || LinkExpired(meeting)))))
        {
            if (meeting.AudioPath is null || !File.Exists(meeting.AudioPath))
            {
                return Fail(meeting, $"No se encuentra el audio en esta máquina: {meeting.AudioPath ?? "(sin ruta)"}.");
            }

            long size = new FileInfo(meeting.AudioPath).Length;
            switch (await _api.CreateJobAsync(token, Source, cancellationToken))
            {
                case ApiResult<JobUpload>.Success success:
                    JobUpload job = success.Value;
                    if (job.MaxBytes > 0 && size > job.MaxBytes)
                    {
                        return Fail(meeting, $"La grabación es demasiado grande para enviarla ({size / 1048576} MB; el límite es {job.MaxBytes / 1048576} MB).");
                    }

                    meeting = Save(meeting with
                    {
                        Status = QueuedMeetingStatus.Uploading, IdJob = job.IdJob, UploadUrl = job.UploadUrl, UploadExpiresAt = job.ExpiresAt,
                        BlockBytes = _blockBytes, SentBlocks = [], TotalBytes = size, Notice = null, Error = null
                    });
                    break;
                case ApiResult<JobUpload>.Failure failure:
                    return FailureOf(meeting, session, failure.Error);
            }
        }

        // 2. El audio a Storage, y confirmar.
        if (meeting.Status == QueuedMeetingStatus.Uploading)
        {
            QueuedMeeting current = meeting;
            var sent = new HashSet<int>(current.SentBlocks);
            Progress?.Invoke(this, new QueueProgress(current.Id, current.SentBytes, current.TotalBytes));
            BlockUploadOutcome outcome = await _uploader.UploadAsync(
                current.UploadUrl!,
                current.AudioPath!,
                current.BlockBytes,
                sent,
                index =>
                {
                    // Cada bloque se guarda en seguida: es lo que deja seguir tras un reinicio.
                    current = Save(current with { SentBlocks = [.. current.SentBlocks, index], Notice = null, RetryCount = 0 }, raiseChanged: false);
                    Progress?.Invoke(this, new QueueProgress(current.Id, current.SentBytes, current.TotalBytes));
                    return Task.CompletedTask;
                },
                cancellationToken);
            meeting = current;

            switch (outcome)
            {
                case BlockUploadOutcome.Committed:
                    break;
                case BlockUploadOutcome.LinkRejected:
                    // Un trabajo nuevo es un blob nuevo: los bloques de éste ya no sirven.
                    Save(meeting with { Status = QueuedMeetingStatus.Waiting, IdJob = null, UploadUrl = null, UploadExpiresAt = null, SentBlocks = [] });
                    return ScheduleRetry(Get(meeting.Id)!, "El enlace de subida venció; se pide otro.", immediate: true);
                case BlockUploadOutcome.Interrupted:
                    return ScheduleRetry(meeting, "Se cortó la conexión durante la subida; sigue donde quedó.");
                case BlockUploadOutcome.Failed failed:
                    return ScheduleRetry(meeting, $"Storage rechazó la subida (HTTP {failed.Code}); se reintenta.");
            }

            switch (await _api.ConfirmJobAsync(token, meeting.IdJob!.Value, cancellationToken))
            {
                case ApiResult<JobRow>.Success:
                    meeting = Save(meeting with { Status = QueuedMeetingStatus.Processing, UploadUrl = null, Notice = null, RetryCount = 0 });
                    break;
                case ApiResult<JobRow>.Failure { Error: ApiError.Rejected { Code: 409 } } when await IsPastUploadAsync(token, meeting.IdJob.Value, cancellationToken):
                    // Ya pasó ese paso (una confirmación cuya respuesta se perdió): a esperar.
                    meeting = Save(meeting with { Status = QueuedMeetingStatus.Processing, UploadUrl = null, Notice = null, RetryCount = 0 });
                    break;
                case ApiResult<JobRow>.Failure { Error: ApiError.Rejected { Code: 400 } }:
                    return Fail(meeting, "El servidor rechazó el archivo por demasiado grande.");
                case ApiResult<JobRow>.Failure { Error: ApiError.NotFound }:
                    Save(meeting with { Status = QueuedMeetingStatus.Waiting, IdJob = null, UploadUrl = null, SentBlocks = [] });
                    return ScheduleRetry(Get(meeting.Id)!, "El servidor ya no tiene esta subida; empieza de nuevo.", immediate: true);
                case ApiResult<JobRow>.Failure failure:
                    return FailureOf(meeting, session, failure.Error);
            }
        }

        // 3. Esperar al servidor.
        if (meeting.Status == QueuedMeetingStatus.Processing)
        {
            JobRow job;
            switch (await _api.GetJobAsync(token, meeting.IdJob!.Value, cancellationToken))
            {
                case ApiResult<JobRow>.Success success:
                    job = success.Value;
                    break;
                case ApiResult<JobRow>.Failure failure:
                    return FailureOf(meeting, session, failure.Error);
                default:
                    throw new InvalidOperationException();
            }

            switch (job.Status)
            {
                case "done" when job.IdSession is { } idSession:
                    meeting = Save(meeting with { Status = QueuedMeetingStatus.WritingReport, IdSession = idSession, Notice = null, RetryCount = 0 });
                    break;
                case "failed":
                    return Fail(meeting, "El servidor no pudo procesar esta reunión" + (string.IsNullOrWhiteSpace(job.Error) ? "." : $": {Clean(job.Error)}"));
                default:
                    // queued con un error = el servidor reintenta (retraso de 017); se dice por qué.
                    string? notice = job.Status == "queued" && !string.IsNullOrWhiteSpace(job.Error)
                        ? $"El servidor reintenta (intento {job.Attempts}): {Clean(job.Error)}"
                        : null;
                    if (notice != meeting.Notice) Save(meeting with { Notice = notice });
                    return QueueStep.Waiting;
            }
        }

        // 4. El reporte al vault.
        if (meeting.Status == QueuedMeetingStatus.WritingReport)
        {
            IReadOnlyList<ApiReport> reports;
            switch (await _api.GetReportsAsync(token, meeting.IdSession!.Value, cancellationToken))
            {
                case ApiResult<IReadOnlyList<ApiReport>>.Success success:
                    reports = success.Value;
                    break;
                case ApiResult<IReadOnlyList<ApiReport>>.Failure failure:
                    return FailureOf(meeting, session, failure.Error);
                default:
                    throw new InvalidOperationException();
            }

            ApiReport? report = PickReport(meeting, reports);
            if (report is null) return QueueStep.Waiting;

            // Si la app se cierra entre escribir el .md y guardar Ready, la próxima corrida lo
            // vuelve a escribir (un .md repetido, nunca uno perdido). Es el lado barato del error.
            meeting = Save(meeting with { IdReport = report.IdReport });
            string path = await _reports.SaveMarkdownAsync(report.Markdown, ReportFileMetadata.From(report), cancellationToken);
            meeting = Save(meeting with { Status = QueuedMeetingStatus.Ready, ReportPath = path, Notice = null, Error = null, RetryCount = 0 });
            DeleteConvertedCopy(meeting);
            _diagnostic($"Cola: {meeting.Id} lista (trabajo {meeting.IdJob}, sesión {meeting.IdSession}, reporte {report.IdReport}).");
            Raise(ReportReady, meeting);
            return QueueStep.Done;
        }

        return ScheduleRetry(meeting, $"Nada que hacer en el estado {meeting.Status}.");
    }

    /// <summary>
    /// El reporte de este trabajo: el más nuevo de la sesión (para una re-extracción, el más nuevo de
    /// su prompt, por si otra re-extracción de la misma sesión terminó a la vez).
    /// </summary>
    private static ApiReport? PickReport(QueuedMeeting meeting, IReadOnlyList<ApiReport> reports)
    {
        if (meeting.IdReport is { } known && reports.FirstOrDefault(report => report.IdReport == known) is { } same) return same;

        IEnumerable<ApiReport> candidates = meeting.Kind == QueuedMeetingKind.Reextract && meeting.PromptId is { } promptId
            ? reports.Where(report => string.Equals(report.PromptId, promptId, StringComparison.OrdinalIgnoreCase))
            : reports;
        return candidates.MaxBy(report => report.IdReport);
    }

    private async Task<bool> IsPastUploadAsync(string token, long idJob, CancellationToken cancellationToken) =>
        await _api.GetJobAsync(token, idJob, cancellationToken) is ApiResult<JobRow>.Success { Value.Status: "queued" or "processing" or "done" };

    private bool LinkExpired(QueuedMeeting meeting) =>
        meeting.UploadExpiresAt is { } expires && expires <= _time.GetUtcNow() + TimeSpan.FromMinutes(1);

    private QueueStep FailureOf(QueuedMeeting meeting, DeviceSession session, ApiError error)
    {
        switch (error)
        {
            case ApiError.Unauthorized:
                // Revocado. La reunión NO falla: espera a que se vuelva a iniciar sesión (los trabajos
                // son del usuario, no del dispositivo, así que siguen siendo suyos con el token nuevo).
                _auth.HandleUnauthorized(session);
                Save(meeting with { Notice = SignedOutNotice });
                return QueueStep.SignedOut;
            case ApiError.CapReached cap:
                TimeSpan wait = cap.Refusal.IsInFlightLimit || cap.Refusal.ResetsAt is not { } resets
                    ? TimeSpan.FromMinutes(1)
                    : Max(resets - _time.GetUtcNow(), TimeSpan.FromMinutes(1));
                return ScheduleRetry(meeting, cap.Refusal.Describe(), wait);
            case ApiError.RateLimited:
            case ApiError.Network:
            case ApiError.Server:
                return ScheduleRetry(meeting, error.Describe() + " Se reintenta solo.");
            case ApiError.NotFound:
                return Fail(meeting, "El servidor ya no tiene el trabajo de esta reunión.");
            default:
                return Fail(meeting, error.Describe());
        }
    }

    private QueueStep ScheduleRetry(QueuedMeeting meeting, string reason, TimeSpan? wait = null, bool immediate = false)
    {
        int count = immediate ? meeting.RetryCount : meeting.RetryCount + 1;
        TimeSpan delay = immediate
            ? TimeSpan.Zero
            : wait ?? Min(TimeSpan.FromTicks(FirstRetry.Ticks * (1L << Math.Min(count - 1, 10))), MaxRetry);
        DateTimeOffset next = _time.GetUtcNow() + delay;
        _notBefore[meeting.Id] = next;
        string notice = delay > TimeSpan.Zero ? $"{reason} Próximo intento: {next.ToLocalTime():HH:mm:ss}." : reason;
        Save(meeting with { Notice = notice, RetryCount = count });
        return QueueStep.Retry;
    }

    private QueueStep Fail(QueuedMeeting meeting, string reason)
    {
        QueuedMeeting failed = Save(meeting with { Status = QueuedMeetingStatus.Failed, Error = reason, Notice = null, UploadUrl = null });
        _diagnostic($"Cola: {meeting.Id} falló (trabajo {meeting.IdJob}).");
        Raise(MeetingFailed, failed);
        return QueueStep.Failed;
    }

    /// <summary>La copia convertida a 16 kHz ya no hace falta: el audio quedó archivado en el servidor y el original sigue aquí.</summary>
    private void DeleteConvertedCopy(QueuedMeeting meeting)
    {
        if (meeting.AudioPath is not { } path || meeting.OriginalAudioPath is null ||
            string.Equals(path, meeting.OriginalAudioPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            _failure("MeetingUploadQueue.DeleteConvertedCopy", exception);
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        // Una sola espera del semáforo viva a la vez. Antes cada vuelta creaba otra y, cuando ganaba
        // el Task.Delay, la vieja quedaba colgada y se comía el siguiente Kick: una grabación nueva
        // no despertaba al bucle (encontrado en la prueba real del 2026-11-11).
        Task? wake = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunPendingAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _failure("MeetingUploadQueue.Loop", exception);
            }

            try
            {
                TimeSpan? delay = NextWake();
                wake ??= _wake.WaitAsync(cancellationToken);
                if (delay is { } wait) await Task.WhenAny(wake, Task.Delay(wait, _time, cancellationToken));
                else await wake;
                if (wake.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    wake = null;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Cuánto dormir: hasta el turno más cercano; nada pendiente (o sin sesión) = hasta un <see cref="Kick"/>.</summary>
    private TimeSpan? NextWake()
    {
        if (!_auth.IsSignedIn) return null;

        DateTimeOffset now = _time.GetUtcNow();
        TimeSpan? soonest = null;
        foreach (QueuedMeeting meeting in Items.Where(item => !item.IsFinished))
        {
            TimeSpan wait = _notBefore.TryGetValue(meeting.Id, out DateTimeOffset at) ? Max(at - now, TimeSpan.Zero) : TimeSpan.Zero;
            soonest = soonest is null ? wait : Min(soonest.Value, wait);
        }

        return soonest is null ? null : Max(soonest.Value, TimeSpan.FromSeconds(1));
    }

    private QueuedMeeting? Get(string id)
    {
        lock (_storeLock) return _store.Get(id);
    }

    private QueuedMeeting Save(QueuedMeeting meeting, bool raiseChanged = true)
    {
        lock (_storeLock) _store.Save(meeting);
        if (raiseChanged) Raise(Changed, meeting);
        return meeting;
    }

    private void Raise(EventHandler<QueuedMeeting>? handlers, QueuedMeeting meeting)
    {
        foreach (EventHandler<QueuedMeeting> handler in handlers?.GetInvocationList().Cast<EventHandler<QueuedMeeting>>() ?? [])
        {
            try
            {
                handler(this, meeting);
            }
            catch (Exception exception)
            {
                _failure($"MeetingUploadQueue.Event ({handler.Method.DeclaringType?.Name})", exception);
            }
        }
    }

    private string NewId() => $"{_time.GetLocalNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";

    /// <summary>El texto del servidor, sin comillas de JSON y corto: es para leerlo en una fila.</summary>
    private static string Clean(string message)
    {
        string text = message.Trim().Trim('"').Trim();
        return text.Length <= 240 ? text : text[..240] + "…";
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private const string SignedOutNotice = "Sin sesión en el servidor: inicia sesión para seguir. La grabación está guardada.";
}
