using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.Core.Abstractions;

namespace MeetingAssistant.App.ViewModels;

public partial class RecordViewModel : ObservableObject
{
    private readonly IMeetingPipeline _pipeline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    private bool isRecording;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    private bool isProcessing;

    [ObservableProperty]
    private string statusMessage = "Listo para grabar.";

    [ObservableProperty]
    private string? lastSavedReportPath;

    public string ButtonText => IsRecording ? "Detener grabación" : "Grabar reunión";

    public RecordViewModel(IMeetingPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    private bool CanToggleRecording() => !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanToggleRecording))]
    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            await StopAsync();
        }
        else
        {
            await StartAsync();
        }
    }

    private async Task StartAsync()
    {
        try
        {
            await _pipeline.StartRecordingAsync();
            IsRecording = true;
            StatusMessage = "Grabando...";
            LastSavedReportPath = null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al iniciar la grabación: {ex.Message}";
        }
    }

    private async Task StopAsync()
    {
        IsProcessing = true;
        StatusMessage = "Procesando (transcribiendo y extrayendo el reporte)...";
        try
        {
            MeetingPipelineResult result = await _pipeline.StopRecordingAndProcessAsync();
            IsRecording = false;
            LastSavedReportPath = result.SavedReportPath;
            StatusMessage = $"Reporte guardado en: {result.SavedReportPath}";
        }
        catch (Exception ex)
        {
            IsRecording = false;
            StatusMessage = $"Error al procesar la reunión: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }
}
