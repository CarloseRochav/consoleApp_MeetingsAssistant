using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Catálogo de prompts de extracción. Core solo conoce definiciones
/// in-process (texto + id + versión); no lee archivos ni SDKs.
/// </summary>
public interface IPromptCatalog
{
    PromptDefinition Default { get; }

    IReadOnlyList<PromptDefinition> GetAll();

    PromptDefinition GetById(string id);
}
