using MeetingAssistant.App.Services;
using MeetingAssistant.App.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MeetingAssistant.App.Views;

/// <summary>Los ajustes de esta máquina: vault, subcarpeta y el token del endpoint local.</summary>
public sealed partial class SettingsForm : UserControl
{
    private readonly UserSettingsService _userSettingsService;
    private bool _isSynchronizing;
    private UserSettings? _savedFields;

    public event EventHandler? Saved;

    public SettingsForm()
    {
        InitializeComponent();
        _userSettingsService = App.Services.GetRequiredService<UserSettingsService>();
        Load();
        Loaded += async (_, _) => await RefreshLeftoverAsync();
    }

    public bool HasUnsavedChanges => _savedFields is not null && ReadFields() != _savedFields;

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
        ApiAuthToken = ApiTokenBox.Password
    };

    private void Load()
    {
        _isSynchronizing = true;
        try
        {
            UserSettings settings = _userSettingsService.LoadEffective();
            VaultPathBox.Text = settings.VaultPath ?? string.Empty;
            SubFolderBox.Text = settings.SubFolder ?? string.Empty;
            ApiTokenBox.Password = settings.ApiAuthToken ?? string.Empty;
            ValidateVaultPath();
            SettingsFilePathText.Text = $"Se guarda en: {AppPaths.DatabasePath} (el token, cifrado para este usuario de Windows)";
            _savedFields = ReadFields();
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private async Task RefreshLeftoverAsync()
    {
        try
        {
            IReadOnlyList<string> leftover = await _userSettingsService.FindLeftoverKeysAsync();
            LeftoverPanel.Visibility = leftover.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LeftoverText.Text =
                $"Esta máquina todavía guarda {leftover.Count} ajuste(s) del modo local, que ya no se usan: {string.Join(", ", leftover)}. " +
                "Borrarlos no rota las claves en los proveedores (eso es parte del corte).";
        }
        catch (Exception exception)
        {
            AppLog.Failure("SettingsForm.RefreshLeftover", exception);
        }
    }

    private async void DeleteLeftoverButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteLeftoverButton.IsEnabled = false;
        try
        {
            int deleted = await _userSettingsService.DeleteLeftoverKeysAsync();
            SaveStatusText.Text = $"Se borraron {deleted} ajuste(s) viejos de la base local.";
            await RefreshLeftoverAsync();
        }
        catch (Exception exception)
        {
            AppLog.Failure("SettingsForm.DeleteLeftover", exception);
            SaveErrorPanel.Details = exception.ToString();
        }
        finally
        {
            DeleteLeftoverButton.IsEnabled = true;
        }
    }

    private void VaultPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSynchronizing) return;
        ValidateVaultPath();
    }

    private async void BrowseVaultButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
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
                : "La carpeta no existe todavía. Se creará al guardar el primer reporte, pero si la ruta está mal escrita no te vas a enterar hasta entonces.";
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
            await _userSettingsService.SaveAsync(settings);
            _savedFields = settings;
            SaveStatusText.Text = $"Guardado {DateTime.Now:HH:mm:ss}. Reinicia la app para que los cambios tengan efecto.";
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
