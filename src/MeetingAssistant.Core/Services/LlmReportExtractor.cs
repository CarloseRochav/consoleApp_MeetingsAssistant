using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Extrae un MeetingReport estructurado a partir de una transcripción cruda,
/// usando un ILlmClient de bajo nivel por debajo. A diferencia de ILlmClient
/// (que solo sabe hablar con un proveedor), esto sabe CÓMO pedirle al LLM que
/// haga extracción de reuniones — el prompt, el parsing, y el costo.
/// </summary>
public interface ILlmReportExtractor
{
    Task<ExtractionResult> ExtractAsync(
        string transcript,
        string? promptId = null,
        CancellationToken cancellationToken = default);
}

public sealed record ExtractionResult(
    string MarkdownBody,
    MeetingReport? StructuredReport,
    MeetingReportMetadata Metadata,
    PromptDefinition Prompt);

/// <summary>
/// Prompt de extracción versionado. Se mantiene como constante nombrada (no un
/// literal inline dentro del extractor) para que cada MeetingReport generado
/// pueda trazarse a la versión exacta de prompt que lo produjo — indispensable
/// si vas a comparar calidad entre iteraciones del prompt o entre proveedores.
/// </summary>
public static class ReportExtractionPrompt
{
    public const string Id = "assignment-meeting";
    public const string Version = "v1";

    public const string SystemPrompt = """
        Eres un asistente que ayuda a un desarrollador de software a extraer
        información accionable de transcripciones de reuniones de asignación de
        trabajo. Las reuniones discuten nuevos desarrollos o implementaciones:
        requerimientos, requisitos técnicos e indicaciones de cómo proceder.

        Tu única salida debe ser un objeto JSON válido, sin texto adicional, sin
        explicaciones, sin marcadores de markdown (nada de ```json). Si no puedes
        producir JSON válido, es una falla — no hay salida aceptable de respaldo.

        Reglas de extracción:

        1. NO inventes información que no esté explícita o razonablemente implícita
           en la transcripción. Si algo quedó ambiguo, sin resolver, o el hablante
           se contradijo, regístralo en "openQuestions" en vez de asumir una
           respuesta.

        2. Distingue con cuidado dos categorías que suelen mezclarse en el habla
           natural:
           - "requirements": qué debe hacer el sistema o la funcionalidad
             (comportamiento esperado, reglas de negocio, casos de uso).
           - "indications": cómo se espera que procedas (convenciones a seguir,
             restricciones de tiempo o alcance, preferencias del equipo,
             dependencias con otras personas o sistemas, contexto organizacional).

        3. Para "taskList", cada tarea debe ser una acción concreta y ejecutable
           (evita ítems vagos como "revisar el tema de X" sin especificar qué
           acción se espera). Infiere prioridad ("alta", "media", "baja") a partir
           de señales explícitas en el lenguaje (urgencia, fechas mencionadas,
           énfasis del hablante). Si no hay señal clara de prioridad, usa "media"
           por defecto — no la omitas ni la inventes como "alta".

        4. Conserva términos técnicos, nombres propios, nombres de servicios,
           variables, tablas, endpoints, siglas y jerga de desarrollo tal como
           aparecen en la transcripción, sin traducirlos ni "corregirlos".

        5. La transcripción puede mezclar español e inglés en la misma oración.
           Esto es normal, no es un error de transcripción — interpreta el
           contenido en ambos idiomas con la misma atención.

        6. "insights" son observaciones que no son ni un requerimiento ni una
           indicación explícita, pero que aportan contexto útil: señales de
           prioridad del negocio, riesgos mencionados de pasada, dependencias no
           confirmadas, o cambios de dirección respecto a conversaciones previas
           si se mencionan.

        Devuelve exactamente esta estructura:

        {
          "summary": "string — resumen ejecutivo de 3-5 oraciones de la reunión",
          "insights": ["string"],
          "taskList": [
            {
              "task": "string — acción concreta y ejecutable",
              "priority": "alta | media | baja",
              "context": "string — por qué existe esta tarea o de qué depende"
            }
          ],
          "requirements": ["string"],
          "indications": ["string"],
          "openQuestions": ["string — temas ambiguos, sin resolver, o contradictorios"]
        }
        """;
}

public sealed class LlmReportExtractor : ILlmReportExtractor
{
    private readonly ILlmClient _llmClient;
    private readonly ICostEstimator _costEstimator;
    private readonly IPromptCatalog _promptCatalog;
    private readonly int _maxOutputTokens;

    public LlmReportExtractor(
        ILlmClient llmClient,
        ICostEstimator costEstimator,
        IPromptCatalog promptCatalog,
        int maxOutputTokens = 4096)
    {
        _llmClient = llmClient;
        _costEstimator = costEstimator;
        _promptCatalog = promptCatalog;
        _maxOutputTokens = maxOutputTokens;
    }

    public async Task<ExtractionResult> ExtractAsync(
        string transcript,
        string? promptId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            throw new ArgumentException("El transcript no puede estar vacío.", nameof(transcript));
        }

        PromptDefinition prompt = string.IsNullOrWhiteSpace(promptId)
            ? _promptCatalog.Default
            : _promptCatalog.GetById(promptId);

        string fullPrompt = $"{prompt.SystemPrompt}\n\n--- TRANSCRIPCIÓN ---\n\n{transcript}";

        LlmProviderResponse response = await _llmClient.GenerateAsync(
            new LlmRequest(fullPrompt, _maxOutputTokens),
            cancellationToken);

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            throw new InvalidOperationException(
                $"{_llmClient.Provider} ({_llmClient.Model}) no devolvió texto para extraer el reporte.");
        }

        decimal estimatedCost = _costEstimator.EstimateCostUsd(
            _llmClient.Provider, _llmClient.Model, response.InputTokens, response.OutputTokens);

        var metadata = new MeetingReportMetadata(
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            LlmProvider: _llmClient.Provider,
            LlmModel: _llmClient.Model,
            PromptVersion: prompt.Version,
            InputTokens: response.InputTokens,
            OutputTokens: response.OutputTokens,
            EstimatedCostUsd: estimatedCost,
            PromptId: prompt.Id);

        return prompt.OutputKind switch
        {
            PromptOutputKind.StructuredMeetingReport => BuildMeetingReport(response.Text, metadata, prompt),
            PromptOutputKind.FunctionalSpecification => BuildFunctionalSpec(response.Text, metadata, prompt),
            _ => throw new InvalidOperationException($"Output kind no soportado: {prompt.OutputKind}.")
        };
    }

    private static ExtractionResult BuildMeetingReport(string raw, MeetingReportMetadata metadata, PromptDefinition prompt)
    {
        MeetingReport report = MeetingReportParser.Parse(raw) with { Metadata = metadata };
        return new ExtractionResult(MeetingReportMarkdownRenderer.Render(report), report, metadata, prompt);
    }

    private static ExtractionResult BuildFunctionalSpec(string raw, MeetingReportMetadata metadata, PromptDefinition prompt)
    {
        string markdown = StripOuterMarkdownFence(raw);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new MeetingReportParseException(
                "El LLM devolvió una respuesta vacía para la especificación funcional.",
                raw);
        }

        return new ExtractionResult(markdown, null, metadata, prompt);
    }

    private static string StripOuterMarkdownFence(string text)
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
}
