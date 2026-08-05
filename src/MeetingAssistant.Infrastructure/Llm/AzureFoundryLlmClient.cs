using Azure.Identity;
using MeetingAssistant.Core.Abstractions;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace MeetingAssistant.Infrastructure.Llm;

#pragma warning disable OPENAI001
public sealed class AzureFoundryLlmClient : ILlmClient
{
    private const string FoundryScope = "https://ai.azure.com/.default";
    private readonly ResponsesClient client;

    public AzureFoundryLlmClient(string endpoint, string deployment, string? apiKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deployment);
        Uri baseEndpoint = NormalizeEndpoint(endpoint);
        Model = deployment;
        client = string.IsNullOrWhiteSpace(apiKey)
            ? new ResponsesClient(new BearerTokenPolicy(new DefaultAzureCredential(), FoundryScope), new ResponsesClientOptions { Endpoint = baseEndpoint })
            : new ResponsesClient(new ApiKeyCredential(apiKey), new ResponsesClientOptions { Endpoint = baseEndpoint });
    }

    public string Provider => "Azure AI Foundry";
    public string Model { get; }

    public async Task<LlmProviderResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var options = new CreateResponseOptions { Model = Model, MaxOutputTokenCount = request.MaxOutputTokens };
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(request.Prompt));
        ResponseResult response = (await client.CreateResponseAsync(options, cancellationToken)).Value;
        return new LlmProviderResponse(
            response.GetOutputText(),
            response.Usage?.InputTokenCount ?? 0,
            response.Usage?.OutputTokenCount ?? 0,
            response.Usage?.OutputTokenDetails?.ReasoningTokenCount ?? 0);
    }

    private static Uri NormalizeEndpoint(string endpoint)
    {
        string normalized = endpoint.TrimEnd('/') + "/";
        if (!normalized.EndsWith("/openai/v1/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("El endpoint de Azure Foundry debe terminar en '/openai/v1/'.", nameof(endpoint));
        return new Uri(normalized, UriKind.Absolute);
    }
}
#pragma warning restore OPENAI001
