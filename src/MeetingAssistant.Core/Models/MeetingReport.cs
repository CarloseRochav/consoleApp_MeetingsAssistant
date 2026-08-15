// Fase 1 — Contrato del reporte de reunión.
// Corresponde 1:1 con el JSON que el system prompt de extracción le pide al LLM.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingAssistant.Core.Models;

public enum Priority
{
    [JsonPropertyName("baja")]
    Low,
    [JsonPropertyName("media")]
    Medium,
    [JsonPropertyName("alta")]
    High
}

/// <summary>
/// Convierte entre los literales en español que usa el prompt ("alta"/"media"/"baja")
/// y el enum de C#. Se hace explícito en vez de depender de JsonStringEnumConverter
/// genérico porque los nombres del enum (Low/Medium/High) no coinciden textualmente
/// con los valores en español del prompt.
/// </summary>
public sealed class PriorityJsonConverter : JsonConverter<Priority>
{
    public override Priority Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.GetString();
        return value?.Trim().ToLowerInvariant() switch
        {
            "alta" => Priority.High,
            "media" => Priority.Medium,
            "baja" => Priority.Low,
            _ => Priority.Medium // Regla 3 del prompt: ante ausencia/ambigüedad, "media" por defecto.
        };
    }

    public override void Write(Utf8JsonWriter writer, Priority value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            Priority.High => "alta",
            Priority.Low => "baja",
            _ => "media"
        });
    }
}

public sealed record TaskItem(
    [property: JsonPropertyName("task")] string Task,
    [property: JsonPropertyName("priority")] Priority Priority,
    [property: JsonPropertyName("context")] string Context);

public sealed record MeetingReport(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("insights")] IReadOnlyList<string> Insights,
    [property: JsonPropertyName("taskList")] IReadOnlyList<TaskItem> TaskList,
    [property: JsonPropertyName("requirements")] IReadOnlyList<string> Requirements,
    [property: JsonPropertyName("indications")] IReadOnlyList<string> Indications,
    [property: JsonPropertyName("openQuestions")] IReadOnlyList<string> OpenQuestions)
{
    /// <summary>
    /// Metadata generada por el pipeline, no por el LLM — se agrega después de deserializar.
    /// </summary>
    public MeetingReportMetadata? Metadata { get; init; }
}

public sealed record MeetingReportMetadata(
    DateTimeOffset GeneratedAtUtc,
    string LlmProvider,
    string LlmModel,
    string PromptVersion,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCostUsd,
    string? PromptId = null);

/// <summary>
/// Parsing robusto: aunque el prompt prohíbe explícitamente markdown fences y texto
/// adicional, algunos modelos los agregan de todas formas. Esta capa limpia el output
/// antes de deserializar en vez de asumir que el LLM siempre obedece la instrucción al pie
/// de la letra.
/// </summary>
public static class MeetingReportParser
{
    public static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static MeetingReport Parse(string rawLlmOutput)
    {
        if (string.IsNullOrWhiteSpace(rawLlmOutput))
        {
            throw new MeetingReportParseException("El LLM devolvió una respuesta vacía.", rawLlmOutput);
        }

        string cleaned = StripMarkdownFences(rawLlmOutput).Trim();

        try
        {
            MeetingReport? report = JsonSerializer.Deserialize<MeetingReport>(cleaned, SerializerOptions);
            return report ?? throw new MeetingReportParseException(
                "La deserialización produjo un resultado nulo.", cleaned);
        }
        catch (JsonException exception)
        {
            throw new MeetingReportParseException(
                $"El output del LLM no es JSON válido según el schema esperado: {exception.Message}",
                cleaned,
                exception);
        }
    }

    private static string StripMarkdownFences(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```")) return trimmed;

        int firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return trimmed;

        int closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return closingFence > firstNewline
            ? trimmed[(firstNewline + 1)..closingFence].Trim()
            : trimmed[(firstNewline + 1)..].Trim();
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new PriorityJsonConverter());
        return options;
    }
}

public sealed class MeetingReportParseException : Exception
{
    public string RawOutput { get; }

    public MeetingReportParseException(string message, string rawOutput, Exception? innerException = null)
        : base(message, innerException)
    {
        RawOutput = rawOutput;
    }
}
