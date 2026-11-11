using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Startup;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;

namespace MeetingAssistant.App.ViewModels;

/// <summary>Un mes (UTC) de costos, listo para mostrar.</summary>
public sealed class CostMonthRow(CostPeriod period)
{
    public string Title { get; } = period.Month is { } month
        ? month.UtcDateTime.ToString("MMMM yyyy", SessionListItem.DisplayCulture)
        : "Sin mes";

    public string Details { get; } =
        $"{(period.Sessions == 1 ? "1 reunión" : $"{period.Sessions} reuniones")} · transcripción {period.Transcripts.Text()} · " +
        $"reportes {period.Reports.Text()} · total {period.Total.Text()}";

    public override string ToString() => $"{Title}. {Details}";
}

/// <summary>
/// Lo que costaron mis reuniones de todos los dispositivos (Meetings/Select_Costs, paso B). Un
/// precio desconocido nunca se muestra como 0: se cuenta aparte y se dice (handoff del paso B).
/// </summary>
public partial class CostViewModel(MeetingCostsService costs) : ObservableObject
{
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

    public ObservableCollection<string> Caveats { get; } = [];

    public ObservableCollection<CostMonthRow> MonthRows { get; } = [];

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorDetails = null;
        StatusMessage = "Calculando...";
        try
        {
            Show(await costs.LoadAsync());
        }
        catch (Exception exception)
        {
            AppLog.Failure("CostViewModel.Refresh", exception);
            HasData = false;
            Caveats.Clear();
            MonthRows.Clear();
            StatusMessage = "No se pudieron leer los costos del servidor. " +
                (exception is ApiException api ? api.Error.Describe() : exception.Message);
            ErrorDetails = exception.ToString();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Show(CostOverview overview)
    {
        Caveats.Clear();
        MonthRows.Clear();
        CostPeriod totals = overview.Totals;
        HasData = totals.Sessions > 0;
        StatusMessage = null;
        if (!HasData) return;

        LlmCostText = totals.Reports.Text();
        TranscriptionCostText = totals.Transcripts.Text();
        TotalLabel = totals.Total.IsPartlyUnknown || totals.Total.KnownMicroUsd is null ? "Total (incompleto, ver abajo)" : "Total";
        TotalCostText = totals.Total.Text();

        if (totals.Reports.UnknownCount > 0)
        {
            Caveats.Add($"{totals.Reports.UnknownCount} reporte(s) no tienen precio (el modelo no tenía precio en la base del servidor). " +
                "No suman al total, y no cuentan como 0.");
        }

        if (totals.Transcripts.UnknownCount > 0)
        {
            Caveats.Add($"{totals.Transcripts.UnknownCount} transcripción(es) no tienen precio (se hicieron antes de que hubiera un precio por minuto). " +
                "La transcripción puede costar más que el LLM, así que el total está incompleto.");
        }

        AverageText = totals.TokensInput is { } input && totals.TokensOutput is { } output
            ? $"{input:N0} tokens de entrada y {output:N0} de salida en {(totals.Sessions == 1 ? "1 reunión" : $"{totals.Sessions} reuniones")}."
            : null;
        PeriodText = overview.Months.Count > 0 ? $"{overview.Months.Count} mes(es) con reuniones, por mes UTC." : null;

        foreach (CostPeriod month in overview.Months) MonthRows.Add(new CostMonthRow(month));
    }
}
