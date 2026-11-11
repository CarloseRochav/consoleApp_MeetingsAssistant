using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Startup;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;

namespace MeetingAssistant.App.ViewModels;

/// <summary>Una fila de la lista de versiones: el original del catálogo o una edición.</summary>
public sealed class PromptVersionItem
{
    public PromptVersionItem(string title, string subtitle, string? text)
    {
        Title = title;
        Subtitle = subtitle;
        Text = text;
    }

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>El texto de esa versión; null en una vuelta al original (no hay texto que cargar).</summary>
    public string? Text { get; }

    public override string ToString() => $"{Title}. {Subtitle}";
}

/// <summary>
/// El editor de prompts por la API (paso B): el catálogo que publica el worker y MIS ediciones (la
/// capa propia, nunca la compartida, como el teléfono). Guardar crea una versión nueva que vale
/// para los reportes nuevos de todos mis dispositivos; "volver al original" escribe una vuelta al
/// texto del catálogo.
/// </summary>
public partial class PromptEditorViewModel(PromptLibraryService library) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StructuredOutputWarning))]
    [NotifyPropertyChangedFor(nameof(HasStructuredOutputWarning))]
    [NotifyPropertyChangedFor(nameof(SelectedPromptDescription))]
    private PromptEntry? selectedPrompt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(DirtyHint))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    private string editorText = string.Empty;

    [ObservableProperty]
    private string? note;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string versionName = string.Empty;

    [ObservableProperty]
    private string? activeVersionText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreOriginalCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? errorDetails;

    [ObservableProperty]
    private string storageHint = "Cargando los prompts del servidor...";

    private string? _loadedText;

    public ObservableCollection<PromptEntry> Prompts { get; } = [];

    public ObservableCollection<PromptVersionItem> Versions { get; } = [];

    public string SelectedPromptDescription => SelectedPrompt?.Catalog.Description ?? string.Empty;

    public bool IsDirty => SelectedPrompt is not null && Normalize(EditorText) != Normalize(SelectedPrompt.Text);

    public bool HasUnsavedEdits => _loadedText is not null && Normalize(EditorText) != Normalize(_loadedText);

    public Func<string, Task<bool>>? ConfirmDiscard { get; set; }

    public string DirtyHint => IsDirty
        ? "Hay cambios sin guardar. Guardar crea una versión nueva en tu capa y vale para los reportes nuevos (también los del teléfono)."
        : "Sin cambios: el editor muestra la versión que vale para ti.";

    public bool HasStructuredOutputWarning =>
        string.Equals(SelectedPrompt?.Catalog.OutputKind, "StructuredMeetingReport", StringComparison.OrdinalIgnoreCase);

    public string? StructuredOutputWarning => HasStructuredOutputWarning
        ? "Este prompt tiene que devolver JSON con el esquema que espera el servidor: summary, insights, " +
          "taskList (task, priority alta/media/baja, context), requirements, indications y openQuestions. " +
          "Puedes cambiar las instrucciones, pero si cambias el formato de salida, el próximo reporte va a fallar al interpretarse."
        : null;

    /// <summary>Carga el catálogo y mis ediciones. Lo llama el panel al abrirse.</summary>
    public async Task LoadAsync(string? keepPromptId = null)
    {
        IsBusy = true;
        ErrorDetails = null;
        try
        {
            IReadOnlyList<PromptEntry> entries = await library.LoadAsync();
            Prompts.Clear();
            foreach (PromptEntry entry in entries) Prompts.Add(entry);
            StorageHint = "Los prompts vienen del servidor. Tus ediciones se guardan en tu capa y valen para tus reportes nuevos de todos los dispositivos.";
            SelectedPrompt = Prompts.FirstOrDefault(prompt => string.Equals(prompt.PromptId, keepPromptId ?? SelectedPrompt?.PromptId, StringComparison.OrdinalIgnoreCase))
                ?? Prompts.FirstOrDefault();
            LoadActive();
        }
        catch (Exception exception)
        {
            AppLog.Failure("PromptEditor.Load", exception);
            StorageHint = "No se pudieron leer los prompts del servidor: " + (exception is ApiException api ? api.Error.Describe() : exception.Message);
            ErrorDetails = exception.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> ConfirmLeaveAsync()
    {
        if (!HasUnsavedEdits || ConfirmDiscard is null) return true;
        return await ConfirmDiscard(SelectedPrompt?.DisplayName ?? "el prompt");
    }

    public async Task<bool> TrySelectPromptAsync(PromptEntry prompt)
    {
        if (string.Equals(prompt.PromptId, SelectedPrompt?.PromptId, StringComparison.OrdinalIgnoreCase)) return true;
        if (!await ConfirmLeaveAsync()) return false;
        SelectedPrompt = prompt;
        return true;
    }

    public async Task LoadVersionAsync(PromptVersionItem item)
    {
        if (item.Text is null)
        {
            StatusMessage = "Esa fila es una vuelta al original: no tiene texto propio.";
            return;
        }

        if (!await ConfirmLeaveAsync()) return;
        EditorText = item.Text;
        _loadedText = item.Text;
        StatusMessage = $"Cargada {item.Title} en el editor. Para que valga, guárdala como versión nueva.";
    }

    partial void OnSelectedPromptChanged(PromptEntry? value)
    {
        Note = null;
        StatusMessage = null;
        ErrorDetails = null;
        LoadActive();
    }

    private bool CanSave() => IsDirty && !IsBusy && !string.IsNullOrWhiteSpace(EditorText) && !string.IsNullOrWhiteSpace(VersionName);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (SelectedPrompt is not { } prompt) return;
        IsBusy = true;
        ErrorDetails = null;
        try
        {
            PromptSaveResult result = await library.SaveAsync(prompt, VersionName, EditorText, Note);
            StatusMessage = result.Message;
            if (result.Saved)
            {
                Note = null;
                await LoadAsync(prompt.PromptId);
                StatusMessage = result.Message;
            }
        }
        catch (Exception exception)
        {
            AppLog.Failure("PromptEditor.Save", exception);
            StatusMessage = "No se pudo guardar. El prompt que vale no cambió.";
            ErrorDetails = exception.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDiscard() => IsDirty;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        LoadActive();
        StatusMessage = "Cambios descartados.";
    }

    private bool CanRestoreOriginal() => !IsBusy && SelectedPrompt?.CanUseBuiltIn == true;

    [RelayCommand(CanExecute = nameof(CanRestoreOriginal))]
    private async Task RestoreOriginalAsync()
    {
        if (SelectedPrompt is not { } prompt) return;
        if (!await ConfirmLeaveAsync()) return;

        IsBusy = true;
        ErrorDetails = null;
        try
        {
            PromptSaveResult result = await library.UseBuiltInAsync(prompt);
            if (result.Saved) await LoadAsync(prompt.PromptId);
            StatusMessage = result.Message;
        }
        catch (Exception exception)
        {
            AppLog.Failure("PromptEditor.RestoreOriginal", exception);
            StatusMessage = "No se pudo volver al original. El prompt que vale no cambió.";
            ErrorDetails = exception.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadActive()
    {
        Versions.Clear();
        if (SelectedPrompt is not { } prompt)
        {
            EditorText = string.Empty;
            _loadedText = null;
            ActiveVersionText = null;
            return;
        }

        EditorText = prompt.Text;
        _loadedText = prompt.Text;
        VersionName = prompt.SuggestedVersion();

        ActiveVersionText = prompt.InEffect switch
        {
            null when prompt.BuiltInChosen => $"Vale el original ({prompt.Catalog.Version}): volviste a él.",
            null => $"Vale el original ({prompt.Catalog.Version}), definido en el worker.",
            { IsMine: true } row => $"Vale tu versión {row.Version}, del {Local(row.CreatedAt)}.",
            { } row => $"Vale la versión compartida {row.Version}, del {Local(row.CreatedAt)} (no tienes una propia)."
        };

        Versions.Add(new PromptVersionItem(prompt.Catalog.Version, prompt.InEffect is null ? "original, definido en el worker · vale" : "original, definido en el worker", prompt.Catalog.SystemPrompt));
        if (prompt.InEffect is { IsMine: false } shared)
        {
            Versions.Add(new PromptVersionItem(shared.Version ?? "(compartida)", $"compartida · {Local(shared.CreatedAt)} · vale", shared.SystemPrompt));
        }

        foreach (PromptVersionRow row in prompt.Own)
        {
            bool active = prompt.InEffect?.IdPromptVersion == row.IdPromptVersion;
            string subtitle = string.Join(" · ", new[] { row.IsRevert ? "vuelta al original" : "tuya", Local(row.CreatedAt), active ? "vale" : null, row.Note }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            Versions.Add(new PromptVersionItem(row.Version ?? "(original)", subtitle, row.SystemPrompt));
        }
    }

    private static string Local(DateTimeOffset value) => value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", SessionListItem.DisplayCulture);

    private static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n").Trim();
}
