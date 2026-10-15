using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Services;
using MeetingAssistant.Infrastructure.Storage.Postgres;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.App.Startup;

/// <summary>
/// El trabajo que la app hace sola, en segundo plano: despertar Postgres,
/// retomar las subidas a la nube y la retención del audio local. Nada de esto
/// se espera ni puede lanzar hacia el arranque: un respaldo opcional no puede
/// impedir abrir la ventana.
/// </summary>
internal sealed class BackgroundMaintenance
{
    private readonly ICloudArchive _archive;
    private readonly ArchiveUploadService _uploads;
    private readonly LocalAudioRetentionService _retention;
    private readonly IConfiguration _configuration;
    private readonly PostgresDatabase? _postgres;

    public BackgroundMaintenance(
        ICloudArchive archive,
        ArchiveUploadService uploads,
        LocalAudioRetentionService retention,
        IConfiguration configuration,
        PostgresDatabase? postgres = null)
    {
        _archive = archive;
        _uploads = uploads;
        _retention = retention;
        _configuration = configuration;
        _postgres = postgres;
    }

    public void Start()
    {
        WakePostgres();
        StartArchiveLoop();
    }

    /// <summary>
    /// La primera consulta a Neon tras un rato sin uso tarda; mejor que esa
    /// espera ocurra acá y no cuando se abre Historial.
    /// </summary>
    private void WakePostgres()
    {
        if (_postgres is not { } postgres) return;

        _ = Task.Run(async () =>
        {
            try
            {
                AppLog.Diagnostic($"Postgres: {await postgres.MigrateAsync()}");
            }
            catch (Exception exception)
            {
                AppLog.Failure("PostgresDatabase.Migrate", exception);
            }
        });
    }

    /// <summary>
    /// Primero las subidas pendientes y después la retención, en ese orden y una
    /// vez por día: la app vive días en la bandeja.
    /// </summary>
    private void StartArchiveLoop()
    {
        AppLog.Diagnostic(_archive.IsConfigured
            ? "Archivo en la nube: configurado; se retoman las subidas pendientes."
            : "Archivo en la nube: sin AzureStorage:ConnectionString, apagado.");

        double retentionDays = _configuration.GetValue<double?>("AzureStorage:LocalAudioRetentionDays") ?? 0;
        AppLog.Diagnostic(retentionDays > 0
            ? $"Retención de audio local: {retentionDays:0.##} días."
            : "Retención de audio local: apagada (AzureStorage:LocalAudioRetentionDays sin valor). No se borra nada.");

        _ = Task.Run(async () =>
        {
            while (true)
            {
                await _uploads.ResumeUnfinishedAsync();

                if (retentionDays > 0)
                {
                    AudioRetentionResult result = await _retention.RunAsync(TimeSpan.FromDays(retentionDays), apply: true);
                    if (result.Deleted.Count > 0 || result.Kept.Count > 0)
                    {
                        AppLog.Diagnostic(
                            $"Retención de audio local: {result.Deleted.Count} borrados ({result.BytesFreed / 1048576.0:F1} MB); " +
                            $"{result.Kept.Count} cumplieron el plazo y se conservaron por no poder confirmarse en la nube.");
                    }
                }

                await Task.Delay(TimeSpan.FromHours(24));
            }
        });
    }
}
