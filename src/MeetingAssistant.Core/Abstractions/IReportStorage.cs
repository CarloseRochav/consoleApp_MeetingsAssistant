using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Persiste un MeetingReport ya generado. La implementación concreta (formato,
/// destino — Obsidian vault, SQLite, lo que sea) vive en Infrastructure; Core
/// solo sabe que "guardar" existe y devuelve dónde quedó.
/// </summary>
public interface IReportStorage
{
    /// <returns>La ruta absoluta donde se guardó el reporte.</returns>
    Task<string> SaveAsync(MeetingReport report, CancellationToken cancellationToken = default);
}
