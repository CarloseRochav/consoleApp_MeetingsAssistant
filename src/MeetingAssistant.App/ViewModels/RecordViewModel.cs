using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using MeetingAssistant.App.Services;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.App.ViewModels;

public partial class RecordViewModel : ObservableObject
{
    private readonly RecordingCoordinator _recordingCoordinator;
    private readonly DispatcherQueue? _dispatcherQueue;

    /// <summary>
    /// Mayor que cero mientras esta vista es la que esta ejecutando la
    /// operacion. Los eventos del coordinador que llegan en ese lapso ya
    /// estan reflejados por el metodo local, asi que se ignoran para no
    /// pisar mensajes de estado mas especificos ("Transcribiendo...",
    /// "Reporte guardado en...").
    /// </summary>
    private int _localOperationDepth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    [NotifyPropertyChangedFor(nameof(CanGenerateReport))]
    [NotifyPropertyChangedFor(nameof(CanLoadExternalSource))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateReportCommand))]
    private bool isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerateReport))]
    [NotifyPropertyChangedFor(nameof(CanLoadExternalSource))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateReportCommand))]
    private bool isProcessing;

    [ObservableProperty]
    private string statusMessage = "Listo para grabar.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedReport))]
    [NotifyCanExecuteChangedFor(nameof(OpenSavedReportCommand))]
    private string? lastSavedReportPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranscript))]
    [NotifyPropertyChangedFor(nameof(CanGenerateReport))]
    [NotifyCanExecuteChangedFor(nameof(GenerateReportCommand))]
    private string? lastTranscript;

    [ObservableProperty]
    private string? lastGeneratedReport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPromptText))]
    [NotifyPropertyChangedFor(nameof(CanGenerateReport))]
    [NotifyCanExecuteChangedFor(nameof(GenerateReportCommand))]
    private PromptDefinition? selectedPrompt;

    [ObservableProperty]
    private string? errorDetails;

    public IReadOnlyList<PromptDefinition> Prompts { get; }

    public string ButtonText => IsRecording ? "Detener grabación" : "Grabar reunión";

    public bool HasTranscript => !string.IsNullOrWhiteSpace(LastTranscript);

    public bool CanGenerateReport =>
        HasTranscript && SelectedPrompt is not null && !IsProcessing && !IsRecording;

    public bool CanLoadExternalSource => !IsRecording && !IsProcessing;

    public bool HasSavedReport => !string.IsNullOrWhiteSpace(LastSavedReportPath) && File.Exists(LastSavedReportPath);

    public string SelectedPromptText => SelectedPrompt?.SystemPrompt ?? string.Empty;

    public RecordViewModel(RecordingCoordinator recordingCoordinator, IPromptCatalog promptCatalog)
    {
        _recordingCoordinator = recordingCoordinator;
        Prompts = promptCatalog.GetAll();
        SelectedPrompt = promptCatalog.Default;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        IsRecording = _recordingCoordinator.IsRecording;
        IsProcessing = _recordingCoordinator.IsProcessing;
        StatusMessage = IsProcessing
            ? "Procesando..."
            : IsRecording ? "Grabando..." : "Listo para grabar.";

        // MainWindow y RecordPage se construyen una sola vez; la bandeja (y
        // mas adelante el hotkey) pueden iniciar o detener una grabacion sin
        // que esta instancia se entere. Sin estas suscripciones la pagina
        // muestra estado viejo al reabrir la ventana.
        _recordingCoordinator.StateChanged += OnCoordinatorStateChanged;
        _recordingCoordinator.RecordingCompleted += OnCoordinatorRecordingCompleted;
        _recordingCoordinator.RecordingFailed += OnCoordinatorRecordingFailed;
    }

    private void OnCoordinatorStateChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _localOperationDepth) > 0) return;

        RunOnUiThread(() =>
        {
            IsRecording = _recordingCoordinator.IsRecording;
            IsProcessing = _recordingCoordinator.IsProcessing;

            if (IsRecording)
            {
                StatusMessage = "Grabando...";
            }
            else if (IsProcessing)
            {
                StatusMessage = "Procesando...";
            }
            else if (StatusMessage is "Grabando..." or "Procesando...")
            {
                // Solo se limpia si el mensaje seguia siendo transitorio: el
                // evento de fin llega antes que este y ya dejo el resultado.
                StatusMessage = "Listo para grabar.";
            }
        });
    }

    private void OnCoordinatorRecordingCompleted(object? sender, RecordingCompletedEventArgs e)
    {
        if (Volatile.Read(ref _localOperationDepth) > 0) return;

        MeetingPipelineResult result = e.Result;
        RunOnUiThread(() =>
        {
            LastTranscript = result.Transcription.Transcript;
            LastGeneratedReport = result.ReportMarkdown;
            LastSavedReportPath = result.SavedReportPath;
            SelectedPrompt = Prompts.FirstOrDefault(p => p.Id == result.Prompt.Id) ?? SelectedPrompt;
            ErrorDetails = null;
            StatusMessage = $"Reporte guardado en el vault de Obsidian: {result.SavedReportPath}";
        });
    }

    private void OnCoordinatorRecordingFailed(object? sender, RecordingFailedEventArgs e)
    {
        if (Volatile.Read(ref _localOperationDepth) > 0) return;

        Exception exception = e.Exception;
        RunOnUiThread(() =>
        {
            StatusMessage = $"Error al procesar la reunion: {exception.Message}";
            ErrorDetails = exception.ToString();
        });
    }

    /// <summary>
    /// El coordinador levanta sus eventos en el hilo que llamo la operacion
    /// (la bandeja usa el hilo de UI, otros disparadores no tienen por que).
    /// Mutar propiedades observables fuera del hilo de UI rompe el binding.
    /// </summary>
    private void RunOnUiThread(Action action)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcherQueue.TryEnqueue(() => action());
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
        _localOperationDepth++;
        try
        {
            await _recordingCoordinator.StartRecordingAsync();
            IsRecording = _recordingCoordinator.IsRecording;
            StatusMessage = "Grabando...";
            LastSavedReportPath = null;
            LastTranscript = null;
            LastGeneratedReport = null;
            ErrorDetails = null;
        }
        catch (Exception ex)
        {
            IsRecording = _recordingCoordinator.IsRecording;
            StatusMessage = $"Error al iniciar la grabación: {ex.Message}";
            ErrorDetails = ex.ToString();
        }
        finally
        {
            _localOperationDepth--;
        }
    }

    private async Task StopAsync()
    {
        IsProcessing = true;
        StatusMessage = "Transcribiendo...";
        _localOperationDepth++;
        try
        {
            TranscriptionSession session = await _recordingCoordinator.StopRecordingAndTranscribeAsync();
            IsRecording = _recordingCoordinator.IsRecording;
            LastTranscript = session.Transcription.Transcript;
            LastGeneratedReport = null;
            LastSavedReportPath = null;
            ErrorDetails = null;
            StatusMessage = "Transcripción lista. Elige un prompt y genera el reporte.";
        }
        catch (Exception ex)
        {
            IsRecording = _recordingCoordinator.IsRecording;
            StatusMessage = $"Error al transcribir la reunión: {ex.Message}";
            ErrorDetails = ex.ToString();
        }
        finally
        {
            _localOperationDepth--;
            IsProcessing = false;
        }
    }

    public async Task ProcessExistingAudioAsync(string audioPath)
    {
        IsProcessing = true;
        StatusMessage = "Transcribiendo archivo existente...";
        _localOperationDepth++;
        LastSavedReportPath = null;
        LastTranscript = null;
        LastGeneratedReport = null;
        ErrorDetails = null;
        try
        {
            TranscriptionSession session = await _recordingCoordinator.TranscribeExistingAudioAsync(audioPath);
            LastTranscript = session.Transcription.Transcript;
            StatusMessage = "Transcripción lista. Elige un prompt y genera el reporte.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al transcribir el archivo: {ex.Message}";
            ErrorDetails = ex.ToString();
        }
        finally
        {
            _localOperationDepth--;
            IsProcessing = _recordingCoordinator.IsProcessing;
        }
    }

    public async Task LoadTranscriptFileAsync(string transcriptPath)
    {
        if (!CanLoadExternalSource)
        {
            StatusMessage = "No se puede adjuntar una transcripción mientras hay una grabación o un proceso en curso.";
            return;
        }

        IsProcessing = true;
        StatusMessage = "Cargando transcripción...";
        LastSavedReportPath = null;
        LastTranscript = null;
        LastGeneratedReport = null;
        ErrorDetails = null;
        try
        {
            if (!string.Equals(Path.GetExtension(transcriptPath), ".txt", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Solo se aceptan archivos .txt.");
            }

            string transcript = await File.ReadAllTextAsync(transcriptPath);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                throw new InvalidOperationException("El archivo de transcripción está vacío.");
            }

            LastTranscript = transcript;
            StatusMessage = $"Transcripción cargada desde {Path.GetFileName(transcriptPath)}. Elige un prompt y genera el reporte.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al cargar la transcripción: {ex.Message}";
            ErrorDetails = ex.ToString();
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGenerateReport))]
    private async Task GenerateReportAsync()
    {
        if (SelectedPrompt is null || string.IsNullOrWhiteSpace(LastTranscript))
        {
            return;
        }

        IsProcessing = true;
        StatusMessage = $"Extrayendo el reporte con «{SelectedPrompt.DisplayName}»...";
        ErrorDetails = null;
        _localOperationDepth++;
        try
        {
            ExtractionSaveResult result = await _recordingCoordinator.ExtractAndSaveAsync(
                LastTranscript, SelectedPrompt.Id);
            LastGeneratedReport = result.ReportMarkdown;
            LastSavedReportPath = result.SavedReportPath;
            StatusMessage = $"Reporte guardado en el vault de Obsidian: {result.SavedReportPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al generar el reporte: {ex.Message}";
            ErrorDetails = ex.ToString();
        }
        finally
        {
            _localOperationDepth--;
            IsProcessing = _recordingCoordinator.IsProcessing;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSavedReport))]
    private void OpenSavedReport()
    {
        if (string.IsNullOrWhiteSpace(LastSavedReportPath) || !File.Exists(LastSavedReportPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{LastSavedReportPath}\"",
            UseShellExecute = true
        });
    }
}
