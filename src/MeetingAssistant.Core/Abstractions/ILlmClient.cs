namespace MeetingAssistant.Core.Abstractions;

public interface ILlmClient
{
    string Provider { get; }
    string Model { get; }

    Task<LlmProviderResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default);
}

public sealed record LlmRequest(string Prompt, int MaxOutputTokens);

public sealed record LlmProviderResponse(
    string? Text,
    int InputTokens = 0,
    int OutputTokens = 0,
    int ThinkingTokens = 0);
