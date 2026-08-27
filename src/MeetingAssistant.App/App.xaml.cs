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
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace MeetingAssistant.App;

public partial class App : Application
{
    private MainWindow? _window;
    private LocalRecordingApiServer? _apiServer;
    private ActivityNotificationService? _activityNotificationService;
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

        try
        {
            _apiServer.Start();
        }
        catch (Exception exception)
        {
            // El endpoint HTTP local es un disparador opcional: la bandeja y el
            // hotkey son los caminos principales, y una grabación disparada por
            // HTTP hoy ni siquiera levanta los eventos del coordinador. Aun así
            // esto era la primera sentencia de LaunchCore y estaba fuera de todo
            // try/catch, así que un puerto que no se puede escuchar abortaba el
            // arranque entero: sin Register() de notificaciones, sin ventana, sin
            // bandeja y sin hotkey — la app muerta por una conveniencia. Mismo
            // criterio que TrayIconService: se registra y se sigue.
            LogStartupFailure("LocalRecordingApiServer.Start", exception);
        }

        // Spike de Fase 5: deja constancia en cada arranque de si el binario
        // nativo de SQLite cargó bajo esta forma de ejecución. Instalada, la app
        // corre desde WindowsApps, y un paquete nativo por RID es justo el tipo
        // de cosa que funciona suelta y falla empaquetada — ya pasó con la ruta
        // del log y con el directorio de audio. Describe() nunca lanza.
        LogDiagnostic(MeetingAssistant.Infrastructure.Storage.SqliteEnvironmentProbe.Describe());

        try
        {
            LogDiagnostic("Base de reuniones: " +
                Services.GetRequiredService<Infrastructure.Storage.Sqlite.SqliteSchemaMigrator>().Migrate());
        }
        catch (Exception exception)
        {
            // Regla escrita al planificar Fase 5: una base rota **no puede**
            // impedir arrancar. El precedente es T4.4, donde una excepción de
            // arranque se llevó puesta la app entera y costó nueve días
            // encontrarla. Grabar, transcribir y guardar en el vault no dependen
            // de esto; lo que se pierde es el historial.
            LogStartupFailure("SqliteSchemaMigrator.Migrate", exception);
        }

        try
        {
            _activityNotificationService = Services.GetRequiredService<ActivityNotificationService>();
            AppNotificationManager.Default.Register();
            _appNotificationsRegistered = true;
            LogDiagnostic("AppNotificationManager.Register OK; ActivityNotificationService suscrito.");
        }
        catch (Exception exception)
        {
            _activityNotificationService?.Dispose();
            _activityNotificationService = null;
            LogStartupFailure("AppNotificationManager.Register", exception);
        }

        _window = Services.GetRequiredService<MainWindow>();
        MainWindow = _window;
        _window.Activate();

        // Program.cs redirige a esta instancia cualquier lanzamiento posterior
        // (incluida la activacion por COM al hacer clic en un toast). Como
        // cerrar la ventana solo la oculta, lo util al recibir la redireccion
        // es traerla de vuelta: si no, un segundo lanzamiento no haria nada
        // visible y pareceria que la app no arranco.
        AppInstance.GetCurrent().Activated += OnInstanceActivated;

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
    ///
    /// Ojo con dónde buscarlo, porque depende de CÓMO esté registrada la app y
    /// no de esta ruta. Hay que mirar los dos lados antes de concluir que no hay
    /// log:
    ///
    ///   - MSIX firmado e instalado: %LOCALAPPDATA%\MeetingAssistant\
    ///     (la ruta plana, tal cual la arma este Path.Combine). Medido en T6a y
    ///     confirmado de nuevo en T5.
    ///   - Registro de desarrollo (`dotnet run`): LocalApplicationData está
    ///     redirigido y el archivo aparece bajo
    ///     %LOCALAPPDATA%\Packages\{PackageFamilyName}\LocalCache\Local\MeetingAssistant.
    ///
    /// Buscar en el lado equivocado ya costó caro dos veces: el 2026-08-23 hizo
    /// dar por bueno un Register() que llevaba fallando en cada arranque desde
    /// que se implementó T4. Este comentario decía que la ruta plana nunca se
    /// usaba, lo cual dejó de ser cierto al instalar el paquete firmado en T6a.
    /// </summary>
    public static string StartupErrorLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingAssistant",
        "startup-errors.log");

    public static string MeetingOutputDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingAssistant",
        "meeting-output");

    /// <summary>
    /// Base local de reuniones (Fase 5). Mismo destino y mismo criterio que el
    /// log, el audio y la configuración de usuario: lo que la app necesita
    /// escribir no puede vivir dentro del paquete instalado, que es de sólo
    /// lectura.
    ///
    /// Ojo: <b>sobrevive a la desinstalación</b>, igual que el resto de la
    /// carpeta. Esa decisión se tomó cuando ahí sólo había un log y unos .wav;
    /// con la base pasa a haber además todos los transcripts.
    /// </summary>
    public static string MeetingDatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingAssistant",
        "meetings.db");

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

    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        // Llega en un hilo del runtime, no en el de UI.
        _window?.DispatcherQueue.TryEnqueue(() => _window?.ShowFromTray());
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

    /// <summary>
    /// Traza de diagnóstico, en el mismo archivo que
    /// <see cref="LogStartupFailure"/>. Existe porque una notificación no tiene
    /// ninguna superficie donde depurarse: cuando no aparece un toast, sin esto
    /// no hay forma de distinguir "el evento nunca llegó al servicio" de "Show
    /// se llamó y Windows no lo mostró", que son problemas opuestos.
    /// </summary>
    internal static void LogDiagnostic(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupErrorLogPath)!);
            File.AppendAllText(
                StartupErrorLogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [diag] {message}\n");
        }
        catch
        {
            // Mismo criterio que LogStartupFailure: si no se puede escribir el
            // diagnóstico, no vale arriesgar un segundo fallo por dejar rastro.
        }
    }

    private static IServiceProvider ConfigureServices()
    {
        // El orden es la regla: empaquetado (valores de fábrica) -> archivo del
        // usuario (lo que edita SettingsPage) -> variables de entorno. Cada capa
        // pisa a la anterior.
        //
        // La capa de usuario existe porque instalada la app lee su
        // appsettings.json desde C:\Program Files\WindowsApps\..., que es de
        // sólo lectura: sin esto, cambiar el vault o una API key obliga a
        // reconstruir y reinstalar el .msix. SetBasePath no le afecta —
        // UserSettingsService.FilePath es una ruta absoluta.
        //
        // optional: true a propósito. Es la instalación limpia: el archivo no
        // existe hasta que alguien guarda algo.
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile(UserSettingsService.FilePath, optional: true, reloadOnChange: false)
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
            MeetingOutputDirectory));
        services.AddSingleton<RecordingCoordinator>();
        services.AddSingleton<ActivityNotificationService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<StartupTaskService>();
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton(_ =>
            new Infrastructure.Storage.Sqlite.SqliteConnectionFactory(MeetingDatabasePath));
        services.AddSingleton<Infrastructure.Storage.Sqlite.SqliteSchemaMigrator>();
        services.AddSingleton<ISecretProtector, Infrastructure.Storage.Sqlite.DpapiSecretProtector>();
        services.AddSingleton<IMeetingHistoryStore, Infrastructure.Storage.Sqlite.SqliteMeetingHistoryStore>();
        services.AddSingleton<ISettingsStore, Infrastructure.Storage.Sqlite.SqliteSettingsStore>();
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
        _activityNotificationService?.Dispose();
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
            // Gemini:Model es opcional: sin él manda el default del cliente
            // (gemini-3.5-flash-lite). Se lee para que SettingsPage pueda
            // cambiarlo sin tocar código, igual que Deployment hace de "modelo"
            // del lado de Azure.
            "gemini" => ReadSetting(configuration, "Gemini", "Model") is { } geminiModel
                ? new GeminiLlmClient(ReadRequiredSetting(configuration, "Gemini", "ApiKey", "GEMINI_API_KEY"), geminiModel)
                : new GeminiLlmClient(ReadRequiredSetting(configuration, "Gemini", "ApiKey", "GEMINI_API_KEY")),
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
