using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Startup;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;

namespace MeetingAssistant.App.ViewModels;

/// <summary>Un ajuste del servidor en la pantalla: el valor de la base como pista, y el mío editable.</summary>
public sealed partial class ServerSettingItem(ServerSetting setting) : ObservableObject
{
    public string Key { get; } = setting.Key;

    public string Label { get; } = setting.Label;

    public string Hint { get; } = setting.Hint;

    /// <summary>Lo que vale si dejo el mío vacío.</summary>
    public string Placeholder { get; } = setting.BaseValue is { Length: > 0 } baseValue ? $"base: {baseValue}" : "sin valor en la base";

    public string? SavedOwnValue { get; set; } = setting.OwnValue;

    [ObservableProperty]
    private string value = setting.OwnValue ?? string.Empty;

    public bool IsChanged => !string.Equals(string.IsNullOrWhiteSpace(Value) ? null : Value.Trim(), SavedOwnValue, StringComparison.Ordinal);

    public override string ToString() => $"{Label}: {Value}";
}

/// <summary>
/// Mis ajustes en el servidor (paso B): proveedor y modelo del LLM, términos clave de Deepgram y
/// los días que se guarda el audio. Sólo mi capa: nunca un secreto, nunca un precio (Pricing es de
/// la base y sólo lo toca un admin). Vacío = vale el de la base.
/// </summary>
public partial class ServerSettingsViewModel(ServerSettingsService settings) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? errorDetails;

    public ObservableCollection<ServerSettingItem> Items { get; } = [];

    public bool HasUnsavedChanges => Items.Any(item => item.IsChanged);

    public async Task LoadAsync()
    {
        IsBusy = true;
        ErrorDetails = null;
        try
        {
            IReadOnlyList<ServerSetting> rows = await settings.LoadAsync();
            Items.Clear();
            foreach (ServerSetting row in rows) Items.Add(new ServerSettingItem(row));
            StatusMessage = null;
        }
        catch (Exception exception)
        {
            AppLog.Failure("ServerSettings.Load", exception);
            StatusMessage = "No se pudieron leer los ajustes del servidor: " + (exception is ApiException api ? api.Error.Describe() : exception.Message);
            ErrorDetails = exception.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        ErrorDetails = null;
        var problems = new List<string>();
        int saved = 0;
        try
        {
            foreach (ServerSettingItem item in Items.Where(item => item.IsChanged).ToList())
            {
                string? problem = await settings.SaveAsync(item.Key, item.Value, item.SavedOwnValue);
                if (problem is null)
                {
                    item.SavedOwnValue = string.IsNullOrWhiteSpace(item.Value) ? null : item.Value.Trim();
                    saved++;
                }
                else
                {
                    problems.Add(problem);
                }
            }

            StatusMessage = problems.Count == 0
                ? saved == 0 ? "No había cambios." : $"Guardado en el servidor ({saved}). Vale para las reuniones que se procesen desde ahora, en todos tus dispositivos."
                : $"Guardados {saved}; el servidor rechazó {problems.Count}: {string.Join(" ", problems)}";
        }
        catch (Exception exception)
        {
            AppLog.Failure("ServerSettings.Save", exception);
            StatusMessage = "No se pudo guardar en el servidor: " + (exception is ApiException api ? api.Error.Describe() : exception.Message);
            ErrorDetails = exception.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
