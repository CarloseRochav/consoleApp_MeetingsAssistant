using System.Text;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// Guarda el reporte como Markdown en el vault de Obsidian del usuario.
/// Ruta configurable vía "Storage:VaultPath" / "Storage:SubFolder" — nunca
/// hardcodeada, porque la ruta del vault es específica de la máquina de cada
/// quien y va a cambiar.
///
/// Formato pensado para Obsidian: frontmatter YAML con metadata consultable,
/// checkboxes de tarea (compatibles con el plugin de tasks), sin emojis en
/// headers/callouts.
/// </summary>
public sealed class MarkdownReportStorage : IReportStorage
{
    private readonly string _vaultPath;
    private readonly string _subFolder;

    public MarkdownReportStorage(IConfiguration configuration)
    {
        _vaultPath = configuration["Storage:VaultPath"]
            ?? throw new InvalidOperationException(
                "Falta configurar \"Storage:VaultPath\" en appsettings.json — la ruta de tu vault de Obsidian.");
        _subFolder = configuration["Storage:SubFolder"] ?? "Meetings";
    }

    public async Task<string> SaveAsync(MeetingReport report, CancellationToken cancellationToken = default)
    {
        string targetDir = Path.Combine(_vaultPath, _subFolder);
        Directory.CreateDirectory(targetDir);

        DateTimeOffset generatedAt = report.Metadata?.GeneratedAtUtc ?? DateTimeOffset.UtcNow;
        string fileName = $"meeting-report-{generatedAt:yyyyMMdd-HHmmss}.md";
        string fullPath = Path.Combine(targetDir, fileName);

        string markdown = Render(report, generatedAt);
        await File.WriteAllTextAsync(fullPath, markdown, Encoding.UTF8, cancellationToken);

        return fullPath;
    }

    private static string Render(MeetingReport report, DateTimeOffset generatedAt)
    {
        var sb = new StringBuilder();

        sb.AppendLine("---");
        sb.AppendLine("type: meeting-report");
        sb.AppendLine($"generated: {generatedAt:yyyy-MM-dd HH:mm}");
        if (report.Metadata is { } metadata)
        {
            sb.AppendLine($"llm-provider: {metadata.LlmProvider}");
            sb.AppendLine($"llm-model: {metadata.LlmModel}");
            sb.AppendLine($"prompt-version: {metadata.PromptVersion}");
            sb.AppendLine($"tokens-input: {metadata.InputTokens}");
            sb.AppendLine($"tokens-output: {metadata.OutputTokens}");
            sb.AppendLine($"cost-usd: {metadata.EstimatedCostUsd:F6}");
        }
        sb.AppendLine("---");
        sb.AppendLine();

        sb.AppendLine($"# Meeting report — {generatedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine(report.Summary);
        sb.AppendLine();

        AppendBulletSection(sb, "Insights", report.Insights);
        AppendBulletSection(sb, "Requirements", report.Requirements);
        AppendBulletSection(sb, "Indications", report.Indications);

        sb.AppendLine("## Task list");
        if (report.TaskList.Count == 0)
        {
            sb.AppendLine("- (none)");
        }
        else
        {
            foreach (TaskItem task in report.TaskList)
            {
                sb.AppendLine($"- [ ] **({FormatPriority(task.Priority)})** {task.Task}");
                sb.AppendLine($"  - {task.Context}");
            }
        }
        sb.AppendLine();

        AppendBulletSection(sb, "Open questions", report.OpenQuestions);

        return sb.ToString();
    }

    private static void AppendBulletSection(StringBuilder sb, string title, IReadOnlyList<string> items)
    {
        sb.AppendLine($"## {title}");
        if (items.Count == 0)
        {
            sb.AppendLine("- (none)");
        }
        else
        {
            foreach (string item in items)
            {
                sb.AppendLine($"- {item}");
            }
        }
        sb.AppendLine();
    }

    private static string FormatPriority(Priority priority) => priority switch
    {
        Priority.High => "high",
        Priority.Low => "low",
        _ => "medium"
    };
}
