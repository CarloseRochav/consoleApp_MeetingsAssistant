using MeetingAssistant.Core.Abstractions;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.App.Services;

/// <summary>Los ajustes de ESTA máquina que la app deja editar. Desde el paso B ninguno es una clave de proveedor.</summary>
public sealed record UserSettings
{
    public string? VaultPath { get; init; }
    public string? SubFolder { get; init; }
    public string? ApiAuthToken { get; init; }
}

/// <summary>
/// Lee y guarda los ajustes de esta máquina en la base local (SQLite, secretos con DPAPI). Los
/// ajustes compartidos (proveedor, modelo, términos clave, retención del audio) ya no viven aquí:
/// son del servidor y se editan en Configuración → "En el servidor" (<c>ServerSettingsService</c>).
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

    /// <summary>El archivo de usuario de T9 (legado): se migra a la base al primer arranque.</summary>
    public static string LegacyFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingAssistant",
        "appsettings.json");

    /// <summary>
    /// Lo que el modo local guardaba en esta máquina y desde el paso B no se usa: claves de
    /// proveedor, cadenas de conexión, conmutadores y ajustes que ahora son del servidor.
    /// </summary>
    public static IReadOnlyList<string> LeftoverKeys { get; } =
    [
        "Deepgram:ApiKey", "Deepgram:Keyterms", "Gemini:ApiKey", "Gemini:Model", "AzureFoundry:ApiKey", "AzureFoundry:Endpoint",
        "AzureFoundry:Deployment", "Llm:Provider", "Postgres:ConnectionString", "AzureStorage:ConnectionString",
        "Database:Provider", "Configuration:Source"
    ];

    public UserSettings LoadEffective() => new()
    {
        VaultPath = Read("Storage:VaultPath"),
        SubFolder = Read("Storage:SubFolder"),
        ApiAuthToken = Read("Api:AuthToken")
    };

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        (string Key, string? Value)[] values =
        [
            ("Storage:VaultPath", settings.VaultPath),
            ("Storage:SubFolder", settings.SubFolder),
            ("Api:AuthToken", settings.ApiAuthToken)
        ];

        foreach ((string key, string? value) in values)
        {
            await _settingsStore.SetAsync(key, value?.Trim(), SettingKeyPolicy.IsSecret(key), cancellationToken);
        }
    }

    /// <summary>Cuántas de las claves viejas siguen guardadas en la base local de esta máquina.</summary>
    public async Task<IReadOnlyList<string>> FindLeftoverKeysAsync(CancellationToken cancellationToken = default) =>
        (await _settingsStore.GetAllAsync(cancellationToken))
            .Where(entry => LeftoverKeys.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .ToList();

    /// <summary>
    /// Borra de la base local las claves del modo local. No toca los archivos appsettings.json ni
    /// las claves en los proveedores: rotarlas es un paso del corte del dueño.
    /// </summary>
    public async Task<int> DeleteLeftoverKeysAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> keys = await FindLeftoverKeysAsync(cancellationToken);
        foreach (string key in keys) await _settingsStore.RemoveAsync(key, cancellationToken);
        return keys.Count;
    }

    /// <summary>Un marcador de posición del example (<c>&lt;...&gt;</c>) cuenta como ausente, igual que en el validador.</summary>
    private string? Read(string key)
    {
        string? value = _configuration[key];
        return string.IsNullOrWhiteSpace(value) || value.StartsWith('<') ? null : value;
    }
}
