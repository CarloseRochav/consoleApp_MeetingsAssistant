using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Escribe en el vault el Markdown de un reporte que hizo el servidor. La implementación concreta
/// (el vault de Obsidian) vive en Infrastructure; Core sólo sabe que "guardar" existe y devuelve
/// dónde quedó. El vault sigue siendo el producto (condición 4 de la Fase 8: el .md lo escribe el
/// cliente).
/// </summary>
public interface IReportStorage
{
    /// <returns>La ruta absoluta donde se guardó el reporte.</returns>
    Task<string> SaveMarkdownAsync(string markdown, ReportFileMetadata metadata, CancellationToken cancellationToken = default);
}
