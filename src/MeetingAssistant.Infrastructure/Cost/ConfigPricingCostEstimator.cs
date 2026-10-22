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

    /// <summary>
    /// Sin precio —falta cualquiera de los dos, o el 0 que trae el archivo de
    /// ejemplo como marcador— devuelve <b>null</b>, igual que la transcripción:
    /// un cero guardado parecería medido. No lanza: que falte un precio no
    /// tumba la extracción del reporte.
    /// </summary>
    public decimal? EstimateCostUsd(string provider, string model, int inputTokens, int outputTokens)
    {
        string key = $"Pricing:{provider}:{model}";
        decimal? inputPerMillion = _configuration.GetValue<decimal?>($"{key}:InputPerMillion");
        decimal? outputPerMillion = _configuration.GetValue<decimal?>($"{key}:OutputPerMillion");
        if (inputPerMillion is null or <= 0m || outputPerMillion is null or <= 0m) return null;

        decimal inputCost = inputTokens / 1_000_000m * inputPerMillion.Value;
        decimal outputCost = outputTokens / 1_000_000m * outputPerMillion.Value;
        return inputCost + outputCost;
    }

    /// <summary>
    /// La transcripción se cobra por minuto de audio. El precio va en
    /// <c>Pricing:{proveedor}:PerMinute</c> (hoy, <c>Pricing:Deepgram:PerMinute</c>)
    /// y lo pone el usuario según su plan: en el código no hay ninguno.
    ///
    /// Sin precio —clave ausente, o el 0 que trae el archivo de ejemplo como
    /// marcador— devuelve <b>null</b>, no 0. Un cero guardado parecería medido.
    ///
    /// Si el proveedor tiene términos clave (<c>{proveedor}:Keyterms</c>, hoy
    /// <c>Deepgram:Keyterms</c>), suma <c>Pricing:{proveedor}:KeytermPerMinute</c>.
    /// Con términos y sin ese precio también devuelve null: el costo base solo
    /// quedaría por debajo del real y parecería completo.
    /// </summary>
    public decimal? EstimateTranscriptionCostUsd(string provider, TimeSpan audioDuration)
    {
        decimal? perMinute = _configuration.GetValue<decimal?>($"Pricing:{provider}:PerMinute");
        if (perMinute is null or <= 0m) return null;

        if (!string.IsNullOrWhiteSpace(_configuration[$"{provider}:Keyterms"]))
        {
            decimal? keytermPerMinute = _configuration.GetValue<decimal?>($"Pricing:{provider}:KeytermPerMinute");
            if (keytermPerMinute is null or <= 0m) return null;
            perMinute += keytermPerMinute.Value;
        }

        return (decimal)audioDuration.TotalMinutes * perMinute.Value;
    }
}
