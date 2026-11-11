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
        ("Storage:VaultPath", "ruta a tu vault de Obsidian donde se guardan los reportes"),
        ("Api:AuthToken", "token del endpoint local — enciende el micrófono, nunca debe quedar vacío"),
        ("DevLog:BaseUrl", "dirección de la API de DevLog (viene en appsettings.defaults.json)")
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
