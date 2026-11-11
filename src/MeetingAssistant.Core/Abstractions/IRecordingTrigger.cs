using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Lo que necesita un disparador de grabaciones (la ventana, la bandeja, el hotkey y el endpoint
/// HTTP local): empezar y terminar. Desde el paso B, terminar deja la reunión en la cola de subidas
/// y vuelve en seguida; el reporte llega después, cuando el servidor lo termina.
///
/// Existe para que el endpoint, que vive en Infrastructure y no puede referenciar App, pase por el
/// mismo candado que la ventana, la bandeja y el hotkey (<c>RecordingCoordinator</c>).
/// </summary>
public interface IRecordingTrigger
{
    Task StartRecordingAsync(string source, CancellationToken cancellationToken = default);

    Task<QueuedMeeting> StopRecordingAndQueueAsync(CancellationToken cancellationToken = default);
}
