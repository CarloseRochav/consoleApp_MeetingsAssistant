using MeetingAssistant.App.Services;
using MeetingAssistant.App.Startup;
using MeetingAssistant.App.Views;
using MeetingAssistant.Infrastructure.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace MeetingAssistant.App;

/// <summary>
/// El ciclo de vida de la app: arrancar, mostrar los errores de arranque y
/// salir. Cómo se arma la configuración está en
/// <see cref="ConfigurationBootstrapper"/>, qué va detrás de cada abstracción en
/// <see cref="ServiceRegistration"/>, y el trabajo en segundo plano en
/// <see cref="BackgroundMaintenance"/>.
/// </summary>
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
    private bool _exitPromptOpen;

    /// <summary>
    /// Lo que hizo el arranque antes de que hubiera ventana. Se vuelca al log en
    /// LaunchCore, que es donde se revisa qué hizo un arranque.
    /// </summary>
    private IReadOnlyList<string> _startupDiagnostics = [];

    /// <summary>Si no está vacía, se abre <see cref="SetupWindow"/> en vez de la principal.</summary>
    private IReadOnlyList<string> _missingSettings = [];

    private SetupWindow? _setupWindow;

    public App()
    {
        // Los manejadores van primero: cualquier cosa que falle después de
        // esta línea queda registrada en vez de terminar como una "stowed
        // exception" (0xc000027b) que el sistema entrega al depurador JIT.
        RegisterGlobalExceptionHandlers();

        // El arranque no puede lanzar desde el constructor: aquí todavía no
        // existe UI donde mostrar el error y la excepción escaparía al framework
        // XAML. Se guarda y se reporta en OnLaunched.
        try
        {
            BootstrapResult bootstrap = ConfigurationBootstrapper.Run();
            _startupDiagnostics = bootstrap.Diagnostics;
            _missingSettings = bootstrap.MissingSettings;
            // Se arma completo aunque falten ajustes: la ventana de primer
            // arranque usa los mismos servicios de configuración, y lo que
            // necesita las claves (los clientes) sólo se construye al pedirlo.
            Services = new ServiceCollection().AddMeetingAssistant(bootstrap).BuildServiceProvider();
        }
        catch (Exception exception)
        {
            _configurationFailure = exception;
            AppLog.Failure("App.ConfigureServices", exception);
            Services = new ServiceCollection().BuildServiceProvider();
        }

        InitializeComponent();
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
            if (_missingSettings.Count > 0)
            {
                LaunchSetup();
                return;
            }

            LaunchCore();
        }
        catch (Exception exception)
        {
            // Sin este catch la excepción sube al framework XAML, se convierte
            // en 0xc000027b y Windows la entrega al depurador JIT: el usuario
            // ve un diálogo de depurador y una segunda instancia de Visual
            // Studio en lugar de un mensaje de error accionable.
            AppLog.Failure("App.OnLaunched", exception);
            ShowStartupError("App.OnLaunched", exception);
        }
    }

    /// <summary>
    /// Instalación nueva o ajustes borrados: sólo la ventana de configuración.
    /// Nada de bandeja, hotkey ni endpoint, que necesitan lo que falta. Cerrarla
    /// sin guardar cierra la app.
    /// </summary>
    private void LaunchSetup()
    {
        foreach (string diagnostic in _startupDiagnostics) AppLog.Diagnostic(diagnostic);

        _setupWindow = new SetupWindow(_missingSettings);
        _setupWindow.Closed += (_, _) => Exit();
        _setupWindow.Activate();
        AppInstance.GetCurrent().Activated += (_, _) =>
            _setupWindow?.DispatcherQueue.TryEnqueue(() => _setupWindow?.Activate());
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
            // hotkey son los caminos principales. Un puerto que no se puede
            // escuchar no puede abortar el arranque entero. Mismo criterio que
            // TrayIconService: se registra y se sigue.
            AppLog.Failure("LocalRecordingApiServer.Start", exception);
        }

        // Spike de Fase 5: deja constancia en cada arranque de si el binario
        // nativo de SQLite cargó bajo esta forma de ejecución. Instalada, la app
        // corre desde WindowsApps, y un paquete nativo por RID es justo el tipo
        // de cosa que funciona suelta y falla empaquetada. Describe() nunca lanza.
        AppLog.Diagnostic(MeetingAssistant.Infrastructure.Storage.SqliteEnvironmentProbe.Describe());
        foreach (string diagnostic in _startupDiagnostics) AppLog.Diagnostic(diagnostic);

        try
        {
            _activityNotificationService = Services.GetRequiredService<ActivityNotificationService>();
            AppNotificationManager.Default.Register();
            _appNotificationsRegistered = true;
            AppLog.Diagnostic("AppNotificationManager.Register OK; ActivityNotificationService suscrito.");
        }
        catch (Exception exception)
        {
            _activityNotificationService?.Dispose();
            _activityNotificationService = null;
            AppLog.Failure("AppNotificationManager.Register", exception);
        }

        // En su propio try porque resolver los servicios sí puede fallar, y un
        // respaldo opcional no puede impedir abrir la ventana.
        try
        {
            Services.GetRequiredService<BackgroundMaintenance>().Start();
        }
        catch (Exception exception)
        {
            AppLog.Failure("BackgroundMaintenance.Start", exception);
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
            AppLog.Failure("TrayIconService.AttachTo", exception);
        }

        _globalHotkeyService = Services.GetRequiredService<GlobalHotkeyService>();
        _globalHotkeyService.Register(_window);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        // Excepciones no manejadas en el hilo de UI de XAML.
        UnhandledException += (_, e) =>
        {
            AppLog.Failure("Application.UnhandledException", e.Exception);
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
                AppLog.Failure("AppDomain.UnhandledException", exception);
            }
        };

        // Excepciones de Task que nadie llegó a observar (por ejemplo, un
        // async void o un Task descartado durante el arranque).
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Failure("TaskScheduler.UnobservedTaskException", e.Exception);
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
            AppLog.Failure("App.ShowStartupError", windowException);
        }
    }

    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        // Llega en un hilo del runtime, no en el de UI.
        _window?.DispatcherQueue.TryEnqueue(() => _window?.ShowFromTray());
    }

    private async Task ExitApplicationAsync()
    {
        // _exitPromptOpen: un segundo "Salir" de la bandeja con la pregunta
        // todavía abierta intentaría abrir otro ContentDialog, y WinUI sólo
        // admite uno a la vez (lanza).
        if (_isExiting || _exitPromptOpen || _window is null || _apiServer is null) return;

        _exitPromptOpen = true;
        try
        {
            if (!await ConfirmExitAsync(_window)) return;
        }
        finally
        {
            _exitPromptOpen = false;
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
                AppLog.Failure("AppNotificationManager.Unregister", exception);
            }
        }
        _globalHotkeyService?.Dispose();
        _trayIconService?.Dispose();
        _window.BeginExitFromTray();
        Exit();
    }

    /// <summary>true si se puede salir: no hay nada en curso, o el usuario aceptó perderlo.</summary>
    private static async Task<bool> ConfirmExitAsync(MainWindow window)
    {
        RecordingCoordinator coordinator = Services.GetRequiredService<RecordingCoordinator>();
        if (coordinator.IsRecording || coordinator.IsProcessing)
        {
            // "Salir" llega desde la bandeja, casi siempre con la ventana
            // oculta, y un ContentDialog se dibuja dentro de la ventana: sin
            // mostrarla, la pregunta queda invisible y parece que Salir no hizo
            // nada.
            window.ShowFromTray();
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                XamlRoot = ((FrameworkElement)window.Content).XamlRoot,
                Title = "Hay una grabación en curso",
                Content = "¿Salir de todas formas? Se perderá la grabación.",
                PrimaryButtonText = "Salir sin guardar",
                CloseButtonText = "Cancelar",
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close
            };

            try
            {
                if (await dialog.ShowAsync() != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return false;
            }
            catch (Exception exception)
            {
                // Otro ContentDialog abierto en la ventana. Ante la duda no se
                // sale: se perdería la grabación sin que nadie lo aceptara.
                AppLog.Failure("App.ConfirmExit", exception);
                return false;
            }
        }

        // Un prompt editado y sin guardar en Configuración se perdería igual que
        // al cambiar de página.
        return await window.ConfirmLeaveCurrentPageAsync();
    }
}
