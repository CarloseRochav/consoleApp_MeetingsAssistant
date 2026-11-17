using MeetingAssistant.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;

namespace MeetingAssistant.App.Views;

public sealed partial class SettingsPage : Page, IConfirmNavigation
{
    /// <summary>
    /// Los ajustes de arriba y el editor de prompts. Cada uno se guarda con su
    /// propio botón, así que se pregunta por cada uno.
    /// </summary>
    public bool HasUnsavedChanges => Form.HasUnsavedChanges || PromptEditor.HasUnsavedEdits || ServerSettings.HasUnsavedChanges;

    public async Task<bool> ConfirmLeaveAsync()
    {
        if (!await Form.ConfirmDiscardAsync()) return false;
        return await PromptEditor.ConfirmLeaveAsync();
    }

    private readonly MeetingAssistant.Core.Services.DeviceAuthService _auth;
    private readonly StartupTaskService _startupTaskService;
    private bool _isSynchronizing;
    private bool _isBusy;

    public SettingsPage()
    {
        InitializeComponent();
        _startupTaskService = App.Services.GetRequiredService<StartupTaskService>();
        _auth = App.Services.GetRequiredService<MeetingAssistant.Core.Services.DeviceAuthService>();
        Loaded += SettingsPage_Loaded;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        ShowDevice(null);
        await RefreshStateAsync();
        await CheckDeviceAsync();
    }

    // ------------------------------------------------------- dispositivo (paso B)

    private void ShowDevice(string? serverCheck)
    {
        DeviceText.Text = _auth.Current is { } session
            ? $"Signed in as {session.Username} (idUser {session.IdUser}). This device: \"{session.DeviceName}\" (idDevice {session.IdDevice}). " +
              $"Server: {App.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["DevLog:BaseUrl"]}." +
              (serverCheck is null ? string.Empty : $" {serverCheck}")
            : "Not signed in on the server.";
        SignOutButton.IsEnabled = _auth.IsSignedIn;
    }

    private async Task CheckDeviceAsync()
    {
        if (!_auth.IsSignedIn) return;
        MeetingAssistant.Core.Services.VerifyOutcome outcome = await _auth.VerifyAsync();
        ShowDevice(outcome switch
        {
            MeetingAssistant.Core.Services.VerifyOutcome.Valid => "The server accepts this device.",
            MeetingAssistant.Core.Services.VerifyOutcome.Unknown => "Could not check with the server (offline?).",
            _ => null
        });
    }

    private void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        // App escucha SignedOut y abre el inicio de sesión.
        _auth.SignOut();
        ShowDevice(null);
    }

    // ------------------------------------------------------- inicio automático

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizing || _isBusy)
        {
            return;
        }

        _isBusy = true;
        StartupToggle.IsEnabled = false;
        StartupStatusText.Text = "Updating the startup setting...";

        try
        {
            StartupTaskState state = StartupToggle.IsOn
                ? await _startupTaskService.EnableAsync()
                : await _startupTaskService.DisableAsync();
            ApplyState(state);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task RefreshStateAsync()
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        StartupToggle.IsEnabled = false;
        StartupStatusText.Text = "Checking the startup state configured in Windows...";

        try
        {
            ApplyState(await _startupTaskService.GetStateAsync());
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void ApplyState(StartupTaskState state)
    {
        _isSynchronizing = true;
        try
        {
            switch (state)
            {
                case StartupTaskState.Enabled:
                    StartupToggle.IsOn = true;
                    StartupToggle.IsEnabled = true;
                    StartupStatusText.Text = "Meeting Assistant will start when you sign in to Windows.";
                    break;

                case StartupTaskState.Disabled:
                    StartupToggle.IsOn = false;
                    StartupToggle.IsEnabled = true;
                    StartupStatusText.Text = "Start with Windows is off.";
                    break;

                case StartupTaskState.DisabledByUser:
                    StartupToggle.IsOn = false;
                    StartupToggle.IsEnabled = false;
                    StartupStatusText.Text =
                        "Windows disabled start with Windows. Turn it on from " +
                        "Windows Settings > Apps > Startup.";
                    break;

                case StartupTaskState.DisabledByPolicy:
                    StartupToggle.IsOn = false;
                    StartupToggle.IsEnabled = false;
                    StartupStatusText.Text =
                        "A Windows policy blocks start with Windows. " +
                        "Check Windows Settings > Apps > Startup, or ask the administrator.";
                    break;

                case StartupTaskState.EnabledByPolicy:
                    StartupToggle.IsOn = true;
                    StartupToggle.IsEnabled = false;
                    StartupStatusText.Text =
                        "A Windows policy keeps start with Windows on.";
                    break;

                default:
                    StartupToggle.IsOn = false;
                    StartupToggle.IsEnabled = false;
                    StartupStatusText.Text = $"Windows returned an unknown startup state: {state}.";
                    break;
            }
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void SectionBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        int index = sender.Items.IndexOf(sender.SelectedItem);
        AccountPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        MachinePanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ServerPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PromptsPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(Exception exception)
    {
        _isSynchronizing = true;
        try
        {
            StartupToggle.IsOn = false;
            StartupToggle.IsEnabled = false;
            StartupStatusText.Text = $"Could not read the startup setting: {exception.Message}";
        }
        finally
        {
            _isSynchronizing = false;
        }
    }
}
