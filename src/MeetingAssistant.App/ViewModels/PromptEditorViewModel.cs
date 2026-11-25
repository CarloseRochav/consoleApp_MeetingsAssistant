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
    private string storageHint = "Loading prompts from the server...";

    private string? _loadedText;

    public ObservableCollection<PromptEntry> Prompts { get; } = [];

    public ObservableCollection<PromptVersionItem> Versions { get; } = [];

    public string SelectedPromptDescription => SelectedPrompt?.Catalog.Description ?? string.Empty;

    public bool IsDirty => SelectedPrompt is not null && Normalize(EditorText) != Normalize(SelectedPrompt.Text);

    public bool HasUnsavedEdits => _loadedText is not null && Normalize(EditorText) != Normalize(_loadedText);

    public Func<string, Task<bool>>? ConfirmDiscard { get; set; }

    public string DirtyHint => IsDirty
        ? "There are unsaved changes. Saving creates a new version in your layer and applies to new reports (including the phone's)."
        : "No changes: the editor shows the version in effect for you.";

    public bool HasStructuredOutputWarning =>
        string.Equals(SelectedPrompt?.Catalog.OutputKind, "StructuredMeetingReport", StringComparison.OrdinalIgnoreCase);

    public string? StructuredOutputWarning => HasStructuredOutputWarning
        ? "This prompt has to return JSON with the schema the server expects: summary, insights, " +
          "taskList (task, priority high/medium/low, context), requirements, indications, and openQuestions. " +
          "You can change the instructions, but if you change the output format, the next report will fail when it is read."
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
            StorageHint = "Prompts come from the server. Your edits are saved in your layer and apply to new reports on every device.";
            SelectedPrompt = Prompts.FirstOrDefault(prompt => string.Equals(prompt.PromptId, keepPromptId ?? SelectedPrompt?.PromptId, StringComparison.OrdinalIgnoreCase))
                ?? Prompts.FirstOrDefault();
            LoadActive();
        }
        catch (Exception exception)
        {
            AppLog.Failure("PromptEditor.Load", exception);
            StorageHint = "Could not read prompts from the server: " + (exception is ApiException api ? api.Error.Describe() : exception.Message);
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
        return await ConfirmDiscard(SelectedPrompt?.DisplayName ?? "this prompt");
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
            StatusMessage = "That row is a return to the original: it has no text of its own.";
            return;
        }

        if (!await ConfirmLeaveAsync()) return;
        EditorText = item.Text;
        _loadedText = item.Text;
        StatusMessage = $"Loaded {item.Title} into the editor. To put it in effect, save it as a new version.";
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
            StatusMessage = "Could not save. The prompt in effect did not change.";
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
        StatusMessage = "Changes discarded.";
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
            StatusMessage = "Could not go back to the original. The prompt in effect did not change.";
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
            null when prompt.BuiltInChosen => $"The original ({prompt.Catalog.Version}) is in effect: you went back to it.",
            null => $"The original ({prompt.Catalog.Version}) is in effect, as defined in the worker.",
            { IsMine: true } row => $"Your version {row.Version} is in effect, from {Local(row.CreatedAt)}.",
            { } row => $"The shared version {row.Version} is in effect, from {Local(row.CreatedAt)} (you do not have your own)."
        };

        Versions.Add(new PromptVersionItem(prompt.Catalog.Version, prompt.InEffect is null ? "original, defined in the worker · in effect" : "original, defined in the worker", prompt.Catalog.SystemPrompt));
        if (prompt.InEffect is { IsMine: false } shared)
        {
            Versions.Add(new PromptVersionItem(shared.Version ?? "(shared)", $"shared · {Local(shared.CreatedAt)} · in effect", shared.SystemPrompt));
        }

        foreach (PromptVersionRow row in prompt.Own)
        {
            bool active = prompt.InEffect?.IdPromptVersion == row.IdPromptVersion;
            string subtitle = string.Join(" · ", new[] { row.IsRevert ? "back to the original" : "yours", Local(row.CreatedAt), active ? "in effect" : null, row.Note }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            Versions.Add(new PromptVersionItem(row.Version ?? "(original)", subtitle, row.SystemPrompt));
        }
    }

    private static string Local(DateTimeOffset value) => value.ToLocalTime().ToString("MMM d, yyyy, HH:mm", SessionListItem.DisplayCulture);

    private static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n").Trim();
}
