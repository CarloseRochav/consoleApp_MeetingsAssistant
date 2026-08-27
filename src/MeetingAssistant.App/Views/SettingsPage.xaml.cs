using MeetingAssistant.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;

namespace MeetingAssistant.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly StartupTaskService _startupTaskService;
    private readonly UserSettingsService _userSettingsService;
    private bool _isSynchronizing;
    private bool _isBusy;

    public SettingsPage()
    {
        InitializeComponent();
        _startupTaskService = App.Services.GetRequiredService<StartupTaskService>();
        _userSettingsService = App.Services.GetRequiredService<UserSettingsService>();
        Loaded += SettingsPage_Loaded;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        LoadUserSettings();
        await RefreshStateAsync();
    }

    // ---------------------------------------------------------------- ajustes

    /// <summary>
    /// Rellena los campos con la configuración **efectiva**, no con el archivo
    /// de usuario: si una clave viene del appsettings empaquetado o de una
    /// variable de entorno, se muestra igual. Un campo vacío tiene que
    /// significar "no hay valor", nunca "hay uno pero viene de otra capa".
    /// </summary>
    private void LoadUserSettings()
    {
        _isSynchronizing = true;
        try
        {
            UserSettings settings = _userSettingsService.LoadEffective();

            VaultPathBox.Text = settings.VaultPath ?? string.Empty;
            SubFolderBox.Text = settings.SubFolder ?? string.Empty;
            GeminiModelBox.Text = settings.GeminiModel ?? string.Empty;
            AzureEndpointBox.Text = settings.AzureEndpoint ?? string.Empty;
            AzureDeploymentBox.Text = settings.AzureDeployment ?? string.Empty;

            // Las claves se precargan para que "guardar" sin tocarlas no las
            // borre. Van en PasswordBox, así que se ven enmascaradas.
            DeepgramKeyBox.Password = settings.DeepgramApiKey ?? string.Empty;
            GeminiKeyBox.Password = settings.GeminiApiKey ?? string.Empty;
            AzureKeyBox.Password = settings.AzureApiKey ?? string.Empty;

            SelectProvider(settings.LlmProvider);
            UpdateProviderPanels();
            ValidateVaultPath();

            SettingsFilePathText.Text = $"Se guarda en: {UserSettingsService.FilePath}";
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void SelectProvider(string? provider)
    {
        string wanted = string.IsNullOrWhiteSpace(provider) ? "Gemini" : provider;
        foreach (object item in ProviderCombo.Items)
        {
            if (item is ComboBoxItem candidate &&
                string.Equals(candidate.Tag as string, wanted, StringComparison.OrdinalIgnoreCase))
            {
                ProviderCombo.SelectedItem = candidate;
                return;
            }
        }

        // Un proveedor desconocido en la configuración no debe dejar el combo
        // en blanco: se cae al primero y el usuario ve cuál va a usarse.
        ProviderCombo.SelectedIndex = 0;
    }

    private string SelectedProvider =>
        (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Gemini";

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateProviderPanels();

    /// <summary>
    /// Sólo se muestran las credenciales del proveedor elegido. Enseñar ambas
    /// invita a rellenar las de un servicio que no se usa, que es justo lo que
    /// <c>StartupConfigurationValidator</c> evita pedir.
    /// </summary>
    private void UpdateProviderPanels()
    {
        if (GeminiPanel is null || AzurePanel is null) return;

        bool isGemini = string.Equals(SelectedProvider, "Gemini", StringComparison.OrdinalIgnoreCase);
        GeminiPanel.Visibility = isGemini ? Visibility.Visible : Visibility.Collapsed;
        AzurePanel.Visibility = isGemini ? Visibility.Collapsed : Visibility.Visible;
    }

    private void VaultPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSynchronizing) return;
        ValidateVaultPath();
    }

    /// <summary>
    /// Comprueba que la carpeta exista de verdad, no sólo que el texto esté
    /// presente. Es el hueco que dejó abierto T7: una ruta bien formada pero
    /// inexistente pasa la validación de arranque y revienta recién al guardar
    /// el primer reporte, con la reunión ya grabada.
    /// </summary>
    private void ValidateVaultPath()
    {
        string path = VaultPathBox.Text.Trim();

        if (string.IsNullOrEmpty(path))
        {
            VaultPathStatusText.Text = "Sin ruta configurada: los reportes no se pueden guardar.";
            return;
        }

        try
        {
            VaultPathStatusText.Text = Directory.Exists(path)
                ? "La carpeta existe."
                : "La carpeta no existe todavía. Se creará al guardar el primer reporte, " +
                  "pero si la ruta está mal escrita no te vas a enterar hasta entonces.";
        }
        catch (Exception exception)
        {
            VaultPathStatusText.Text = $"Ruta no válida: {exception.Message}";
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            var settings = new UserSettings
            {
                VaultPath = VaultPathBox.Text,
                SubFolder = SubFolderBox.Text,
                LlmProvider = SelectedProvider,
                DeepgramApiKey = DeepgramKeyBox.Password,
                GeminiApiKey = GeminiKeyBox.Password,
                GeminiModel = GeminiModelBox.Text,
                AzureEndpoint = AzureEndpointBox.Text,
                AzureDeployment = AzureDeploymentBox.Text,
                AzureApiKey = AzureKeyBox.Password
            };

            _userSettingsService.Save(settings);
            SaveStatusText.Text =
                $"Guardado {DateTime.Now:HH:mm:ss}. Reinicia la app para que los cambios tengan efecto.";
        }
        catch (Exception exception)
        {
            App.LogStartupFailure("SettingsPage.Save", exception);
            SaveStatusText.Text = $"No se pudo guardar: {exception.Message}";
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
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
