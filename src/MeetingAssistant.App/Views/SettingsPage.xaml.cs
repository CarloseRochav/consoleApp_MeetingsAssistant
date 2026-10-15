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
    public bool HasUnsavedChanges => Form.HasUnsavedChanges || PromptEditor.HasUnsavedEdits;

    public async Task<bool> ConfirmLeaveAsync()
    {
        if (!await Form.ConfirmDiscardAsync()) return false;
        return await PromptEditor.ConfirmLeaveAsync();
    }

    private readonly StartupTaskService _startupTaskService;
    private bool _isSynchronizing;
    private bool _isBusy;

    public SettingsPage()
    {
        InitializeComponent();
        _startupTaskService = App.Services.GetRequiredService<StartupTaskService>();
        Loaded += SettingsPage_Loaded;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e) => await RefreshStateAsync();

    // ------------------------------------------------------- inicio automático

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizing || _isBusy)
        {
            return;
        }

        _isBusy = true;
        StartupToggle.IsEnabled = false;
        StartupStatusText.Text = "Actualizando la configuración de inicio...";

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
        StartupStatusText.Text = "Consultando el estado configurado en Windows...";

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
                    StartupStatusText.Text = "Meeting Assistant se iniciará cuando entres a Windows.";
                    break;

                case StartupTaskState.Disabled:
                    StartupToggle.IsOn = false;
                    StartupToggle.IsEnabled = true;
                    StartupStatusText.Text = "El inicio automático está desactivado.";
                    break;

                case StartupTaskState.DisabledByUser:
                    StartupToggle.IsOn = false;
                    StartupToggle.IsEnabled = false;
                    StartupStatusText.Text =
                        "Windows deshabilitó el inicio automático. Actívalo desde " +
                        "Configuración de Windows > Aplicaciones > Inicio.";
                    break;

                case StartupTaskState.DisabledByPolicy:
                    StartupToggle.IsOn = false;
                    StartupToggle.IsEnabled = false;
                    StartupStatusText.Text =
                        "Una directiva de Windows bloquea el inicio automático. " +
                        "Revisa Configuración de Windows > Aplicaciones > Inicio o consulta al administrador.";
                    break;

                case StartupTaskState.EnabledByPolicy:
                    StartupToggle.IsOn = true;
                    StartupToggle.IsEnabled = false;
                    StartupStatusText.Text =
                        "Una directiva de Windows mantiene activo el inicio automático.";
                    break;

                default:
                    StartupToggle.IsOn = false;
                    StartupToggle.IsEnabled = false;
                    StartupStatusText.Text = $"Windows devolvió un estado de inicio desconocido: {state}.";
                    break;
            }
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void ShowError(Exception exception)
    {
        _isSynchronizing = true;
        try
        {
            StartupToggle.IsOn = false;
            StartupToggle.IsEnabled = false;
            StartupStatusText.Text = $"No se pudo consultar el inicio automático: {exception.Message}";
        }
        finally
        {
            _isSynchronizing = false;
        }
    }
}
