using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Muestra el resultado del coordinador incluso cuando la ventana principal
/// esta oculta. El cuerpo se limita a la ruta guardada o al mensaje de error:
/// nunca incluye el transcript ni el contenido de la reunion.
/// </summary>
public sealed class ReportNotificationService : IDisposable
{
    private readonly RecordingCoordinator _coordinator;
    private bool _isDisposed;

    public ReportNotificationService(RecordingCoordinator coordinator)
    {
        _coordinator = coordinator;
        _coordinator.RecordingCompleted += OnRecordingCompleted;
        _coordinator.RecordingFailed += OnRecordingFailed;
    }

    private void OnRecordingCompleted(object? sender, RecordingCompletedEventArgs e) =>
        Show("Reporte listo", e.Result.SavedReportPath);

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs e) =>
        Show("No se pudo crear el reporte", e.Exception.Message);

    private static void Show(string title, string body)
    {
        try
        {
            AppNotification notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception exception)
        {
            App.LogStartupFailure("ReportNotificationService.Show", exception);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _coordinator.RecordingCompleted -= OnRecordingCompleted;
        _coordinator.RecordingFailed -= OnRecordingFailed;
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
