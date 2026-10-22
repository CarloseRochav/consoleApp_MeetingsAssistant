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
        return VerifyCostAndTime();
    }

    /// <summary>
    /// Hallazgos de la primera corrida real del worker (2026-10-21): un modelo sin
    /// precio guardaba costo 0 en vez de desconocido, y el título salía en UTC.
    /// </summary>
    private static int VerifyCostAndTime()
    {
        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        Console.WriteLine($"{Environment.NewLine}--- Costo del LLM ---");
        decimal? Cost(Dictionary<string, string?> values) =>
            new ConfigPricingCostEstimator(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
                .EstimateCostUsd("Gemini", "m", 1_000_000, 1_000_000);

        Check("con los dos precios suma entrada y salida",
            Cost(new() { ["Pricing:Gemini:m:InputPerMillion"] = "0.1", ["Pricing:Gemini:m:OutputPerMillion"] = "0.4" }) == 0.5m);
        Check("sin precio: null, no 0", Cost([]) is null);
        Check("con un solo precio: null, no un costo incompleto",
            Cost(new() { ["Pricing:Gemini:m:InputPerMillion"] = "0.1" }) is null);
        Check("el 0 del archivo de ejemplo cuenta como sin precio",
            Cost(new() { ["Pricing:Gemini:m:InputPerMillion"] = "0", ["Pricing:Gemini:m:OutputPerMillion"] = "0" }) is null);

        Console.WriteLine($"{Environment.NewLine}--- Hora del titulo ---");
        var report = new MeetingReport("s", [], [], [], [], [])
        {
            Metadata = new MeetingReportMetadata(
                new DateTimeOffset(2026, 10, 22, 0, 39, 0, TimeSpan.Zero), "Gemini", "m", "v1", 1, 1, EstimatedCostUsd: null)
        };
        TimeZoneInfo pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        Check("sin zona sigue en UTC", MeetingReportMarkdownRenderer.Render(report).Contains("— 2026-10-22 00:39", StringComparison.Ordinal));
        Check("con zona usa la hora local (PDT, -7)",
            MeetingReportMarkdownRenderer.Render(report, pacific).Contains("— 2026-10-21 17:39", StringComparison.Ordinal));

        Console.WriteLine($"{Environment.NewLine}--- Frontmatter del vault ---");
        string vault = Path.Combine(Path.GetTempPath(), $"ma-render-{Guid.NewGuid():N}");
        try
        {
            var storage = new MarkdownReportStorage(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:VaultPath"] = vault }).Build());
            string unknown = File.ReadAllText(storage.SaveAsync(report).GetAwaiter().GetResult());
            string known = File.ReadAllText(storage.SaveAsync(report with { Metadata = report.Metadata! with { EstimatedCostUsd = 0.000123m } })
                .GetAwaiter().GetResult());
            Check("sin precio no escribe cost-usd", !unknown.Contains("cost-usd:", StringComparison.Ordinal));
            Check("con precio lo escribe", known.Contains("cost-usd: 0.000123", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(vault)) Directory.Delete(vault, recursive: true);
        }

        Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
        return failures == 0 ? 0 : 1;
    }

}
