using Google.GenAI;
using Google.GenAI.Types;
using MeetingAssistant.Core.Abstractions;

namespace MeetingAssistant.Infrastructure.Llm;

public sealed class GeminiLlmClient : ILlmClient
{
    private readonly Client client;

    public GeminiLlmClient(string apiKey, string model = "gemini-3.5-flash-lite")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        client = new Client(apiKey: apiKey);
        Model = model;
    }

    public string Provider => "Gemini";
    public string Model { get; }

    public async Task<LlmProviderResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        GenerateContentResponse response = await client.Models.GenerateContentAsync(
            model: Model,
            contents: request.Prompt,
            config: new GenerateContentConfig { MaxOutputTokens = request.MaxOutputTokens },
            cancellationToken: cancellationToken);

        // Gemini cuenta el razonamiento aparte de la respuesta
        // (CandidatesTokenCount no lo incluye) pero lo cobra al precio de
        // salida. Se suma para que la salida signifique lo mismo que en Foundry.
        int thinkingTokens = response.UsageMetadata?.ThoughtsTokenCount ?? 0;
        return new LlmProviderResponse(
            response.Text,
            response.UsageMetadata?.PromptTokenCount ?? 0,
            (response.UsageMetadata?.CandidatesTokenCount ?? 0) + thinkingTokens,
            thinkingTokens);
    }
}
