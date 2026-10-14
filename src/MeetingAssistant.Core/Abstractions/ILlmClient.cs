namespace MeetingAssistant.Core.Abstractions;

public interface ILlmClient
{
    string Provider { get; }
    string Model { get; }

    Task<LlmProviderResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default);
}

public sealed record LlmRequest(string Prompt, int MaxOutputTokens);

/// <summary>
/// <paramref name="OutputTokens"/> es <b>todo</b> lo que se cobra como salida,
/// razonamiento incluido: es lo que se multiplica por el precio de salida.
/// <paramref name="ThinkingTokens"/> es sólo informativo, la parte de
/// <paramref name="OutputTokens"/> que fue razonamiento. Cada proveedor lo cuenta
/// distinto (Foundry ya incluye el razonamiento en la salida; Gemini no), así
/// que el cliente es quien lo deja en esta forma.
/// </summary>
public sealed record LlmProviderResponse(
    string? Text,
    int InputTokens = 0,
    int OutputTokens = 0,
    int ThinkingTokens = 0);
