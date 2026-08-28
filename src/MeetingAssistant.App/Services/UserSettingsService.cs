using MeetingAssistant.Core.Abstractions;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Valores que la app deja editar desde <c>SettingsPage</c>. Un <c>null</c> o
/// vacío significa "sin override": se borra la clave y vuelve a mandar lo que
/// traiga el <c>appsettings.json</c> empaquetado.
/// </summary>
public sealed record UserSettings
{
    public string? VaultPath { get; init; }
    public string? SubFolder { get; init; }
    public string? LlmProvider { get; init; }
    public string? DeepgramApiKey { get; init; }
    public string? GeminiApiKey { get; init; }
    public string? GeminiModel { get; init; }
    public string? AzureEndpoint { get; init; }
    public string? AzureDeployment { get; init; }
    public string? AzureApiKey { get; init; }
}

/// <summary>
/// Lee la configuración efectiva y escribe los overrides del usuario en la base
/// local (<c>meetings.db</c>, tabla <c>setting</c>), con las credenciales
/// cifradas con DPAPI.
///
/// **Antes escribía un JSON en claro.** La capa de usuario nació en T9 para
/// resolver un problema real: instalada, la app lee su <c>appsettings.json</c>
/// desde <c>C:\Program Files\WindowsApps\...</c>, que es de sólo lectura, así que
/// cambiar el vault o una API key obligaba a reconstruir, refirmar y reinstalar
/// el <c>.msix</c>. Eso lo resolvió, pero dejó las claves en texto plano en el
/// perfil del usuario — anotado entonces como decisión diferida, no como olvido.
/// Fase 5 paso 5 la cobra: mismo lugar en la pila de configuración, mismo
/// aspecto en la UI, pero el destino es la base y los secretos se cifran.
///
/// El archivo de T9 lo migra una sola vez <c>UserSettingsImporter</c> en el
/// arranque. <see cref="LegacyFilePath"/> sigue existiendo por dos razones:
/// el importador necesita saber dónde mirar, y la capa <c>AddJsonFile</c> se
/// mantiene en la pila (por debajo de SQLite) para que un archivo puesto a mano
/// siga siendo una vía de escape si la base no abre.
/// </summary>
public sealed class UserSettingsService
{
    private readonly IConfiguration _configuration;
    private readonly ISettingsStore _settingsStore;

    public UserSettingsService(IConfiguration configuration, ISettingsStore settingsStore)
    {
        _configuration = configuration;
        _settingsStore = settingsStore;
    }

    /// <summary>
    /// Ruta del archivo de overrides de T9. Puede no existir — de hecho, tras el
    /// primer arranque con la base ya no existe: queda una copia redactada al
    /// lado (<c>appsettings.pre-sqlite.json</c>).
    /// </summary>
    public static string LegacyFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingAssistant",
        "appsettings.json");

    /// <summary>
    /// Devuelve lo que la app está usando ahora mismo — o sea el resultado de
    /// apilar empaquetado + archivo de usuario + base + variables de entorno, no
    /// sólo la base. Es lo que hay que mostrar en la UI: si una clave viene de una
    /// variable de entorno, el usuario tiene que verla tal como está en efecto,
    /// no un campo vacío que le haga creer que falta.
    /// </summary>
    public UserSettings LoadEffective() => new()
    {
        VaultPath = Read("Storage:VaultPath"),
        SubFolder = Read("Storage:SubFolder"),
        LlmProvider = Read("Llm:Provider") ?? "Gemini",
        DeepgramApiKey = Read("Deepgram:ApiKey"),
        GeminiApiKey = Read("Gemini:ApiKey"),
        GeminiModel = Read("Gemini:Model"),
        AzureEndpoint = Read("AzureFoundry:Endpoint"),
        AzureDeployment = Read("AzureFoundry:Deployment"),
        AzureApiKey = Read("AzureFoundry:ApiKey")
    };

    /// <summary>
    /// Escribe los overrides en la base. Cada clave se guarda por separado y las
    /// credenciales van marcadas como secretas — el flag no se decide acá sino en
    /// <see cref="SettingKeyPolicy"/>, para que la UI y el importador no puedan
    /// discrepar sobre qué se cifra.
    ///
    /// Un valor vacío <b>borra</b> la clave, que es como se vuelve al valor
    /// empaquetado. Es el mismo criterio que ya tenía el archivo de T9: si
    /// guardar un vacío dejara una cadena vacía, no habría forma de volver atrás
    /// desde la UI.
    /// </summary>
    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        (string Key, string? Value)[] values =
        [
            ("Storage:VaultPath", settings.VaultPath),
            ("Storage:SubFolder", settings.SubFolder),
            ("Llm:Provider", settings.LlmProvider),
            ("Deepgram:ApiKey", settings.DeepgramApiKey),
            ("Gemini:ApiKey", settings.GeminiApiKey),
            ("Gemini:Model", settings.GeminiModel),
            ("AzureFoundry:Endpoint", settings.AzureEndpoint),
            ("AzureFoundry:Deployment", settings.AzureDeployment),
            ("AzureFoundry:ApiKey", settings.AzureApiKey)
        ];

        foreach ((string key, string? value) in values)
        {
            await _settingsStore.SetAsync(
                key,
                value?.Trim(),
                SettingKeyPolicy.IsSecret(key),
                cancellationToken);
        }
    }

    /// <summary>
    /// Misma regla que <c>App.ReadSetting</c>: un marcador de posición del
    /// example (<c>&lt;your-api-key&gt;</c>) cuenta como ausente. Si validar y
    /// leer discreparan, la UI mostraría como configurado algo que la app
    /// rechaza al arrancar.
    /// </summary>
    private string? Read(string key)
    {
        string? value = _configuration[key];
        return string.IsNullOrWhiteSpace(value) || value.StartsWith('<') ? null : value;
    }
}
