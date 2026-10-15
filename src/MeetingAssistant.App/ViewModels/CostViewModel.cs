using MeetingAssistant.App.Startup;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.App.ViewModels;

/// <summary>Una fila de la tabla por prompt y versión.</summary>
public sealed class PromptUsageRow
{
    public PromptUsageRow(PromptUsageSummary summary)
    {
        Title = summary.PromptVersion is null
            ? summary.PromptId
            : $"{summary.PromptId} @{summary.PromptVersion}";

        var parts = new List<string>
        {
            summary.ReportCount == 1 ? "1 reporte" : $"{summary.ReportCount} reportes",
            CostViewModel.FormatUsd(summary.TotalCostUsd)
        };

        // El promedio por reporte sólo se muestra si todos los reportes del
        // grupo tienen precio: dividir un total al que le faltan sumandos por
        // la cuenta completa daría un promedio más bajo que el verdadero.
        if (summary.ReportsMissingPriceCount == 0 && summary.ReportCount > 0)
        {
            parts.Add($"{CostViewModel.FormatUsd(summary.TotalCostUsd / summary.ReportCount)} por reporte");
        }

        parts.Add(summary.AverageOutputTokens > 0
            ? $"{summary.AverageOutputTokens:N0} tokens de salida en promedio"
            : "sin dato de tokens de salida");

        if (summary.LastUsedUtc is { } lastUsed)
        {
            parts.Add($"último uso {lastUsed.ToLocalTime().ToString("dd/MM/yyyy", SessionListItem.DisplayCulture)}");
        }

        if (summary.ReportsMissingPriceCount > 0)
        {
            parts.Add($"{summary.ReportsMissingPriceCount} sin precio configurado");
        }

        Details = string.Join(" · ", parts);
    }

    public string Title { get; }

    public string Details { get; }

    /// <summary>Mismo motivo que en <see cref="SessionListItem.ToString"/>.</summary>
    public override string ToString() => $"{Title}. {Details}";
}

/// <summary>
/// Costo estimado acumulado y uso por prompt. Es el lado medible de lo que pide
/// Fase 4; la calidad de los reportes sigue siendo juicio humano.
///
/// Toda la página es sobre números de dinero, así que la regla es una sola:
/// <b>cada monto va con lo que no incluye</b>. Un total sin calificar, cuando
/// falta la transcripción o hay reportes sin precio, es peor que no mostrarlo.
/// </summary>
public partial class CostViewModel : ObservableObject
{
    private readonly IMeetingHistoryStore _historyStore;
    private readonly ITranscriptionCostEstimator _transcriptionCostEstimator;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private bool hasData;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? errorDetails;

    [ObservableProperty]
    private string? llmCostText;

    [ObservableProperty]
    private string? transcriptionCostText;

    [ObservableProperty]
    private string? totalLabel;

    [ObservableProperty]
    private string? totalCostText;

    [ObservableProperty]
    private string? averageText;

    [ObservableProperty]
    private string? periodText;

    /// <summary>Lo que los montos de arriba no incluyen, y por qué.</summary>
    public ObservableCollection<string> Caveats { get; } = [];

    public ObservableCollection<PromptUsageRow> PromptRows { get; } = [];

    public CostViewModel(
        IMeetingHistoryStore historyStore,
        ITranscriptionCostEstimator transcriptionCostEstimator)
    {
        _historyStore = historyStore;
        _transcriptionCostEstimator = transcriptionCostEstimator;
    }

    /// <summary>
    /// Seis decimales, igual que el frontmatter de los reportes: son montos de
    /// fracciones de centavo, y con cuatro decimales se redondean a cero cosas
    /// que sí costaron algo.
    /// </summary>
    internal static string FormatUsd(decimal amount) => $"US${amount:F6}";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorDetails = null;
        StatusMessage = "Calculando...";

        try
        {
            CostSummary summary = await _historyStore.GetCostSummaryAsync();
            IReadOnlyList<PromptUsageSummary> usage = await _historyStore.GetPromptUsageAsync();

            Show(summary, usage);
        }
        catch (Exception exception)
        {
            // Regla de la fase: un fallo de la base no puede tumbar nada.
            AppLog.Failure("CostViewModel.Refresh", exception);
            HasData = false;
            Caveats.Clear();
            PromptRows.Clear();
            StatusMessage = "No se pudieron leer los costos. El resto de la app sigue funcionando: " +
                "grabar, transcribir y guardar en el vault no dependen de la base.";
            ErrorDetails = exception.ToString();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Show(CostSummary summary, IReadOnlyList<PromptUsageSummary> usage)
    {
        Caveats.Clear();
        PromptRows.Clear();

        HasData = summary.ReportCount > 0 || summary.TranscriptCount > 0;
        if (!HasData)
        {
            StatusMessage = null;
            return;
        }

        StatusMessage = null;

        // ------------------------------------------------------------- LLM
        LlmCostText = $"{FormatUsd(summary.LlmCostUsd)} en " +
            (summary.ReportCount == 1 ? "1 reporte" : $"{summary.ReportCount} reportes");

        if (summary.ReportsMissingPriceCount > 0)
        {
            Caveats.Add(
                (summary.ReportsMissingPriceCount == 1
                    ? "1 reporte consumió tokens y quedó con costo 0"
                    : $"{summary.ReportsMissingPriceCount} reportes consumieron tokens y quedaron con costo 0") +
                ": no había precio configurado para su modelo en la sección Pricing. No suman nada al total, " +
                "y agregar el precio ahora no los recalcula.");
        }

        if (summary.ReportsWithoutCostCount > 0)
        {
            Caveats.Add(summary.ReportsWithoutCostCount == 1
                ? "1 reporte no tiene costo registrado y no cuenta en el total."
                : $"{summary.ReportsWithoutCostCount} reportes no tienen costo registrado y no cuentan en el total.");
        }

        // --------------------------------------------------- transcripción
        int transcriptsWithCost = summary.TranscriptCount - summary.TranscriptsWithoutCostCount;
        TranscriptionCostText = (summary.TranscriptCount, transcriptsWithCost) switch
        {
            (0, _) => "todavía no hay transcripciones",
            (_, 0) => "desconocido",
            _ when summary.TranscriptsWithoutCostCount == 0 =>
                $"{FormatUsd(summary.TranscriptionCostUsd)} en " +
                (summary.TranscriptCount == 1 ? "1 transcripción" : $"{summary.TranscriptCount} transcripciones"),
            _ => $"{FormatUsd(summary.TranscriptionCostUsd)} en {transcriptsWithCost} de {summary.TranscriptCount} transcripciones"
        };

        bool transcriptionPriceConfigured = _transcriptionCostEstimator.EstimateTranscriptionCostUsd(
            MeetingPipeline.TranscriptionProviderName, TimeSpan.FromMinutes(1)) is not null;

        if (summary.TranscriptsWithoutCostCount > 0)
        {
            Caveats.Add(
                (summary.TranscriptsWithoutCostCount == 1
                    ? "1 transcripción no tiene costo calculado"
                    : $"{summary.TranscriptsWithoutCostCount} transcripciones no tienen costo calculado") +
                ": se grabaron sin un precio por minuto configurado. La transcripción se cobra por minuto de audio " +
                "y puede costar más que el LLM, así que el total de abajo está incompleto.");
        }

        if (!transcriptionPriceConfigured)
        {
            Caveats.Add(
                $"No hay precio de transcripción configurado (Pricing:{MeetingPipeline.TranscriptionProviderName}:PerMinute, " +
                "en dólares por minuto según tu plan). Hasta que lo pongas, las grabaciones nuevas se guardan sin " +
                "costo de transcripción. Las ya grabadas no se recalculan.");
        }

        // ----------------------------------------------------------- total
        bool incomplete = summary.TranscriptsWithoutCostCount > 0 ||
            summary.ReportsMissingPriceCount > 0 ||
            summary.ReportsWithoutCostCount > 0;
        TotalLabel = incomplete ? "Total estimado (incompleto, ver abajo)" : "Total estimado";
        TotalCostText = FormatUsd(summary.TotalCostUsd);

        // El promedio divide por las reuniones que produjeron reporte, no por
        // todas: las grabaciones fallidas también son sesiones, y contarlas
        // hundiría el promedio. Se dice por cuántas se dividió.
        AverageText = summary.SessionsWithReportsCount > 0
            ? $"{FormatUsd(summary.LlmCostUsd / summary.SessionsWithReportsCount)} de LLM por reunión con reporte " +
              $"({summary.SessionsWithReportsCount} de {summary.SessionCount} reuniones registradas)"
            : null;

        PeriodText = summary is { FirstReportUtc: { } first, LastReportUtc: { } last }
            ? $"Reportes del {first.ToLocalTime().ToString("dd/MM/yyyy", SessionListItem.DisplayCulture)} " +
              $"al {last.ToLocalTime().ToString("dd/MM/yyyy", SessionListItem.DisplayCulture)}"
            : null;

        foreach (PromptUsageSummary row in usage) PromptRows.Add(new PromptUsageRow(row));
    }
}
