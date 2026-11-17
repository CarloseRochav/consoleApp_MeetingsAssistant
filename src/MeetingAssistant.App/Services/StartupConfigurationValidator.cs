using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Los ajustes sin los que la app no puede arrancar. Desde el paso B ninguno es una clave de
/// proveedor: la máquina sólo necesita dónde escribir los .md, la dirección de la API y el token del
/// endpoint local. La sesión con la API no es un ajuste: si falta, se abre el inicio de sesión.
/// </summary>
public static class StartupConfigurationValidator
{
    private static readonly (string Key, string Hint)[] AlwaysRequired =
    [
        ("Storage:VaultPath", "path to your Obsidian vault, where reports are saved"),
        ("Api:AuthToken", "token for the local endpoint — it turns the microphone on; it must not stay empty"),
        ("DevLog:BaseUrl", "DevLog API address (it comes in appsettings.defaults.json)")
    ];

    public static IReadOnlyList<string> FindMissing(IConfiguration configuration)
    {
        var missing = new List<string>();
        foreach ((string key, string hint) in AlwaysRequired)
        {
            if (!HasValue(configuration, key)) missing.Add($"{key} — {hint}");
        }

        return missing;
    }

    private static bool HasValue(IConfiguration configuration, string key)
    {
        string? value = configuration[key];
        return !string.IsNullOrWhiteSpace(value) && !value.StartsWith('<');
    }
}
