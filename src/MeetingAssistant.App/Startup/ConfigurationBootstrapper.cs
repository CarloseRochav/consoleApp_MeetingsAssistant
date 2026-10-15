using MeetingAssistant.App.Services;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Services;
using MeetingAssistant.Infrastructure.Storage.Postgres;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.App.Startup;

/// <summary>
/// Lo que deja armado el arranque antes de que exista el contenedor: la
/// configuración por capas y las piezas que se usaron para leerla, que después
/// se registran tal cual para que la configuración y la UI hablen con la misma
/// base.
/// </summary>
internal sealed record BootstrapResult(
    IConfiguration Configuration,
    SqliteConnectionFactory ConnectionFactory,
    ISecretProtector SecretProtector,
    SqliteSchemaMigrator SchemaMigrator,
    ISettingsStore SettingsStore,
    PostgresDatabase? Postgres,
    ICloudSettingsStore? CloudSettings,
    bool HistoryInPostgres,
    EditablePromptCatalog PromptCatalog,
    IReadOnlyList<string> MissingSettings,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Arma la configuración en el orden que manda AGENTS.md: empaquetado -&gt;
/// archivo de usuario (legado de T9) -&gt; SQLite local -&gt; nube (si se pide)
/// -&gt; variables de entorno. Corre antes que cualquier UI, así que todo lo
/// que pasa queda en <see cref="BootstrapResult.Diagnostics"/> para el log.
///
/// No lanza por configuración faltante: la deja en
/// <see cref="BootstrapResult.MissingSettings"/>. Una base rota o una nube que
/// no contesta caen a lo local.
/// </summary>
internal static class ConfigurationBootstrapper
{
    public static BootstrapResult Run()
    {
        var diagnostics = new List<string>();

        // La base tiene que estar migrada ANTES de construir la configuración,
        // porque desde el paso 5 la configuración se lee en parte de ella. Estos
        // cuatro no dependen de IConfiguration — la ruta la impone
        // AppPaths.DatabasePath —, así que se construyen a mano y después se
        // registran como instancias: no hay forma de resolverlos por DI si el
        // contenedor todavía no existe.
        var connectionFactory = new SqliteConnectionFactory(AppPaths.DatabasePath);
        ISecretProtector secretProtector = new DpapiSecretProtector();
        var schemaMigrator = new SqliteSchemaMigrator(connectionFactory);
        ISettingsStore settingsStore = new SqliteSettingsStore(connectionFactory, secretProtector);

        // Ninguno de los dos pasos que siguen puede tumbar el arranque. Regla
        // escrita al planificar Fase 5: **una base rota no puede impedir arrancar
        // la app**, y la base está en el camino crítico de la configuración, no
        // sólo en el del historial. El precedente es T4.4, donde una excepción de
        // arranque se llevó puesta la app entera y costó nueve días encontrarla.
        // Si esto falla, se cae a empaquetado + archivo de usuario + entorno.
        try
        {
            diagnostics.Add($"Base de reuniones: {schemaMigrator.Migrate()}");
        }
        catch (Exception exception)
        {
            AppLog.Failure("SqliteSchemaMigrator.Migrate", exception);
            diagnostics.Add($"Base de reuniones: NO se pudo migrar ni leer: {exception.Message}");
        }

        // Migración de una sola vez del appsettings.json de usuario que creó T9.
        // Nunca lanza: devuelve el resultado, y si falló deja el archivo original
        // intacto — mientras ese archivo exista su capa sigue alimentando a la app.
        diagnostics.Add(new UserSettingsImporter(settingsStore)
            .ImportOnceAsync(UserSettingsService.LegacyFilePath)
            .GetAwaiter()
            .GetResult()
            .Describe());

        // Las variables de entorno **siguen arriba de todo**: son la vía de escape
        // cuando un ajuste guardado mal impide arrancar. Si la base pisara al
        // entorno, un valor malo en la base sería irreparable desde afuera.
        //
        // La capa de usuario se conserva por debajo de SQLite aunque el importador
        // se lleve el archivo: es lo que sostiene el caso en que la base NO abrió.
        // SetBasePath no le afecta: LegacyFilePath es una ruta absoluta.
        //
        // La capa empaquetada son dos archivos: appsettings.defaults.json, que
        // está en git y no tiene secretos, y appsettings.json, el real, que sólo
        // existe en las compilaciones Debug. Un paquete para instalar en otra
        // máquina no lleva ninguna clave: se escriben en Configuración.
        IConfigurationBuilder PackagedLayers() => new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(PackagedDefaultsFile, optional: false, reloadOnChange: false)
            .AddJsonFile(PackagedSettingsFile, optional: true, reloadOnChange: false);

        IConfigurationBuilder LocalLayers() => PackagedLayers()
            .AddJsonFile(UserSettingsService.LegacyFilePath, optional: true, reloadOnChange: false)
            .AddSqliteSettings(settingsStore, AppLog.Failure);

        EnsureApiToken(LocalLayers().AddEnvironmentVariables().Build(), settingsStore, diagnostics);
        ReportShadowedPackagedValues(PackagedLayers().Build(), settingsStore, diagnostics);

        // En dos tiempos (Fase 6, paso 3). Primero sólo lo local, porque de ahí
        // sale **dónde está la nube**: los conmutadores y la cadena de conexión no
        // se pueden leer de la base que describen.
        IConfiguration localConfiguration = LocalLayers().AddEnvironmentVariables().Build();

        bool historyInPostgres = string.Equals(
            localConfiguration["Database:Provider"], "Postgres", StringComparison.OrdinalIgnoreCase);
        bool settingsFromCloud = string.Equals(
            localConfiguration["Configuration:Source"], "Database", StringComparison.OrdinalIgnoreCase);

        PostgresDatabase? postgres = historyInPostgres || settingsFromCloud
            ? TryCreatePostgres(localConfiguration)
            : null;

        diagnostics.Add((historyInPostgres, postgres) switch
        {
            (false, _) => "Historial: SQLite local.",
            (true, null) => "Historial: Database:Provider pide Postgres pero no hay una Postgres:ConnectionString utilizable. Se usa SQLite local.",
            (true, not null) => $"Historial: Postgres en {postgres.Description}."
        });

        EditablePromptCatalog promptCatalog = StartPromptCatalog(historyInPostgres, postgres, connectionFactory);

        // Si Configuration:Source lo pide, se intercala la capa de la nube. Si no
        // contesta dentro del plazo, esa capa queda vacía y manda lo local.
        IConfiguration configuration = localConfiguration;
        ICloudSettingsStore? cloudSettings = null;
        if (settingsFromCloud && postgres is not null)
        {
            cloudSettings = new PostgresSettingsStore(postgres);
            TimeSpan timeout = TimeSpan.FromSeconds(
                localConfiguration.GetValue<double?>("Configuration:CloudTimeoutSeconds") is > 0 and var seconds ? seconds : 5);

            // Esta espera sí bloquea el arranque, hasta el plazo: los servicios se
            // construyen con lo que diga la nube (proveedor, modelo). Se mide
            // para que el log diga cuánto costó, sobre todo con Neon dormido.
            var cloudClock = System.Diagnostics.Stopwatch.StartNew();
            IConfigurationRoot withCloud = LocalLayers()
                .AddCloudSettings(cloudSettings, timeout, AppLog.Failure)
                .AddEnvironmentVariables()
                .Build();
            configuration = withCloud;

            diagnostics.Add(
                (withCloud.Providers.OfType<CloudSettingsConfigurationProvider>().Single().LoadedFromCloud
                    ? "Ajustes: primero la nube (Postgres), después lo local."
                    : "Ajustes: la nube no respondió a tiempo; en este arranque se usan sólo los locales.") +
                $" Espera del arranque: {cloudClock.ElapsedMilliseconds} ms.");
        }
        else
        {
            diagnostics.Add(settingsFromCloud
                ? "Ajustes: Configuration:Source pide la nube pero no hay una Postgres:ConnectionString utilizable. Se usan los locales."
                : "Ajustes: locales.");
        }

        // Antes de registrar nada: si falta configuración, es preferible una
        // lista completa aquí que ir descubriéndola clave a clave. No lanza: en
        // una instalación nueva faltar valores es lo normal, y App abre la
        // ventana de primer arranque en vez de la de error.
        IReadOnlyList<string> missingSettings = StartupConfigurationValidator.FindMissing(configuration);
        if (missingSettings.Count > 0)
        {
            diagnostics.Add($"Faltan {missingSettings.Count} ajuste(s) obligatorio(s); se abre la ventana de primer arranque.");
        }

        return new BootstrapResult(
            configuration, connectionFactory, secretProtector, schemaMigrator, settingsStore,
            postgres, cloudSettings, historyInPostgres, promptCatalog, missingSettings, diagnostics);
    }

    private const string PackagedDefaultsFile = "appsettings.defaults.json";
    private const string PackagedSettingsFile = "appsettings.json";

    /// <summary>
    /// El token del endpoint HTTP no es algo que el usuario tenga que inventar:
    /// si ninguna capa lo trae (una instalación nueva), se genera uno aleatorio
    /// y se guarda cifrado en la base. Se ve y se cambia en Configuración.
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
            // Sin token, el validador lo reporta como faltante: el usuario lo ve.
            AppLog.Failure("ConfigurationBootstrapper.EnsureApiToken", exception);
        }
    }

    /// <summary>
    /// La base local está por encima del appsettings empaquetado, así que un
    /// valor guardado alguna vez desde Configuración tapa para siempre al del
    /// archivo. Ya pasó: se rotó la clave de Deepgram en el appsettings.json y
    /// la app siguió usando la vieja, guardada en la base, sin decir nada. Esto
    /// lo deja en el log. Sólo nombra la clave: nunca escribe un valor.
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

    /// <summary>
    /// Prompts editables (Fase 2, paso 6): el texto del código más las ediciones
    /// guardadas, en la misma base que el historial. <b>No se espera esta
    /// lectura.</b> Antes se esperaba hasta 5 s, en serie con los 5 s de los
    /// ajustes de la nube: con Neon dormido, el arranque podía quedarse unos
    /// 10 s sin ventana. Arranca antes que la lectura de la nube, para que las
    /// dos esperen juntas a que la base despierte. Mientras no termine, el
    /// catálogo sirve los originales del código.
    /// </summary>
    private static EditablePromptCatalog StartPromptCatalog(
        bool historyInPostgres,
        PostgresDatabase? postgres,
        SqliteConnectionFactory connectionFactory)
    {
        IPromptStore promptStore = historyInPostgres && postgres is not null
            ? new PostgresPromptStore(postgres)
            : new SqlitePromptStore(connectionFactory);
        var promptCatalog = new EditablePromptCatalog(new BuiltInPromptCatalog(), promptStore, AppLog.Failure);
        var promptClock = System.Diagnostics.Stopwatch.StartNew();
        _ = Task.Run(async () =>
        {
            await promptCatalog.RefreshAsync();
            AppLog.Diagnostic(promptCatalog.LoadedFromStore
                ? $"Prompts: activos {string.Join(", ", promptCatalog.GetAll().Select(prompt => $"{prompt.Id} {prompt.Version}"))} " +
                  $"(leídos en {promptClock.ElapsedMilliseconds} ms)."
                : "Prompts: no se pudieron leer las ediciones; se usan los originales del código.");
        });

        return promptCatalog;
    }

    /// <summary>
    /// La base de Postgres, o null si no hay una cadena de conexión utilizable.
    /// <b>No abre ninguna conexión</b>: el esquema se asegura la primera vez que
    /// algo la usa. Un valor mal escrito queda en el log y se cae a lo local.
    /// </summary>
    private static PostgresDatabase? TryCreatePostgres(IConfiguration configuration)
    {
        string? connectionString = configuration["Postgres:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString)) return null;

        try
        {
            return new PostgresDatabase(connectionString);
        }
        catch (Exception exception)
        {
            AppLog.Failure("PostgresDatabase", exception);
            return null;
        }
    }
}
