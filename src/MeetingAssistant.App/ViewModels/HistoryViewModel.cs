using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Startup;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;

namespace MeetingAssistant.App.ViewModels;

/// <summary>
/// Una fila del listado, sobre una sesión de la API (de cualquier dispositivo). Las fechas vienen en
/// UTC y sólo se convierten al mostrar.
/// </summary>
public sealed partial class SessionListItem : ObservableObject
{
    /// <summary>Fijada a en-US: la UI es inglés (con la del sistema, un Windows en español mezclaba los dos).</summary>
    internal static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("en-US");

    private static readonly Dictionary<string, string> SourceLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["desktop"] = "Desktop",
        ["android"] = "Phone",
        [SessionSource.Hotkey] = "Desktop (keyboard shortcut)",
        [SessionSource.Tray] = "Desktop (tray)",
        [SessionSource.Http] = "Desktop (HTTP endpoint)",
        [SessionSource.Window] = "Desktop (window)",
        [SessionSource.Import] = "Desktop (imported)",
        [SessionSource.Harness] = "Harness"
    };

    public SessionListItem(ApiSession session)
    {
        Session = session;
        StartedAtLocal = session.StartedAt.ToLocalTime();
        Cost = new CostAmount(session.TranscriptCostMicroUsd, session.HasTranscript && session.TranscriptCostMicroUsd is null ? 1 : 0)
            + new CostAmount(session.ReportsCostMicroUsd, session.ReportsCostUnknown);
        SourceLabel = SourceLabels.TryGetValue(session.Source, out string? label) ? label : session.Source;
    }

    public ApiSession Session { get; }

    public long SessionId => Session.IdSession;

    public DateTimeOffset StartedAtLocal { get; }

    public CostAmount Cost { get; }

    public bool HasTranscript => Session.HasTranscript;

    public string SourceLabel { get; }

    /// <summary>Hay audio archivado en el servidor para escuchar (DevLog 018).</summary>
    public bool HasArchivedAudio => Session.AudioExpiresAt is { } expires && expires > DateTimeOffset.UtcNow;

    /// <summary>El fragmento donde apareció lo buscado; sólo con una búsqueda activa.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnippet))]
    private string? snippet;

    public bool HasSnippet => !string.IsNullOrWhiteSpace(Snippet);

    public string Title => StartedAtLocal.ToString("dddd, d MMMM, HH:mm", DisplayCulture);

    /// <summary>Una sesión sin transcript se dice, no se disfraza. Un costo desconocido nunca se muestra como 0.</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string> { SourceLabel };
            if (Session.DurationSeconds is { } seconds) parts.Add($"{seconds / 60:F0} min");
            parts.Add(!HasTranscript
                ? "no transcript"
                : Session.ReportCount switch { 0 => "no reports", 1 => "1 report", _ => $"{Session.ReportCount} reports" });
            if (!Cost.IsNothing) parts.Add(Cost.Text());
            if (HasArchivedAudio) parts.Add($"audio until {Session.AudioExpiresAt!.Value.ToLocalTime().ToString("d", DisplayCulture)}");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Lo que lee un lector de pantalla (sin esto, el nombre del tipo).</summary>
    public override string ToString() => HasSnippet ? $"{Title}. {Subtitle}. {Snippet}" : $"{Title}. {Subtitle}";
}

/// <summary>Un reporte de la sesión abierta, listo para mostrar.</summary>
public sealed class ReportListItem(ApiReport report)
{
    public ApiReport Report { get; } = report;

    public string Title => Report.PromptVersion is null ? Report.PromptId : $"{Report.PromptId} @{Report.PromptVersion}";

    public string Subtitle
    {
        get
        {
            var parts = new List<string> { Report.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", SessionListItem.DisplayCulture) };
            if (Report.LlmModel is not null) parts.Add(Report.LlmModel);
            parts.Add(Report.CostMicroUsd is { } cost ? CostAmount.Usd(cost) : "unknown cost");
            if (Report.TokensOutput is { } tokens) parts.Add($"{tokens:N0} tokens");
            return string.Join(" · ", parts);
        }
    }

    public override string ToString() => $"{Title}. {Subtitle}";
}

/// <summary>
/// El historial por la API (paso B): las reuniones de todos mis dispositivos, su transcript y sus
/// reportes, búsqueda, escuchar el audio archivado, y re-extraer con otro prompt como un trabajo
/// del servidor (Jobs opción 2) que llega al vault por la cola.
/// </summary>
public partial class HistoryViewModel : ObservableObject
{
    /// <summary>Una página amplia: el corpus es de decenas de reuniones. La API acepta hasta 200.</summary>
    private const int PageSize = 200;

    private const int SearchLimit = 50;

    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(350);

    private readonly MeetingHistoryService _history;
    private readonly PromptLibraryService _prompts;
    private readonly MeetingUploadQueue _queue;
    private readonly IReportStorage _reportStorage;
    private readonly List<SessionListItem> _allSessions = [];
    private int _searchVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRegenerate))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ListenCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string? errorDetails;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string? searchSummary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanRegenerate))]
    [NotifyPropertyChangedFor(nameof(RegenerateHint))]
    [NotifyPropertyChangedFor(nameof(AudioHint))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ListenCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(SaveToVaultCommand))]
    private ReportListItem? selectedReport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPromptText))]
    [NotifyPropertyChangedFor(nameof(SelectedPromptDescription))]
    [NotifyPropertyChangedFor(nameof(CanRegenerate))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateReportCommand))]
    private PromptEntry? selectedPrompt;

    /// <summary>El enlace de escucha de 15 minutos (una credencial: sólo va al reproductor, nunca al log).</summary>
    [ObservableProperty]
    private Uri? audioSource;

    public ObservableCollection<SessionListItem> Sessions { get; } = [];

    public ObservableCollection<ReportListItem> Reports { get; } = [];

    public ObservableCollection<PromptEntry> Prompts { get; } = [];

    public bool IsEmpty => !IsLoading && string.IsNullOrEmpty(ErrorDetails) && _allSessions.Count == 0;

    public bool HasSelection => SelectedSession is not null;

    public bool HasTranscript => !string.IsNullOrWhiteSpace(Transcript);

    public string? SelectedReportMarkdown => SelectedReport?.Report.Markdown;

    public bool CanRegenerate => SelectedSession is not null && HasTranscript && SelectedPrompt is not null && !IsBusy;

    public string SelectedPromptText => SelectedPrompt?.Text ?? string.Empty;

    public string SelectedPromptDescription => SelectedPrompt?.Catalog.Description ?? string.Empty;

    public string RegenerateHint
    {
        get
        {
            if (SelectedSession is null) return "Pick a meeting from the list.";
            if (!HasTranscript)
            {
                return "This meeting has no transcript on the server, so it cannot be extracted again. " +
                    "That usually means the recording failed before it was transcribed.";
            }

            return "The server does this, and it costs one LLM call (it counts toward the daily cap). It is added as another report " +
                "for this meeting, and the new .md reaches the vault through the Record queue, without replacing the previous one.";
        }
    }

    public string AudioHint => SelectedSession switch
    {
        null => string.Empty,
        { HasArchivedAudio: true } session =>
            $"The audio is kept on the server until {session.Session.AudioExpiresAt!.Value.ToLocalTime().ToString("d", SessionListItem.DisplayCulture)}.",
        _ => "There is no audio to play on the server (meetings from before the archive, imported from the old desktop, or expired)."
    };

    public HistoryViewModel(MeetingHistoryService history, PromptLibraryService prompts, MeetingUploadQueue queue, IReportStorage reportStorage)
    {
        _history = history;
        _prompts = prompts;
        _queue = queue;
        _reportStorage = reportStorage;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorDetails = null;
        StatusMessage = "Loading history...";
        long? previouslySelected = SelectedSession?.SessionId;

        try
        {
            IReadOnlyList<ApiSession> sessions = await _history.ListSessionsAsync(PageSize);
            _allSessions.Clear();
            foreach (ApiSession session in sessions) _allSessions.Add(new SessionListItem(session));

            StatusMessage = sessions.Count switch
            {
                0 => null,
                1 => "1 meeting on record.",
                PageSize => $"The {PageSize} most recent meetings.",
                _ => $"{sessions.Count} meetings on record."
            };

            await ApplySearchAsync(++_searchVersion, previouslySelected);
            await LoadPromptsAsync();
        }
        catch (Exception exception)
        {
            // Sin la API no hay historial; grabar sigue funcionando (la cola espera).
            AppLog.Failure("HistoryViewModel.Refresh", exception);
            _allSessions.Clear();
            Sessions.Clear();
            SearchSummary = null;
            StatusMessage = "Could not read history from the server. Recording still works: what you record waits in the queue. " +
                (exception is ApiException api ? api.Error.Describe() : exception.Message);
            ErrorDetails = exception.ToString();
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private async Task LoadPromptsAsync()
    {
        if (Prompts.Count > 0) return;
        IReadOnlyList<PromptEntry> entries = await _prompts.LoadAsync();
        foreach (PromptEntry entry in entries) Prompts.Add(entry);
        SelectedPrompt = Prompts.FirstOrDefault();
    }

    partial void OnSearchTextChanged(string value) => _ = SearchAfterPauseAsync();

    private async Task SearchAfterPauseAsync()
    {
        int version = ++_searchVersion;
        await Task.Delay(SearchDebounce);
        if (version != _searchVersion) return;
        await ApplySearchAsync(version, SelectedSession?.SessionId);
    }

    /// <summary>La búsqueda filtra y anota las mismas filas (por relevancia), en vez de reemplazarlas. No lanza.</summary>
    private async Task ApplySearchAsync(int version, long? sessionToKeepSelected)
    {
        string query = SearchText.Trim();
        if (query.Length == 0)
        {
            ShowSessions(_allSessions, snippets: null, sessionToKeepSelected);
            SearchSummary = null;
            return;
        }

        IReadOnlyList<ApiSearchHit> hits;
        try
        {
            hits = await _history.SearchAsync(query, SearchLimit + 1);
        }
        catch (Exception exception)
        {
            if (version != _searchVersion) return;
            AppLog.Failure("HistoryViewModel.Search", exception);
            ShowSessions(_allSessions, snippets: null, sessionToKeepSelected);
            SearchSummary = "Could not search the transcripts. The full list is shown, unfiltered.";
            ErrorDetails = exception.ToString();
            return;
        }

        if (version != _searchVersion) return;

        bool truncated = hits.Count > SearchLimit;
        int outsideLoadedPage = 0;
        var snippets = new Dictionary<long, string>();
        var matches = new List<SessionListItem>();
        foreach (ApiSearchHit hit in hits.Take(SearchLimit))
        {
            SessionListItem? session = _allSessions.FirstOrDefault(item => item.SessionId == hit.IdSession);
            if (session is null)
            {
                outsideLoadedPage++;
                continue;
            }

            snippets[hit.IdSession] = hit.Snippet;
            matches.Add(session);
        }

        ShowSessions(matches, snippets, sessionToKeepSelected);
        var parts = new List<string>
        {
            matches.Count switch
            {
                0 => $"No transcript mentions \"{query}\".",
                1 => $"1 meeting mentions \"{query}\".",
                _ => $"{matches.Count} meetings mention \"{query}\", ordered by relevance rather than date."
            }
        };
        if (truncated) parts.Add($"There are more: the first {SearchLimit} are shown.");
        if (outsideLoadedPage > 0) parts.Add($"{outsideLoadedPage} more match(es) are from meetings older than the {PageSize} loaded.");
        int withoutTranscript = _allSessions.Count(session => !session.HasTranscript);
        if (withoutTranscript > 0) parts.Add($"{withoutTranscript} of your {_allSessions.Count} meetings have no transcript and cannot be searched.");
        SearchSummary = string.Join(" ", parts);
    }

    private void ShowSessions(IReadOnlyList<SessionListItem> visible, IReadOnlyDictionary<long, string>? snippets, long? sessionToKeepSelected)
    {
        foreach (SessionListItem session in _allSessions)
        {
            session.Snippet = snippets is not null && snippets.TryGetValue(session.SessionId, out string? snippet) ? snippet : null;
        }

        if (!Sessions.SequenceEqual(visible))
        {
            Sessions.Clear();
            foreach (SessionListItem session in visible) Sessions.Add(session);
        }

        SelectedSession = sessionToKeepSelected is null ? null : Sessions.FirstOrDefault(session => session.SessionId == sessionToKeepSelected);
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSelectedSessionChanged(SessionListItem? value)
    {
        Reports.Clear();
        SelectedReport = null;
        Transcript = null;
        TranscriptDetails = null;
        AudioSource = null;
        if (value is null) return;
        _ = LoadSessionDetailAsync(value.SessionId);
    }

    private async Task LoadSessionDetailAsync(long sessionId)
    {
        try
        {
            ApiTranscript? storedTranscript = await _history.GetTranscriptAsync(sessionId);
            IReadOnlyList<ApiReport> reports = await _history.GetReportsAsync(sessionId);
            if (SelectedSession?.SessionId != sessionId) return;

            Transcript = storedTranscript?.Text;
            TranscriptDetails = storedTranscript is null
                ? "No transcript on the server."
                : $"{storedTranscript.Text.Length:N0} characters · {storedTranscript.Provider ?? "unknown provider"} · " +
                  (storedTranscript.CostMicroUsd is { } cost ? CostAmount.Usd(cost) : "unknown cost") + " · " +
                  storedTranscript.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", SessionListItem.DisplayCulture);

            Reports.Clear();
            foreach (ApiReport report in reports.OrderByDescending(report => report.CreatedAt)) Reports.Add(new ReportListItem(report));
            SelectedReport = Reports.FirstOrDefault();
        }
        catch (Exception exception)
        {
            AppLog.Failure("HistoryViewModel.LoadSessionDetail", exception);
            TranscriptDetails = "Could not read this meeting's detail.";
            ErrorDetails = exception.ToString();
        }
    }

    /// <summary>Re-extraer: un trabajo del servidor (Jobs opción 2) que la cola sigue y lleva al vault.</summary>
    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private void RegenerateReport()
    {
        if (SelectedSession is not { } session || SelectedPrompt is not { } prompt) return;

        QueuedMeeting queued = _queue.EnqueueReextract(session.SessionId, prompt.PromptId, SessionSource.Window);
        StatusMessage = $"Extracting the meeting of {session.StartedAtLocal.ToString("d", SessionListItem.DisplayCulture)} again with " +
            $"\"{prompt.DisplayName}\" is in the queue ({queued.Id}). Its status is on Record; the .md reaches the vault when the server finishes.";
    }

    private bool CanListen() => SelectedSession?.HasArchivedAudio == true && !IsBusy;

    /// <summary>Pide el enlace de escucha (15 min) y lo pasa al reproductor de la página. No se guarda en ningún lado.</summary>
    [RelayCommand(CanExecute = nameof(CanListen))]
    private async Task ListenAsync()
    {
        if (SelectedSession is not { } session) return;
        IsBusy = true;
        try
        {
            AudioLink? link = await _history.GetAudioLinkAsync(session.SessionId);
            if (SelectedSession?.SessionId != session.SessionId) return;
            if (link is null)
            {
                StatusMessage = "The server no longer has this meeting's audio (expired or deleted).";
                return;
            }

            AudioSource = new Uri(link.Url);
            StatusMessage = $"Audio ready to play (the link lasts until {link.ExpiresAt.ToLocalTime():HH:mm}).";
        }
        catch (Exception exception)
        {
            AppLog.Failure("HistoryViewModel.Listen", exception);
            StatusMessage = $"Could not request the audio: {(exception is ApiException api ? api.Error.Describe() : exception.Message)}";
            ErrorDetails = exception.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSaveToVault() => SelectedReport is not null;

    /// <summary>Escribe en el vault el .md de un reporte del historial (p. ej. uno hecho en el teléfono).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveToVault))]
    private async Task SaveToVaultAsync()
    {
        if (SelectedReport?.Report is not { } report) return;
        try
        {
            string path = await _reportStorage.SaveMarkdownAsync(report.Markdown, ReportFileMetadata.From(report));
            StatusMessage = $"Saved to the vault: {path}";
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AppLog.Failure("HistoryViewModel.SaveToVault", exception);
            StatusMessage = $"Could not write to the vault: {exception.Message}";
            ErrorDetails = exception.ToString();
        }
    }
}
