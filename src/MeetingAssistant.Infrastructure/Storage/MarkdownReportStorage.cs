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

    public Task<string> SaveAsync(MeetingReport report, CancellationToken cancellationToken = default)
    {
        return SaveMarkdownAsync(MeetingReportMarkdownRenderer.Render(report), report.Metadata, cancellationToken);
    }

    public async Task<string> SaveMarkdownAsync(
        string markdown,
        MeetingReportMetadata? metadata,
        CancellationToken cancellationToken = default)
    {
        string targetDir = Path.Combine(_vaultPath, _subFolder);
        Directory.CreateDirectory(targetDir);

        DateTimeOffset generatedAt = metadata?.GeneratedAtUtc ?? DateTimeOffset.UtcNow;
        string prefix = SanitizeFilePrefix(metadata?.PromptId) ?? "meeting-report";
        string fileName = $"{prefix}-{generatedAt:yyyyMMdd-HHmmss}.md";
        string fullPath = Path.Combine(targetDir, fileName);

        string document = RenderDocument(markdown, metadata, generatedAt);
        await File.WriteAllTextAsync(fullPath, document, Encoding.UTF8, cancellationToken);

        return fullPath;
    }

    private static string RenderDocument(string markdown, MeetingReportMetadata? metadata, DateTimeOffset generatedAt)
    {
        var sb = new StringBuilder();

        sb.AppendLine("---");
        sb.AppendLine(string.IsNullOrWhiteSpace(metadata?.PromptId) || metadata.PromptId == ReportExtractionPrompt.Id
            ? "type: meeting-report"
            : $"type: {metadata.PromptId}");
        sb.AppendLine($"generated: {generatedAt:yyyy-MM-dd HH:mm}");
        if (metadata is not null)
        {
            sb.AppendLine($"llm-provider: {metadata.LlmProvider}");
            sb.AppendLine($"llm-model: {metadata.LlmModel}");
            if (!string.IsNullOrWhiteSpace(metadata.PromptId))
            {
                sb.AppendLine($"prompt-id: {metadata.PromptId}");
            }
            sb.AppendLine($"prompt-version: {metadata.PromptVersion}");
            sb.AppendLine($"tokens-input: {metadata.InputTokens}");
            sb.AppendLine($"tokens-output: {metadata.OutputTokens}");
            sb.AppendLine($"cost-usd: {metadata.EstimatedCostUsd:F6}");
        }
        sb.AppendLine("---");
        sb.AppendLine();

        string body = markdown.TrimStart();
        if (body.StartsWith("---", StringComparison.Ordinal))
        {
            // El renderer no debe incluir frontmatter; si llega uno, se
            // descarta para no duplicar el bloque YAML de arriba.
            int secondFence = body.IndexOf("---", 3, StringComparison.Ordinal);
            if (secondFence >= 0)
            {
                body = body[(secondFence + 3)..].TrimStart();
            }
        }

        sb.Append(body);
        if (!body.EndsWith('\n'))
        {
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string? SanitizeFilePrefix(string? promptId)
    {
        if (string.IsNullOrWhiteSpace(promptId)) return null;

        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(promptId.Length);
        foreach (char c in promptId)
        {
            builder.Append(invalid.Contains(c) ? '-' : c);
        }

        string sanitized = builder.ToString().Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }
}
