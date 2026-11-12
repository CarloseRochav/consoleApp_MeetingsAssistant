using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Services;

/// <summary>Lo que hizo una pasada de <see cref="LocalAudioRetention"/>.</summary>
public sealed record LocalAudioRetentionResult(int Deleted, long BytesFreed, int Failed);

/// <summary>
/// Borra el WAV original de esta máquina N días después de que su reporte llegó al vault (decisión
/// del dueño, 2026-11-11, opción C: 30 días, como el archivo del servidor). Reemplaza la retención
/// del paso anterior, que dependía del Blob propio del escritorio.
///
/// Sólo borra lo que es seguro borrar: la reunión está lista (el .md en el vault y el audio
/// archivado en el servidor), el archivo está dentro de la carpeta de grabaciones de la app (nunca
/// un audio importado de otra carpeta del usuario), y pasaron los días. Con 0 días o menos no borra
/// nada. Nunca lanza: lo que no pudo borrar queda para la próxima pasada.
/// </summary>
public sealed class LocalAudioRetention(IQueuedMeetingStore store, string recordingsDirectory, TimeProvider? time = null, Action<string, Exception>? failure = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public LocalAudioRetentionResult Run(double retentionDays)
    {
        if (retentionDays <= 0) return new LocalAudioRetentionResult(0, 0, 0);

        string root = Path.GetFullPath(recordingsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        DateTimeOffset cutoff = _time.GetUtcNow() - TimeSpan.FromDays(retentionDays);
        int deleted = 0, failed = 0;
        long freed = 0;

        IReadOnlyList<QueuedMeeting> meetings;
        try
        {
            meetings = store.All();
        }
        catch (Exception exception)
        {
            failure?.Invoke("LocalAudioRetention.Read", exception);
            return new LocalAudioRetentionResult(0, 0, 1);
        }

        foreach (QueuedMeeting meeting in meetings)
        {
            if (meeting.Status != QueuedMeetingStatus.Ready || meeting.OriginalAudioPath is not { } path) continue;

            // Las listas antes de que existiera ReadyAtUtc cuentan desde que se grabaron (antes, no después).
            DateTimeOffset readyAt = meeting.ReadyAtUtc ?? meeting.CreatedAtUtc;
            if (readyAt > cutoff) continue;

            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;

            try
            {
                long size = new FileInfo(full).Length;
                File.Delete(full);
                deleted++;
                freed += size;
            }
            catch (Exception exception)
            {
                failed++;
                failure?.Invoke("LocalAudioRetention.Delete", exception);
            }
        }

        return new LocalAudioRetentionResult(deleted, freed, failed);
    }
}
