using MeetingAssistant.App.Services;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Infrastructure.Api;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Cost;
using MeetingAssistant.Infrastructure.Llm;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace MeetingAssistant.App;

public partial class App : Application
{
    private MainWindow? _window;
    private LocalRecordingApiServer? _apiServer;
    private TrayIconService? _trayIconService;
    private bool _isExiting;

    public App()
    {
        Services = ConfigureServices();
        InitializeComponent();
    }

    public static IServiceProvider Services { get; private set; } = null!;

    public static Window MainWindow { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _apiServer = Services.GetRequiredService<LocalRecordingApiServer>();
        _apiServer.Start();

        _window = Services.GetRequiredService<MainWindow>();
        MainWindow = _window;
        _window.Activate();

        _trayIconService = Services.GetRequiredService<TrayIconService>();
        _trayIconService.AttachTo(_window);
        _trayIconService.OpenMainWindowRequested += (_, _) => _window.ShowFromTray();
        _trayIconService.ExitRequested += async (_, _) => await ExitApplicationAsync();
    }

    private static IServiceProvider ConfigureServices()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<ITranscriptionClient>(_ => new DeepgramTranscriptionClient(
            ReadRequiredSetting(configuration, "Deepgram", "ApiKey", "DEEPGRAM_API_KEY")));
        services.AddSingleton<ILlmClient>(_ => CreateLlmClient(configuration));
        services.AddSingleton<ICostEstimator, ConfigPricingCostEstimator>();
        services.AddSingleton<ILlmReportExtractor, LlmReportExtractor>();
        services.AddSingleton<IReportStorage, MarkdownReportStorage>();
        services.AddSingleton<IMeetingPipeline>(provider => new MeetingPipeline(
            provider.GetRequiredService<IAudioCaptureService>(),
            provider.GetRequiredService<ITranscriptionClient>(),
            provider.GetRequiredService<ILlmReportExtractor>(),
            provider.GetRequiredService<IReportStorage>(),
            Path.Combine(AppContext.BaseDirectory, "meeting-output")));
        services.AddSingleton<RecordingCoordinator>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<LocalRecordingApiServer>();
        services.AddTransient<RecordViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting || _window is null || _apiServer is null) return;

        RecordingCoordinator coordinator = Services.GetRequiredService<RecordingCoordinator>();
        if (coordinator.IsRecording || coordinator.IsProcessing)
        {
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                XamlRoot = ((FrameworkElement)_window.Content).XamlRoot,
                Title = "Hay una grabación en curso",
                Content = "¿Salir de todas formas? Se perderá la grabación.",
                PrimaryButtonText = "Salir sin guardar",
                CloseButtonText = "Cancelar",
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close
            };

            if (await dialog.ShowAsync() != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return;
        }

        _isExiting = true;
        _apiServer.Stop();
        _trayIconService?.Dispose();
        _window.BeginExitFromTray();
        Exit();
    }

    private static ILlmClient CreateLlmClient(IConfiguration configuration)
    {
        string provider = ReadSetting(configuration, "Llm", "Provider") ?? "Gemini";
        return provider.ToLowerInvariant() switch
        {
            "gemini" => new GeminiLlmClient(ReadRequiredSetting(configuration, "Gemini", "ApiKey", "GEMINI_API_KEY")),
            "azurefoundry" => new AzureFoundryLlmClient(
                ReadRequiredSetting(configuration, "AzureFoundry", "Endpoint"),
                ReadRequiredSetting(configuration, "AzureFoundry", "Deployment"),
                ReadSetting(configuration, "AzureFoundry", "ApiKey")),
            _ => throw new InvalidOperationException($"Proveedor '{provider}' no soportado. Usa 'Gemini' o 'AzureFoundry'.")
        };
    }

    private static string ReadRequiredSetting(IConfiguration configuration, string section, string property, string? environmentVariable = null) =>
        ReadSetting(configuration, section, property, environmentVariable) ?? throw new InvalidOperationException(
            $"Falta configurar {section}:{property} en appsettings.json o la variable de entorno {environmentVariable}.");

    private static string? ReadSetting(IConfiguration configuration, string section, string property, string? environmentVariable = null)
    {
        string? value = configuration[$"{section}:{property}"] ??
            (environmentVariable is null ? null : configuration[environmentVariable]);
        return string.IsNullOrWhiteSpace(value) || value.StartsWith("<", StringComparison.Ordinal) ? null : value;
    }
}
