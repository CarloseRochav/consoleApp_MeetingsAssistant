using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Comprueba al arrancar que estén todas las claves de configuración
/// obligatorias.
///
/// Existe por un fallo real: <c>appsettings.json</c> se quedó sin la sección
/// "Api" que sí estaba documentada en <c>appsettings.example.json</c>, y el
/// desfase no se detectó hasta que el constructor de
/// <c>LocalRecordingApiServer</c> lanzó en pleno arranque. Cada consumidor
/// valida su propia clave y lanza en cuanto la echa en falta, así que el
/// usuario solo ve la primera de las que falten: arregla una, vuelve a
/// arrancar, descubre la siguiente.
///
/// Este validador recorre todas las claves antes de construir nada y las
/// reporta juntas en un único mensaje.
/// </summary>
public static class StartupConfigurationValidator
{
    /// <summary>
    /// Claves exigidas siempre, sea cual sea el proveedor de LLM elegido.
    /// El texto de cada una explica de dónde sale el valor, porque este
    /// mensaje es lo único que verá quien esté configurando la app.
    /// </summary>
    private static readonly (string Key, string Hint)[] AlwaysRequired =
    [
        ("Deepgram:ApiKey", "clave de API de Deepgram (o la variable de entorno DEEPGRAM_API_KEY)"),
        ("Api:AuthToken", "token del endpoint local — enciende el micrófono, nunca debe quedar vacío"),
        ("Storage:VaultPath", "ruta a tu vault de Obsidian donde se guardan los reportes")
    ];

    /// <summary>
    /// Lanza si falta alguna clave obligatoria, enumerándolas todas.
    /// </summary>
    public static void Validate(IConfiguration configuration)
    {
        List<string> missing = FindMissing(configuration);
        if (missing.Count == 0) return;

        string detail = string.Join(Environment.NewLine, missing.Select(entry => $"  - {entry}"));
        throw new InvalidOperationException(
            $"Faltan {missing.Count} valor(es) de configuración en appsettings.json:" +
            $"{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}" +
            "Compara tu appsettings.json con appsettings.example.json — normalmente " +
            "significa que el ejemplo ganó una sección nueva que tu copia local no tiene.");
    }

    private static List<string> FindMissing(IConfiguration configuration)
    {
        var missing = new List<string>();

        foreach ((string key, string hint) in AlwaysRequired)
        {
            if (!HasValue(configuration, key)) missing.Add($"{key} — {hint}");
        }

        // El proveedor de LLM decide qué credenciales hacen falta: pedir las de
        // ambos obligaría a rellenar claves de un servicio que no se usa.
        string provider = configuration["Llm:Provider"] ?? "Gemini";
        switch (provider.ToLowerInvariant())
        {
            case "gemini":
                if (!HasValue(configuration, "Gemini:ApiKey"))
                {
                    missing.Add("Gemini:ApiKey — clave de API de Gemini (o la variable de entorno GEMINI_API_KEY), requerida porque Llm:Provider es 'Gemini'");
                }

                break;

            case "azurefoundry":
                foreach (string key in new[] { "AzureFoundry:Endpoint", "AzureFoundry:Deployment" })
                {
                    // AzureFoundry:ApiKey se omite a propósito: es opcional
                    // porque el cliente puede autenticarse con Azure.Identity.
                    if (!HasValue(configuration, key))
                    {
                        missing.Add($"{key} — requerida porque Llm:Provider es 'AzureFoundry'");
                    }
                }

                break;

            default:
                missing.Add($"Llm:Provider — valor '{provider}' no soportado. Usa 'Gemini' o 'AzureFoundry'.");
                break;
        }

        return missing;
    }

    /// <summary>
    /// Un valor cuenta como ausente si está vacío o si sigue siendo el
    /// marcador de posición del example (que empieza por '&lt;'). Misma regla
    /// que usa <c>App.ReadSetting</c>, para que validar y leer no discrepen.
    /// </summary>
    private static bool HasValue(IConfiguration configuration, string key)
    {
        string? value = configuration[key];
        return !string.IsNullOrWhiteSpace(value) && !value.StartsWith('<');
    }
}
