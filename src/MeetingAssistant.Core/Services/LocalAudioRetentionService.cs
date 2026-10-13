using MeetingAssistant.Core.Abstractions;

namespace MeetingAssistant.Core.Services;

/// <summary>Un audio que cumplía el plazo y no se borró, con el motivo.</summary>
public sealed record RetainedAudio(string LocalPath, string Reason);

/// <summary>Lo que hizo, o haría, una pasada de retención.</summary>
public sealed record AudioRetentionResult(
    IReadOnlyList<string> Deleted,
    long BytesFreed,
    int StillWithinRetention,
    IReadOnlyList<RetainedAudio> Kept);

/// <summary>
/// Borra los WAV locales que ya están respaldados en la nube y pasaron el plazo
/// de retención (Fase 6, paso 5). Sólo compone abstracciones.
///
/// Es lo único de toda la app que <b>borra archivos del usuario</b>, así que un
/// audio se borra sólo si se cumplen las cinco condiciones, todas:
/// <list type="number">
/// <item>Hay una subida registrada como terminada para ese archivo.</item>
/// <item><b>El blob existe ahora mismo y pesa lo mismo que el archivo local.</b>
/// No alcanza con que la base diga "subido": se le pregunta a la nube en el
/// momento de borrar. Una fila es una afirmación; esto es la comprobación.</item>
/// <item>El archivo está dentro de la carpeta de audio de la app. Un audio
/// importado desde otra carpeta del usuario <b>nunca</b> se toca, aunque se
/// haya subido.</item>
/// <item>Pasó el plazo de retención desde su última modificación.</item>
/// <item>El plazo es mayor que cero. Sin plazo configurado no se borra nada.</item>
/// </list>
/// Ante cualquier duda —error de red, tamaños distintos, blob ausente— el
/// archivo se queda y se anota por qué.
/// </summary>
public sealed class LocalAudioRetentionService
{
    private readonly ICloudArchive _archive;
    private readonly IArchiveUploadStore _store;
    private readonly string _audioDirectory;
    private readonly Action<string, Exception>? _onFailure;

    public LocalAudioRetentionService(
        ICloudArchive archive,
        IArchiveUploadStore store,
        string audioDirectory,
        Action<string, Exception>? onFailure = null)
    {
        _archive = archive;
        _store = store;
        _audioDirectory = Path.GetFullPath(audioDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _onFailure = onFailure;
    }

    /// <param name="apply"><c>false</c> sólo informa lo que borraría.</param>
    /// <param name="now">Para los tests. Por defecto, ahora.</param>
    /// <remarks>Nunca lanza.</remarks>
    public async Task<AudioRetentionResult> RunAsync(
        TimeSpan retention,
        bool apply,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var deleted = new List<string>();
        var kept = new List<RetainedAudio>();
        long bytesFreed = 0;
        int stillWithinRetention = 0;

        if (retention <= TimeSpan.Zero || !_archive.IsConfigured)
        {
            return new AudioRetentionResult(deleted, 0, 0, kept);
        }

        DateTimeOffset cutoff = (now ?? DateTimeOffset.UtcNow) - retention;

        IReadOnlyList<ArchiveUpload> uploads;
        try
        {
            uploads = await _store.ListUploadedAsync(ArchiveKind.Audio, cancellationToken);
        }
        catch (Exception exception)
        {
            _onFailure?.Invoke("LocalAudioRetentionService.ListUploaded", exception);
            return new AudioRetentionResult(deleted, 0, 0, kept);
        }

        foreach (ArchiveUpload upload in uploads)
        {
            try
            {
                // Ya no está: borrado en una pasada anterior, o por el usuario.
                if (!File.Exists(upload.LocalPath)) continue;

                string fullPath = Path.GetFullPath(upload.LocalPath);
                if (!fullPath.StartsWith(_audioDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    // No es ruido que valga reportar en cada pasada: es la regla.
                    continue;
                }

                var file = new FileInfo(fullPath);
                if (file.LastWriteTimeUtc > cutoff.UtcDateTime)
                {
                    stillWithinRetention++;
                    continue;
                }

                long? remoteSize = await _archive.GetRemoteSizeAsync(upload.Kind, upload.RemoteName, cancellationToken);
                if (remoteSize is null)
                {
                    kept.Add(new RetainedAudio(fullPath, "La base lo da por subido, pero el archivo no está en la nube."));
                    continue;
                }

                if (remoteSize.Value != file.Length)
                {
                    kept.Add(new RetainedAudio(
                        fullPath,
                        $"El tamaño en la nube ({remoteSize.Value:N0} bytes) no coincide con el local ({file.Length:N0} bytes)."));
                    continue;
                }

                long size = file.Length;
                if (apply) file.Delete();

                deleted.Add(fullPath);
                bytesFreed += size;
            }
            catch (Exception exception)
            {
                _onFailure?.Invoke("LocalAudioRetentionService.Check", exception);
                kept.Add(new RetainedAudio(upload.LocalPath, $"No se pudo comprobar o borrar: {exception.Message}"));
            }
        }

        return new AudioRetentionResult(deleted, bytesFreed, stillWithinRetention, kept);
    }
}
