using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;
using MeetingAssistant.Infrastructure.Cost;
using MeetingAssistant.Infrastructure.Storage.Postgres;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Autotest de los prompts editables (Fase 2, paso 6). El mismo recorrido corre
/// contra un SQLite temporal y, si hay cadena de conexion, contra Postgres en un
/// esquema descartable.
/// </summary>
internal static class PromptCommands
{
    public static async Task<int> VerifyAsync(IConfiguration configuration)
    {
        int failures = 0;

        string sqlitePath = Path.Combine(Path.GetTempPath(), $"ma-prompts-{Guid.NewGuid():N}.db");
        Console.WriteLine($"=== Prompts editables: SQLite ==={Environment.NewLine}{sqlitePath}");
        try
        {
            var factory = new SqliteConnectionFactory(sqlitePath);
            new SqliteSchemaMigrator(factory).Migrate();
            failures += await RunAsync(new SqlitePromptStore(factory));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string leftover in new[] { sqlitePath, sqlitePath + "-wal", sqlitePath + "-shm" })
            {
                try { if (File.Exists(leftover)) File.Delete(leftover); } catch { /* best effort */ }
            }
        }

        // --- Sin base: los originales, sin lanzar ------------------------------
        Console.WriteLine($"{Environment.NewLine}=== Prompts editables: base inalcanzable ===");
        int unreachableFailures = 0;
        void CheckUnreachable(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) unreachableFailures++;
        }
        var logged = new List<string>();
        var broken = new EditablePromptCatalog(
            new BuiltInPromptCatalog(),
            new SqlitePromptStore(new SqliteConnectionFactory(@"\\?\Z:\no-existe\jamas\meetings.db")),
            (operation, _) => logged.Add(operation));
        bool threw = false;
        try { await broken.RefreshAsync(); } catch (Exception) { threw = true; }
        CheckUnreachable("con la base rota, refrescar no lanza", !threw);
        CheckUnreachable("y quedan los originales del codigo, con el fallo registrado",
            !broken.LoadedFromStore && logged.Count == 1 &&
            broken.GetAll().Select(prompt => prompt.Version)
                .SequenceEqual(new BuiltInPromptCatalog().GetAll().Select(prompt => prompt.Version)));
        bool saveThrew = false;
        try { await broken.SaveEditAsync(ReportExtractionPrompt.Id, "texto nuevo", null); }
        catch (Exception) { saveThrew = true; }
        CheckUnreachable("guardar contra una base rota SI lanza (la pantalla lo muestra), y no cambia el prompt activo",
            saveThrew && broken.Default.Version == ReportExtractionPrompt.Version);
        failures += unreachableFailures;

        // --- Postgres -------------------------------------------------------
        string? connectionString = configuration["Postgres:ConnectionString"];
        Console.WriteLine($"{Environment.NewLine}=== Prompts editables: Postgres ===");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Falta Postgres:ConnectionString. Postgres NO se probo.");
            return 2;
        }

        string schema = $"ma_selftest_{Guid.NewGuid():N}";
        await using var database = new PostgresDatabase(connectionString, schema);
        Console.WriteLine(database.Description);
        try
        {
            await database.MigrateAsync();
            failures += await RunAsync(new PostgresPromptStore(database));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Reviento contra Postgres: {exception}");
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

    private static async Task<int> RunAsync(IPromptStore store)
    {
        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        try
        {
            var builtIn = new BuiltInPromptCatalog();
            var catalog = new EditablePromptCatalog(builtIn, store);
            await catalog.RefreshAsync();
            string id = FunctionalSpecPrompt.Id;
            PromptDefinition original = builtIn.GetById(id);

            // --- Sin ediciones ----------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Sin ediciones ---");
            Check("sin ediciones, cada prompt es el del codigo",
                catalog.LoadedFromStore &&
                catalog.GetAll().Zip(builtIn.GetAll()).All(pair => pair.First == pair.Second));
            Check("el historial tiene solo el original, activo",
                catalog.GetHistory(id) is [{ IsOriginal: true, IsActive: true } only] && only.Version == original.Version);
            Check("un id que no existe lanza igual que el catalogo del codigo",
                Throws(() => catalog.GetById("no-existe")));

            // --- Editar -----------------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Editar ---");
            PromptDefinition first = await catalog.SaveEditAsync(id, "Texto editado uno.", "primera prueba");
            Check($"la primera edicion es {original.Version}.1 y queda activa",
                first.Version == $"{original.Version}.1" && catalog.GetById(id).SystemPrompt == "Texto editado uno.");
            Check("conserva id, nombre y tipo de salida del original",
                first.Id == original.Id && first.DisplayName == original.DisplayName && first.OutputKind == original.OutputKind);
            Check("los otros prompts no cambian",
                catalog.GetById(FeatureHandoffPrompt.Id) == builtIn.GetById(FeatureHandoffPrompt.Id));

            PromptDefinition second = await catalog.SaveEditAsync(id, "  Texto editado dos.\r\n", null);
            Check("la segunda es .2, con el texto sin espacios de mas", second.Version == $"{original.Version}.2" && second.SystemPrompt == "Texto editado dos.");

            PromptDefinition unchanged = await catalog.SaveEditAsync(id, "Texto editado dos.\n", "no deberia guardarse");
            Check("guardar el mismo texto no crea una version nueva", unchanged.Version == second.Version &&
                (await store.GetAllAsync()).Count == 2);

            Check("guardar un texto vacio lanza", await ThrowsAsync(() => catalog.SaveEditAsync(id, "   ", null)));

            IReadOnlyList<PromptVersionEntry> history = catalog.GetHistory(id);
            Check("el historial va de la mas nueva al original, con la activa marcada",
                history.Select(entry => entry.Version).SequenceEqual([second.Version, first.Version, original.Version]) &&
                history.Count(entry => entry.IsActive) == 1 && history[0].IsActive);
            Check("la nota de una version se conserva", history[1].Note == "primera prueba");

            // Otra instancia leyendo la misma base: lo que ve otra maquina.
            var otherMachine = new EditablePromptCatalog(builtIn, store);
            await otherMachine.RefreshAsync();
            Check("otra instancia que lee la base ve la misma version activa", otherMachine.GetById(id) == catalog.GetById(id));

            PromptDefinition third = await otherMachine.SaveEditAsync(id, "Texto editado tres, desde la otra.", null);
            PromptDefinition fourth = await catalog.SaveEditAsync(id, "Texto editado cuatro, desde la primera.", null);
            Check("dos instancias que guardan por turnos no repiten numero: releen antes de numerar",
                third.Version == $"{original.Version}.3" && fourth.Version == $"{original.Version}.4");

            // --- Volver al original -----------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Volver al original ---");
            PromptDefinition restored = await catalog.RestoreOriginalAsync(id);
            Check("volver al original deja activo el texto del codigo", restored == original && catalog.GetById(id) == original);
            Check("y las ediciones siguen en el historial",
                catalog.GetHistory(id).Count == 5 && catalog.GetHistory(id).Single(entry => entry.IsActive).IsOriginal);
            int rowsBefore = (await store.GetAllAsync()).Count;
            await catalog.RestoreOriginalAsync(id);
            Check("volver al original estando en el original no guarda nada", (await store.GetAllAsync()).Count == rowsBefore);

            PromptDefinition afterRestore = await catalog.SaveEditAsync(id, "Texto editado cinco.", null);
            Check("editar despues de volver al original sigue la numeracion", afterRestore.Version == $"{original.Version}.5");

            // --- La etiqueta de version no se repite -----------------------
            bool duplicateRejected = await ThrowsAsync(() => store.SaveAsync(
                new PromptEdit(id, original.Version, afterRestore.Version, "otro texto", null, DateTimeOffset.UtcNow)));
            Check("la base rechaza dos textos con la misma version", duplicateRejected);

            // --- Una version nueva del prompt en el codigo -----------------
            Console.WriteLine($"{Environment.NewLine}--- Cuando el codigo trae una version nueva ---");
            var upgradedCode = new UpgradedBuiltInCatalog(builtIn, id, "v99", "Texto nuevo del codigo.");
            var afterUpgrade = new EditablePromptCatalog(upgradedCode, store);
            await afterUpgrade.RefreshAsync();
            Check("las ediciones hechas sobre la version vieja no tapan la nueva del codigo",
                afterUpgrade.GetById(id) is { Version: "v99", SystemPrompt: "Texto nuevo del codigo." });
            PromptDefinition onNewBase = await afterUpgrade.SaveEditAsync(id, "Edicion sobre la nueva.", null);
            Check("y editar sobre la nueva empieza su propia numeracion", onNewBase.Version == "v99.1");

            // --- El extractor usa la version activa --------------------------
            Console.WriteLine($"{Environment.NewLine}--- El extractor ---");
            var llm = new CapturingLlmClient();
            var extractor = new LlmReportExtractor(
                llm, new ConfigPricingCostEstimator(new ConfigurationBuilder().Build()), catalog);
            ExtractionResult result = await extractor.ExtractAsync("Transcript de prueba.", id);
            Check("el texto que recibe el modelo es el de la version activa",
                llm.LastPrompt!.StartsWith("Texto editado cinco.", StringComparison.Ordinal));
            Check("y el reporte queda marcado con esa version",
                result.Metadata.PromptVersion == $"{original.Version}.5" && result.Prompt.Version == $"{original.Version}.5");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"El autotest reviento: {exception}");
            failures++;
        }

        return failures;
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (Exception) { return true; }
    }

    private static async Task<bool> ThrowsAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (Exception) { return true; }
    }

    /// <summary>El catalogo del codigo con un prompt subido de version, como tras una actualizacion.</summary>
    private sealed class UpgradedBuiltInCatalog : IPromptCatalog
    {
        private readonly IReadOnlyList<PromptDefinition> _all;

        public UpgradedBuiltInCatalog(IPromptCatalog inner, string id, string version, string text)
        {
            _all = inner.GetAll()
                .Select(prompt => prompt.Id == id ? prompt with { Version = version, SystemPrompt = text } : prompt)
                .ToList();
            Default = _all.Single(prompt => prompt.Id == inner.Default.Id);
        }

        public PromptDefinition Default { get; }

        public IReadOnlyList<PromptDefinition> GetAll() => _all;

        public PromptDefinition GetById(string id) =>
            _all.FirstOrDefault(prompt => string.Equals(prompt.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No existe un prompt con id '{id}'.", nameof(id));
    }

    private sealed class CapturingLlmClient : ILlmClient
    {
        public string Provider => "Prueba";

        public string Model => "modelo-de-prueba";

        public string? LastPrompt { get; private set; }

        public Task<LlmProviderResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            LastPrompt = request.Prompt;
            return Task.FromResult(new LlmProviderResponse("# Especificacion de prueba", 10, 20));
        }
    }
}
