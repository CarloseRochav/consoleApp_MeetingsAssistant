using MeetingAssistant.App.Services;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.App.Views;
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
using Microsoft.Windows.AppNotifications;

namespace MeetingAssistant.App;

public partial class App : Application
{
    private MainWindow? _window;
    private LocalRecordingApiServer? _apiServer;
    private ReportNotificationService? _reportNotificationService;
    private bool _appNotificationsRegistered;
    private TrayIconService? _trayIconService;
    private GlobalHotkeyService? _globalHotkeyService;
    private StartupErrorWindow? _errorWindow;
    private Exception? _configurationFailure;
    private bool _isExiting;

    public App()
    {
        // Los manejadores van primero: cualquier cosa que falle después de
        // esta línea queda registrada en vez de terminar como una "stowed
        // exception" (0xc000027b) que el sistema entrega al depurador JIT.
        RegisterGlobalExceptionHandlers();

        // ConfigureServices no puede lanzar desde el constructor: aquí todavía
        // no existe UI donde mostrar el error y la excepción escaparía al
        // framework XAML. Se guarda y se reporta en OnLaunched.
        try
        {
            Services = ConfigureServices();
        }
        catch (Exception exception)
        {
            _configurationFailure = exception;
            LogStartupFailure("App.ConfigureServices", exception);
            Services = new ServiceCollection().BuildServiceProvider();
        }

        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Red de seguridad de diagnóstico: captura cualquier excepción no
        // manejada en el hilo de UI (incluidas las que ocurren en callbacks
        // asíncronos, fuera del alcance de los try/catch en OnLaunched) para
        // que quede registrada en vez de perderse en un fallo nativo opaco.
        LogStartupFailure("Application.UnhandledException", e.Exception);
    }

    public static IServiceProvider Services { get; private set; } = null!;

    public static Window MainWindow { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_configurationFailure is not null)
        {
            ShowStartupError("App.ConfigureServices", _configurationFailure);
            return;
        }

        try
        {
            LaunchCore();
        }
        catch (Exception exception)
        {
            // Sin este catch la excepción sube al framework XAML, se convierte
            // en 0xc000027b y Windows la entrega al depurador JIT: el usuario
            // ve un diálogo de depurador y una segunda instancia de Visual
            // Studio en lugar de un mensaje de error accionable.
            LogStartupFailure("App.OnLaunched", exception);
            ShowStartupError("App.OnLaunched", exception);
        }
    }

    private void LaunchCore()
    {
        _apiServer = Services.GetRequiredService<LocalRecordingApiServer>();
        _apiServer.Start();

        try
        {
            _reportNotificationService = Services.GetRequiredService<ReportNotificationService>();
            AppNotificationManager.Default.Register();
            _appNotificationsRegistered = true;
        }
        catch (Exception exception)
        {
            _reportNotificationService?.Dispose();
            _reportNotificationService = null;
            LogStartupFailure("AppNotificationManager.Register", exception);
        }

        _window = Services.GetRequiredService<MainWindow>();
        MainWindow = _window;
        _window.Activate();

        try
        {
            _trayIconService = Services.GetRequiredService<TrayIconService>();
            _trayIconService.AttachTo(_window);
            _trayIconService.OpenMainWindowRequested += (_, _) => _window.ShowFromTray();
            _trayIconService.ExitRequested += async (_, _) => await ExitApplicationAsync();
        }
        catch (Exception exception)
        {
            // El icono de bandeja es una conveniencia, no una dependencia
            // crítica: si falla al crearse (driver de shell, versión de
            // Windows, recurso de icono, etc.), la app debe seguir siendo
            // usable desde la ventana principal en vez de crashear por
            // completo. Se registra en texto plano porque este código corre
            // antes de que exista cualquier UI para mostrar el error.
            LogStartupFailure("TrayIconService.AttachTo", exception);
        }

        _globalHotkeyService = Services.GetRequiredService<GlobalHotkeyService>();
        _globalHotkeyService.Register(_window);
    }

    /// <summary>
    /// Ruta del log de diagnóstico. Deliberadamente NO usa
    /// <see cref="AppContext.BaseDirectory"/>: cuando la app corre empaquetada
    /// el directorio base queda bajo WindowsApps, donde la escritura falla o se
    /// redirige de forma opaca, y el log se perdía justo en el escenario que
    /// más importa depurar.
    /// </summary>
    public static string StartupErrorLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingAssistant",
        "startup-errors.log");

    private void RegisterGlobalExceptionHandlers()
    {
        // Excepciones no manejadas en el hilo de UI de XAML.
        UnhandledException += (_, e) =>
        {
            LogStartupFailure("Application.UnhandledException", e.Exception);
            e.Handled = true;

            // La ventana de error solo tiene sentido si la app aún no llegó a
            // arrancar. Una vez la ventana principal está viva, un fallo suelto
            // (p. ej. el icono de bandeja, que falla de forma asíncrona y por
            // eso escapa al try/catch de OnLaunched) deja la app perfectamente
            // usable: queda en el log y no se interrumpe al usuario.
            if (_window is null)
            {
                ShowStartupError("Application.UnhandledException", e.Exception);
            }
        };

        // Hilos que no son el de UI: aquí ya no se puede evitar la terminación
        // del proceso, pero sí dejar rastro de la causa antes de morir.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                LogStartupFailure("AppDomain.UnhandledException", exception);
            }
        };

        // Excepciones de Task que nadie llegó a observar (por ejemplo, un
        // async void o un Task descartado durante el arranque).
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogStartupFailure("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    private void ShowStartupError(string context, Exception exception)
    {
        try
        {
            // Solo la primera falla abre ventana: si el fallo se repite en
            // cascada, apilar ventanas de error no aporta información nueva.
            if (_errorWindow is not null) return;

            _errorWindow = new StartupErrorWindow(context, exception);
            _errorWindow.Closed += (_, _) => _errorWindow = null;
            _errorWindow.Activate();
        }
        catch (Exception windowException)
        {
            // Si ni siquiera se puede abrir la ventana de error, el log ya
            // escrito por el llamador es el único registro que queda.
            LogStartupFailure("App.ShowStartupError", windowException);
        }
    }

    internal static void LogStartupFailure(string context, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupErrorLogPath)!);
            string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{context}] {exception}\n\n";
            File.AppendAllText(StartupErrorLogPath, entry);
        }
        catch
        {
            // Si ni siquiera se puede escribir el log de diagnóstico, no hay
            // nada más que hacer aquí sin arriesgar un segundo crash.
        }
    }

    private static IServiceProvider ConfigureServices()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        // Antes de registrar nada: si falta configuración, es preferible una
        // lista completa aquí que ir descubriéndola clave a clave según cada
        // servicio se construya bajo demanda.
        StartupConfigurationValidator.Validate(configuration);

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<ITranscriptionClient>(_ => new DeepgramTranscriptionClient(
            ReadRequiredSetting(configuration, "Deepgram", "ApiKey", "DEEPGRAM_API_KEY")));
        services.AddSingleton<ILlmClient>(_ => CreateLlmClient(configuration));
        services.AddSingleton<ICostEstimator, ConfigPricingCostEstimator>();
        services.AddSingleton<IPromptCatalog, BuiltInPromptCatalog>();
        services.AddSingleton<ILlmReportExtractor, LlmReportExtractor>();
        services.AddSingleton<IReportStorage, MarkdownReportStorage>();
        services.AddSingleton<IMeetingPipeline>(provider => new MeetingPipeline(
            provider.GetRequiredService<IAudioCaptureService>(),
            provider.GetRequiredService<ITranscriptionClient>(),
            provider.GetRequiredService<ILlmReportExtractor>(),
            provider.GetRequiredService<IReportStorage>(),
            Path.Combine(AppContext.BaseDirectory, "meeting-output")));
        services.AddSingleton<RecordingCoordinator>();
        services.AddSingleton<ReportNotificationService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<GlobalHotkeyService>();
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
        _reportNotificationService?.Dispose();
        if (_appNotificationsRegistered)
        {
            try
            {
                AppNotificationManager.Default.Unregister();
            }
            catch (Exception exception)
            {
                LogStartupFailure("AppNotificationManager.Unregister", exception);
            }
        }
        _globalHotkeyService?.Dispose();
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
