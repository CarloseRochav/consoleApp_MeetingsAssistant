using Google.GenAI;
using Google.GenAI.Types;

namespace MeetingAssistant.LlmSpike.Services;

/// <summary>
/// Gemini-specific implementation of the provider-neutral LLM client contract.
/// </summary>
internal sealed class GeminiLlmClient : ILlmClient
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

    public async Task<LlmProviderResponse> GenerateAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        GenerateContentResponse response = await client.Models.GenerateContentAsync(
            model: Model,
            contents: request.Prompt,
            config: new GenerateContentConfig { MaxOutputTokens = request.MaxOutputTokens },
            cancellationToken: cancellationToken);

        return new LlmProviderResponse(
            response.Text,
            response.UsageMetadata?.PromptTokenCount ?? 0,
            response.UsageMetadata?.CandidatesTokenCount ?? 0,
            response.UsageMetadata?.ThoughtsTokenCount ?? 0);
    }
}
