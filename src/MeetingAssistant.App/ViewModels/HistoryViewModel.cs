using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Services;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.App.ViewModels;

/// <summary>
/// Una fila del listado. Es una proyección de presentación sobre
/// <see cref="SessionSummary"/> y existe por una razón concreta: <b>las fechas
/// se guardan en UTC y sólo se convierten al mostrar</b>. Si el XAML hiciera el
/// binding directo contra el record de Core, la lista mostraría UTC y volvería a
/// aparecer el desfase ya conocido — el <c>.wav</c> se nombra en hora local y el
/// reporte en UTC, así que una reunión de la noche parece del día siguiente.
/// </summary>
public sealed class SessionListItem
{
    /// <summary>
    /// Cultura con la que se formatean las fechas que se muestran.
    ///
    /// Fijada a propósito, y no heredada del sistema. Toda la UI de esta app está
    /// escrita en español y no hay localización: con la cultura del sistema
    /// (en-US en esta máquina) el título salía <c>"Thursday 27 de August"</c> —
    /// los nombres de día y mes en inglés alrededor de un "de" español. Visto en
    /// pantalla, no deducido.
    /// </summary>
    internal static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("es-ES");

    private static readonly Dictionary<string, string> SourceLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        [SessionSource.Hotkey] = "Atajo de teclado",
        [SessionSource.Tray] = "Bandeja",
        [SessionSource.Http] = "Endpoint HTTP",
        [SessionSource.Window] = "Ventana",
        [SessionSource.Import] = "Importado",
        [SessionSource.Harness] = "Harness"
    };

    public SessionListItem(SessionSummary summary)
    {
        SessionId = summary.SessionId;
        StartedAtLocal = summary.StartedAtUtc.ToLocalTime();
        Duration = summary.Duration;
        ReportCount = summary.ReportCount;
        TotalCostUsd = summary.TotalCostUsd;
        HasTranscript = !string.IsNullOrWhiteSpace(summary.TranscriptPreview);
        TranscriptPreview = summary.TranscriptPreview;

        // El origen se traduce, pero el valor crudo se conserva: es lo primero
        // que uno quiere saber al mirar el historial, y un valor nuevo que
        // alguien agregue mañana tiene que verse igual en vez de desaparecer
        // detrás de un "Desconocido".
        SourceLabel = SourceLabels.TryGetValue(summary.Source, out string? label) ? label : summary.Source;
    }

    public long SessionId { get; }

    public DateTimeOffset StartedAtLocal { get; }

    public TimeSpan? Duration { get; }

    public int ReportCount { get; }

    public decimal TotalCostUsd { get; }

    public bool HasTranscript { get; }

    public string? TranscriptPreview { get; }

    public string SourceLabel { get; }

    public string Title => StartedAtLocal.ToString("dddd d 'de' MMMM, HH:mm", DisplayCulture);

    /// <summary>
    /// Segunda línea de la fila. Una sesión <b>sin transcript se dice</b>, no se
    /// disfraza: son grabaciones que fallaron a mitad, y la fila existe justo
    /// para que quede constancia de que la reunión pasó.
    /// </summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string> { SourceLabel };

            if (Duration is { } duration) parts.Add($"{duration.TotalMinutes:F0} min");

            parts.Add(!HasTranscript
                ? "sin transcripción"
                : ReportCount switch
                {
                    0 => "sin reportes",
                    1 => "1 reporte",
                    _ => $"{ReportCount} reportes"
                });

            if (TotalCostUsd > 0) parts.Add($"US${TotalCostUsd:F4}");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Es lo que lee un lector de pantalla. Sin esto, la automatización de
    /// interfaz reportaba cada fila como
    /// <c>"MeetingAssistant.App.ViewModels.SessionListItem"</c> — el
    /// <c>ToString()</c> por defecto, que es lo que WinUI usa como nombre del
    /// <c>ListViewItem</c> cuando la plantilla no expone uno. Encontrado
    /// inspeccionando el árbol de accesibilidad de la app corriendo, no leyendo
    /// el código.
    /// </summary>
    public override string ToString() => $"{Title}. {Subtitle}";
}

/// <summary>Un reporte de la sesión abierta, listo para mostrar.</summary>
public sealed class ReportListItem
{
    public ReportListItem(ReportRecord record)
    {
        Record = record;
        CreatedAtLocal = record.CreatedAtUtc.ToLocalTime();
    }

    public ReportRecord Record { get; }

    public DateTimeOffset CreatedAtLocal { get; }

    /// <summary>
    /// El prompt y su versión al frente: es lo que convierte "comparar calidad
    /// entre versiones de prompt" (Fase 4) en algo que se mira en vez de
    /// recordarse.
    /// </summary>
    public string Title => Record.PromptVersion is null
        ? Record.PromptId
        : $"{Record.PromptId} @{Record.PromptVersion}";

    public string Subtitle
    {
        get
        {
            var parts = new List<string> { CreatedAtLocal.ToString("dd/MM/yyyy HH:mm", SessionListItem.DisplayCulture) };

            if (Record.LlmModel is not null) parts.Add(Record.LlmModel);
            if (Record.CostUsd is { } cost) parts.Add($"US${cost:F6}");
            if (Record.OutputTokens is { } tokens) parts.Add($"{tokens:N0} tokens");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Mismo motivo que en <see cref="SessionListItem.ToString"/>.</summary>
    public override string ToString() => $"{Title}. {Subtitle}";
}

/// <summary>
/// El historial: lista de reuniones, detalle de una, y re-generación de un
/// reporte desde un transcript viejo con otro prompt — lo último era imposible
/// hasta que el paso 4 empezó a guardar los transcripts.
/// </summary>
public partial class HistoryViewModel : ObservableObject
{
    /// <summary>
    /// Cuántas sesiones se traen de una vez. <c>ListSessionsAsync</c> acepta
    /// <c>offset</c>, así que paginar es posible; se carga una página amplia
    /// porque el corpus real es de decenas de reuniones, no de miles, y una
    /// página que se completa sola al hacer scroll es complejidad que hoy no
    /// paga. Queda dicho para que sea una decisión y no un olvido.
    /// </summary>
    private const int PageSize = 200;

    private readonly IMeetingHistoryStore _historyStore;
    private readonly RecordingCoordinator _recordingCoordinator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRegenerate))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateReportCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? errorDetails;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanRegenerate))]
    [NotifyPropertyChangedFor(nameof(RegenerateHint))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateReportCommand))]
    private SessionListItem? selectedSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranscript))]
    [NotifyPropertyChangedFor(nameof(CanRegenerate))]
    [NotifyPropertyChangedFor(nameof(RegenerateHint))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateReportCommand))]
    private string? transcript;

    [ObservableProperty]
    private string? transcriptDetails;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedReportMarkdown))]
    [NotifyPropertyChangedFor(nameof(HasVaultFile))]
    [NotifyCanExecuteChangedFor(nameof(OpenInVaultCommand))]
    private ReportListItem? selectedReport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPromptText))]
    [NotifyPropertyChangedFor(nameof(CanRegenerate))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateReportCommand))]
    private PromptDefinition? selectedPrompt;

    public ObservableCollection<SessionListItem> Sessions { get; } = [];

    public ObservableCollection<ReportListItem> Reports { get; } = [];

    public IReadOnlyList<PromptDefinition> Prompts { get; }

    public bool IsEmpty => !IsLoading && Sessions.Count == 0;

    public bool HasSelection => SelectedSession is not null;

    public bool HasTranscript => !string.IsNullOrWhiteSpace(Transcript);

    /// <summary>
    /// El Markdown que se muestra sale de la <b>base</b>, no del archivo del
    /// vault. La base es el registro; el <c>.md</c> es una exportación que el
    /// usuario puede mover, renombrar o borrar — es su vault. Leer del archivo
    /// haría que el detalle desapareciera por una limpieza de carpetas.
    /// </summary>
    public string? SelectedReportMarkdown => SelectedReport?.Record.Markdown;

    /// <summary>
    /// <c>vault_path</c> puede apuntar a algo que ya no existe, así que se
    /// comprueba en disco. Mismo criterio que <c>RecordViewModel.HasSavedReport</c>.
    /// </summary>
    public bool HasVaultFile =>
        SelectedReport?.Record.VaultPath is { } path &&
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(path);

    public bool CanRegenerate =>
        SelectedSession is not null && HasTranscript && SelectedPrompt is not null && !IsBusy;

    /// <summary>
    /// Por qué el botón está apagado, dicho antes de pulsarlo. Un botón inerte
    /// sin explicación es la versión moderna de "Próximamente".
    /// </summary>
    public string RegenerateHint
    {
        get
        {
            if (SelectedSession is null) return "Elige una reunión de la lista.";
            if (!HasTranscript)
            {
                return "Esta reunión no tiene transcripción guardada, así que no se puede volver a extraer. " +
                    "Suele significar que la grabación falló antes de transcribir.";
            }

            return "Cuesta una llamada real al LLM: se cobra igual que un reporte nuevo. " +
                "Se guarda como un reporte más de esta reunión, y el .md nuevo llega al vault sin pisar el anterior.";
        }
    }

    public string SelectedPromptText => SelectedPrompt?.SystemPrompt ?? string.Empty;

    public HistoryViewModel(
        IMeetingHistoryStore historyStore,
        RecordingCoordinator recordingCoordinator,
        IPromptCatalog promptCatalog)
    {
        _historyStore = historyStore;
        _recordingCoordinator = recordingCoordinator;
        Prompts = promptCatalog.GetAll();
        SelectedPrompt = promptCatalog.Default;
    }

    /// <summary>
    /// Carga la lista. Se llama desde <c>Loaded</c> y no del constructor porque
    /// el <c>Frame</c> reconstruye la página en cada navegación: nada de lo que
    /// haya acá sobrevive a salir y volver a entrar.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorDetails = null;
        StatusMessage = "Cargando el historial...";

        long? previouslySelected = SelectedSession?.SessionId;

        try
        {
            IReadOnlyList<SessionSummary> summaries = await _historyStore.ListSessionsAsync(PageSize);

            Sessions.Clear();
            foreach (SessionSummary summary in summaries) Sessions.Add(new SessionListItem(summary));

            StatusMessage = summaries.Count switch
            {
                0 => null,
                1 => "1 reunión registrada.",
                _ => $"{summaries.Count} reuniones registradas."
            };

            // Refrescar después de re-generar no debe perder de vista la reunión
            // que se estaba mirando.
            SelectedSession = previouslySelected is null
                ? null
                : Sessions.FirstOrDefault(session => session.SessionId == previouslySelected);
        }
        catch (Exception exception)
        {
            // Regla de la fase: un fallo de la base no puede tumbar nada. Grabar,
            // transcribir y guardar en el vault no dependen del historial, así
            // que esta página avisa y el resto de la app sigue.
            App.LogStartupFailure("HistoryViewModel.Refresh", exception);
            Sessions.Clear();
            StatusMessage = "No se pudo leer el historial. El resto de la app sigue funcionando: " +
                "grabar, transcribir y guardar en el vault no dependen de la base.";
            ErrorDetails = exception.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>
    /// Al cambiar de reunión se cargan su transcript y sus reportes. Una sesión
    /// admite <b>varios</b> reportes: es el sentido del esquema, y el catálogo ya
    /// permitía re-correr el mismo transcript con otro prompt aunque hasta ahora
    /// esa comparación se perdiera.
    /// </summary>
    partial void OnSelectedSessionChanged(SessionListItem? value)
    {
        Reports.Clear();
        SelectedReport = null;
        Transcript = null;
        TranscriptDetails = null;

        if (value is null) return;

        _ = LoadSessionDetailAsync(value.SessionId);
    }

    private async Task LoadSessionDetailAsync(long sessionId)
    {
        try
        {
            TranscriptRecord? storedTranscript = await _historyStore.GetTranscriptAsync(sessionId);
            IReadOnlyList<ReportRecord> reports = await _historyStore.GetReportsAsync(sessionId);

            // Mientras se leía, el usuario pudo haber cambiado de fila. Sin este
            // guard el detalle de una reunión aparecería bajo otra.
            if (SelectedSession?.SessionId != sessionId) return;

            Transcript = storedTranscript?.Text;
            TranscriptDetails = storedTranscript is null
                ? "Sin transcripción guardada."
                : $"{storedTranscript.Text.Length:N0} caracteres · {storedTranscript.Provider ?? "proveedor desconocido"} · " +
                  $"{storedTranscript.CreatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", SessionListItem.DisplayCulture)}";

            foreach (ReportRecord report in reports) Reports.Add(new ReportListItem(report));
            SelectedReport = Reports.FirstOrDefault();
        }
        catch (Exception exception)
        {
            App.LogStartupFailure("HistoryViewModel.LoadSessionDetail", exception);
            TranscriptDetails = "No se pudo leer el detalle de esta reunión.";
            ErrorDetails = exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private async Task RegenerateReportAsync()
    {
        if (SelectedSession is not { } session ||
            SelectedPrompt is not { } prompt ||
            string.IsNullOrWhiteSpace(Transcript))
        {
            return;
        }

        IsBusy = true;
        ErrorDetails = null;
        StatusMessage = $"Re-extrayendo la reunión del {session.StartedAtLocal.ToString("dd/MM", SessionListItem.DisplayCulture)} con «{prompt.DisplayName}»...";

        try
        {
            // El sessionId va explícito. Es la corrección del defecto que este
            // paso encontró: ExtractAndSaveAsync deduce la sesión del estado de
            // la última grabación, así que por acá habría colgado el reporte de
            // la reunión equivocada — o creado una sesión fantasma.
            ExtractionSaveResult result = await _recordingCoordinator.ExtractForSessionAsync(
                session.SessionId, Transcript, prompt.Id);

            StatusMessage = $"Reporte nuevo guardado en el vault: {result.SavedReportPath} " +
                $"(US${result.Metadata.EstimatedCostUsd:F6}).";

            await RefreshAsync();
            await LoadSessionDetailAsync(session.SessionId);
            SelectedReport = Reports.FirstOrDefault(report => report.Record.VaultPath == result.SavedReportPath)
                ?? Reports.FirstOrDefault();
        }
        catch (Exception exception)
        {
            StatusMessage = $"No se pudo re-generar el reporte: {exception.Message}";
            ErrorDetails = exception.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasVaultFile))]
    private void OpenInVault()
    {
        if (SelectedReport?.Record.VaultPath is not { } path || !File.Exists(path)) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }
}
