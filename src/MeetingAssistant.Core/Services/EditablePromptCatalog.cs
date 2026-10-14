using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Services;

/// <summary>Una versión de un prompt, tal como se muestra en su historial.</summary>
public sealed record PromptVersionEntry(
    string Version,
    string SystemPrompt,
    bool IsOriginal,
    bool IsActive,
    string? Note,
    DateTimeOffset? CreatedAtUtc);

/// <summary>
/// El catálogo de prompts con las ediciones guardadas encima del original del
/// código. Implementa <see cref="IPromptCatalog"/>, así que el extractor, la
/// ventana y el historial no se enteran de que un prompt se puede editar:
/// siguen pidiendo un prompt por id y reciben el texto activo.
///
/// <b>Qué versión está activa</b>, para cada prompt: la última edición hecha
/// sobre la versión que hoy define el código. Si esa última edición es una
/// vuelta al original, o si no hay ninguna, manda el código.
///
/// El catálogo es síncrono —lo consulta el extractor en el momento de extraer—,
/// así que las ediciones se leen de la base a una caché con
/// <see cref="RefreshAsync"/>: al arrancar y después de cada cambio. Si la base
/// no contesta, quedan los originales y el fallo se registra; nunca lanza.
/// </summary>
public sealed class EditablePromptCatalog : IPromptCatalog
{
    private readonly IPromptCatalog _builtIn;
    private readonly IPromptStore? _store;
    private readonly Action<string, Exception>? _onFailure;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private IReadOnlyList<PromptEdit> _edits = [];
    private IReadOnlyDictionary<string, PromptDefinition> _active;

    public EditablePromptCatalog(
        IPromptCatalog builtIn,
        IPromptStore? store,
        Action<string, Exception>? onFailure = null)
    {
        _builtIn = builtIn;
        _store = store;
        _onFailure = onFailure;
        _active = Resolve([]);
    }

    /// <summary><c>false</c> si la última lectura de la base falló y se están usando los originales.</summary>
    public bool LoadedFromStore { get; private set; }

    public bool CanEdit => _store is not null;

    public PromptDefinition Default => _active[_builtIn.Default.Id];

    public IReadOnlyList<PromptDefinition> GetAll() =>
        _builtIn.GetAll().Select(prompt => _active[prompt.Id]).ToList();

    public PromptDefinition GetById(string id)
    {
        // Mismo contrato que el catálogo del código, incluida la excepción con
        // la lista de ids válidos para un id que no existe.
        PromptDefinition original = _builtIn.GetById(id);
        return _active[original.Id];
    }

    /// <summary>Relee las ediciones de la base. Nunca lanza.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_store is null) return;

        try
        {
            IReadOnlyList<PromptEdit> edits = await _store.GetAllAsync(cancellationToken);
            _edits = edits;
            _active = Resolve(edits);
            LoadedFromStore = true;
        }
        catch (Exception exception)
        {
            // Se queda con lo último que leyó bien; si nunca leyó, con los
            // originales. Un prompt del código siempre funciona.
            _onFailure?.Invoke("EditablePromptCatalog.Refresh", exception);
            LoadedFromStore = false;
        }
    }

    /// <summary>
    /// El historial de un prompt, de la versión más nueva a la más vieja, con el
    /// original del código al final. Las vueltas al original no se listan: no
    /// tienen texto propio.
    /// </summary>
    public IReadOnlyList<PromptVersionEntry> GetHistory(string promptId)
    {
        PromptDefinition original = _builtIn.GetById(promptId);
        PromptDefinition active = _active[original.Id];

        var entries = _edits
            .Where(edit => edit.PromptId == original.Id && !edit.IsRevertToOriginal)
            .Reverse()
            .Select(edit => new PromptVersionEntry(
                edit.Version!, edit.SystemPrompt!, IsOriginal: false,
                IsActive: edit.Version == active.Version, edit.Note, edit.CreatedAtUtc))
            .ToList();

        entries.Add(new PromptVersionEntry(
            original.Version, original.SystemPrompt, IsOriginal: true,
            IsActive: active.Version == original.Version, Note: null, CreatedAtUtc: null));

        return entries;
    }

    /// <summary>
    /// Guarda un texto nuevo como la siguiente versión y la deja activa. Si el
    /// texto es igual al activo no guarda nada y devuelve el activo: una versión
    /// nueva idéntica a la anterior partiría en dos la comparación sin motivo.
    /// </summary>
    public async Task<PromptDefinition> SaveEditAsync(
        string promptId,
        string systemPrompt,
        string? note,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        IPromptStore store = _store ?? throw new InvalidOperationException("No hay dónde guardar ediciones de prompts.");
        PromptDefinition original = _builtIn.GetById(promptId);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // Releer antes de numerar: otra máquina pudo haber guardado una
            // versión desde la última lectura.
            await RefreshFromStoreOrThrowAsync(store, cancellationToken);

            string text = NormalizeLineEndings(systemPrompt).Trim();
            if (text == NormalizeLineEndings(_active[original.Id].SystemPrompt).Trim())
            {
                return _active[original.Id];
            }

            int next = _edits.Count(edit =>
                edit.PromptId == original.Id && edit.BaseVersion == original.Version && !edit.IsRevertToOriginal) + 1;

            await store.SaveAsync(
                new PromptEdit(
                    original.Id, original.Version, $"{original.Version}.{next}", text,
                    string.IsNullOrWhiteSpace(note) ? null : note.Trim(), DateTimeOffset.UtcNow),
                cancellationToken);

            await RefreshFromStoreOrThrowAsync(store, cancellationToken);
            return _active[original.Id];
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Vuelve al texto del código. Las ediciones quedan en el historial.</summary>
    public async Task<PromptDefinition> RestoreOriginalAsync(string promptId, CancellationToken cancellationToken = default)
    {
        IPromptStore store = _store ?? throw new InvalidOperationException("No hay dónde guardar ediciones de prompts.");
        PromptDefinition original = _builtIn.GetById(promptId);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await RefreshFromStoreOrThrowAsync(store, cancellationToken);
            if (_active[original.Id].Version == original.Version) return original;

            await store.SaveAsync(
                new PromptEdit(original.Id, original.Version, null, null, "Vuelta al original", DateTimeOffset.UtcNow),
                cancellationToken);

            await RefreshFromStoreOrThrowAsync(store, cancellationToken);
            return _active[original.Id];
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>El original del código, para comparar o para mostrarlo.</summary>
    public PromptDefinition GetOriginal(string promptId) => _builtIn.GetById(promptId);

    private async Task RefreshFromStoreOrThrowAsync(IPromptStore store, CancellationToken cancellationToken)
    {
        // Al escribir sí se lanza: guardar sobre una lectura que falló podría
        // numerar mal la versión.
        _edits = await store.GetAllAsync(cancellationToken);
        _active = Resolve(_edits);
        LoadedFromStore = true;
    }

    private Dictionary<string, PromptDefinition> Resolve(IReadOnlyList<PromptEdit> edits)
    {
        var active = new Dictionary<string, PromptDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (PromptDefinition original in _builtIn.GetAll())
        {
            PromptEdit? latest = edits
                .Where(edit => string.Equals(edit.PromptId, original.Id, StringComparison.OrdinalIgnoreCase) &&
                               edit.BaseVersion == original.Version)
                // El orden es el de la base (por id), no por fecha: dos
                // guardados en el mismo milisegundo no pueden empatar.
                .LastOrDefault();

            active[original.Id] = latest is null || latest.IsRevertToOriginal
                ? original
                : original with { Version = latest.Version!, SystemPrompt = latest.SystemPrompt! };
        }

        return active;
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");
}
