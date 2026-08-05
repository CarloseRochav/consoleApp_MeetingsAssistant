using Azure.Identity;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace MeetingAssistant.LlmSpike.Services;

#pragma warning disable OPENAI001 // The OpenAI SDK currently marks its Responses API as an evaluation surface.

/// <summary>
/// Microsoft Foundry implementation using its OpenAI/v1 endpoint and the stable
/// OpenAI .NET SDK. Entra ID is used by default; an API key is optional.
/// </summary>
internal sealed class AzureFoundryLlmClient : ILlmClient
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
            ? CreateEntraClient(baseEndpoint)
            : new ResponsesClient(new ApiKeyCredential(apiKey), new ResponsesClientOptions { Endpoint = baseEndpoint });
    }

    public string Provider => "Azure AI Foundry";

    // Foundry requests use the deployment name in the model field.
    public string Model { get; }

    public async Task<LlmProviderResponse> GenerateAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var options = new CreateResponseOptions
        {
            Model = Model,
            MaxOutputTokenCount = request.MaxOutputTokens,
        };
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(request.Prompt));
        var result = await client.CreateResponseAsync(options, cancellationToken);
        ResponseResult response = result.Value;

        return new LlmProviderResponse(
            response.GetOutputText());
    }

    private static ResponsesClient CreateEntraClient(Uri endpoint)
    {
        return new ResponsesClient(
            new BearerTokenPolicy(new DefaultAzureCredential(), FoundryScope),
            new ResponsesClientOptions { Endpoint = endpoint });
    }

    private static Uri NormalizeEndpoint(string endpoint)
    {
        string normalized = endpoint.TrimEnd('/') + "/";
        if (!normalized.EndsWith("/openai/v1/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "El endpoint de Azure Foundry debe terminar en '/openai/v1/'.",
                nameof(endpoint));
        }

        return new Uri(normalized, UriKind.Absolute);
    }
}

#pragma warning restore OPENAI001
