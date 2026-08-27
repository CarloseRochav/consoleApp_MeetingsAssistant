using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Valores que la app deja editar desde <c>SettingsPage</c>. Un <c>null</c> o
/// vacío significa "sin override": se borra la clave del archivo de usuario y
/// vuelve a mandar lo que traiga el <c>appsettings.json</c> empaquetado.
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
/// Lee y escribe <c>%LOCALAPPDATA%\MeetingAssistant\appsettings.json</c>, la
/// capa de configuración del usuario que se superpone a la empaquetada.
///
/// Existe por un problema que sólo apareció al cerrar Fase 3: instalada, la app
/// lee su <c>appsettings.json</c> desde <c>C:\Program Files\WindowsApps\...</c>,
/// que es de sólo lectura. Cambiar el vault, el proveedor de LLM o una API key
/// obligaba a reconstruir, refirmar y reinstalar el <c>.msix</c>. La única vía
/// de escape era pisar claves con variables de entorno <c>Seccion__Clave</c>,
/// que funciona pero no es una interfaz.
///
/// Es el mismo criterio, y el mismo destino, que ya resolvieron
/// <see cref="App.StartupErrorLogPath"/> y <see cref="App.MeetingOutputDirectory"/>:
/// lo que la app necesita escribir no puede vivir dentro del paquete.
///
/// **No guarda las claves cifradas.** Quedan en texto plano en el perfil del
/// usuario, que es la misma exposición que ya tenían dentro del <c>.msix</c>
/// (documentado en T6a) — no la empeora, pero tampoco la mejora. Cifrarlas con
/// DPAPI es una decisión tomada y diferida a propósito, no un olvido.
/// </summary>
public sealed class UserSettingsService
{
    private readonly IConfiguration _configuration;

    public UserSettingsService(IConfiguration configuration) => _configuration = configuration;

    /// <summary>
    /// Ruta del archivo de overrides. Puede no existir: el primer
    /// <see cref="Save"/> lo crea.
    /// </summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingAssistant",
        "appsettings.json");

    /// <summary>
    /// Devuelve lo que la app está usando ahora mismo — o sea el resultado de
    /// apilar empaquetado + usuario + variables de entorno, no sólo el archivo
    /// de usuario. Es lo que hay que mostrar en la UI: si una clave viene de una
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
    /// Escribe los overrides preservando cualquier otra clave que ya hubiera en
    /// el archivo: se edita el JSON existente, no se reemplaza. Así, agregar
    /// aquí un campo nuevo mañana no borra lo que alguien haya puesto a mano.
    /// </summary>
    public void Save(UserSettings settings)
    {
        JsonObject root = ReadExistingDocument();

        SetOrRemove(root, "Storage", "VaultPath", settings.VaultPath);
        SetOrRemove(root, "Storage", "SubFolder", settings.SubFolder);
        SetOrRemove(root, "Llm", "Provider", settings.LlmProvider);
        SetOrRemove(root, "Deepgram", "ApiKey", settings.DeepgramApiKey);
        SetOrRemove(root, "Gemini", "ApiKey", settings.GeminiApiKey);
        SetOrRemove(root, "Gemini", "Model", settings.GeminiModel);
        SetOrRemove(root, "AzureFoundry", "Endpoint", settings.AzureEndpoint);
        SetOrRemove(root, "AzureFoundry", "Deployment", settings.AzureDeployment);
        SetOrRemove(root, "AzureFoundry", "ApiKey", settings.AzureApiKey);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        // Escritura en dos pasos: un corte de luz a mitad de un File.WriteAllText
        // deja un JSON truncado, y un JSON inválido acá **impide arrancar la
        // app** (AddJsonFile lanza al parsear aunque sea optional). El archivo
        // temporal convierte ese riesgo en "se perdió el último cambio".
        string temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, FilePath, overwrite: true);
    }

    private JsonObject ReadExistingDocument()
    {
        if (!File.Exists(FilePath)) return [];

        try
        {
            return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? [];
        }
        catch (JsonException exception)
        {
            // Si el archivo quedó corrupto, se parte de cero en vez de arrastrar
            // el error: la alternativa es que guardar falle para siempre y el
            // usuario no tenga forma de arreglarlo desde la propia UI.
            App.LogStartupFailure($"UserSettingsService.Read({FilePath})", exception);
            return [];
        }
    }

    private static void SetOrRemove(JsonObject root, string section, string property, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (root[section] is not JsonObject existing) return;

            existing.Remove(property);
            // Una sección vacía no aporta nada y ensucia el archivo.
            if (existing.Count == 0) root.Remove(section);
            return;
        }

        if (root[section] is not JsonObject target)
        {
            target = [];
            root[section] = target;
        }

        target[property] = value.Trim();
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
