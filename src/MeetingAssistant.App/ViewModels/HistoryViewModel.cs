using MeetingAssistant.App.Startup;
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
public sealed partial class SessionListItem : ObservableObject
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
        AudioArchiveLabel = summary.AudioArchiveState switch
        {
            "uploaded" => "audio en la nube",
            "failed" => "subida del audio fallida",
            "pending" => "audio pendiente de subir",
            _ => null
        };

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

    /// <summary>
    /// Null cuando la subida nunca se pidió: archivar apagado, o una reunión
    /// anterior a que existiera. No se muestra nada en ese caso, en vez de un
    /// "sin respaldo" que sonaría a fallo.
    /// </summary>
    public string? AudioArchiveLabel { get; }

    /// <summary>
    /// El fragmento del transcript donde apareció lo buscado. Sólo tiene valor
    /// mientras hay una búsqueda activa.
    ///
    /// Es una <b>anotación sobre la misma fila</b>, y no un tipo de fila aparte,
    /// a propósito: <c>TranscriptSearchHit</c> no trae origen, duración ni
    /// reportes, y todo el detalle —y la re-extracción— cuelga de que
    /// <c>SelectedSession</c> sea uno de estos. Reemplazar la lista por
    /// resultados habría roto esa cadena en silencio.
    ///
    /// Se muestra tal cual lo devuelve <c>snippet()</c>: el término entre
    /// corchetes y «…» donde se recortó. Resaltar de verdad obligaría a
    /// interpretar los corchetes, y un transcript que traiga uno propio se
    /// pintaría mal; así no hay nada que interpretar.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnippet))]
    private string? snippet;

    public bool HasSnippet => !string.IsNullOrWhiteSpace(Snippet);

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
            if (AudioArchiveLabel is not null) parts.Add(AudioArchiveLabel);

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
    public override string ToString() => HasSnippet ? $"{Title}. {Subtitle}. {Snippet}" : $"{Title}. {Subtitle}";
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

    /// <summary>Tope de resultados de una búsqueda. Si se alcanza, se dice.</summary>
    private const int SearchLimit = 50;

    /// <summary>
    /// Espera tras la última tecla antes de consultar. La base es local y
    /// responde al instante, así que esto no es por rendimiento: es para que la
    /// lista no parpadee con el resultado de cada prefijo intermedio.
    /// </summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly IMeetingHistoryStore _historyStore;
    private readonly RecordingCoordinator _recordingCoordinator;

    /// <summary>
    /// Todas las reuniones cargadas, en orden de fecha. <see cref="Sessions"/> es
    /// lo que se ve: esta misma lista, o el subconjunto que devuelve la búsqueda.
    /// </summary>
    private readonly List<SessionListItem> _allSessions = [];

    /// <summary>
    /// Sube con cada cambio de la caja y con cada recarga. Una consulta que
    /// vuelve con un número viejo se descarta: escribiendo rápido, una búsqueda
    /// anterior puede terminar después de una posterior y pisar lo que está en
    /// pantalla. Mismo motivo que el guard de <see cref="LoadSessionDetailAsync"/>.
    /// </summary>
    private int _searchVersion;

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
    private string searchText = string.Empty;

    /// <summary>
    /// Qué se está viendo mientras hay una búsqueda: cuántas reuniones salieron,
    /// que el orden pasó a ser por relevancia, y —lo que más confunde si se
    /// calla— cuántas reuniones <b>no tienen transcripción</b> y por eso no
    /// pueden aparecer. Sin eso, una búsqueda que esconde reuniones se ve igual
    /// que una búsqueda rota.
    /// </summary>
    [ObservableProperty]
    private string? searchSummary;

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

    /// <summary>
    /// "No hay historial todavía". Mira la lista completa y no la visible: una
    /// búsqueda sin resultados sobre un historial lleno es otra cosa, y la
    /// explica <see cref="SearchSummary"/>.
    /// </summary>
    public bool IsEmpty => !IsLoading && _allSessions.Count == 0;

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

            _allSessions.Clear();
            foreach (SessionSummary summary in summaries) _allSessions.Add(new SessionListItem(summary));

            StatusMessage = summaries.Count switch
            {
                0 => null,
                1 => "1 reunión registrada.",
                _ => $"{summaries.Count} reuniones registradas."
            };

            // Refrescar después de re-generar no debe perder de vista la reunión
            // que se estaba mirando, ni la búsqueda que estaba escrita: se vuelve
            // a aplicar sobre la lista recién cargada. El número de versión nuevo
            // descarta además cualquier búsqueda en camino contra la lista vieja.
            await ApplySearchAsync(++_searchVersion, previouslySelected);
        }
        catch (Exception exception)
        {
            // Regla de la fase: un fallo de la base no puede tumbar nada. Grabar,
            // transcribir y guardar en el vault no dependen del historial, así
            // que esta página avisa y el resto de la app sigue.
            AppLog.Failure("HistoryViewModel.Refresh", exception);
            _allSessions.Clear();
            Sessions.Clear();
            SearchSummary = null;
            StatusMessage = "No se pudo leer el historial. El resto de la app sigue funcionando: " +
                "grabar, transcribir y guardar en el vault no dependen de la base.";
            ErrorDetails = exception.ToString();
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    partial void OnSearchTextChanged(string value) => _ = SearchAfterPauseAsync();

    private async Task SearchAfterPauseAsync()
    {
        int version = ++_searchVersion;

        await Task.Delay(SearchDebounce);
        if (version != _searchVersion) return;

        await ApplySearchAsync(version, SelectedSession?.SessionId);
    }

    /// <summary>
    /// Deja en <see cref="Sessions"/> lo que corresponde a lo que está escrito
    /// <b>ahora</b>: la lista completa por fecha, o las reuniones que mencionan
    /// la búsqueda, por relevancia y con su fragmento.
    ///
    /// La búsqueda <b>filtra y anota</b> las mismas filas en vez de reemplazarlas
    /// por otro tipo — ver <see cref="SessionListItem.Snippet"/>.
    ///
    /// No lanza. Es la ruta que el usuario dispara más veces en esta página, y
    /// un fallo de la base acá tiene que quedar en un aviso, igual que en
    /// <see cref="RefreshAsync"/>.
    /// </summary>
    private async Task ApplySearchAsync(int version, long? sessionToKeepSelected)
    {
        string query = SearchText.Trim();

        if (query.Length == 0)
        {
            ShowSessions(_allSessions, snippets: null, sessionToKeepSelected);
            SearchSummary = null;
            return;
        }

        IReadOnlyList<TranscriptSearchHit> hits;
        try
        {
            // Uno de más, para poder decir que hay más en vez de recortar callado.
            // La consulta va cruda: sanearla es trabajo del store (ToFts5Query),
            // y armarla acá "para agregarle algo" es como se pierde esa garantía.
            hits = await _historyStore.SearchTranscriptsAsync(query, SearchLimit + 1);
        }
        catch (Exception exception)
        {
            if (version != _searchVersion) return;

            AppLog.Failure("HistoryViewModel.Search", exception);
            ShowSessions(_allSessions, snippets: null, sessionToKeepSelected);
            SearchSummary = "No se pudo buscar en las transcripciones. Se muestra la lista completa, sin filtrar.";
            ErrorDetails = exception.ToString();
            return;
        }

        // Mientras se consultaba, el usuario pudo seguir escribiendo.
        if (version != _searchVersion) return;

        bool truncated = hits.Count > SearchLimit;
        int outsideLoadedPage = 0;
        var snippets = new Dictionary<long, string>();
        var matches = new List<SessionListItem>();
        foreach (TranscriptSearchHit hit in hits.Take(SearchLimit))
        {
            // Una reunión fuera de la página cargada (PageSize) no tiene fila que
            // anotar. Con el corpus de hoy no ocurre; si ocurre, se dice.
            SessionListItem? session = _allSessions.FirstOrDefault(item => item.SessionId == hit.SessionId);
            if (session is null)
            {
                outsideLoadedPage++;
                continue;
            }

            snippets[hit.SessionId] = hit.Snippet;
            matches.Add(session);
        }

        ShowSessions(matches, snippets, sessionToKeepSelected);
        SearchSummary = DescribeSearch(query, matches.Count, truncated, outsideLoadedPage);
    }

    private void ShowSessions(
        IReadOnlyList<SessionListItem> visible,
        IReadOnlyDictionary<long, string>? snippets,
        long? sessionToKeepSelected)
    {
        foreach (SessionListItem session in _allSessions)
        {
            session.Snippet = snippets is not null && snippets.TryGetValue(session.SessionId, out string? snippet)
                ? snippet
                : null;
        }

        // Si la lista visible no cambia, no se toca: vaciar la colección le
        // suelta la selección al ListView, y eso recarga el detalle y parpadea.
        if (!Sessions.SequenceEqual(visible))
        {
            Sessions.Clear();
            foreach (SessionListItem session in visible) Sessions.Add(session);
        }

        // La reunión abierta se conserva si sigue a la vista. Si la búsqueda la
        // dejó fuera, el detalle se cierra: dejarlo abierto mostraría una
        // reunión que no está en la lista de al lado.
        SelectedSession = sessionToKeepSelected is null
            ? null
            : Sessions.FirstOrDefault(session => session.SessionId == sessionToKeepSelected);

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// La búsqueda es por comienzo de palabra, sin distinguir acentos, y varias
    /// palabras tienen que aparecer todas. Es lo que hace <c>ToFts5Query</c> para
    /// que la caja nunca lance; los textos de acá no prometen otra cosa.
    /// </summary>
    private string DescribeSearch(string query, int shown, bool truncated, int outsideLoadedPage)
    {
        var parts = new List<string>
        {
            shown switch
            {
                0 => $"Ninguna transcripción menciona «{query}».",
                1 => $"1 reunión menciona «{query}».",
                _ => $"{shown} reuniones mencionan «{query}», ordenadas por relevancia y no por fecha."
            }
        };

        if (truncated) parts.Add($"Hay más: se muestran las primeras {SearchLimit}.");

        if (outsideLoadedPage > 0)
        {
            parts.Add($"{outsideLoadedPage} coincidencia(s) más son de reuniones anteriores a las {PageSize} cargadas.");
        }

        int withoutTranscript = _allSessions.Count(session => !session.HasTranscript);
        if (withoutTranscript > 0)
        {
            parts.Add(withoutTranscript == 1
                ? $"1 de tus {_allSessions.Count} reuniones no tiene transcripción y no se puede buscar."
                : $"{withoutTranscript} de tus {_allSessions.Count} reuniones no tienen transcripción y no se pueden buscar.");
        }

        return string.Join(" ", parts);
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

            // Se vacía acá y no sólo al cambiar de selección: re-generar llama a
            // esta carga dos veces seguidas para la misma reunión (una por la
            // selección que restaura el refresco, otra explícita), y sin esto
            // cada reporte saldría duplicado en la lista.
            Reports.Clear();
            foreach (ReportRecord report in reports) Reports.Add(new ReportListItem(report));
            SelectedReport = Reports.FirstOrDefault();
        }
        catch (Exception exception)
        {
            AppLog.Failure("HistoryViewModel.LoadSessionDetail", exception);
            TranscriptDetails = "No se pudo leer el detalle de esta reunión.";
            ErrorDetails = exception.ToString();
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
