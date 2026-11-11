using System.Net.NetworkInformation;
using MeetingAssistant.Core.Services;

namespace MeetingAssistant.App.Startup;

/// <summary>
/// Lo que corre en segundo plano desde el arranque (paso B): la cola de subidas retoma lo pendiente
/// de la sesión anterior, se despierta cuando vuelve la red, y se comprueba con la API que este
/// dispositivo no fue revocado (Select_Me). Sin red, la app abre igual y la cola espera.
/// </summary>
internal sealed class BackgroundMaintenance(MeetingUploadQueue queue, DeviceAuthService auth)
{
    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;

        int pending = queue.Items.Count(item => !item.IsFinished);
        AppLog.Diagnostic($"Cola de subidas: {pending} reunión(es) pendiente(s) de una sesión anterior; se retoman.");
        queue.Start();

        // Al volver la red, lo que esperaba por ella se intenta ya (sin esperar su turno espaciado).
        NetworkChange.NetworkAvailabilityChanged += (_, args) =>
        {
            if (!args.IsAvailable) return;
            AppLog.Diagnostic("Red disponible otra vez: se despierta la cola de subidas.");
            queue.Kick(resetBackoff: true);
        };

        // Al iniciar sesión (o volver a hacerlo tras un revocado), la cola sigue.
        auth.SignedIn += (_, _) => queue.Kick(resetBackoff: true);

        _ = Task.Run(async () =>
        {
            try
            {
                VerifyOutcome outcome = await auth.VerifyAsync();
                AppLog.Diagnostic($"Dispositivo en la API: {outcome}.");
            }
            catch (Exception exception)
            {
                AppLog.Failure("BackgroundMaintenance.Verify", exception);
            }
        });
    }
}
