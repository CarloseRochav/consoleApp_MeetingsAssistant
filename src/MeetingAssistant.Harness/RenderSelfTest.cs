using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;
using MeetingAssistant.Infrastructure;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Cost;
using MeetingAssistant.Infrastructure.Llm;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using MeetingAssistant.Infrastructure.Transcription;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

/// <summary>--verify-render: el catalogo de prompts y el render a Markdown. Offline.</summary>
internal static class RenderSelfTest
{
    public static int Run()
    {
        IPromptCatalog catalog = new BuiltInPromptCatalog();
        Console.WriteLine("=== Catalogo de prompts ===");
        foreach (PromptDefinition prompt in catalog.GetAll())
        {
            Console.WriteLine($"- {prompt.Id} @{prompt.Version}: {prompt.DisplayName} ({prompt.OutputKind})");
        }

        PromptDefinition functional = catalog.GetById("functional-spec");
        Console.WriteLine();
        Console.WriteLine($"=== Prompt functional-spec @{functional.Version} ===");
        Console.WriteLine(functional.SystemPrompt);

        string[] requiredFragments =
        [
            "same language as the transcript",
            "Executive summary",
            "Identified entities and states",
            "Text-based flow diagram",
            "Business rules and conditions",
            "Ambiguous or pending points to confirm",
            "Agreed actions/decisions"
        ];

        string[] missing = requiredFragments
            .Where(fragment => !functional.SystemPrompt.Contains(fragment, StringComparison.Ordinal))
            .ToArray();

        if (missing.Length > 0)
        {
            Console.Error.WriteLine("El prompt functional-spec no contiene: " + string.Join(", ", missing));
            return 1;
        }

        if (functional.SystemPrompt.Contains("CFS", StringComparison.Ordinal) ||
            functional.Version != FunctionalSpecPrompt.Version)
        {
            Console.Error.WriteLine("El prompt functional-spec no es la versión esperada o todavía menciona un dominio ajeno.");
            return 1;
        }

        Console.WriteLine("OK: catalogo y prompt de especificación funcional.");
        return 0;
    }

}
