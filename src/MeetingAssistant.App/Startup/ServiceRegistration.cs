using MeetingAssistant.App.Services;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Services;
using MeetingAssistant.Infrastructure;
using MeetingAssistant.Infrastructure.Api;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Cost;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Storage.Postgres;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingAssistant.App.Startup;

/// <summary>
/// La raíz de composición de la app: qué implementación concreta va detrás de
/// cada abstracción. Un método por área, para que cambiar, por ejemplo, dónde
/// vive el historial no obligue a leer cómo se arma la ventana.
/// </summary>
internal static class ServiceRegistration
{
    public static IServiceCollection AddMeetingAssistant(this IServiceCollection services, BootstrapResult bootstrap) =>
        services
            .AddBootstrapInstances(bootstrap)
            .AddProviders(bootstrap.Configuration)
            .AddPrompts(bootstrap.PromptCatalog)
            .AddHistory(bootstrap)
            .AddCloudArchive()
            .AddRecording()
            .AddShell();

    /// <summary>
    /// Las mismas instancias que ya se usaron para migrar el esquema y para
    /// alimentar la configuración. Registrarlas, en vez de dejar que el
    /// contenedor construya unas segundas, garantiza que la configuración y la
    /// UI hablen con la misma base.
    /// </summary>
    private static IServiceCollection AddBootstrapInstances(this IServiceCollection services, BootstrapResult bootstrap)
    {
        services.AddSingleton(bootstrap.Configuration);
        services.AddSingleton(bootstrap.ConnectionFactory);
        services.AddSingleton(bootstrap.SchemaMigrator);
        services.AddSingleton(bootstrap.SecretProtector);
        services.AddSingleton(bootstrap.SettingsStore);
        // La base de Postgres puede existir por el historial o por los ajustes:
        // cada uno tiene su conmutador.
        if (bootstrap.Postgres is not null) services.AddSingleton(bootstrap.Postgres);
        if (bootstrap.CloudSettings is not null) services.AddSingleton(bootstrap.CloudSettings);
        return services;
    }

    /// <summary>Audio, transcripción, LLM y precios: los proveedores externos.</summary>
    private static IServiceCollection AddProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton(_ => ProviderClientFactory.CreateTranscriptionClient(configuration));
        services.AddSingleton(_ => ProviderClientFactory.CreateLlmClient(configuration));
        services.AddSingleton<ICostEstimator, ConfigPricingCostEstimator>();
        services.AddSingleton<ITranscriptionCostEstimator, ConfigPricingCostEstimator>();
        return services;
    }

    private static IServiceCollection AddPrompts(this IServiceCollection services, EditablePromptCatalog promptCatalog)
    {
        services.AddSingleton(promptCatalog);
        services.AddSingleton<IPromptCatalog>(promptCatalog);
        services.AddSingleton<ILlmReportExtractor, LlmReportExtractor>();
        return services;
    }

    /// <summary>Dónde vive el historial (Fase 6, paso 2): Database:Provider elige entre SQLite y Postgres.</summary>
    private static IServiceCollection AddHistory(this IServiceCollection services, BootstrapResult bootstrap)
    {
        if (bootstrap.HistoryInPostgres && bootstrap.Postgres is not null)
        {
            services.AddSingleton<IMeetingHistoryStore, PostgresMeetingHistoryStore>();
            services.AddSingleton<IArchiveUploadStore, PostgresArchiveUploadStore>();
        }
        else
        {
            services.AddSingleton<IMeetingHistoryStore, SqliteMeetingHistoryStore>();
            services.AddSingleton<IArchiveUploadStore, SqliteArchiveUploadStore>();
        }

        services.AddSingleton<IReportStorage, MarkdownReportStorage>();
        return services;
    }

    /// <summary>
    /// Subida en segundo plano del audio y los reportes (Fase 6, paso 1) y la
    /// retención del audio local (paso 5). Sin AzureStorage:ConnectionString
    /// quedan apagadas.
    /// </summary>
    private static IServiceCollection AddCloudArchive(this IServiceCollection services)
    {
        services.AddSingleton<ICloudArchive, AzureBlobCloudArchive>();
        services.AddSingleton(provider => new ArchiveUploadService(
            provider.GetRequiredService<ICloudArchive>(),
            provider.GetRequiredService<IArchiveUploadStore>(),
            AppLog.Failure));
        // Sólo mira MeetingOutputDirectory: un audio importado desde otra
        // carpeta del usuario no se borra nunca.
        services.AddSingleton(provider => new LocalAudioRetentionService(
            provider.GetRequiredService<ICloudArchive>(),
            provider.GetRequiredService<IArchiveUploadStore>(),
            AppPaths.MeetingOutputDirectory,
            AppLog.Failure));
        services.AddSingleton<BackgroundMaintenance>();
        return services;
    }

    /// <summary>El pipeline y los cuatro disparadores: ventana, bandeja, hotkey y HTTP.</summary>
    private static IServiceCollection AddRecording(this IServiceCollection services)
    {
        services.AddSingleton<IMeetingPipeline>(provider => new MeetingPipeline(
            provider.GetRequiredService<IAudioCaptureService>(),
            provider.GetRequiredService<ITranscriptionClient>(),
            provider.GetRequiredService<ILlmReportExtractor>(),
            provider.GetRequiredService<IReportStorage>(),
            AppPaths.MeetingOutputDirectory,
            // El historial se engancha en el pipeline y no en RecordingCoordinator
            // porque el pipeline es el único punto por el que pasan **todos** los
            // caminos, incluido el harness. Un solo punto, sin filas duplicadas.
            provider.GetRequiredService<IMeetingHistoryStore>(),
            AppLog.Failure,
            provider.GetRequiredService<ITranscriptionCostEstimator>(),
            provider.GetRequiredService<ArchiveUploadService>()));
        services.AddSingleton<RecordingCoordinator>();
        // El endpoint HTTP vive en Infrastructure y no puede ver App: entra por
        // esta interfaz de Core, que es el mismo coordinador. Así comparte el
        // candado y los avisos con la ventana, la bandeja y el hotkey.
        services.AddSingleton<IRecordingTrigger>(provider => provider.GetRequiredService<RecordingCoordinator>());
        services.AddSingleton(provider => new LocalRecordingApiServer(
            provider.GetRequiredService<IRecordingTrigger>(),
            provider.GetRequiredService<IConfiguration>(),
            AppLog.Failure));
        services.AddSingleton<ActivityNotificationService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<GlobalHotkeyService>();
        return services;
    }

    /// <summary>
    /// La ventana y sus páginas. Los ViewModels de página son transient: el
    /// Frame reconstruye la página en cada navegación, y un singleton sólo
    /// conservaría estado que la página ya no puede mostrar.
    /// </summary>
    private static IServiceCollection AddShell(this IServiceCollection services)
    {
        services.AddSingleton<StartupTaskService>();
        services.AddSingleton<UserSettingsService>();
        services.AddTransient<RecordViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<CostViewModel>();
        services.AddTransient<PromptEditorViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
