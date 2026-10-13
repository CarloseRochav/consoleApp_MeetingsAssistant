namespace MeetingAssistant.Core.Abstractions;

/// <summary>Qué se está archivando. Decide a qué contenedor va.</summary>
public enum ArchiveKind
{
    Audio,
    Report
}

public enum ArchiveUploadState
{
    Pending,
    Uploaded,
    Failed
}

/// <summary>
/// Copia un archivo local a almacenamiento en la nube. La implementación
/// concreta (Azure Blob, o lo que sea) vive en Infrastructure.
///
/// Sólo sabe subir <b>un</b> archivo, una vez. Reintentar, recordar qué quedó
/// pendiente y avisar cuando algo falla es trabajo de
/// <c>ArchiveUploadService</c>, que no conoce ningún SDK.
/// </summary>
public interface ICloudArchive
{
    /// <summary>
    /// <c>false</c> cuando no hay credencial configurada. Archivar es opcional:
    /// sin configurar, la app graba y guarda en el vault igual que siempre.
    /// </summary>
    bool IsConfigured { get; }

    /// <returns>La URL del objeto subido, sin credenciales.</returns>
    Task<string> UploadAsync(
        ArchiveKind kind,
        string localPath,
        string remoteName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tamaño en bytes del objeto remoto, o <c>null</c> si no existe. Es lo que
    /// permite comprobar contra la nube, y no contra la base, que un archivo
    /// está respaldado antes de borrar su copia local.
    /// </summary>
    Task<long?> GetRemoteSizeAsync(
        ArchiveKind kind,
        string remoteName,
        CancellationToken cancellationToken = default);
}

/// <summary>Una subida, con su estado. Fila de <c>cloud_upload</c>.</summary>
public sealed record ArchiveUpload(
    long Id,
    long SessionId,
    ArchiveKind Kind,
    string LocalPath,
    string RemoteName,
    ArchiveUploadState State,
    int Attempts,
    string? RemoteUrl,
    string? LastError);

/// <summary>
/// La cola persistente de subidas. Es lo que hace que una subida fallida se vea
/// y se pueda reintentar después de reiniciar la app: sin esto, un WAV que no
/// llegó a la nube no dejaría ningún rastro de que faltaba.
/// </summary>
public interface IArchiveUploadStore
{
    /// <summary>
    /// Registra la subida como pendiente y devuelve la fila. Es idempotente por
    /// (tipo, ruta local): pedir dos veces el mismo archivo devuelve la fila que
    /// ya existía, con el estado que tenga.
    /// </summary>
    Task<ArchiveUpload> EnqueueAsync(
        long sessionId,
        ArchiveKind kind,
        string localPath,
        string remoteName,
        CancellationToken cancellationToken = default);

    Task MarkUploadedAsync(long uploadId, string remoteUrl, CancellationToken cancellationToken = default);

    /// <summary>Suma un intento y deja el error del último.</summary>
    Task MarkFailedAsync(long uploadId, string error, CancellationToken cancellationToken = default);

    /// <summary>Todo lo que no terminó: pendiente o fallido.</summary>
    Task<IReadOnlyList<ArchiveUpload>> ListUnfinishedAsync(CancellationToken cancellationToken = default);

    /// <summary>Las subidas terminadas de un tipo. Las usa la retención de audio local.</summary>
    Task<IReadOnlyList<ArchiveUpload>> ListUploadedAsync(ArchiveKind kind, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lo que el pipeline necesita saber de todo esto: que puede pedir que algo se
/// archive. No espera, no se entera del resultado y <b>no puede fallar</b> por
/// culpa de la subida.
/// </summary>
public interface IArchiveQueue
{
    void Enqueue(long sessionId, ArchiveKind kind, string localPath);
}
