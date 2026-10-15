using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Infrastructure.Llm;
using MeetingAssistant.Infrastructure.Transcription;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.Infrastructure;

/// <summary>
/// Arma los clientes de los proveedores (LLM y transcripción) a partir de la
/// configuración. Lo usan la app y el harness: antes cada uno tenía su copia, y
/// ya se habían separado (el harness no leía <c>Gemini:Model</c>).
///
/// Elegir el proveedor por un valor de configuración es lo único que hace: los
/// clientes no saben de dónde salió su clave, y quien llama no sabe qué clase
/// concreta recibe.
/// </summary>
public static class ProviderClientFactory
{
    public static ILlmClient CreateLlmClient(IConfiguration configuration)
    {
        string provider = ReadSetting(configuration, "Llm", "Provider") ?? "Gemini";
        return provider.ToLowerInvariant() switch
        {
            // Gemini:Model es opcional: sin él manda el default del cliente.
            // Deployment hace de "modelo" del lado de Azure.
            "gemini" => ReadSetting(configuration, "Gemini", "Model") is { } geminiModel
                ? new GeminiLlmClient(ReadRequiredSetting(configuration, "Gemini", "ApiKey", "GEMINI_API_KEY"), geminiModel)
                : new GeminiLlmClient(ReadRequiredSetting(configuration, "Gemini", "ApiKey", "GEMINI_API_KEY")),
            "azurefoundry" => new AzureFoundryLlmClient(
                ReadRequiredSetting(configuration, "AzureFoundry", "Endpoint"),
                ReadRequiredSetting(configuration, "AzureFoundry", "Deployment"),
                ReadSetting(configuration, "AzureFoundry", "ApiKey")),
            _ => throw new InvalidOperationException($"Proveedor '{provider}' no soportado. Usa 'Gemini' o 'AzureFoundry'.")
        };
    }

    public static ITranscriptionClient CreateTranscriptionClient(IConfiguration configuration) =>
        new DeepgramTranscriptionClient(ReadRequiredSetting(configuration, "Deepgram", "ApiKey", "DEEPGRAM_API_KEY"));

    public static string ReadRequiredSetting(
        IConfiguration configuration,
        string section,
        string property,
        string? environmentVariable = null) =>
        ReadSetting(configuration, section, property, environmentVariable) ?? throw new InvalidOperationException(
            $"Falta configurar {section}:{property} en appsettings.json o la variable de entorno {environmentVariable}.");

    /// <summary>
    /// Null si falta, si está en blanco o si es un marcador de los archivos de
    /// ejemplo (<c>&lt;your-...&gt;</c>): un marcador nunca es un valor de verdad.
    /// </summary>
    public static string? ReadSetting(
        IConfiguration configuration,
        string section,
        string property,
        string? environmentVariable = null)
    {
        string? value = configuration[$"{section}:{property}"] ??
            (environmentVariable is null ? null : configuration[environmentVariable]);
        return string.IsNullOrWhiteSpace(value) || value.StartsWith('<') ? null : value;
    }
}
