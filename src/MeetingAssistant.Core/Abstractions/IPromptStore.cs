namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Una edición de un prompt del catálogo, guardada desde Configuración.
///
/// Las ediciones no pisan nada: cada una es una fila nueva con su propia
/// versión (<c>v1.1</c>, <c>v1.2</c>...). Es lo que permite que cada reporte siga
/// diciendo con qué texto exacto se generó, y que Fase 4 pueda comparar
/// versiones.
///
/// <see cref="BaseVersion"/> es la versión del prompt definido en el código
/// sobre la que se editó. Si una actualización del código trae una versión
/// nueva del prompt, las ediciones hechas sobre la vieja dejan de estar activas:
/// el cambio del código no queda tapado en silencio por un texto viejo.
///
/// Una fila con <see cref="Version"/> y <see cref="SystemPrompt"/> en null es una
/// <b>vuelta al original</b>: desde ese momento manda otra vez el texto del
/// código.
/// </summary>
public sealed record PromptEdit(
    string PromptId,
    string BaseVersion,
    string? Version,
    string? SystemPrompt,
    string? Note,
    DateTimeOffset CreatedAtUtc)
{
    public bool IsRevertToOriginal => SystemPrompt is null;
}

/// <summary>
/// Dónde se guardan las ediciones de prompts. Vive en la misma base que el
/// historial, así que con Postgres las ven todas las máquinas.
/// </summary>
public interface IPromptStore
{
    /// <summary>Todas las ediciones de todos los prompts, de la más vieja a la más nueva.</summary>
    Task<IReadOnlyList<PromptEdit>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Guarda una edición. Lanza si ya existe esa versión para ese prompt: dos
    /// textos distintos con la misma etiqueta harían mentir a los reportes.
    /// </summary>
    Task SaveAsync(PromptEdit edit, CancellationToken cancellationToken = default);
}
