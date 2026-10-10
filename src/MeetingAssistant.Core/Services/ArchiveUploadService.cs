using MeetingAssistant.Core.Abstractions;

namespace MeetingAssistant.Core.Services;

public sealed class ArchiveUploadFailedEventArgs : EventArgs
{
    public ArchiveUploadFailedEventArgs(ArchiveKind kind, string localPath, string error)
    {
        Kind = kind;
        LocalPath = localPath;
        Error = error;
    }

    public ArchiveKind Kind { get; }
    public string LocalPath { get; }
    public string Error { get; }
}

/// <summary>
/// Sube a la nube, en segundo plano, el audio crudo y los <c>.md</c> de los
/// reportes. Sólo compone abstracciones: no conoce Azure.
///
/// Tres reglas, las tres por el mismo motivo —la subida es un respaldo, no parte
/// de la reunión—:
/// <list type="bullet">
/// <item><b>Nunca lanza hacia quien encola.</b> Un fallo acá no puede tumbar una
/// grabación ni el arranque.</item>
/// <item><b>Una subida a la vez.</b> Un WAV pesa unos 320 MB por hora; subir
/// varios en paralelo sólo los haría competir por el mismo ancho de banda, y con
/// la llamada a la transcripción.</item>
/// <item><b>Todo intento deja rastro en la base.</b> Lo que no llegó se ve en el
/// historial y se reintenta al próximo arranque.</item>
/// </list>
/// </summary>
public sealed class ArchiveUploadService : IArchiveQueue
{
    /// <summary>
    /// Esperas entre intentos. Cuatro intentos en total: el SDK ya reintenta
    /// solo los errores transitorios de una llamada, así que esto cubre lo que
    /// dura más — la red caída un rato, el portátil saliendo de suspensión.
    /// </summary>
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2)
    ];

    private readonly ICloudArchive _archive;
    private readonly IArchiveUploadStore _store;
    private readonly Action<string, Exception>? _onFailure;
    private readonly TimeSpan[] _retryDelays;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly object _pendingLock = new();
    private Task _pending = Task.CompletedTask;

    public ArchiveUploadService(
        ICloudArchive archive,
        IArchiveUploadStore store,
        Action<string, Exception>? onFailure = null,
        TimeSpan[]? retryDelays = null)
    {
        _archive = archive;
        _store = store;
        _onFailure = onFailure;
        _retryDelays = retryDelays ?? DefaultRetryDelays;
    }

    /// <summary>
    /// Una subida agotó sus intentos. Se levanta desde un hilo de fondo. No se
    /// levanta para lo que se retoma al arrancar: si la credencial está mal,
    /// eso sería una notificación por cada archivo pendiente en cada arranque.
    /// </summary>
    public event EventHandler<ArchiveUploadFailedEventArgs>? UploadFailed;

    public void Enqueue(long sessionId, ArchiveKind kind, string localPath)
    {
        if (!_archive.IsConfigured || string.IsNullOrWhiteSpace(localPath)) return;

        Track(Task.Run(async () =>
        {
            try
            {
                ArchiveUpload upload = await _store.EnqueueAsync(
                    sessionId, kind, localPath, BuildRemoteName(localPath, DateTimeOffset.UtcNow));

                if (upload.State == ArchiveUploadState.Uploaded) return;

                await UploadWithRetriesAsync(upload, notify: true);
            }
            catch (Exception exception)
            {
                // La base falló al registrar la subida. Sin fila no hay cola que
                // retomar, así que al menos queda en el log.
                _onFailure?.Invoke("ArchiveUploadService.Enqueue", exception);
            }
        }));
    }

    /// <summary>
    /// Retoma lo que quedó pendiente o fallido de una corrida anterior. Se llama
    /// una vez al arrancar. Nunca lanza.
    /// </summary>
    public Task ResumeUnfinishedAsync()
    {
        if (!_archive.IsConfigured) return Task.CompletedTask;

        Task resume = Task.Run(async () =>
        {
            try
            {
                foreach (ArchiveUpload upload in await _store.ListUnfinishedAsync())
                {
                    await UploadWithRetriesAsync(upload, notify: false);
                }
            }
            catch (Exception exception)
            {
                _onFailure?.Invoke("ArchiveUploadService.ResumeUnfinished", exception);
            }
        });

        Track(resume);
        return resume;
    }

    /// <summary>Termina cuando no queda ninguna subida en curso. Para tests y para el cierre.</summary>
    public Task WhenIdleAsync()
    {
        lock (_pendingLock) return _pending;
    }

    private void Track(Task task)
    {
        lock (_pendingLock) _pending = Task.WhenAll(_pending, task);
    }

    private async Task UploadWithRetriesAsync(ArchiveUpload upload, bool notify)
    {
        await _oneAtATime.WaitAsync();
        try
        {
            string lastError = "sin intentos";

            for (int attempt = 0; attempt <= _retryDelays.Length; attempt++)
            {
                if (!File.Exists(upload.LocalPath))
                {
                    // Reintentar no va a hacer aparecer el archivo.
                    lastError = "El archivo local ya no existe.";
                    await _store.MarkFailedAsync(upload.Id, lastError);
                    break;
                }

                try
                {
                    string remoteUrl = await _archive.UploadAsync(upload.Kind, upload.LocalPath, upload.RemoteName);
                    await _store.MarkUploadedAsync(upload.Id, remoteUrl);
                    return;
                }
                catch (Exception exception)
                {
                    lastError = exception.Message;
                    _onFailure?.Invoke($"ArchiveUploadService.Upload(intento {attempt + 1})", exception);
                    await _store.MarkFailedAsync(upload.Id, lastError);
                }

                if (attempt < _retryDelays.Length) await Task.Delay(_retryDelays[attempt]);
            }

            if (notify)
            {
                UploadFailed?.Invoke(this, new ArchiveUploadFailedEventArgs(upload.Kind, upload.LocalPath, lastError));
            }
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// <c>2026/10/meeting-20261009-131511.wav</c>. El nombre del archivo ya es
    /// único (lleva fecha y hora), y el prefijo por año y mes es sólo para que el
    /// contenedor se pueda recorrer a ojo. Se fija al encolar y se guarda en la
    /// fila, así que un reintento sube siempre al mismo nombre.
    /// </summary>
    internal static string BuildRemoteName(string localPath, DateTimeOffset nowUtc) =>
        $"{nowUtc.UtcDateTime:yyyy}/{nowUtc.UtcDateTime:MM}/{Path.GetFileName(localPath)}";
}
