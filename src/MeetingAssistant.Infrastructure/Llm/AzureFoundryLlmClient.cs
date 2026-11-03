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
    // El SDK corta cada llamada a los 100 s y la reintenta 3 veces: el reporte de una reunión de
    // 60 min con grok-4.6 tardó más y fallaron los 4 intentos (2026-11-03). 10 min por llamada y
    // un solo reintento del SDK, porque el worker ya reintenta el trabajo entero (3 veces): en el
    // peor caso 3 x 2 x 10 min caben en las 2 h de una ejecución, y un intento (2 x 10 min) queda
    // bajo los 60 min de Worker:StaleMinutes, así que otra ejecución no retoma un trabajo vivo.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);
    private const int SdkRetries = 1;
    private readonly ResponsesClient client;

    public AzureFoundryLlmClient(string endpoint, string deployment, string? apiKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deployment);
        Uri baseEndpoint = NormalizeEndpoint(endpoint);
        Model = deployment;
        var options = new ResponsesClientOptions
        {
            Endpoint = baseEndpoint,
            NetworkTimeout = RequestTimeout,
            RetryPolicy = new ClientRetryPolicy(SdkRetries),
        };
        client = string.IsNullOrWhiteSpace(apiKey)
            ? new ResponsesClient(new BearerTokenPolicy(new DefaultAzureCredential(), FoundryScope), options)
            : new ResponsesClient(new ApiKeyCredential(apiKey), options);
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
