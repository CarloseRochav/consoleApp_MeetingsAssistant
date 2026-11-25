using MeetingAssistant.App.Startup;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Convierte los eventos del coordinador y de la cola de subidas en toasts de Windows. Su razón de
/// ser es la ventana oculta: grabar con el hotkey sin abrir la app es el camino normal. Desde el
/// paso B el reporte llega minutos después de detener (lo hace el servidor), así que el aviso de
/// "reporte listo" sale de la cola, no del coordinador.
///
/// El cuerpo se limita a rutas, estados y motivos de error: nunca transcript ni contenido de la
/// reunión, porque el historial de notificaciones de Windows lo persistiría fuera del vault.
/// </summary>
public sealed class ActivityNotificationService : IDisposable
{
    private readonly RecordingCoordinator _coordinator;
    private readonly MeetingUploadQueue _queue;
    private readonly DeviceAuthService _auth;
    private bool _isDisposed;

    public ActivityNotificationService(RecordingCoordinator coordinator, MeetingUploadQueue queue, DeviceAuthService auth)
    {
        _coordinator = coordinator;
        _queue = queue;
        _auth = auth;
        _coordinator.RecordingStarted += OnRecordingStarted;
        _coordinator.RecordingQueued += OnRecordingQueued;
        _coordinator.RecordingFailed += OnRecordingFailed;
        _queue.ReportReady += OnReportReady;
        _queue.MeetingFailed += OnMeetingFailed;
        _auth.SignedOut += OnSignedOut;
        AppLog.Diagnostic("ActivityNotificationService suscrito al coordinador y a la cola.");
    }

    private void OnRecordingStarted(object? sender, EventArgs e) =>
        Show("RecordingStarted", "Recording started", "Meeting Assistant is capturing the meeting audio.");

    private void OnRecordingQueued(object? sender, QueuedMeeting meeting) =>
        Show("RecordingQueued", "Recording saved",
            "It is sent to the server; the report reaches the vault when it is ready.",
            meeting.DurationSeconds is { } seconds ? $"{seconds / 60:F0} min of audio" : null);

    private void OnReportReady(object? sender, QueuedMeeting meeting) =>
        Show("ReportReady", meeting.Kind == QueuedMeetingKind.Reextract ? "New report ready" : "Report ready",
            meeting.ReportPath ?? "(no path)");

    private void OnMeetingFailed(object? sender, QueuedMeeting meeting) =>
        Show("MeetingFailed", "Could not create the report", meeting.Error ?? "Unknown error.",
            "The recording is still saved: you can retry it from Record.");

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs e) =>
        Show("RecordingFailed", e.Operation, e.Exception.Message);

    private void OnSignedOut(object? sender, SignedOutReason reason)
    {
        if (reason != SignedOutReason.Revoked) return;
        Show("DeviceRevoked", "Signed out by the server",
            "This device was revoked. Sign in again; pending recordings are sent afterwards.");
    }

    /// <summary><paramref name="eventName"/> existe sólo para la traza en el log.</summary>
    private static void Show(string eventName, string title, string body, string? footer = null)
    {
        try
        {
            AppNotificationBuilder builder = new AppNotificationBuilder().AddText(title).AddText(body);
            if (!string.IsNullOrWhiteSpace(footer)) builder.AddText(footer);
            AppNotificationManager.Default.Show(builder.BuildNotification());
            AppLog.Diagnostic($"Toast mostrado por {eventName}.");
        }
        catch (Exception exception)
        {
            AppLog.Failure($"ActivityNotificationService.Show({eventName})", exception);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _coordinator.RecordingStarted -= OnRecordingStarted;
        _coordinator.RecordingQueued -= OnRecordingQueued;
        _coordinator.RecordingFailed -= OnRecordingFailed;
        _queue.ReportReady -= OnReportReady;
        _queue.MeetingFailed -= OnMeetingFailed;
        _auth.SignedOut -= OnSignedOut;
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
