namespace MeetingAssistant.Core.Models;

public enum PromptOutputKind
{
    StructuredMeetingReport,
    FunctionalSpecification
}

/// <summary>
/// Entrada del catálogo de prompts. El id + la versión viajan en el
/// metadata de cada reporte para poder comparar calidad entre iteraciones.
/// </summary>
public sealed record PromptDefinition(
    string Id,
    string DisplayName,
    string Description,
    string Version,
    string SystemPrompt,
    PromptOutputKind OutputKind);
