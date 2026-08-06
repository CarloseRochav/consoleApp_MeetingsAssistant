namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Estima el costo en USD de una llamada a un LLM dado su proveedor/modelo y el
/// uso de tokens reportado. La tabla de precios real (que cambia con el tiempo)
/// vive en Infrastructure, no aquí — esta interfaz solo declara la capacidad.
/// </summary>
public interface ICostEstimator
{
    decimal EstimateCostUsd(string provider, string model, int inputTokens, int outputTokens);
}
