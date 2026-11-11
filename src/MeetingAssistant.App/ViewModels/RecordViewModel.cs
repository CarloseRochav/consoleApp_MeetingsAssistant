using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Services;
using MeetingAssistant.App.Startup;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;
using Microsoft.UI.Dispatching;

namespace MeetingAssistant.App.ViewModels;

/// <summary>Una reunión de la cola, lista para mostrar: dónde está y qué se puede hacer con ella.</summary>
public sealed partial class QueueItemViewModel : ObservableObject
{
    private static readonly Dictionary<string, string> TriggerLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        [SessionSource.Hotkey] = "atajo de teclado",
        [SessionSource.Tray] = "bandeja",
        [SessionSource.Http] = "endpoint HTTP",
        [SessionSource.Window] = "ventana",
        [SessionSource.Import] = "archivo importado",
        [SessionSource.Harness] = "harness"
    };

    public QueueItemViewModel(QueuedMeeting meeting) => Update(meeting);

    public QueuedMeeting Meeting { get; private set; } = null!;

    public string Id => Meeting.Id;

    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string? detail;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private bool isUploading;

    [ObservableProperty]
    private bool isFailed;

    /// <summary>waiting, uploading, processing, ready o failed. El color lo resuelve el tema.</summary>
    [ObservableProperty]
    private string statusTone = "waiting";

    [ObservableProperty]
    private bool canRetry;

    [ObservableProperty]
    private bool canRemove;

    [ObservableProperty]
    private bool canOpen;

    public void Update(QueuedMeeting meeting)
    {
        Meeting = meeting;
        string when = meeting.CreatedAtUtc.ToLocalTime().ToString("dddd d 'de' MMMM, HH:mm", SessionListItem.DisplayCulture);
        string what = meeting.Kind == QueuedMeetingKind.Reextract
            ? $"Re-extracción de la reunión {meeting.IdSession} con {meeting.PromptId}"
            : $"Grabación ({(TriggerLabels.TryGetValue(meeting.Trigger, out string? label) ? label : meeting.Trigger)}" +
              (meeting.DurationSeconds is { } seconds ? $", {seconds / 60:F0} min" : string.Empty) + ")";
        Title = $"{when} · {what}";
        UpdateProgress(meeting.SentBytes, meeting.TotalBytes);
        StatusText = meeting.Status switch
        {
            QueuedMeetingStatus.Waiting => "Esperando para enviar",
            QueuedMeetingStatus.Uploading => $"Enviando {Progress:F0}%",
            QueuedMeetingStatus.Processing => "En el servidor (transcribiendo y escribiendo el reporte)",
            QueuedMeetingStatus.WritingReport => "Escribiendo el reporte en el vault",
            QueuedMeetingStatus.Ready => "Listo",
            QueuedMeetingStatus.Failed => "Falló",
            _ => meeting.Status.ToString()
        };
        Detail = meeting.Status switch
        {
            QueuedMeetingStatus.Failed => meeting.Error,
            QueuedMeetingStatus.Ready => meeting.ReportPath,
            _ => meeting.Notice
        };
        IsUploading = meeting.Status == QueuedMeetingStatus.Uploading;
        IsFailed = meeting.Status == QueuedMeetingStatus.Failed;
        StatusTone = meeting.Status switch
        {
            QueuedMeetingStatus.Uploading => "uploading",
            QueuedMeetingStatus.Processing or QueuedMeetingStatus.WritingReport => "processing",
            QueuedMeetingStatus.Ready => "ready",
            QueuedMeetingStatus.Failed => "failed",
            _ => "waiting"
        };
        CanRetry = meeting.Status != QueuedMeetingStatus.Ready;
        CanRemove = meeting.IsFinished;
        CanOpen = meeting.Status == QueuedMeetingStatus.Ready && meeting.ReportPath is { } path && File.Exists(path);
    }

    public void UpdateProgress(long sent, long total)
    {
        Progress = total <= 0 ? 0 : Math.Min(100, sent * 100.0 / total);
        if (Meeting.Status == QueuedMeetingStatus.Uploading) StatusText = $"Enviando {Progress:F0}%";
    }

    public override string ToString() => $"{Title}. {StatusText}. {Detail}";
}

/// <summary>
/// Grabar y la cola de subidas (paso B). Grabar siempre funciona, con o sin red o sesión: la
/// grabación queda en la cola y se envía cuando se puede. Cada fila dice dónde está su reunión.
/// </summary>
public partial class RecordViewModel : ObservableObject
{
    private readonly RecordingCoordinator _coordinator;
    private readonly MeetingUploadQueue _queue;
    private readonly DeviceAuthService _auth;
    private readonly DispatcherQueue? _dispatcherQueue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    [NotifyPropertyChangedFor(nameof(CanLoadExternalSource))]
    [NotifyPropertyChangedFor(nameof(StageTitle))]
    [NotifyPropertyChangedFor(nameof(StageTone))]
    private bool isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadExternalSource))]
    [NotifyPropertyChangedFor(nameof(StageTitle))]
    [NotifyPropertyChangedFor(nameof(StageTone))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    private bool isProcessing;

    [ObservableProperty]
    private string statusMessage = "Listo para grabar.";

    [ObservableProperty]
    private string? errorDetails;

    [ObservableProperty]
    private string sessionText = string.Empty;

    [ObservableProperty]
    private QueueItemViewModel? selectedMeeting;

    /// <summary>El Markdown del reporte de la fila elegida (leído del .md del vault), para la vista previa.</summary>
    [ObservableProperty]
    private string? lastGeneratedReport;

    public ObservableCollection<QueueItemViewModel> Meetings { get; } = [];

    public string ButtonText => IsRecording ? "Detener grabación" : "Grabar reunión";

    /// <summary>La palabra grande de la tarjeta de grabación.</summary>
    public string StageTitle => IsRecording ? "Grabando" : IsProcessing ? "Guardando" : "Listo";

    /// <summary>waiting no aplica: uploading mientras graba, processing mientras guarda, ready en reposo.</summary>
    public string StageTone => IsRecording ? "uploading" : IsProcessing ? "processing" : "ready";

    /// <summary>Tono de cada paso del camino (Grabar, Enviar, Servidor, Reporte). Lo pinta el tema.</summary>
    [ObservableProperty]
    private string recordPhaseTone = "uploading";

    [ObservableProperty]
    private string sendPhaseTone = "waiting";

    [ObservableProperty]
    private string serverPhaseTone = "waiting";

    [ObservableProperty]
    private string reportPhaseTone = "waiting";

    public bool CanLoadExternalSource => !IsRecording && !IsProcessing;

    public RecordViewModel(RecordingCoordinator coordinator, MeetingUploadQueue queue, DeviceAuthService auth)
    {
        _coordinator = coordinator;
        _queue = queue;
        _auth = auth;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        IsRecording = coordinator.IsRecording;
        IsProcessing = coordinator.IsProcessing;
        StatusMessage = IsRecording ? "Grabando..." : "Listo para grabar.";
        UpdateSessionText();

        foreach (QueuedMeeting meeting in queue.Items) Meetings.Add(new QueueItemViewModel(meeting));
        SelectedMeeting = Meetings.FirstOrDefault(item => item.CanOpen);

        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _coordinator.RecordingFailed += OnRecordingFailed;
        _queue.Changed += OnQueueChanged;
        _queue.Progress += OnQueueProgress;
        _auth.SignedIn += OnSessionChanged;
        _auth.SignedOut += OnSessionChanged;
    }

    /// <summary>La página se va: deja de escuchar (el Frame la reconstruye en cada navegación).</summary>
    public void Detach()
    {
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        _coordinator.RecordingFailed -= OnRecordingFailed;
        _queue.Changed -= OnQueueChanged;
        _queue.Progress -= OnQueueProgress;
        _auth.SignedIn -= OnSessionChanged;
        _auth.SignedOut -= OnSessionChanged;
    }

    private void OnSessionChanged(object? sender, DeviceSession e) => RunOnUiThread(UpdateSessionText);

    private void OnSessionChanged(object? sender, SignedOutReason e) => RunOnUiThread(UpdateSessionText);

    private void UpdateSessionText() =>
        SessionText = _auth.Current is { } session
            ? $"Conectado al servidor como {session.Username}, dispositivo «{session.DeviceName}»."
            : "Sin sesión en el servidor: se puede grabar igual; lo grabado se envía al iniciar sesión.";

    private void OnCoordinatorStateChanged(object? sender, EventArgs e) => RunOnUiThread(() =>
    {
        IsRecording = _coordinator.IsRecording;
        IsProcessing = _coordinator.IsProcessing;
        if (IsRecording) StatusMessage = "Grabando...";
        else if (IsProcessing) StatusMessage = "Guardando la grabación...";
        else if (StatusMessage is "Grabando..." or "Guardando la grabación...") StatusMessage = "Listo para grabar.";
        UpdatePhases();
    });

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs e) => RunOnUiThread(() =>
    {
        StatusMessage = $"{e.Operation}: {e.Exception.Message}";
        ErrorDetails = e.Exception.ToString();
    });

    private void OnQueueChanged(object? sender, QueuedMeeting meeting) => RunOnUiThread(() =>
    {
        QueueItemViewModel? item = Meetings.FirstOrDefault(row => row.Id == meeting.Id);
        if (item is null)
        {
            var created = new QueueItemViewModel(meeting);
            Meetings.Insert(0, created);
            SelectedMeeting = created;
            return;
        }

        item.Update(meeting);
        if (SelectedMeeting == item) UpdatePhases();
        if (meeting.Status == QueuedMeetingStatus.Ready && (SelectedMeeting is null || SelectedMeeting == item)) ShowReport(item);
    });

    private void OnQueueProgress(object? sender, QueueProgress progress) => RunOnUiThread(() =>
        Meetings.FirstOrDefault(row => row.Id == progress.Id)?.UpdateProgress(progress.SentBytes, progress.TotalBytes));

    partial void OnSelectedMeetingChanged(QueueItemViewModel? value)
    {
        UpdatePhases();
        if (value is not null) ShowReport(value);
    }

    /// <summary>
    /// Un solo camino, el de la reunión elegida (o el de la grabación en curso):
    /// Grabar, Enviar, Servidor, Reporte. El paso activo va en índigo, los hechos
    /// en verde, el que falló en rojo.
    /// </summary>
    private void UpdatePhases()
    {
        if (IsRecording)
        {
            SetPhases("uploading", "waiting", "waiting", "waiting");
            return;
        }

        if (IsProcessing)
        {
            SetPhases("ready", "uploading", "waiting", "waiting");
            return;
        }

        QueuedMeeting? meeting = SelectedMeeting?.Meeting;
        switch (meeting?.Status)
        {
            case QueuedMeetingStatus.Waiting:
            case QueuedMeetingStatus.Uploading:
                SetPhases("ready", "uploading", "waiting", "waiting");
                break;
            case QueuedMeetingStatus.Processing:
                SetPhases("ready", "ready", "processing", "waiting");
                break;
            case QueuedMeetingStatus.WritingReport:
                SetPhases("ready", "ready", "ready", "uploading");
                break;
            case QueuedMeetingStatus.Ready:
                SetPhases("ready", "ready", "ready", "ready");
                break;
            case QueuedMeetingStatus.Failed when meeting.IdSession is null:
                SetPhases("ready", "failed", "waiting", "waiting");
                break;
            case QueuedMeetingStatus.Failed:
                SetPhases("ready", "ready", "failed", "waiting");
                break;
            default:
                SetPhases("uploading", "waiting", "waiting", "waiting");
                break;
        }
    }

    private void SetPhases(string record, string send, string server, string report)
    {
        RecordPhaseTone = record;
        SendPhaseTone = send;
        ServerPhaseTone = server;
        ReportPhaseTone = report;
    }

    private void ShowReport(QueueItemViewModel item)
    {
        if (SelectedMeeting != item) SelectedMeeting = item;
        try
        {
            LastGeneratedReport = item.CanOpen ? File.ReadAllText(item.Meeting.ReportPath!) : null;
        }
        catch (Exception exception)
        {
            AppLog.Failure("RecordViewModel.ShowReport", exception);
            LastGeneratedReport = null;
        }
    }

    private void RunOnUiThread(Action action)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess) action();
        else _dispatcherQueue.TryEnqueue(() => action());
    }

    private bool CanToggleRecording() => !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanToggleRecording))]
    private async Task ToggleRecordingAsync()
    {
        ErrorDetails = null;
        try
        {
            if (_coordinator.IsRecording)
            {
                QueuedMeeting queued = await _coordinator.StopRecordingAndQueueAsync();
                StatusMessage = $"Grabación guardada ({queued.TotalBytes / 1048576.0:F1} MB para enviar). El reporte llega al vault cuando el servidor termine.";
            }
            else
            {
                await _coordinator.StartRecordingAsync(SessionSource.Window);
                StatusMessage = "Grabando...";
            }
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            ErrorDetails = exception.ToString();
        }
    }

    public async Task ProcessExistingAudioAsync(string audioPath)
    {
        ErrorDetails = null;
        StatusMessage = "Convirtiendo el archivo...";
        try
        {
            QueuedMeeting queued = await _coordinator.QueueExistingAudioAsync(audioPath);
            StatusMessage = $"{Path.GetFileName(audioPath)} en la cola ({queued.TotalBytes / 1048576.0:F1} MB para enviar).";
        }
        catch (Exception exception)
        {
            StatusMessage = $"No se pudo encolar el archivo: {exception.Message}";
            ErrorDetails = exception.ToString();
        }
    }

    public void Retry(QueueItemViewModel item)
    {
        if (_queue.Retry(item.Id)) StatusMessage = "Se reintenta ahora.";
    }

    public void Remove(QueueItemViewModel item)
    {
        if (!_queue.Remove(item.Id)) return;
        Meetings.Remove(item);
        if (SelectedMeeting == item)
        {
            SelectedMeeting = null;
            LastGeneratedReport = null;
        }
    }

    public static void ShowInExplorer(QueueItemViewModel item)
    {
        if (item.Meeting.ReportPath is not { } path || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
    }

    /// <summary>"Enviar ahora": lo que espera su turno espaciado se intenta ya.</summary>
    [RelayCommand]
    private void SendNow()
    {
        _queue.Kick(resetBackoff: true);
        StatusMessage = _auth.IsSignedIn ? "Enviando lo pendiente..." : "Primero hay que iniciar sesión (Configuración).";
    }
}
