using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;

namespace MeetingAssistant.App.ViewModels;

/// <summary>Una fila del historial de versiones de un prompt.</summary>
public sealed class PromptVersionItem
{
    public PromptVersionItem(PromptVersionEntry entry)
    {
        Entry = entry;

        var parts = new List<string>();
        if (entry.IsActive) parts.Add("activa");
        parts.Add(entry.IsOriginal
            ? "original, definida en el código"
            : entry.CreatedAtUtc!.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", SessionListItem.DisplayCulture));
        if (!string.IsNullOrWhiteSpace(entry.Note)) parts.Add(entry.Note);

        Title = entry.Version;
        Subtitle = string.Join(" · ", parts);
    }

    public PromptVersionEntry Entry { get; }

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>Mismo motivo que en <see cref="SessionListItem.ToString"/>.</summary>
    public override string ToString() => $"{Title}. {Subtitle}";
}

/// <summary>
/// Editar el texto de los prompts del catálogo desde Configuración.
///
/// Guardar <b>no pisa</b>: crea la versión siguiente (<c>v1.1</c>, <c>v1.2</c>...)
/// y la deja activa. Los reportes ya generados siguen diciendo con qué versión
/// salieron, y Costos puede comparar versiones. Elegir una versión del
/// historial sólo la carga en el editor; para volver a usarla hay que guardarla.
/// </summary>
public partial class PromptEditorViewModel : ObservableObject
{
    private readonly EditablePromptCatalog _catalog;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StructuredOutputWarning))]
    [NotifyPropertyChangedFor(nameof(HasStructuredOutputWarning))]
    private PromptDefinition? selectedPrompt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(DirtyHint))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    private string editorText = string.Empty;

    [ObservableProperty]
    private string? note;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(DirtyHint))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreOriginalCommand))]
    private PromptDefinition? activePrompt;

    [ObservableProperty]
    private string? activeVersionText;

    [ObservableProperty]
    private PromptVersionItem? selectedVersion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreOriginalCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? errorDetails;

    public PromptEditorViewModel(EditablePromptCatalog catalog)
    {
        _catalog = catalog;
        Prompts = catalog.GetAll();
        StorageHint = !catalog.CanEdit
            ? "No hay base de datos donde guardar ediciones: los prompts se pueden leer pero no cambiar."
            : catalog.LoadedFromStore
                ? "Las ediciones se guardan en la base del historial. Con Postgres, las ven todas tus máquinas al reiniciar la app."
                : "No se pudieron leer las ediciones guardadas: se están usando los textos originales. Guardar vuelve a intentarlo.";
        SelectedPrompt = catalog.Default;
    }

    public IReadOnlyList<PromptDefinition> Prompts { get; }

    public ObservableCollection<PromptVersionItem> Versions { get; } = [];

    public string StorageHint { get; }

    public bool IsDirty =>
        ActivePrompt is not null && Normalize(EditorText) != Normalize(ActivePrompt.SystemPrompt);

    public string DirtyHint => IsDirty
        ? "Hay cambios sin guardar. Guardar crea una versión nueva y la deja activa para los reportes que se generen desde ahora."
        : "Sin cambios: el editor muestra la versión activa.";

    public bool HasStructuredOutputWarning => SelectedPrompt?.OutputKind == PromptOutputKind.StructuredMeetingReport;

    /// <summary>
    /// Este prompt es el único cuya respuesta la app interpreta como JSON. Un
    /// cambio en el formato de salida no falla al guardar sino al generar el
    /// próximo reporte, que es peor; por eso se avisa antes.
    /// </summary>
    public string? StructuredOutputWarning => HasStructuredOutputWarning
        ? "Este prompt tiene que devolver JSON con el esquema que espera la app: summary, insights, " +
          "taskList (task, priority alta/media/baja, context), requirements, indications y openQuestions. " +
          "Puedes cambiar las instrucciones, pero si cambias el formato de salida, el próximo reporte va a fallar al interpretarse."
        : null;

    partial void OnSelectedPromptChanged(PromptDefinition? value)
    {
        Note = null;
        StatusMessage = null;
        ErrorDetails = null;
        LoadActive();
    }

    /// <summary>Elegir una versión del historial la carga en el editor, sin activarla.</summary>
    partial void OnSelectedVersionChanged(PromptVersionItem? value)
    {
        if (value is null) return;

        EditorText = value.Entry.SystemPrompt;
        StatusMessage = value.Entry.IsActive
            ? null
            : $"Cargada {value.Entry.Version} en el editor. No está activa: para usarla, guárdala.";
    }

    private bool CanSave() => IsDirty && !IsBusy && _catalog.CanEdit && !string.IsNullOrWhiteSpace(EditorText);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (SelectedPrompt is not { } prompt) return;

        IsBusy = true;
        ErrorDetails = null;
        try
        {
            PromptDefinition saved = await _catalog.SaveEditAsync(prompt.Id, EditorText, Note);
            Note = null;
            LoadActive();
            StatusMessage = $"Guardada como {saved.Version}. Los reportes que se generen desde ahora la usan.";
        }
        catch (Exception exception)
        {
            App.LogStartupFailure("PromptEditor.Save", exception);
            StatusMessage = "No se pudo guardar. El prompt activo no cambió.";
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

    private bool CanRestoreOriginal() =>
        !IsBusy && _catalog.CanEdit && ActivePrompt is not null && SelectedPrompt is not null &&
        ActivePrompt.Version != _catalog.GetOriginal(SelectedPrompt.Id).Version;

    [RelayCommand(CanExecute = nameof(CanRestoreOriginal))]
    private async Task RestoreOriginalAsync()
    {
        if (SelectedPrompt is not { } prompt) return;

        IsBusy = true;
        ErrorDetails = null;
        try
        {
            PromptDefinition restored = await _catalog.RestoreOriginalAsync(prompt.Id);
            LoadActive();
            StatusMessage = $"Vuelve a estar activa la versión original ({restored.Version}). Las ediciones siguen en el historial.";
        }
        catch (Exception exception)
        {
            App.LogStartupFailure("PromptEditor.RestoreOriginal", exception);
            StatusMessage = "No se pudo volver al original. El prompt activo no cambió.";
            ErrorDetails = exception.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadActive()
    {
        if (SelectedPrompt is null) return;

        ActivePrompt = _catalog.GetById(SelectedPrompt.Id);
        EditorText = ActivePrompt.SystemPrompt;

        IReadOnlyList<PromptVersionEntry> history = _catalog.GetHistory(SelectedPrompt.Id);
        Versions.Clear();
        foreach (PromptVersionEntry entry in history) Versions.Add(new PromptVersionItem(entry));

        PromptVersionEntry active = history.First(entry => entry.IsActive);
        ActiveVersionText = active.IsOriginal
            ? $"Versión activa: {active.Version} (original, definida en el código)."
            : $"Versión activa: {active.Version}, editada el " +
              $"{active.CreatedAtUtc!.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", SessionListItem.DisplayCulture)}.";

        // Sin selección en el historial: así elegir de nuevo la misma fila
        // vuelve a disparar la carga.
        SelectedVersion = null;
    }

    private static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n").Trim();
}
