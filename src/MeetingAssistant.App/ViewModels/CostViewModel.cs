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
        : "No month";

    public string Details { get; } =
        $"{(period.Sessions == 1 ? "1 meeting" : $"{period.Sessions} meetings")} · transcription {period.Transcripts.Text()} · " +
        $"reports {period.Reports.Text()} · total {period.Total.Text()}";

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
        StatusMessage = "Calculating...";
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
            StatusMessage = "Could not read costs from the server. " +
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
        TotalLabel = totals.Total.IsPartlyUnknown || totals.Total.KnownMicroUsd is null ? "Total (incomplete, see below)" : "Total";
        TotalCostText = totals.Total.Text();

        if (totals.Reports.UnknownCount > 0)
        {
            Caveats.Add($"{totals.Reports.UnknownCount} report(s) have no price (the model had no price in the server's table). " +
                "They are not added to the total, and they are not counted as 0.");
        }

        if (totals.Transcripts.UnknownCount > 0)
        {
            Caveats.Add($"{totals.Transcripts.UnknownCount} transcript(s) have no price (they were made before there was a price per minute). " +
                "Transcription can cost more than the LLM, so the total is incomplete.");
        }

        AverageText = totals.TokensInput is { } input && totals.TokensOutput is { } output
            ? $"{input:N0} input tokens and {output:N0} output tokens across {(totals.Sessions == 1 ? "1 meeting" : $"{totals.Sessions} meetings")}."
            : null;
        PeriodText = overview.Months.Count > 0 ? $"{overview.Months.Count} month(s) with meetings, by UTC month." : null;

        foreach (CostPeriod month in overview.Months) MonthRows.Add(new CostMonthRow(month));
    }
}
