using System.Diagnostics;

namespace MeetingAssistant.LlmSpike.Services;

/// <summary>
/// Contains the application use case for producing a meeting summary. It does not
/// depend on a specific LLM SDK; providers are supplied through <see cref="ILlmClient"/>.
/// </summary>
internal sealed class LLMService
{
    private readonly ILlmClient client;

    public LLMService(ILlmClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Provider => client.Provider;

    public string Model => client.Model;

    public async Task<LlmResponse> SummarizeAsync(string transcript, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);

        const string instruction = "Resume la siguiente transcripcion de reunion en exactamente 3 puntos breves. " +
            "Conserva los nombres, decisiones y pendientes cuando existan.\n\n";

        var stopwatch = Stopwatch.StartNew();
        LlmProviderResponse response = await client.GenerateAsync(
            new LlmRequest(instruction + transcript, MaxOutputTokens: 300),
            cancellationToken);
        stopwatch.Stop();

        string answer = response.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new InvalidOperationException($"{Provider} no devolvio texto para el resumen.");
        }

        return new LlmResponse(
            answer,
            stopwatch.Elapsed,
            response.InputTokens,
            response.OutputTokens,
            response.ThinkingTokens);
    }
}

/// <summary>
/// Provider boundary. Implement this interface for Gemini, Azure AI Foundry, or
/// another model host without changing the application service.
/// </summary>
internal interface ILlmClient
{
    string Provider { get; }
    string Model { get; }

    Task<LlmProviderResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default);
}

internal sealed record LlmRequest(string Prompt, int MaxOutputTokens);

internal sealed record LlmProviderResponse(
    string? Text,
    int InputTokens = 0,
    int OutputTokens = 0,
    int ThinkingTokens = 0);

internal sealed record LlmResponse(
    string Text,
    TimeSpan Latency,
    int InputTokens,
    int OutputTokens,
    int ThinkingTokens);
