using System.Text;

namespace MeetingAssistant.Core.Models;

/// <summary>
/// Render del MeetingReport a Markdown. Vive en Core (sin I/O) para que
/// extractor, storage y UI muestren el mismo documento.
/// </summary>
public static class MeetingReportMarkdownRenderer
{
    public static string Render(MeetingReport report)
    {
        DateTimeOffset generatedAt = report.Metadata?.GeneratedAtUtc ?? DateTimeOffset.UtcNow;
        var sb = new StringBuilder();

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
