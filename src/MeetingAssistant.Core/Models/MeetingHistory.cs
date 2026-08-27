namespace MeetingAssistant.Core.Models;

/// <summary>
/// De dónde salió una grabación. Se guarda porque hoy no queda rastro de ello y
/// es lo primero que uno quiere saber al mirar el historial: si una reunión la
/// disparó el hotkey, la bandeja, el endpoint HTTP o una importación de archivo.
/// </summary>
public static class SessionSource
{
    public const string Hotkey = "hotkey";
    public const string Tray = "tray";
    public const string Http = "http";
    public const string Window = "window";
    public const string Import = "import";
}

/// <summary>Una grabación, con o sin transcript y reportes todavía.</summary>
public sealed record SessionRecord(
    long Id,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string? AudioPath,
    TimeSpan? Duration,
    string Source);

/// <summary>
/// El transcript de una sesión. Hoy esto es efímero — vive durante el pipeline y
/// se pierde —, y guardarlo es lo que habilita buscar por contenido y volver a
/// extraer con otro prompt más adelante.
/// </summary>
public sealed record TranscriptRecord(
    long SessionId,
    string Text,
    string? Provider,
    string? Model,
    decimal? CostUsd,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Un reporte ya generado. <see cref="StructuredJson"/> sólo viene con el prompt
/// <c>assignment-meeting</c>, que es el único que produce un
/// <see cref="MeetingReport"/> estructurado; el resto del catálogo devuelve
/// Markdown suelto y lo dejan en null.
///
/// <see cref="VaultPath"/> es dónde quedó el <c>.md</c> exportado. El vault sigue
/// siendo el producto: esto es el registro, no el reemplazo.
/// </summary>
public sealed record ReportRecord(
    long Id,
    long SessionId,
    string PromptId,
    string? PromptVersion,
    string Markdown,
    string? StructuredJson,
    string? LlmProvider,
    string? LlmModel,
    int? InputTokens,
    int? OutputTokens,
    decimal? CostUsd,
    string? VaultPath,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Un reporte todavía sin guardar. Es un tipo aparte de
/// <see cref="ReportRecord"/> a propósito: un record de escritura con un
/// <c>Id</c> que nadie rellena es una invitación a usarlo mal.
/// </summary>
public sealed record NewReport(
    long SessionId,
    string PromptId,
    string? PromptVersion,
    string Markdown,
    string? StructuredJson,
    string? LlmProvider,
    string? LlmModel,
    int? InputTokens,
    int? OutputTokens,
    decimal? CostUsd,
    string? VaultPath,
    DateTimeOffset CreatedAtUtc);

/// <summary>Fila del listado de historial. Proyección, no la sesión entera.</summary>
public sealed record SessionSummary(
    long SessionId,
    DateTimeOffset StartedAtUtc,
    TimeSpan? Duration,
    string Source,
    int ReportCount,
    decimal TotalCostUsd,
    string? TranscriptPreview);

/// <summary>Un resultado de búsqueda, con el fragmento donde apareció el término.</summary>
public sealed record TranscriptSearchHit(
    long SessionId,
    DateTimeOffset StartedAtUtc,
    string Snippet);

/// <summary>
/// Lo que Fase 4 pide como "revisar costo real acumulado vs. estimado". Hoy el
/// dato ya se genera en el frontmatter de cada reporte y nadie lo consulta.
/// </summary>
public sealed record CostSummary(
    int SessionCount,
    int ReportCount,
    decimal TotalCostUsd,
    DateTimeOffset? FirstReportUtc,
    DateTimeOffset? LastReportUtc);

/// <summary>
/// Uso y costo por prompt y versión — el otro pedido de Fase 4: "comparar
/// calidad de reportes entre versiones de prompt". Esto da el lado medible
/// (cuántos, cuánto costaron, qué tan largos salen); la calidad sigue siendo
/// juicio humano, pero al menos deja de ser también un recuento manual.
/// </summary>
public sealed record PromptUsageSummary(
    string PromptId,
    string? PromptVersion,
    int ReportCount,
    decimal TotalCostUsd,
    double AverageOutputTokens,
    DateTimeOffset? LastUsedUtc);
