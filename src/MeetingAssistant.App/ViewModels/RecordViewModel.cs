using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Services;
using MeetingAssistant.Core.Abstractions;

namespace MeetingAssistant.App.ViewModels;

public partial class RecordViewModel : ObservableObject
{
    private readonly RecordingCoordinator _recordingCoordinator;

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

    [ObservableProperty]
    private string? lastTranscript;

    public string ButtonText => IsRecording ? "Detener grabación" : "Grabar reunión";

    public RecordViewModel(RecordingCoordinator recordingCoordinator)
    {
        _recordingCoordinator = recordingCoordinator;
        IsRecording = recordingCoordinator.IsRecording;
        IsProcessing = recordingCoordinator.IsProcessing;
        StatusMessage = IsProcessing
            ? "Procesando (transcribiendo y extrayendo el reporte)..."
            : IsRecording ? "Grabando..." : "Listo para grabar.";
    }

    private bool CanToggleRecording() => !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanToggleRecording))]
    private async Task ToggleRecordingAsync()
    {
        if (_recordingCoordinator.IsRecording)
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
            await _recordingCoordinator.StartRecordingAsync();
            IsRecording = _recordingCoordinator.IsRecording;
            StatusMessage = "Grabando...";
            LastSavedReportPath = null;
            LastTranscript = null;
        }
        catch (Exception ex)
        {
            IsRecording = _recordingCoordinator.IsRecording;
            StatusMessage = $"Error al iniciar la grabación: {ex.Message}";
        }
    }

    private async Task StopAsync()
    {
        IsProcessing = true;
        StatusMessage = "Procesando (transcribiendo y extrayendo el reporte)...";
        try
        {
            MeetingPipelineResult result = await _recordingCoordinator.StopRecordingAndProcessAsync();
            IsRecording = _recordingCoordinator.IsRecording;
            LastTranscript = result.Transcription.Transcript;
            LastSavedReportPath = result.SavedReportPath;
            StatusMessage = $"Reporte guardado en: {result.SavedReportPath}";
        }
        catch (Exception ex)
        {
            IsRecording = _recordingCoordinator.IsRecording;
            StatusMessage = $"Error al procesar la reunión: {ex.Message}";
        }
        finally
        {
            IsProcessing = _recordingCoordinator.IsProcessing;
        }
    }
}
