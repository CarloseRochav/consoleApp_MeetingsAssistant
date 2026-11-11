using MeetingAssistant.App.Services;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.App.Startup;

/// <summary>
/// Lo que deja armado el arranque antes de que exista el contenedor: la configuración por capas y
/// las piezas que se usaron para leerla, que después se registran tal cual para que la
/// configuración y la UI hablen con la misma base.
/// </summary>
internal sealed record BootstrapResult(
    IConfiguration Configuration,
    SqliteConnectionFactory ConnectionFactory,
    ISecretProtector SecretProtector,
    SqliteSchemaMigrator SchemaMigrator,
    ISettingsStore SettingsStore,
    IReadOnlyList<string> MissingSettings,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Arma la configuración en el orden que manda AGENTS.md: empaquetado -&gt; archivo de usuario
/// (legado de T9) -&gt; SQLite local -&gt; variables de entorno. Desde el paso B de la Fase 8 no hay
/// capa de la nube: los ajustes compartidos (proveedor, modelo, términos clave) viven en la API de
/// DevLog y los usa el servidor, no esta máquina. Corre antes que cualquier UI, así que todo lo que
/// pasa queda en <see cref="BootstrapResult.Diagnostics"/> para el log.
///
/// No lanza por configuración faltante: la deja en <see cref="BootstrapResult.MissingSettings"/>.
/// </summary>
internal static class ConfigurationBootstrapper
{
    public static BootstrapResult Run()
    {
        var diagnostics = new List<string>();

        // La base tiene que estar migrada ANTES de construir la configuración, porque la
        // configuración se lee en parte de ella. Estos cuatro no dependen de IConfiguration (la
        // ruta la impone AppPaths.DatabasePath): se construyen a mano y se registran como instancias.
        var connectionFactory = new SqliteConnectionFactory(AppPaths.DatabasePath);
        ISecretProtector secretProtector = new DpapiSecretProtector();
        var schemaMigrator = new SqliteSchemaMigrator(connectionFactory);
        ISettingsStore settingsStore = new SqliteSettingsStore(connectionFactory, secretProtector);

        // Regla de la Fase 5: una base rota no puede impedir arrancar la app (T4.4). Si esto falla,
        // se cae a empaquetado + archivo de usuario + entorno.
        try
        {
            diagnostics.Add($"Base local de ajustes: {schemaMigrator.Migrate()}");
        }
        catch (Exception exception)
        {
            AppLog.Failure("SqliteSchemaMigrator.Migrate", exception);
            diagnostics.Add($"Base local de ajustes: NO se pudo migrar ni leer: {exception.Message}");
        }

        // Migración de una sola vez del appsettings.json de usuario que creó T9. Nunca lanza.
        diagnostics.Add(new UserSettingsImporter(settingsStore)
            .ImportOnceAsync(UserSettingsService.LegacyFilePath)
            .GetAwaiter()
            .GetResult()
            .Describe());

        // Las variables de entorno **siguen arriba de todo**: son la vía de escape cuando un ajuste
        // guardado mal impide arrancar. La capa empaquetada son dos archivos: appsettings.defaults.json
        // (en git, sin secretos, con la dirección de la API) y appsettings.json (sólo Debug).
        IConfigurationBuilder PackagedLayers() => new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(PackagedDefaultsFile, optional: false, reloadOnChange: false)
            .AddJsonFile(PackagedSettingsFile, optional: true, reloadOnChange: false);

        IConfigurationBuilder AllLayers() => PackagedLayers()
            .AddJsonFile(UserSettingsService.LegacyFilePath, optional: true, reloadOnChange: false)
            .AddSqliteSettings(settingsStore, AppLog.Failure)
            .AddEnvironmentVariables();

        EnsureApiToken(AllLayers().Build(), settingsStore, diagnostics);
        ReportShadowedPackagedValues(PackagedLayers().Build(), settingsStore, diagnostics);
        ReportLeftoverProviderSettings(settingsStore, diagnostics);

        IConfiguration configuration = AllLayers().Build();
        diagnostics.Add($"API de DevLog: {configuration["DevLog:BaseUrl"] ?? "(sin DevLog:BaseUrl)"}.");

        IReadOnlyList<string> missingSettings = StartupConfigurationValidator.FindMissing(configuration);
        if (missingSettings.Count > 0)
        {
            diagnostics.Add($"Faltan {missingSettings.Count} ajuste(s) obligatorio(s); se abre la ventana de primer arranque.");
        }

        return new BootstrapResult(configuration, connectionFactory, secretProtector, schemaMigrator, settingsStore, missingSettings, diagnostics);
    }

    private const string PackagedDefaultsFile = "appsettings.defaults.json";
    private const string PackagedSettingsFile = "appsettings.json";

    /// <summary>
    /// Las claves que el modo local guardaba en esta máquina y que desde el paso B no se usan:
    /// claves de proveedor, cadenas de conexión, conmutadores de la nube. Se dice en el log (sólo el
    /// nombre, nunca el valor). No se borran solas: rotarlas y limpiar es paso del corte del dueño.
    /// </summary>
    private static readonly string[] LeftoverProviderKeys =
    [
        "Deepgram:ApiKey", "Gemini:ApiKey", "AzureFoundry:ApiKey", "Postgres:ConnectionString",
        "AzureStorage:ConnectionString", "Database:Provider", "Configuration:Source"
    ];

    private static void ReportLeftoverProviderSettings(ISettingsStore settingsStore, List<string> diagnostics)
    {
        try
        {
            string[] leftover = settingsStore.GetAllAsync().GetAwaiter().GetResult()
                .Where(entry => LeftoverProviderKeys.Contains(entry.Key, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry.Value))
                .Select(entry => entry.Key)
                .ToArray();
            if (leftover.Length > 0)
            {
                diagnostics.Add(
                    $"Quedan en la base local {leftover.Length} ajuste(s) del modo local que ya no se usan: {string.Join(", ", leftover)}. " +
                    "Se borran desde Configuración (\"Borrar las claves viejas de esta máquina\").");
            }
        }
        catch (Exception exception)
        {
            AppLog.Failure("ConfigurationBootstrapper.ReportLeftoverProviderSettings", exception);
        }
    }

    /// <summary>
    /// El token del endpoint HTTP no es algo que el usuario tenga que inventar: si ninguna capa lo
    /// trae (una instalación nueva), se genera uno aleatorio y se guarda cifrado en la base.
    /// </summary>
    private static void EnsureApiToken(IConfiguration configuration, ISettingsStore settingsStore, List<string> diagnostics)
    {
        string? current = configuration["Api:AuthToken"];
        if (!string.IsNullOrWhiteSpace(current) && !current.StartsWith('<')) return;

        try
        {
            string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            settingsStore.SetAsync("Api:AuthToken", token, SettingKeyPolicy.IsSecret("Api:AuthToken")).GetAwaiter().GetResult();
            diagnostics.Add("Api:AuthToken no estaba configurado: se generó uno nuevo y se guardó cifrado en la base.");
        }
        catch (Exception exception)
        {
            AppLog.Failure("ConfigurationBootstrapper.EnsureApiToken", exception);
        }
    }

    /// <summary>
    /// La base local está por encima del appsettings empaquetado, así que un valor guardado alguna
    /// vez desde Configuración tapa para siempre al del archivo. Esto lo deja en el log. Sólo nombra
    /// la clave: nunca escribe un valor.
    /// </summary>
    private static void ReportShadowedPackagedValues(IConfiguration packaged, ISettingsStore settingsStore, List<string> diagnostics)
    {
        try
        {
            foreach (SettingEntry entry in settingsStore.GetAllAsync().GetAwaiter().GetResult())
            {
                string? packagedValue = packaged[entry.Key];
                if (string.IsNullOrWhiteSpace(packagedValue) || packagedValue.StartsWith('<')) continue;
                if (string.Equals(packagedValue, entry.Value, StringComparison.Ordinal)) continue;

                diagnostics.Add(
                    $"Ojo: {entry.Key} guardado en la base (Configuración) tapa un valor distinto del appsettings empaquetado. " +
                    "Manda el de la base; para usar el del archivo, cámbialo en Configuración o bórralo con --set-setting.");
            }
        }
        catch (Exception exception)
        {
            AppLog.Failure("ConfigurationBootstrapper.ReportShadowedPackagedValues", exception);
        }
    }
}
