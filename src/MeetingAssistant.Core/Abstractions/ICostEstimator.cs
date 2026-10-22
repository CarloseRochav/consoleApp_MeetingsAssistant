namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Estima el costo en USD de una llamada a un LLM dado su proveedor/modelo y el
/// uso de tokens reportado. La tabla de precios real (que cambia con el tiempo)
/// vive en Infrastructure, no aquí — esta interfaz solo declara la capacidad.
/// </summary>
public interface ICostEstimator
{
    /// <summary>null cuando el modelo no tiene precio configurado: costo desconocido, no gratis.</summary>
    decimal? EstimateCostUsd(string provider, string model, int inputTokens, int outputTokens);
}

/// <summary>
/// Estima el costo en USD de transcribir un audio, que se cobra por minuto y no
/// por token. Es una interfaz aparte de <see cref="ICostEstimator"/> porque la
/// respuesta tiene otra forma: <b>null cuando no hay precio configurado</b>.
///
/// <see cref="ICostEstimator"/> devuelve 0 en ese caso para no tumbar la
/// extracción, y ese cero después no se distingue de un costo real de cero. Acá
/// no hay nada que tumbar, así que lo desconocido se devuelve como desconocido.
/// </summary>
public interface ITranscriptionCostEstimator
{
    decimal? EstimateTranscriptionCostUsd(string provider, TimeSpan audioDuration);
}
