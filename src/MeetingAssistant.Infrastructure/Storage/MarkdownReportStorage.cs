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

    public async Task<string> SaveMarkdownAsync(
        string markdown,
        ReportFileMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        string targetDir = Path.Combine(_vaultPath, _subFolder);
        Directory.CreateDirectory(targetDir);

        DateTimeOffset generatedAt = metadata.GeneratedAtUtc;
        string prefix = SanitizeFilePrefix(metadata.PromptId) ?? "meeting-report";
        string baseName = $"{prefix}-{generatedAt:yyyyMMdd-HHmmss}";
        byte[] content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
            RenderDocument(markdown, metadata, generatedAt))).ToArray();

        // El nombre sólo es único al segundo: dos reportes del mismo prompt en
        // el mismo segundo (re-extraer dos veces seguidas) pisaban al primero.
        // CreateNew no pisa nunca; si el nombre ya existe se prueba -2, -3...
        for (int attempt = 1; ; attempt++)
        {
            string fullPath = Path.Combine(targetDir, attempt == 1 ? $"{baseName}.md" : $"{baseName}-{attempt}.md");
            try
            {
                await using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await stream.WriteAsync(content, cancellationToken);
                return fullPath;
            }
            catch (IOException) when (File.Exists(fullPath) && attempt < 100)
            {
            }
        }
    }

    private static string RenderDocument(string markdown, ReportFileMetadata metadata, DateTimeOffset generatedAt)
    {
        var sb = new StringBuilder();

        sb.AppendLine("---");
        sb.AppendLine(string.IsNullOrWhiteSpace(metadata.PromptId) || metadata.PromptId == DefaultPromptId
            ? "type: meeting-report"
            : $"type: {metadata.PromptId}");
        sb.AppendLine($"generated: {generatedAt:yyyy-MM-dd HH:mm}");
        AppendIfKnown(sb, "llm-provider", metadata.LlmProvider);
        AppendIfKnown(sb, "llm-model", metadata.LlmModel);
        AppendIfKnown(sb, "prompt-id", metadata.PromptId);
        AppendIfKnown(sb, "prompt-version", metadata.PromptVersion);
        AppendIfKnown(sb, "tokens-input", metadata.InputTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendIfKnown(sb, "tokens-output", metadata.OutputTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Sin precio no se escribe: un 0 en el frontmatter parecería medido.
        AppendIfKnown(sb, "cost-usd", metadata.CostUsd?.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
        // Desde el paso B el reporte vive en DevLog: con esto se encuentra la sesión desde el .md.
        AppendIfKnown(sb, "devlog-session", metadata.IdSession?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendIfKnown(sb, "devlog-report", metadata.IdReport?.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

    /// <summary>El prompt de las reuniones de asignación, el que usa el servidor por defecto: su .md es "meeting-report".</summary>
    public const string DefaultPromptId = "assignment-meeting";

    private static void AppendIfKnown(StringBuilder sb, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine($"{key}: {value}");
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
