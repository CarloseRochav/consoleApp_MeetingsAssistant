using Markdig;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Convierte el Markdown del reporte a un documento HTML para WebView2.
/// Vive en App: es solo presentación, no forma parte del pipeline.
/// </summary>
public static class MarkdownPreviewRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static string ToHtmlDocument(string? markdown, bool darkTheme)
    {
        string body = string.IsNullOrWhiteSpace(markdown)
            ? "<p class=\"empty\">Todavía no hay un reporte generado.</p>"
            : Markdig.Markdown.ToHtml(StripFrontMatter(markdown), Pipeline);

        // Misma pizarra e índigo que AppTheme.xaml (DevLog), para que el reporte no parezca otra app.
        string background = darkTheme ? "#0f172a" : "#f8fafc";
        string foreground = darkTheme ? "#f1f5f9" : "#1e293b";
        string muted = darkTheme ? "#94a3b8" : "#64748b";
        string border = darkTheme ? "#334155" : "#e2e8f0";
        string codeBackground = darkTheme ? "#1e293b" : "#f1f5f9";
        string tableHeader = darkTheme ? "#1e293b" : "#e2e8f0";
        string link = darkTheme ? "#a5b4fc" : "#4f46e5";

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8" />
            <style>
              html, body {
                margin: 0;
                padding: 0;
                background: {{background}};
                color: {{foreground}};
                font-family: "Segoe UI", sans-serif;
                font-size: 15px;
                line-height: 1.5;
              }
              body { padding: 16px 20px 32px; }
              h1, h2, h3, h4 { line-height: 1.25; margin-top: 1.4em; }
              h1 { font-size: 1.6em; }
              h2 { font-size: 1.3em; border-bottom: 1px solid {{border}}; padding-bottom: 0.2em; }
              p { margin: 0.7em 0; }
              a { color: {{link}}; }
              code {
                font-family: Consolas, "Cascadia Mono", monospace;
                font-size: 0.9em;
                background: {{codeBackground}};
                padding: 0.1em 0.35em;
                border-radius: 4px;
              }
              pre {
                background: {{codeBackground}};
                border: 1px solid {{border}};
                border-radius: 6px;
                padding: 12px;
                overflow-x: auto;
              }
              pre code { padding: 0; background: transparent; }
              table {
                border-collapse: collapse;
                width: 100%;
                margin: 1em 0;
              }
              th, td {
                border: 1px solid {{border}};
                padding: 6px 10px;
                text-align: left;
                vertical-align: top;
              }
              th { background: {{tableHeader}}; }
              blockquote {
                margin: 0.8em 0;
                padding: 0.2em 0.9em;
                border-left: 4px solid {{border}};
                color: {{muted}};
              }
              ul, ol { padding-left: 1.4em; }
              hr { border: none; border-top: 1px solid {{border}}; }
              .empty { color: {{muted}}; }
            </style>
            </head>
            <body>
            {{body}}
            </body>
            </html>
            """;
    }

    private static string StripFrontMatter(string markdown)
    {
        string trimmed = markdown.TrimStart();
        if (!trimmed.StartsWith("---", StringComparison.Ordinal)) return markdown;

        int secondFence = trimmed.IndexOf("---", 3, StringComparison.Ordinal);
        return secondFence < 0 ? markdown : trimmed[(secondFence + 3)..].TrimStart();
    }
}
