using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MeetingAssistant.App.Services;

/// <summary>Mantiene el icono de bandeja y delega el ciclo de vida a App.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly RecordingCoordinator _coordinator;
    private TaskbarIcon? _trayIcon;
    private MenuFlyoutItem? _toggleRecordingItem;

    public TrayIconService(RecordingCoordinator coordinator)
    {
        _coordinator = coordinator;
        _coordinator.StateChanged += OnCoordinatorStateChanged;
    }

    public event EventHandler? OpenMainWindowRequested;
    public event EventHandler? ExitRequested;

    public void AttachTo(Window mainWindow)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        if (_trayIcon is not null) return;

        var menu = new MenuFlyout();

        // El label se refresca por StateChanged del coordinador, pero el
        // endpoint HTTP llama a IMeetingPipeline directo y no levanta ese
        // evento (hueco documentado en T1). Releer el estado vivo cada vez que
        // se abre el menu es lo que lo mantiene correcto tras un start por
        // HTTP; se valido en T2 y se perdio al migrar los Click a Command.
        // Con ContextMenuMode.PopupMenu (el default de H.NotifyIcon, y la
        // razon por la que los Click eran inertes) el MenuFlyout nunca se
        // muestra como flyout XAML, asi que Opening por si solo no basta; de
        // ahi el RightClickCommand de mas abajo. Opening se deja porque es el
        // que aplica si el modo cambia a SecondWindow/ActiveWindow.
        // RefreshToggleLabel es idempotente, correr los dos no molesta.
        menu.Opening += (_, _) => RefreshToggleLabel();

        _toggleRecordingItem = new MenuFlyoutItem
        {
            Command = new AsyncRelayCommand(ToggleRecordingAsync)
        };
        menu.Items.Add(_toggleRecordingItem);

        var openWindowItem = new MenuFlyoutItem
        {
            Text = "Abrir ventana principal",
            Command = new RelayCommand(() => OpenMainWindowRequested?.Invoke(this, EventArgs.Empty))
        };
        menu.Items.Add(openWindowItem);
        menu.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem
        {
            Text = "Salir",
            Command = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty))
        };
        menu.Items.Add(exitItem);

        // IMPORTANTE: esto debe ser Assets/TrayIcon.ico, un .ico de UN SOLO
        // frame codificado como DIB/BMP (32x32, BGRA). No un .png y no el
        // Assets/AppIcon.ico multi-frame. H.NotifyIcon convierte IconSource
        // con ImageExtensions.ToIconAsync -> StreamExtensions.ToSmallIcon,
        // que pasa el stream crudo a System.Drawing.Icon(Stream), y ese
        // constructor solo acepta un stream ICO. Con el .png fallaba con
        // "Argument 'picture' must be a picture that can be used as a Icon"
        // de forma asincrona (continuacion en el dispatcher), fuera del
        // alcance del try/catch de App.xaml.cs: la app quedaba sin icono de
        // bandeja, la ventana se ocultaba al cerrar y el proceso seguia vivo
        // e inalcanzable, reteniendo el puerto de la API local.
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Meeting Assistant",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/TrayIcon.ico")),
            ContextFlyout = menu
        };
        _trayIcon.RightClickCommand = new RelayCommand(RefreshToggleLabel);
        _trayIcon.ForceCreate();
        RefreshToggleLabel();
    }

    public void Dispose()
    {
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _toggleRecordingItem = null;
        GC.SuppressFinalize(this);
    }

    public void SetStatus(string text)
    {
        if (_trayIcon is not null)
            _trayIcon.ToolTipText = text;
    }

    public void ShowError(string title, string message) =>
        _trayIcon?.ShowNotification(title, message, NotificationIcon.Error);

    private async Task ToggleRecordingAsync()
    {
        try
        {
            if (_coordinator.IsRecording)
                await _coordinator.StopRecordingAndProcessAsync();
            else
                await _coordinator.StartRecordingAsync();
        }
        catch (Exception exception)
        {
            App.LogStartupFailure("TrayIconService.ToggleRecording", exception);
            _trayIcon?.ShowNotification("Meeting Assistant", exception.Message, NotificationIcon.Error);
        }
        finally
        {
            RefreshToggleLabel();
        }
    }

    private void OnCoordinatorStateChanged(object? sender, EventArgs e) => RefreshToggleLabel();

    private void RefreshToggleLabel()
    {
        if (_toggleRecordingItem is not null)
            _toggleRecordingItem.Text = _coordinator.IsRecording ? "Detener grabación" : "Grabar reunión";
    }
}
