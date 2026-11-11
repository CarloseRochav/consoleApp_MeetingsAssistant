using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Abstractions;

/// <summary>Dónde se guarda la cola de subidas (Windows: un JSON por reunión). Sobrevive a reinicios.</summary>
public interface IQueuedMeetingStore
{
    IReadOnlyList<QueuedMeeting> All();

    QueuedMeeting? Get(string id);

    void Save(QueuedMeeting meeting);

    void Delete(string id);
}

/// <summary>
/// Sube un archivo a Azure Storage por un enlace SAS, bloque a bloque (Put Block y Put Block List),
/// para que una subida cortada siga donde quedó: la referencia es el <c>BlockUploader</c> del
/// teléfono. Los bloques sin confirmar quedan 7 días en Storage.
/// </summary>
public interface IAudioUploader
{
    /// <param name="alreadySent">Bloques que ya llegaron (de una corrida anterior).</param>
    /// <param name="onBlockSent">Se llama después de cada bloque aceptado; se espera antes de seguir, para guardarlo.</param>
    Task<BlockUploadOutcome> UploadAsync(
        string uploadUrl,
        string filePath,
        int blockBytes,
        IReadOnlySet<int> alreadySent,
        Func<int, Task> onBlockSent,
        CancellationToken cancellationToken = default);
}

public abstract record BlockUploadOutcome
{
    private BlockUploadOutcome()
    {
    }

    /// <summary>Todos los bloques están y la lista se confirmó: el archivo existe.</summary>
    public sealed record Committed : BlockUploadOutcome;

    /// <summary>403: el enlace venció. Hace falta otro (un trabajo nuevo).</summary>
    public sealed record LinkRejected : BlockUploadOutcome;

    /// <summary>Se cortó la conexión. Otra corrida sigue después del último bloque que llegó.</summary>
    public sealed record Interrupted(string Detail) : BlockUploadOutcome;

    /// <summary>Storage lo rechazó por otro motivo.</summary>
    public sealed record Failed(int Code) : BlockUploadOutcome;
}

/// <summary>
/// Convierte una grabación al formato de subida: 16 kHz, 16 bits, mono (como el teléfono). El
/// escritorio graba a 44,1 kHz; convertir reduce la subida ~2,76 veces.
/// </summary>
public interface IAudioConverter
{
    Task<AudioConversion> ConvertForUploadAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default);
}

/// <param name="SourceFormat">El formato de origen en palabras, para el log (p. ej. "44100 Hz, 16 bits, 1 canal").</param>
public sealed record AudioConversion(string TargetPath, TimeSpan Duration, long SourceBytes, long TargetBytes, string SourceFormat);
