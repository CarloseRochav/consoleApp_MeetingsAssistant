using MeetingAssistant.App.Services;
using MeetingAssistant.App.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MeetingAssistant.App.Views;

/// <summary>
/// Los campos de ajustes y su botón de guardar. Vive aparte de
/// <see cref="SettingsPage"/> porque la ventana de primer arranque necesita
/// exactamente lo mismo: dos copias de estos campos terminarían guardando
/// claves distintas.
/// </summary>
public sealed partial class SettingsForm : UserControl
{
    private readonly UserSettingsService _userSettingsService;
    private bool _isSynchronizing;

    /// <summary>
    /// Los campos tal como quedaron al cargarse o al último guardado. Se toma de
    /// los controles y no de la configuración, para que normalizaciones como el
    /// proveedor por defecto no cuenten como un cambio del usuario.
    /// </summary>
    private UserSettings? _savedFields;

    /// <summary>Se dispara después de guardar bien, con todo ya escrito.</summary>
    public event EventHandler? Saved;

    public SettingsForm()
    {
        InitializeComponent();
        _userSettingsService = App.Services.GetRequiredService<UserSettingsService>();
        Load();
    }

    public bool HasUnsavedChanges => _savedFields is not null && ReadFields() != _savedFields;

    /// <summary>
    /// Pregunta antes de perder cambios. Si el diálogo no se puede mostrar, no
    /// se pierde nada: devuelve false.
    /// </summary>
    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!HasUnsavedChanges) return true;
        if (XamlRoot is null) return false;

        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Hay ajustes sin guardar",
                Content = "Cambiaste ajustes y no pulsaste «Guardar cambios». Si sigues, se pierden.",
                PrimaryButtonText = "Descartar cambios",
                CloseButtonText = "Seguir editando",
                DefaultButton = ContentDialogButton.Close
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception exception)
        {
            AppLog.Failure("SettingsForm.ConfirmDiscard", exception);
            return false;
        }
    }

    private UserSettings ReadFields() => new()
    {
        VaultPath = VaultPathBox.Text,
        SubFolder = SubFolderBox.Text,
        LlmProvider = SelectedProvider,
        DeepgramApiKey = DeepgramKeyBox.Password,
        GeminiApiKey = GeminiKeyBox.Password,
        GeminiModel = GeminiModelBox.Text,
        AzureEndpoint = AzureEndpointBox.Text,
        AzureDeployment = AzureDeploymentBox.Text,
        AzureApiKey = AzureKeyBox.Password,
        PostgresConnectionString = PostgresConnectionBox.Password,
        AzureStorageConnectionString = StorageConnectionBox.Password,
        ApiAuthToken = ApiTokenBox.Password
    };

    /// <summary>
    /// Rellena los campos con la configuración **efectiva**, no con lo guardado
    /// en la base: si una clave viene del appsettings empaquetado o de una
    /// variable de entorno, se muestra igual. Un campo vacío tiene que
    /// significar "no hay valor", nunca "hay uno pero viene de otra capa".
    /// </summary>
    private void Load()
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
            PostgresConnectionBox.Password = settings.PostgresConnectionString ?? string.Empty;
            StorageConnectionBox.Password = settings.AzureStorageConnectionString ?? string.Empty;
            ApiTokenBox.Password = settings.ApiAuthToken ?? string.Empty;

            SelectProvider(settings.LlmProvider);
            UpdateProviderPanels();
            ValidateVaultPath();

            // Desde Fase 5 paso 5 esto se guarda en la base local y las
            // credenciales se cifran con DPAPI, atadas a este usuario de
            // Windows. Vale decirlo acá: copiar la base a otro perfil deja las
            // claves ilegibles y hay que volver a escribirlas.
            SettingsFilePathText.Text =
                $"Se guarda en: {AppPaths.DatabasePath} (las claves y cadenas de conexión, cifradas para este usuario de Windows)";

            _savedFields = ReadFields();
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
    /// En una máquina nueva la ruta del vault es lo único que no se puede copiar
    /// de otra: elegirla con el selector evita una ruta mal escrita.
    /// </summary>
    private async void BrowseVaultButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");

            // Una app de escritorio tiene que decirle al selector de qué ventana
            // es hijo; sin esto lanza. La ventana sale del propio control, así
            // que sirve igual dentro de la principal o de la de primer arranque.
            nint windowHandle = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is not null) VaultPathBox.Text = folder.Path;
        }
        catch (Exception exception)
        {
            AppLog.Failure("SettingsForm.BrowseVault", exception);
            VaultPathStatusText.Text = $"No se pudo abrir el selector de carpetas: {exception.Message}";
        }
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

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        SaveErrorPanel.Details = null;
        try
        {
            UserSettings settings = ReadFields();

            // Sólo se prueban las cadenas que cambiaron: Neon dormido tarda
            // segundos en contestar, y guardar un cambio de vault no tiene por
            // qué esperarlo.
            SaveStatusText.Text = "Probando las conexiones que cambiaron...";
            string? connectionProblem = await _userSettingsService.CheckChangedConnectionsAsync(settings, _savedFields);
            if (connectionProblem is not null)
            {
                SaveStatusText.Text = "No se guardó nada: una cadena de conexión no funciona. Revisa que esté copiada entera.";
                SaveErrorPanel.Details = connectionProblem;
                return;
            }

            await _userSettingsService.SaveAsync(settings);
            _savedFields = settings;
            SaveStatusText.Text =
                $"Guardado {DateTime.Now:HH:mm:ss}. Reinicia la app para que los cambios tengan efecto.";
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            AppLog.Failure("SettingsForm.Save", exception);
            SaveStatusText.Text = "No se pudo guardar.";
            SaveErrorPanel.Details = exception.ToString();
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }
}
