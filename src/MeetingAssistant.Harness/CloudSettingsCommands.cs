using System.Diagnostics;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Infrastructure.Storage.Postgres;
using Microsoft.Extensions.Configuration;
using Npgsql;

/// <summary>
/// Los ajustes compartidos en la nube (Fase 6, paso 3).
///
/// <c>--verify-cloud-settings</c> corre contra la base configurada, en un
/// esquema propio que borra al terminar. <c>--set-cloud-setting</c> y
/// <c>--list-cloud-settings</c> tocan los ajustes <b>reales</b>.
/// </summary>
internal static class CloudSettingsCommands
{
    public static async Task<int> VerifyAsync(IConfiguration configuration)
    {
        Console.WriteLine("=== Autotest de ajustes en la nube ===");

        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        // --- Que puede ir a la nube: no necesita base -------------------------
        Console.WriteLine($"{Environment.NewLine}--- Que claves pueden vivir en la nube ---");
        Check("los precios, el proveedor y los modelos si",
            new[] { "Pricing:Deepgram:PerMinute", "Pricing:Azure AI Foundry:grok-4.6:InputPerMillion", "Llm:Provider", "Gemini:Model", "AzureFoundry:Deployment", "AzureFoundry:Endpoint" }
                .All(SettingKeyPolicy.CanLiveInCloud));
        Check("ninguna credencial, este en la seccion que este",
            new[] { "Gemini:ApiKey", "AzureFoundry:ApiKey", "Deepgram:ApiKey", "Api:AuthToken", "Pricing:Algo:Secret", "Llm:Password" }
                .All(key => !SettingKeyPolicy.CanLiveInCloud(key)));
        Check("nada que diga donde esta la nube",
            new[] { "Postgres:ConnectionString", "Database:Provider", "Configuration:Source", "Configuration:CloudTimeoutSeconds", "AzureStorage:ConnectionString" }
                .All(key => !SettingKeyPolicy.CanLiveInCloud(key)));
        Check("nada propio de esta maquina",
            new[] { "Storage:VaultPath", "Storage:SubFolder", "Api:Port", "Hotkey:Key", "AzureStorage:AudioContainer" }
                .All(key => !SettingKeyPolicy.CanLiveInCloud(key)));
        Check("ni claves vacias o sin seccion",
            new[] { "", "   ", "SinSeccion", ":Hoja" }.All(key => !SettingKeyPolicy.CanLiveInCloud(key)));

        // --- El plazo: tampoco necesita base ----------------------------------
        Console.WriteLine($"{Environment.NewLine}--- La nube no puede demorar ni tumbar el arranque ---");
        var local = new Dictionary<string, string?>
        {
            ["Gemini:Model"] = "modelo-local",
            ["Pricing:Deepgram:PerMinute"] = "0.001",
            ["Storage:VaultPath"] = @"C:\vault-local",
            ["Gemini:ApiKey"] = "clave-local",
            ["Postgres:ConnectionString"] = "cadena-local"
        };

        var slowFailures = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        IConfigurationRoot slow = new ConfigurationBuilder()
            .AddInMemoryCollection(local)
            .AddCloudSettings(new SlowCloudSettingsStore(TimeSpan.FromSeconds(30)), TimeSpan.FromSeconds(1), (operation, _) => slowFailures.Add(operation))
            .Build();
        stopwatch.Stop();
        Check($"una nube que tarda 30 s no retiene el arranque mas que el plazo de 1 s (tardo {stopwatch.Elapsed.TotalSeconds:F1} s)",
            stopwatch.Elapsed < TimeSpan.FromSeconds(4));
        Check("y la app queda con lo local", slow["Gemini:Model"] == "modelo-local");
        Check("el fallo queda registrado y la capa lo declara",
            slowFailures.Count == 1 && !slow.Providers.OfType<CloudSettingsConfigurationProvider>().Single().LoadedFromCloud);

        var unreachableFailures = new List<string>();
        bool built;
        IConfigurationRoot? unreachableConfig = null;
        try
        {
            await using var unreachable = new PostgresDatabase("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x");
            unreachableConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(local)
                .AddCloudSettings(new PostgresSettingsStore(unreachable), TimeSpan.FromSeconds(5), (operation, _) => unreachableFailures.Add(operation))
                .Build();
            built = true;
        }
        catch (Exception) { built = false; }
        Check("una base inalcanzable no impide construir la configuracion", built);
        Check("y deja ver lo local, con el fallo registrado",
            unreachableConfig?["Pricing:Deepgram:PerMinute"] == "0.001" && unreachableFailures.Count == 1);

        // --- Contra Postgres, en un esquema descartable -------------------------
        string? connectionString = configuration["Postgres:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Falta Postgres:ConnectionString en la configuracion del harness. El store NO se probo.");
            return 2;
        }

        string schema = $"ma_selftest_{Guid.NewGuid():N}";
        await using var database = new PostgresDatabase(connectionString, schema);
        Console.WriteLine($"{Environment.NewLine}--- El almacen ({database.Description}) ---");
        try
        {
            await database.MigrateAsync();
            var store = new PostgresSettingsStore(database);

            Check("vacio al empezar", (await store.GetAllAsync()).Count == 0);

            await store.SetAsync("Gemini:Model", "modelo-nube");
            await store.SetAsync("Pricing:Deepgram:PerMinute", "0.0052");
            Check("guarda y devuelve", await store.GetAsync("Gemini:Model") == "modelo-nube");
            Check("lista lo guardado, nunca marcado como secreto",
                (await store.GetAllAsync()) is [{ Key: "Gemini:Model", IsSecret: false }, { Key: "Pricing:Deepgram:PerMinute", Value: "0.0052" }]);

            await store.SetAsync("Gemini:Model", "modelo-nube-2");
            Check("guardar de nuevo reemplaza",
                await store.GetAsync("Gemini:Model") == "modelo-nube-2" && (await store.GetAllAsync()).Count == 2);

            await store.SetAsync("Pricing:Deepgram:PerMinute", "  ");
            Check("guardar vacio borra la clave (vuelve a mandar lo local)",
                await store.GetAsync("Pricing:Deepgram:PerMinute") is null && (await store.GetAllAsync()).Count == 1);

            async Task<bool> Rejects(string key, bool isSecret = false)
            {
                try { await store.SetAsync(key, "valor", isSecret); return false; }
                catch (InvalidOperationException) { return true; }
            }
            Check("rechaza una credencial", await Rejects("Gemini:ApiKey"));
            Check("rechaza la direccion de la propia base", await Rejects("Postgres:ConnectionString"));
            Check("rechaza un ajuste de esta maquina", await Rejects("Storage:VaultPath"));
            Check("rechaza cualquier cosa marcada como secreta, aunque la clave sea inocente", await Rejects("Gemini:Model", isSecret: true));
            Check("y nada de eso llego a la tabla", (await store.GetAllAsync()).Count == 1);

            // Filas metidas a mano en la tabla, saltandose el store. Es el caso
            // de alguien con acceso a la base: no puede cambiarle a la app una
            // credencial ni adonde se conecta.
            await using (NpgsqlConnection connection = await database.OpenAsync())
            await using (var rogue = new NpgsqlCommand(
                """
                insert into setting(key, value, updated_at_utc) values
                    ('Gemini:ApiKey', 'clave-intrusa', now()),
                    ('Postgres:ConnectionString', 'cadena-intrusa', now()),
                    ('Storage:VaultPath', 'C:\vault-intruso', now());
                """,
                connection))
            {
                await rogue.ExecuteNonQueryAsync();
            }

            Check("una fila intrusa no sale por el store",
                (await store.GetAllAsync()).Count == 1 && await store.GetAsync("Gemini:ApiKey") is null);

            // --- La pila completa ------------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- La pila: local -> nube -> entorno ---");
            await store.SetAsync("Llm:Provider", "ProveedorNube");
            var environment = new Dictionary<string, string?> { ["Llm:Provider"] = "ProveedorEntorno" };

            IConfigurationRoot stacked = new ConfigurationBuilder()
                .AddInMemoryCollection(local)
                .AddCloudSettings(store, TimeSpan.FromSeconds(10))
                .AddInMemoryCollection(environment)
                .Build();

            Check("la capa declara que leyo de la nube",
                stacked.Providers.OfType<CloudSettingsConfigurationProvider>().Single().LoadedFromCloud);
            Check("la nube pisa a lo local", stacked["Gemini:Model"] == "modelo-nube-2");
            Check("lo que la nube no tiene sale de lo local", stacked["Pricing:Deepgram:PerMinute"] == "0.001");
            Check("el entorno pisa a la nube", stacked["Llm:Provider"] == "ProveedorEntorno");
            Check("las filas intrusas no pisan nada: credencial, cadena de conexion y vault siguen siendo los locales",
                stacked["Gemini:ApiKey"] == "clave-local" &&
                stacked["Postgres:ConnectionString"] == "cadena-local" &&
                stacked["Storage:VaultPath"] == @"C:\vault-local");

            await store.SetAsync("Gemini:Model", "modelo-nube-3");
            stacked.Reload();
            Check("Reload() trae el valor nuevo", stacked["Gemini:Model"] == "modelo-nube-3");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"El autotest reviento: {exception}");
            failures++;
        }
        finally
        {
            try
            {
                await database.DropSchemaAsync();
                Console.WriteLine($"{Environment.NewLine}Esquema de prueba {schema} borrado.");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"NO se pudo borrar el esquema de prueba {schema}: {exception.Message}");
                failures++;
            }
        }

        Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Escribe (o borra, sin valor) un ajuste compartido en la base real.</summary>
    public static async Task<int> SetAsync(IConfiguration configuration, string key, string? value)
    {
        if (!SettingKeyPolicy.CanLiveInCloud(key))
        {
            Console.Error.WriteLine(
                $"'{key}' no se guarda en la nube: es un secreto, o un ajuste propio de esta maquina. Usa --set-setting para guardarlo localmente.");
            return 1;
        }

        if (await OpenAsync(configuration) is not { } database) return 1;
        await using (database)
        {
            var store = new PostgresSettingsStore(database);
            await store.SetAsync(key, value);
            Console.WriteLine(string.IsNullOrWhiteSpace(value)
                ? $"Borrado de la nube: {key}"
                : $"Guardado en la nube ({database.Description}): {key} = {await store.GetAsync(key)}");
            Console.WriteLine("Tiene efecto en las maquinas con Configuration:Source = Database, al reiniciar la app.");
        }

        return 0;
    }

    public static async Task<int> ListAsync(IConfiguration configuration)
    {
        if (await OpenAsync(configuration) is not { } database) return 1;
        await using (database)
        {
            IReadOnlyList<SettingEntry> entries = await new PostgresSettingsStore(database).GetAllAsync();
            Console.WriteLine($"=== Ajustes en la nube ({database.Description}) ===");
            foreach (SettingEntry entry in entries)
            {
                Console.WriteLine($"  {entry.Key,-52} {entry.Value}");
            }
            if (entries.Count == 0) Console.WriteLine("  (ninguno: manda lo local en todas las claves)");
        }

        return 0;
    }

    private static Task<PostgresDatabase?> OpenAsync(IConfiguration configuration)
    {
        string? connectionString = configuration["Postgres:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Falta Postgres:ConnectionString en la configuracion del harness.");
            return Task.FromResult<PostgresDatabase?>(null);
        }

        return Task.FromResult<PostgresDatabase?>(new PostgresDatabase(connectionString));
    }

    /// <summary>Una nube que contesta tarde, para probar el plazo sin depender de la red.</summary>
    private sealed class SlowCloudSettingsStore : ICloudSettingsStore
    {
        private readonly TimeSpan _delay;

        public SlowCloudSettingsStore(TimeSpan delay) => _delay = delay;

        public async Task<IReadOnlyList<SettingEntry>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return [new SettingEntry("Gemini:Model", "llego-tarde", false, DateTimeOffset.UtcNow)];
        }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task SetAsync(string key, string? value, bool isSecret = false, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
