namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Prompt versionado para extraer el handoff de una feature a partir de la
/// llamada entre el desarrollador y su tech lead. El LLM escribe el reporte
/// en Markdown (requisitos, alcance, riesgos, pasos y criterios de aceptación).
/// </summary>
public static class FeatureHandoffPrompt
{
    public const string Id = "feature-handoff";
    public const string Version = "v1";

    public const string DisplayName = "Handoff de feature (tech lead)";

    public const string Description =
        "Extrae resumen, requisitos técnicos, alcance, riesgos abiertos, pasos de implementación y criterios de aceptación de una llamada con el tech lead.";

    public const string SystemPrompt = """
        You are helping a software developer extract actionable information from a call
        transcript between them and their tech lead about an upcoming feature.

        The transcript below is raw (it may be informal, contain filler words, tangents,
        or off-topic conversation). Your job is to extract only the information relevant
        to the developer who will implement the feature.

        Please produce:

        1. **Feature summary** — a short description of what is being built and why.

        2. **Technical requirements** — a clear, itemized list of what must be implemented,
           including any constraints (e.g., which existing endpoints/logic to reuse, what NOT
           to change, integration approach).

        3. **Scope** — what's explicitly in scope for this phase/release, and what's explicitly
           out of scope or deferred.

        4. **Open risks / unresolved questions** — anything the tech lead flagged as uncertain,
           risky, or "needs to be figured out," stated precisely so nothing gets lost.

        5. **Suggested implementation steps** — a logical order of steps to approach the work,
           based on what was discussed.

        6. **Acceptance criteria** — a checklist of conditions that must be true for the feature
           to be considered done, inferred from what the tech lead described as required
           behavior.

        Ignore small talk, tangents, and anything not relevant to the technical implementation.
        Do not invent requirements that weren't stated or clearly implied — if something is
        ambiguous, flag it under "Open risks / unresolved questions" instead of guessing.

        Output only the report. No preamble, no JSON, no markdown fences around the
        whole document.
        """;
}
