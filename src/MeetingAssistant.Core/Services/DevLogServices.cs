using System.Globalization;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Services;

// Lo que la UI lee y escribe por la API (Fase 8, paso B): historial, búsqueda, costos, prompts y
// ajustes. Cada servicio compone IDevLogApi con la sesión del dispositivo (DeviceAuthService.CallAsync):
// devuelve el valor o lanza ApiException, y un 401 cierra la sesión antes de lanzar.

/// <summary>Historial, transcript, búsqueda y el enlace de escucha del audio archivado.</summary>
public sealed class MeetingHistoryService(IDevLogApi api, DeviceAuthService auth)
{
    public Task<IReadOnlyList<ApiSession>> ListSessionsAsync(int limit, int offset = 0, CancellationToken cancellationToken = default) =>
        auth.CallAsync(token => api.GetSessionsAsync(token, Math.Clamp(limit, 1, 200), Math.Max(offset, 0), cancellationToken));

    /// <summary>Null si la reunión no tiene transcript.</summary>
    public async Task<ApiTranscript?> GetTranscriptAsync(long idSession, CancellationToken cancellationToken = default)
    {
        try
        {
            return await auth.CallAsync(token => api.GetTranscriptAsync(token, idSession, cancellationToken));
        }
        catch (ApiException exception) when (exception.Error is ApiError.NotFound)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<ApiReport>> GetReportsAsync(long idSession, CancellationToken cancellationToken = default) =>
        auth.CallAsync(token => api.GetReportsAsync(token, idSession, cancellationToken));

    /// <summary>La consulta va cruda: la API la arma con <c>plainto_tsquery</c>, que no lanza por la sintaxis.</summary>
    public Task<IReadOnlyList<ApiSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default) =>
        auth.CallAsync(token => api.SearchAsync(token, query, Math.Clamp(limit, 1, 100), cancellationToken));

    /// <summary>Null si no hay audio que escuchar (no archivado, vencido, borrado, o importado del escritorio viejo).</summary>
    public async Task<AudioLink?> GetAudioLinkAsync(long idSession, CancellationToken cancellationToken = default)
    {
        try
        {
            return await auth.CallAsync(token => api.GetAudioLinkAsync(token, idSession, cancellationToken));
        }
        catch (ApiException exception) when (exception.Error is ApiError.NotFound)
        {
            return null;
        }
    }
}

/// <summary>
/// Un costo como lo da DevLog: <see cref="KnownMicroUsd"/> es la suma de los precios conocidos (null
/// si no conoce ninguno) y <see cref="UnknownCount"/> cuenta lo que no tiene precio. Lo desconocido
/// nunca se muestra como 0 (handoff del paso B).
/// </summary>
public sealed record CostAmount(long? KnownMicroUsd, long UnknownCount = 0)
{
    private static readonly CultureInfo Display = CultureInfo.GetCultureInfo("es-ES");

    /// <summary>Nada que costear: ni transcripción ni reporte (no es un precio desconocido).</summary>
    public bool IsNothing => KnownMicroUsd is null && UnknownCount == 0;

    public bool IsPartlyUnknown => UnknownCount > 0;

    /// <summary>"US$1,23", "desconocido", "US$1,23 + 2 sin precio" o "nada".</summary>
    public string Text() => (KnownMicroUsd, UnknownCount) switch
    {
        (null, 0) => "nada",
        (null, _) => UnknownCount == 1 ? "desconocido (1 sin precio)" : $"desconocido ({UnknownCount} sin precio)",
        ({ } known, > 0) => $"{Usd(known)} + {UnknownCount} sin precio",
        ({ } known, _) => Usd(known)
    };

    public static CostAmount operator +(CostAmount a, CostAmount b) => new(
        a.KnownMicroUsd is null && b.KnownMicroUsd is null ? null : (a.KnownMicroUsd ?? 0) + (b.KnownMicroUsd ?? 0),
        a.UnknownCount + b.UnknownCount);

    /// <summary>Centavos desde un dólar; cuatro decimales por debajo (una reunión cuesta unos centavos).</summary>
    public static string Usd(long micro)
    {
        decimal dollars = micro / 1_000_000m;
        return micro >= 1_000_000 || micro == 0
            ? "US$" + dollars.ToString("N2", Display)
            : "US$" + dollars.ToString("0.0000", Display);
    }
}

/// <summary>Un período (todo, o un mes UTC), con lo desconocido aparte.</summary>
public sealed record CostPeriod(DateTimeOffset? Month, long Sessions, CostAmount Transcripts, CostAmount Reports, long? TokensInput, long? TokensOutput)
{
    public CostAmount Total => Transcripts + Reports;

    public static CostPeriod From(ApiCostRow row) => new(
        row.Month,
        row.Sessions,
        new CostAmount(row.TranscriptsCostMicroUsd, row.TranscriptsCostUnknown),
        new CostAmount(row.ReportsCostMicroUsd, row.ReportsCostUnknown),
        row.TokensInput,
        row.TokensOutput);
}

public sealed record CostOverview(CostPeriod Totals, IReadOnlyList<CostPeriod> Months);

/// <summary>Lo que costaron mis reuniones (Meetings/Select_Costs): totales y por mes.</summary>
public sealed class MeetingCostsService(IDevLogApi api, DeviceAuthService auth)
{
    public async Task<CostOverview> LoadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ApiCostRow> totals = await auth.CallAsync(token => api.GetCostsAsync(token, 0, cancellationToken));
        IReadOnlyList<ApiCostRow> months = await auth.CallAsync(token => api.GetCostsAsync(token, 1, cancellationToken));

        // El servidor siempre manda una fila de totales; vacío = nada registrado todavía.
        CostPeriod total = totals.Count > 0
            ? CostPeriod.From(totals[0])
            : new CostPeriod(null, 0, new CostAmount(null), new CostAmount(null), null, null);
        return new CostOverview(total, months.Select(CostPeriod.From).OrderByDescending(period => period.Month).ToList());
    }
}

/// <summary>
/// Un prompt del catálogo con lo que vale para este usuario y sus ediciones propias (la más nueva
/// primero). <see cref="InEffect"/> null = el texto del catálogo (el original).
/// </summary>
public sealed record PromptEntry(CatalogPrompt Catalog, PromptVersionRow? InEffect, bool BuiltInChosen, IReadOnlyList<PromptVersionRow> Own)
{
    public string PromptId => Catalog.PromptId;

    public string DisplayName => string.IsNullOrWhiteSpace(Catalog.Name) ? Catalog.PromptId : Catalog.Name;

    /// <summary>El texto que corre el worker para este usuario.</summary>
    public string Text => InEffect?.SystemPrompt ?? Catalog.SystemPrompt;

    public string Version => InEffect?.Version ?? Catalog.Version;

    /// <summary>Volver al original sólo tiene sentido mientras una edición está en efecto.</summary>
    public bool CanUseBuiltIn => InEffect is not null;

    /// <summary>
    /// Un nombre para la próxima edición propia, p. ej. "v1-mine-3". El mismo esquema que el
    /// teléfono, que escribe en la misma capa: así no chocan por nombre.
    /// </summary>
    public string SuggestedVersion() => $"{Catalog.Version}-mine-{Own.Count(row => !row.IsRevert) + 1}";

    public override string ToString() => $"PromptEntry(promptId={PromptId}, version={Version})";
}

public sealed record PromptSaveResult(bool Saved, string Message);

/// <summary>
/// Los prompts del código (Meetings/Select_Prompt_Catalog, los publica el worker) y las ediciones
/// propias (Select_Prompt_Versions, Insert_Prompt_Versions). El escritorio escribe sólo la capa del
/// usuario, nunca la compartida (como el teléfono).
/// </summary>
public sealed class PromptLibraryService(IDevLogApi api, DeviceAuthService auth)
{
    private const int Effective = 2;
    private const int Mine = 3;

    public async Task<IReadOnlyList<PromptEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CatalogPrompt> catalog = await auth.CallAsync(token => api.GetPromptCatalogAsync(token, cancellationToken));
        IReadOnlyList<PromptVersionRow> effective = await auth.CallAsync(token => api.GetPromptVersionsAsync(token, Effective, cancellationToken));
        IReadOnlyList<PromptVersionRow> own = await auth.CallAsync(token => api.GetPromptVersionsAsync(token, Mine, cancellationToken));

        return catalog
            .OrderBy(prompt => (string.IsNullOrWhiteSpace(prompt.Name) ? prompt.PromptId : prompt.Name).ToLowerInvariant())
            .Select(prompt => EntryOf(prompt, effective, own))
            .ToList();
    }

    public async Task<PromptSaveResult> SaveAsync(PromptEntry entry, string version, string text, string? note, CancellationToken cancellationToken = default)
    {
        string name = version.Trim();
        if (name.Length == 0) return new PromptSaveResult(false, "Ponle un nombre a esta versión.");
        if (string.IsNullOrWhiteSpace(text)) return new PromptSaveResult(false, "El prompt no puede quedar vacío.");

        return await SaveOfAsync(token => api.InsertPromptVersionAsync(
            token, entry.PromptId, entry.Catalog.Version, name, text, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), cancellationToken),
            $"Guardada como {name}. Los reportes nuevos (también los del teléfono) la usan.");
    }

    /// <summary>Vuelta al original en mi capa (nunca choca en el servidor).</summary>
    public Task<PromptSaveResult> UseBuiltInAsync(PromptEntry entry, CancellationToken cancellationToken = default) =>
        SaveOfAsync(token => api.InsertPromptVersionAsync(token, entry.PromptId, entry.Catalog.Version, null, null, null, cancellationToken),
            $"Vuelve a valer el original ({entry.Catalog.Version}). Tus ediciones siguen en la lista.");

    private async Task<PromptSaveResult> SaveOfAsync(Func<string, Task<ApiResult<long>>> call, string success)
    {
        try
        {
            await auth.CallAsync(call);
            return new PromptSaveResult(true, success);
        }
        catch (ApiException exception) when (exception.Error is ApiError.Rejected { Code: 409 })
        {
            return new PromptSaveResult(false, "Ya tienes una versión con ese nombre. Elige otro.");
        }
        catch (ApiException exception) when (exception.Error is ApiError.Rejected { Code: 400 } rejected)
        {
            return new PromptSaveResult(false, string.IsNullOrWhiteSpace(rejected.Message) ? "El servidor rechazó este prompt." : rejected.Message.Trim().Trim('"'));
        }
    }

    /// <summary>
    /// Lo que vale para un prompt: la más nueva de las filas que la opción 2 devolvió para él (las
    /// mías si tengo, si no las de la base, como ya eligió el servidor). Una vuelta al original, o
    /// ninguna fila, = el texto del catálogo. Los ids se comparan sin mayúsculas.
    /// </summary>
    public static PromptEntry EntryOf(CatalogPrompt prompt, IReadOnlyList<PromptVersionRow> effective, IReadOnlyList<PromptVersionRow> own)
    {
        IEnumerable<PromptVersionRow> Of(IEnumerable<PromptVersionRow> rows) => rows
            .Where(row => string.Equals(row.PromptId, prompt.PromptId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.IdPromptVersion);

        PromptVersionRow? newest = Of(effective).FirstOrDefault();
        return newest switch
        {
            null => new PromptEntry(prompt, null, false, Of(own).ToList()),
            { IsRevert: true } => new PromptEntry(prompt, null, true, Of(own).ToList()),
            _ => new PromptEntry(prompt, newest, false, Of(own).ToList())
        };
    }
}

/// <summary>Un ajuste del servidor: el valor de la base y el mío (que manda si existe).</summary>
public sealed record ServerSetting(string Key, string Label, string Hint, string? BaseValue, string? OwnValue)
{
    public string? Effective => OwnValue ?? BaseValue;
}

/// <summary>
/// Mis ajustes en el servidor (migración 014): proveedor, modelo, términos clave, retención del
/// audio. Sólo la capa propia: <b>nunca un secreto, nunca Pricing</b> (los precios son de la base y
/// sólo los toca un admin). El servidor también lo rechaza; aquí ni se ofrece.
/// </summary>
public sealed class ServerSettingsService(IDevLogApi api, DeviceAuthService auth)
{
    /// <summary>Los que el escritorio deja editar, en este orden.</summary>
    public static IReadOnlyList<(string Key, string Label, string Hint)> Editable { get; } =
    [
        ("Llm:Provider", "Proveedor del LLM", "Gemini o AzureFoundry. El servidor sólo acepta un modelo que la base tenga con precio."),
        ("Gemini:Model", "Modelo de Gemini", "Vale cuando el proveedor es Gemini."),
        ("AzureFoundry:Deployment", "Deployment de Azure AI Foundry", "Vale cuando el proveedor es AzureFoundry."),
        ("Deepgram:Keyterms", "Términos clave de Deepgram", "Separados por ';'. Hasta 100; con al menos uno Deepgram cobra un extra por minuto."),
        ("Audio:RetentionDays", "Días que se guarda el audio en la nube", "Entero de 0 a 365. Sin valor, 30. Vale para las reuniones que terminen desde ahora.")
    ];

    /// <summary>Lo que se puede escribir en mi capa: ni secretos, ni precios, ni un Endpoint.</summary>
    public static bool IsAllowed(string key) =>
        !string.IsNullOrWhiteSpace(key) &&
        !SettingKeyPolicy.IsSecret(key) &&
        !key.StartsWith("Pricing:", StringComparison.OrdinalIgnoreCase) &&
        !key.EndsWith(":Endpoint", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ServerSetting>> LoadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ApiSetting> shared = await auth.CallAsync(token => api.GetSettingsAsync(token, 0, cancellationToken));
        IReadOnlyList<ApiSetting> mine = await auth.CallAsync(token => api.GetSettingsAsync(token, 1, cancellationToken));

        string? Find(IEnumerable<ApiSetting> rows, string key) =>
            rows.FirstOrDefault(row => string.Equals(row.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

        var result = Editable.Select(item => new ServerSetting(item.Key, item.Label, item.Hint, Find(shared, item.Key), Find(mine, item.Key))).ToList();

        // Lo propio que el escritorio no conoce (lo escribió el teléfono, o una versión más nueva)
        // también se muestra, para poder verlo y borrarlo.
        foreach (ApiSetting extra in mine.Where(row => IsAllowed(row.Key) && result.All(item => !string.Equals(item.Key, row.Key, StringComparison.OrdinalIgnoreCase))))
        {
            result.Add(new ServerSetting(extra.Key, extra.Key, "Escrito desde otro cliente.", Find(shared, extra.Key), extra.Value));
        }

        return result;
    }

    /// <summary>
    /// Guarda (o borra, con <paramref name="value"/> vacío) mi valor. Null si salió bien; si no, el
    /// motivo del servidor (400) en palabras.
    /// </summary>
    public async Task<string?> SaveAsync(string key, string? value, string? previousOwnValue, CancellationToken cancellationToken = default)
    {
        if (!IsAllowed(key)) return $"{key} no se puede guardar desde aquí (es secreto, un precio o un Endpoint).";

        string? trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (trimmed is null && previousOwnValue is null) return null;
        if (string.Equals(trimmed, previousOwnValue, StringComparison.Ordinal)) return null;

        try
        {
            await auth.CallAsync(token => api.SetSettingAsync(token, key, trimmed, cancellationToken));
            return null;
        }
        catch (ApiException exception) when (exception.Error is ApiError.NotFound && trimmed is null)
        {
            return null;
        }
        catch (ApiException exception) when (exception.Error is ApiError.Rejected rejected)
        {
            return $"{key}: {(string.IsNullOrWhiteSpace(rejected.Message) ? "el servidor lo rechazó." : rejected.Message.Trim().Trim('"'))}";
        }
    }
}
