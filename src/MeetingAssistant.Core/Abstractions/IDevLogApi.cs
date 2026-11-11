using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// La API de DevLog (Fase 8, paso B): el escritorio sólo habla con ella. La implementación HTTP vive
/// en Infrastructure; los servicios de Core la componen. Cada llamada recibe el token explícito (el
/// del dispositivo, salvo para crear el dispositivo, que usa el del login), como en el teléfono:
/// sin estado escondido, y los autotests pasan uno falso. Nada se escribe en un log: ni tokens, ni
/// enlaces, ni contenido de reuniones.
/// </summary>
public interface IDevLogApi
{
    /// <summary>
    /// Auth/Login. La contraseña viaja en esta petición y en ningún otro lado: la implementación
    /// arma el cuerpo en un buffer propio y lo borra al terminar. Quien llama borra su arreglo.
    /// </summary>
    Task<ApiResult<LoginResult>> LoginAsync(string username, ReadOnlyMemory<char> password, CancellationToken cancellationToken = default);

    /// <summary>Devices/InsertUpdate_Devices opción 0, con el token del login: un token nuevo para esta máquina.</summary>
    Task<ApiResult<DeviceCreated>> CreateDeviceAsync(string userToken, string name, CancellationToken cancellationToken = default);

    /// <summary>Devices/Select_Me: 401 una vez revocado el dispositivo.</summary>
    Task<ApiResult<DeviceIdentity>> MeAsync(string deviceToken, CancellationToken cancellationToken = default);

    /// <summary>Jobs/InsertUpdate_Jobs opción 0: un trabajo y su enlace de subida. 429 = tope diario o demasiados en curso.</summary>
    Task<ApiResult<JobUpload>> CreateJobAsync(string deviceToken, string source, CancellationToken cancellationToken = default);

    /// <summary>Jobs/InsertUpdate_Jobs opción 1: la subida terminó. 409 = sin archivo o ya pasó ese paso; 400 = demasiado grande.</summary>
    Task<ApiResult<JobRow>> ConfirmJobAsync(string deviceToken, long idJob, CancellationToken cancellationToken = default);

    /// <summary>Jobs/InsertUpdate_Jobs opción 2: re-extraer una sesión con otro prompt. Nace en cola.</summary>
    Task<ApiResult<JobRow>> ReextractAsync(string deviceToken, long idSession, string promptId, string source, CancellationToken cancellationToken = default);

    /// <summary>Jobs/Select_Jobs opción 1: un trabajo (404 si no es mío).</summary>
    Task<ApiResult<JobRow>> GetJobAsync(string deviceToken, long idJob, CancellationToken cancellationToken = default);

    /// <summary>Meetings/Select_Sessions opción 0: mis sesiones de todos los dispositivos, la más nueva primero.</summary>
    Task<ApiResult<IReadOnlyList<ApiSession>>> GetSessionsAsync(string deviceToken, int limit, int offset, CancellationToken cancellationToken = default);

    /// <summary>Meetings/Select_Transcripts: 404 si no tiene, o no es mía.</summary>
    Task<ApiResult<ApiTranscript>> GetTranscriptAsync(string deviceToken, long idSession, CancellationToken cancellationToken = default);

    /// <summary>Meetings/Select_Reports opción 0: los reportes de una sesión.</summary>
    Task<ApiResult<IReadOnlyList<ApiReport>>> GetReportsAsync(string deviceToken, long idSession, CancellationToken cancellationToken = default);

    /// <summary>Meetings/Select_Search: sin acentos, la más relevante primero (límite 1-100).</summary>
    Task<ApiResult<IReadOnlyList<ApiSearchHit>>> SearchAsync(string deviceToken, string query, int limit, CancellationToken cancellationToken = default);

    /// <summary>Meetings/Select_Costs: opción 0 = totales (siempre una fila), 1 = por mes UTC.</summary>
    Task<ApiResult<IReadOnlyList<ApiCostRow>>> GetCostsAsync(string deviceToken, int option, CancellationToken cancellationToken = default);

    /// <summary>Meetings/Select_Prompt_Catalog opción 0: los prompts del código, como los corre el worker.</summary>
    Task<ApiResult<IReadOnlyList<CatalogPrompt>>> GetPromptCatalogAsync(string deviceToken, CancellationToken cancellationToken = default);

    /// <summary>Meetings/Select_Prompt_Versions: 2 = las que valen para mí (las mías sobre la base), 3 = las mías.</summary>
    Task<ApiResult<IReadOnlyList<PromptVersionRow>>> GetPromptVersionsAsync(string deviceToken, int option, CancellationToken cancellationToken = default);

    /// <summary>
    /// Meetings/Insert_Prompt_Versions en MI capa (nunca la compartida desde el escritorio).
    /// <paramref name="version"/> y <paramref name="systemPrompt"/> null = vuelta al original. 409 = esa versión existe.
    /// </summary>
    Task<ApiResult<long>> InsertPromptVersionAsync(string deviceToken, string promptId, string baseVersion, string? version, string? systemPrompt, string? note, CancellationToken cancellationToken = default);

    /// <summary>Meetings/Select_Settings: 0 base, 1 mías, 2 las que valen.</summary>
    Task<ApiResult<IReadOnlyList<ApiSetting>>> GetSettingsAsync(string deviceToken, int option, CancellationToken cancellationToken = default);

    /// <summary>Meetings/InsertUpdate_Settings en MI capa: guardar (value) o borrar (value null). 400 con el motivo del servidor.</summary>
    Task<ApiResult<int>> SetSettingAsync(string deviceToken, string key, string? value, CancellationToken cancellationToken = default);

    /// <summary>Meetings/Select_Audio_Link: enlace de escucha de 15 minutos; 404 si no hay audio archivado.</summary>
    Task<ApiResult<AudioLink>> GetAudioLinkAsync(string deviceToken, long idSession, CancellationToken cancellationToken = default);
}

/// <summary>Dónde se guarda el dispositivo de esta máquina (Windows: DPAPI, atado a este usuario).</summary>
public interface IDeviceSessionStore
{
    DeviceSession? Load();

    void Save(DeviceSession session);

    void Clear();
}
