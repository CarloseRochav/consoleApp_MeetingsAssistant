using MeetingAssistant.Core.Abstractions;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.Infrastructure.Storage.Sqlite;

/// <summary>
/// Expone los ajustes de la base como una <b>capa más</b> de
/// <c>IConfiguration</c>.
///
/// Esto es la regla de diseño 4 de Fase 5 hecha código, y el detalle importa:
/// <b>no reemplaza a <c>IConfiguration</c></b>, se apila dentro. Es lo que deja
/// intactos <c>ReadRequiredSetting</c>, <c>StartupConfigurationValidator</c> y
/// <c>ConfigPricingCostEstimator</c> — ninguno se enteró de que la base
/// existe — y, sobre todo, <b>conserva las variables de entorno
/// <c>Seccion__Clave</c> como capa de arriba</b>. Esa vía de escape ya salvó una
/// validación (se usó para forzar el fallo de Deepgram) y es lo único que queda
/// cuando un ajuste malo impide arrancar: si la base pisara al entorno, un valor
/// guardado mal en la base sería irreparable desde afuera.
///
/// Orden final, de menor a mayor precedencia:
/// empaquetado -> archivo de usuario (legado de T9) -> <b>SQLite</b> -> entorno.
/// </summary>
public sealed class SqliteConfigurationSource : IConfigurationSource
{
    private readonly ISettingsStore _settingsStore;
    private readonly Action<string, Exception>? _onFailure;

    public SqliteConfigurationSource(ISettingsStore settingsStore, Action<string, Exception>? onFailure = null)
    {
        _settingsStore = settingsStore;
        _onFailure = onFailure;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new SqliteConfigurationProvider(_settingsStore, _onFailure);
}

/// <summary>
/// Lee la tabla <c>setting</c> y la entrega como pares clave/valor. Las claves
/// ya están en el formato de <c>IConfiguration</c> (<c>"Storage:VaultPath"</c>),
/// que es por lo que <see cref="ISettingsStore"/> se diseñó así desde el paso 3:
/// no hay traducción en el medio que pueda equivocarse.
/// </summary>
public sealed class SqliteConfigurationProvider : ConfigurationProvider
{
    private readonly ISettingsStore _settingsStore;
    private readonly Action<string, Exception>? _onFailure;

    public SqliteConfigurationProvider(ISettingsStore settingsStore, Action<string, Exception>? onFailure = null)
    {
        _settingsStore = settingsStore;
        _onFailure = onFailure;
    }

    /// <summary>
    /// <b>Nunca lanza.</b> Es el riesgo que se anotó antes de empezar la fase:
    /// una base corrupta no puede impedir arrancar la app, y el precedente es
    /// T4.4, donde una excepción de arranque se llevó puesta la app entera y
    /// costó nueve días encontrarla. Si la base no abre, esta capa queda vacía y
    /// la app cae a la configuración empaquetada más el entorno — degradada,
    /// pero viva, y con el fallo registrado.
    ///
    /// Un secreto que no se puede descifrar (<c>Value == null</c>: base copiada
    /// de otro perfil, perfil recreado) tampoco se inventa ni revienta —
    /// simplemente no se publica la clave. El efecto es el correcto sin escribir
    /// una línea más: la capa de abajo se ve, y si tampoco la tiene,
    /// <c>StartupConfigurationValidator</c> la reporta como faltante con un
    /// mensaje que se entiende, en vez de una excepción de criptografía.
    /// </summary>
    public override void Load()
    {
        // Diccionario nuevo en una variable local: si GetAllAsync falla a mitad,
        // asignar Data directamente dejaría la configuración a medias. Así, o se
        // publica todo o no se publica nada.
        var loaded = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (SettingEntry entry in _settingsStore.GetAllAsync().GetAwaiter().GetResult())
            {
                if (entry.Value is null) continue;

                loaded[entry.Key] = entry.Value;
            }
        }
        catch (Exception exception)
        {
            _onFailure?.Invoke("SqliteConfigurationProvider.Load", exception);
            return;
        }

        Data = loaded;
    }
}

public static class SqliteConfigurationBuilderExtensions
{
    /// <summary>
    /// Agrega los ajustes de la base como capa de configuración. Hay que
    /// llamarlo <b>después</b> del appsettings empaquetado y del archivo de
    /// usuario, y <b>antes</b> de las variables de entorno.
    /// </summary>
    public static IConfigurationBuilder AddSqliteSettings(
        this IConfigurationBuilder builder,
        ISettingsStore settingsStore,
        Action<string, Exception>? onFailure = null) =>
        builder.Add(new SqliteConfigurationSource(settingsStore, onFailure));
}
