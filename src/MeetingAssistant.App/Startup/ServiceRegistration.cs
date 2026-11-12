using MeetingAssistant.App.Services;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Services;
using MeetingAssistant.Infrastructure.Api;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingAssistant.App.Startup;

/// <summary>
/// La raíz de composición de la app: qué implementación concreta va detrás de cada abstracción. Un
/// método por área. Desde el paso B de la Fase 8 (2026-11-10) no hay proveedores ni bases remotas en
/// esta máquina: todo pasa por la API de DevLog con el token de este dispositivo.
/// </summary>
internal static class ServiceRegistration
{
    public static IServiceCollection AddMeetingAssistant(this IServiceCollection services, BootstrapResult bootstrap) =>
        services
            .AddBootstrapInstances(bootstrap)
            .AddDevLogApi(bootstrap.Configuration)
            .AddRecording()
            .AddShell();

    /// <summary>Las mismas instancias que ya se usaron para migrar el esquema y para alimentar la configuración.</summary>
    private static IServiceCollection AddBootstrapInstances(this IServiceCollection services, BootstrapResult bootstrap)
    {
        services.AddSingleton(bootstrap.Configuration);
        services.AddSingleton(bootstrap.ConnectionFactory);
        services.AddSingleton(bootstrap.SchemaMigrator);
        services.AddSingleton(bootstrap.SecretProtector);
        services.AddSingleton(bootstrap.SettingsStore);
        return services;
    }

    /// <summary>
    /// El cliente de la API, la sesión del dispositivo y lo que la UI lee por ella. La dirección sale
    /// de <c>DevLog:BaseUrl</c>; se construye al pedirla, así que una instalación sin ella abre la
    /// ventana de primer arranque en vez de fallar aquí.
    /// </summary>
    private static IServiceCollection AddDevLogApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IDevLogApi>(_ => new DevLogApiClient(
            new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            configuration["DevLog:BaseUrl"] ?? string.Empty));
        services.AddSingleton<IDeviceSessionStore>(provider =>
            new DpapiDeviceSessionStore(AppPaths.DeviceFile, provider.GetRequiredService<MeetingAssistant.Core.Abstractions.ISecretProtector>()));
        // El dispositivo se llama como la máquina (handoff del paso B), así se reconoce al revocarlo.
        services.AddSingleton(provider => new DeviceAuthService(
            provider.GetRequiredService<IDevLogApi>(),
            provider.GetRequiredService<IDeviceSessionStore>(),
            Environment.MachineName));
        services.AddSingleton<MeetingHistoryService>();
        services.AddSingleton<MeetingCostsService>();
        services.AddSingleton<PromptLibraryService>();
        services.AddSingleton<ServerSettingsService>();
        return services;
    }

    /// <summary>La captura, la cola de subidas y los cuatro disparadores: ventana, bandeja, hotkey y HTTP.</summary>
    private static IServiceCollection AddRecording(this IServiceCollection services)
    {
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<IAudioConverter, UploadAudioConverter>();
        services.AddSingleton<IAudioUploader>(_ => new AzureBlockUploader(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }));
        services.AddSingleton<IQueuedMeetingStore>(provider => new JsonQueuedMeetingStore(
            AppPaths.QueueDirectory,
            provider.GetRequiredService<MeetingAssistant.Core.Abstractions.ISecretProtector>(),
            AppLog.Failure));
        services.AddSingleton<IReportStorage, MarkdownReportStorage>();
        services.AddSingleton(provider => new MeetingUploadQueue(
            provider.GetRequiredService<IDevLogApi>(),
            provider.GetRequiredService<DeviceAuthService>(),
            provider.GetRequiredService<IAudioUploader>(),
            provider.GetRequiredService<IQueuedMeetingStore>(),
            provider.GetRequiredService<IReportStorage>(),
            diagnostic: AppLog.Diagnostic,
            failure: AppLog.Failure));
        services.AddSingleton(provider => new MeetingRecorder(
            provider.GetRequiredService<IAudioCaptureService>(),
            provider.GetRequiredService<IAudioConverter>(),
            provider.GetRequiredService<MeetingUploadQueue>(),
            AppPaths.MeetingOutputDirectory,
            AppPaths.UploadDirectory,
            AppLog.Diagnostic,
            AppLog.Failure));
        services.AddSingleton<RecordingCoordinator>();
        // El endpoint HTTP vive en Infrastructure y no puede ver App: entra por esta interfaz de
        // Core, que es el mismo coordinador (mismo candado y mismos avisos).
        services.AddSingleton<IRecordingTrigger>(provider => provider.GetRequiredService<RecordingCoordinator>());
        services.AddSingleton(provider => new LocalRecordingApiServer(
            provider.GetRequiredService<IRecordingTrigger>(),
            provider.GetRequiredService<IConfiguration>(),
            AppLog.Failure));
        services.AddSingleton<ActivityNotificationService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton(provider => new LocalAudioRetention(
            provider.GetRequiredService<IQueuedMeetingStore>(), AppPaths.MeetingOutputDirectory, failure: AppLog.Failure));
        services.AddSingleton<BackgroundMaintenance>();
        return services;
    }

    /// <summary>
    /// La ventana y sus páginas. Los ViewModels de página son transient: el Frame reconstruye la
    /// página en cada navegación.
    /// </summary>
    private static IServiceCollection AddShell(this IServiceCollection services)
    {
        services.AddSingleton<StartupTaskService>();
        services.AddSingleton<UserSettingsService>();
        services.AddTransient<RecordViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<CostViewModel>();
        services.AddTransient<PromptEditorViewModel>();
        services.AddTransient<ServerSettingsViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
