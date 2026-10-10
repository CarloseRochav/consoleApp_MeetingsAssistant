using MeetingAssistant.Core.Abstractions;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.Infrastructure.Cost;

/// <summary>
/// Implementación concreta de ICostEstimator. Deliberadamente NO tiene precios
/// hardcodeados en el código — los lee de configuración (appsettings.json,
/// sección "Pricing") para que actualizar un precio no requiera recompilar,
/// dado que ya viste que los precios de estos proveedores cambian con el tiempo.
///
/// Formato esperado en appsettings.json:
/// {
///   "Pricing": {
///     "Gemini:gemini-3.5-flash-lite": { "InputPerMillion": 0.30, "OutputPerMillion": 2.50 },
///     "AzureFoundry:deepseek-v4-flash": { "InputPerMillion": 0.14, "OutputPerMillion": 0.28 }
///   }
/// }
/// </summary>
public sealed class ConfigPricingCostEstimator : ICostEstimator, ITranscriptionCostEstimator
{
    private readonly IConfiguration _configuration;

    public ConfigPricingCostEstimator(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public decimal EstimateCostUsd(string provider, string model, int inputTokens, int outputTokens)
    {
        string key = $"Pricing:{provider}:{model}";
        decimal inputPerMillion = _configuration.GetValue<decimal?>($"{key}:InputPerMillion") ?? 0m;
        decimal outputPerMillion = _configuration.GetValue<decimal?>($"{key}:OutputPerMillion") ?? 0m;

        if (inputPerMillion == 0m && outputPerMillion == 0m)
        {
            // No hay precio configurado para este proveedor/modelo todavía.
            // Devolver 0 en vez de lanzar — no queremos que falte un precio en
            // el config tumbe el pipeline completo de extracción del reporte.
            return 0m;
        }

        decimal inputCost = inputTokens / 1_000_000m * inputPerMillion;
        decimal outputCost = outputTokens / 1_000_000m * outputPerMillion;
        return inputCost + outputCost;
    }

    /// <summary>
    /// La transcripción se cobra por minuto de audio. El precio va en
    /// <c>Pricing:{proveedor}:PerMinute</c> (hoy, <c>Pricing:Deepgram:PerMinute</c>)
    /// y lo pone el usuario según su plan: en el código no hay ninguno.
    ///
    /// Sin precio —clave ausente, o el 0 que trae el archivo de ejemplo como
    /// marcador— devuelve <b>null</b>, no 0. Un cero guardado parecería medido.
    /// </summary>
    public decimal? EstimateTranscriptionCostUsd(string provider, TimeSpan audioDuration)
    {
        decimal? perMinute = _configuration.GetValue<decimal?>($"Pricing:{provider}:PerMinute");
        if (perMinute is null or <= 0m) return null;

        return (decimal)audioDuration.TotalMinutes * perMinute.Value;
    }
}
