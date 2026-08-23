using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Convierte los eventos del coordinador en toasts de Windows. Su razón de ser
/// es la ventana oculta: desde que existe el hotkey global, grabar sin abrir la
/// app es el camino normal, y sin esto un fallo muere en
/// <c>RecordViewModel.StatusMessage</c>, que nadie ve si RecordPage no está
/// abierto.
///
/// Escucha <c>ReportSaved</c> y no <c>RecordingCompleted</c> a propósito: el
/// flujo de dos pasos de la ventana (transcribir y después extraer con un
/// prompt) guarda el reporte sin levantar <c>RecordingCompleted</c>, así que
/// suscribirse a aquel dejaba sin aviso justo al camino más usado.
///
/// El cuerpo se limita a rutas, nombres de prompt y mensajes de error: nunca
/// transcript ni contenido de la reunión, porque el historial de
/// notificaciones de Windows lo persistiría fuera del vault.
/// </summary>
public sealed class ActivityNotificationService : IDisposable
{
    private readonly RecordingCoordinator _coordinator;
    private bool _isDisposed;

    public ActivityNotificationService(RecordingCoordinator coordinator)
    {
        _coordinator = coordinator;
        _coordinator.RecordingStarted += OnRecordingStarted;
        _coordinator.TranscriptReady += OnTranscriptReady;
        _coordinator.ReportSaved += OnReportSaved;
        _coordinator.RecordingFailed += OnRecordingFailed;
        App.LogDiagnostic("ActivityNotificationService suscrito al coordinador.");
    }

    private void OnRecordingStarted(object? sender, EventArgs e) =>
        Show("RecordingStarted", "Grabación iniciada",
            "MeetingAssistant está capturando el audio de la reunión.");

    private void OnTranscriptReady(object? sender, TranscriptReadyEventArgs e) =>
        Show("TranscriptReady", "Transcripción lista",
            "Abre la ventana para elegir un prompt y generar el reporte.");

    private void OnReportSaved(object? sender, ReportSavedEventArgs e) =>
        Show("ReportSaved", "Reporte listo", e.SavedReportPath, e.Prompt.DisplayName);

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs e) =>
        Show("RecordingFailed", e.Operation, e.Exception.Message);

    /// <summary>
    /// <paramref name="eventName"/> existe solo para la traza: un toast que no
    /// aparece no deja rastro en ningún lado, y sin esto no se puede distinguir
    /// "el evento nunca llegó al servicio" de "Show corrió y Windows no mostró
    /// nada", que son problemas opuestos.
    /// </summary>
    private static void Show(string eventName, string title, string body, string? footer = null)
    {
        try
        {
            AppNotificationBuilder builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body);

            if (!string.IsNullOrWhiteSpace(footer))
            {
                builder.AddText(footer);
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
            App.LogDiagnostic($"Toast mostrado por {eventName}.");
        }
        catch (Exception exception)
        {
            // Una notificación es conveniencia, no dependencia crítica: si el
            // canal de toasts falla, la operación que la disparó sigue siendo
            // válida y lo único que se pierde es el aviso.
            App.LogStartupFailure($"ActivityNotificationService.Show({eventName})", exception);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _coordinator.RecordingStarted -= OnRecordingStarted;
        _coordinator.TranscriptReady -= OnTranscriptReady;
        _coordinator.ReportSaved -= OnReportSaved;
        _coordinator.RecordingFailed -= OnRecordingFailed;
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
