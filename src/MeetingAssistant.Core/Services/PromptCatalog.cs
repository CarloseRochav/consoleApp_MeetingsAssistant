using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Abstractions;

public sealed class BuiltInPromptCatalog : IPromptCatalog
{
    public static readonly PromptDefinition AssignmentMeeting = new(
        Id: ReportExtractionPrompt.Id,
        DisplayName: "Asignación de trabajo",
        Description: "Extrae resumen, tareas, requerimientos e indicaciones de una reunión de asignación.",
        Version: ReportExtractionPrompt.Version,
        SystemPrompt: ReportExtractionPrompt.SystemPrompt,
        OutputKind: PromptOutputKind.StructuredMeetingReport);

    public static readonly PromptDefinition FunctionalSpec = new(
        Id: FunctionalSpecPrompt.Id,
        DisplayName: FunctionalSpecPrompt.DisplayName,
        Description: FunctionalSpecPrompt.Description,
        Version: FunctionalSpecPrompt.Version,
        SystemPrompt: FunctionalSpecPrompt.SystemPrompt,
        OutputKind: PromptOutputKind.FunctionalSpecification);

    public static readonly PromptDefinition FeatureHandoff = new(
        Id: FeatureHandoffPrompt.Id,
        DisplayName: FeatureHandoffPrompt.DisplayName,
        Description: FeatureHandoffPrompt.Description,
        Version: FeatureHandoffPrompt.Version,
        SystemPrompt: FeatureHandoffPrompt.SystemPrompt,
        OutputKind: PromptOutputKind.FunctionalSpecification);

    private static readonly IReadOnlyList<PromptDefinition> All = [AssignmentMeeting, FunctionalSpec, FeatureHandoff];

    public PromptDefinition Default => AssignmentMeeting;

    public IReadOnlyList<PromptDefinition> GetAll() => All;

    public PromptDefinition GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Default;
        }

        PromptDefinition? match = All.FirstOrDefault(prompt =>
            string.Equals(prompt.Id, id, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new ArgumentException(
            $"No existe un prompt con id '{id}'. Prompts disponibles: {string.Join(", ", All.Select(p => p.Id))}.",
            nameof(id));
    }
}
