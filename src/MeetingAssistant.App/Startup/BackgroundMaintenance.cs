using System.Net.NetworkInformation;
using MeetingAssistant.Core.Services;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.App.Startup;

/// <summary>
/// Lo que corre en segundo plano desde el arranque (paso B): la cola de subidas retoma lo pendiente
/// de la sesión anterior, se despierta cuando vuelve la red, se comprueba con la API que este
/// dispositivo no fue revocado (Select_Me), y una vez al día se borran los WAV originales cuyo
/// reporte llegó al vault hace más de <c>Storage:LocalAudioRetentionDays</c> días (30 por defecto).
/// </summary>
internal sealed class BackgroundMaintenance(MeetingUploadQueue queue, DeviceAuthService auth, LocalAudioRetention retention, IConfiguration configuration)
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

        StartRetentionLoop();
    }

    private void StartRetentionLoop()
    {
        double days = configuration.GetValue<double?>("Storage:LocalAudioRetentionDays") ?? 30;
        AppLog.Diagnostic(days > 0
            ? $"Audio local: el WAV original se borra {days:0.##} días después de que su reporte llega al vault."
            : "Audio local: Storage:LocalAudioRetentionDays = 0, no se borra nada.");
        if (days <= 0) return;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                LocalAudioRetentionResult result = retention.Run(days);
                if (result.Deleted > 0 || result.Failed > 0)
                {
                    AppLog.Diagnostic($"Audio local: {result.Deleted} WAV borrado(s) ({result.BytesFreed / 1048576.0:F1} MB); {result.Failed} no se pudieron borrar.");
                }

                await Task.Delay(TimeSpan.FromHours(24));
            }
        });
    }
}
