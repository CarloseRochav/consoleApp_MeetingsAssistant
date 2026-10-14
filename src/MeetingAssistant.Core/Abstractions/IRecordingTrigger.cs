namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Lo que necesita un disparador externo de grabaciones —hoy, el endpoint HTTP
/// local—: empezar y terminar. Lo implementa <c>RecordingCoordinator</c> en App.
///
/// Existe para que el endpoint, que vive en Infrastructure y no puede
/// referenciar App, pase por el mismo candado que la ventana, la bandeja y el
/// hotkey. Llamando a <see cref="IMeetingPipeline"/> directo se saltaba ese
/// candado: un start por HTTP mientras la ventana transcribía pisaba la sesión
/// en curso, y además ninguna grabación por HTTP levantaba avisos.
/// </summary>
public interface IRecordingTrigger
{
    Task StartRecordingAsync(string source, CancellationToken cancellationToken = default);

    Task<MeetingPipelineResult> StopRecordingAndProcessAsync(CancellationToken cancellationToken = default);
}
