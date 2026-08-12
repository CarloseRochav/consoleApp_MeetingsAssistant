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
        menu.Opening += (_, _) => RefreshToggleLabel();

        _toggleRecordingItem = new MenuFlyoutItem();
        _toggleRecordingItem.Click += ToggleRecording_Click;
        menu.Items.Add(_toggleRecordingItem);

        var openWindowItem = new MenuFlyoutItem { Text = "Abrir ventana principal" };
        openWindowItem.Click += (_, _) => OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(openWindowItem);
        menu.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem { Text = "Salir" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exitItem);

        // IMPORTANT: use a plain raster asset here, not Assets/AppIcon.ico.
        // H.NotifyIcon converts IconSource to a native HICON synchronously
        // during ForceCreate(); a multi-frame .ico loaded through
        // BitmapImage is not a reliably decodable source for that
        // conversion and was reproducing a hard, unhandled-exception crash
        // (STATUS_STOWED_EXCEPTION / 0xC000027B in Microsoft.UI.Xaml.dll,
        // confirmed via Windows Event Viewer) on every launch. The 24px
        // unplated PNG is the asset Windows itself generates for exactly
        // this taskbar/tray use case.
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Meeting Assistant",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.targetsize-24_altform-unplated.png")),
            ContextFlyout = menu
        };
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

    private async void ToggleRecording_Click(object sender, RoutedEventArgs e)
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
