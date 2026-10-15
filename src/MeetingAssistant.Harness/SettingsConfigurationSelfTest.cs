using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;
using MeetingAssistant.Infrastructure;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Cost;
using MeetingAssistant.Infrastructure.Llm;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using MeetingAssistant.Infrastructure.Transcription;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

/// <summary>--verify-settings-config: las capas de configuracion, sobre bases temporales. Offline.</summary>
internal static class SettingsConfigurationSelfTest
{
    /// <summary>
    /// Prueba la configuracion en base (Fase 5, paso 5) sobre una base
    /// <b>temporal</b>: la precedencia entre capas y la migracion de una sola vez del
    /// appsettings.json de usuario que creo T9. Nunca toca la base real ni el
    /// archivo real del usuario.
    ///
    /// Lo que importa aca no es que el provider lea filas — eso es trivial — sino
    /// dos cosas que se rompen sin hacer ruido:
    ///
    /// - <b>Que las variables de entorno sigan mandando sobre la base.</b> Es la via
    ///   de escape que queda cuando un ajuste guardado mal impide arrancar. Si la
    ///   base pisara al entorno, un valor malo en la base seria irreparable desde
    ///   afuera de la app, y el sintoma seria una app que no abre y no se puede
    ///   arreglar.
    /// - <b>Que un import a medias no destruya el archivo del usuario.</b> El
    ///   importador borra el original; si lo borrara sin haber verificado la
    ///   relectura, un fallo de DPAPI se llevaria la configuracion entera.
    /// </summary>
    public static int Run()
    {
        string temporaryPath = Path.Combine(Path.GetTempPath(), $"ma-config-{Guid.NewGuid():N}.db");
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ma-config-{Guid.NewGuid():N}");
        Console.WriteLine($"=== Autotest de configuracion en base ==={Environment.NewLine}{temporaryPath}");

        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        // Nombre de variable de entorno con la forma Seccion__Clave. Se limpia en el
        // finally: dejarla puesta contaminaria cualquier corrida posterior del
        // harness en la misma sesion de shell.
        const string environmentVariableName = "Storage__SubFolder";

        try
        {
            Directory.CreateDirectory(temporaryDirectory);

            var factory = new SqliteConnectionFactory(temporaryPath);
            new SqliteSchemaMigrator(factory).Migrate();
            ISettingsStore store = new SqliteSettingsStore(factory, new DpapiSecretProtector());

            // La capa "empaquetada", en memoria: es el appsettings.json de fabrica.
            var packaged = new Dictionary<string, string?>
            {
                ["Storage:VaultPath"] = @"C:\empaquetado\vault",
                ["Storage:SubFolder"] = "EmpaquetadoSubFolder",
                ["Llm:Provider"] = "Gemini",
                ["Deepgram:ApiKey"] = "clave-empaquetada"
            };

            IConfigurationRoot Build(ISettingsStore settingsStore, Action<string, Exception>? onFailure = null) =>
                new ConfigurationBuilder()
                    .AddInMemoryCollection(packaged)
                    .AddSqliteSettings(settingsStore, onFailure)
                    .AddEnvironmentVariables()
                    .Build();

            Console.WriteLine($"{Environment.NewLine}--- Precedencia entre capas ---");

            Check("sin fila en la base, manda el valor empaquetado",
                Build(store)["Storage:VaultPath"] == @"C:\empaquetado\vault");

            store.SetAsync("Storage:VaultPath", @"D:\base\vault").GetAwaiter().GetResult();
            Check("una fila de la base pisa al empaquetado",
                Build(store)["Storage:VaultPath"] == @"D:\base\vault");

            Check("una clave que la base no tiene sigue cayendo al empaquetado",
                Build(store)["Llm:Provider"] == "Gemini");

            // El corazon del paso: el entorno queda ARRIBA de la base.
            store.SetAsync("Storage:SubFolder", "SubFolderDeLaBase").GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable(environmentVariableName, "SubFolderDelEntorno");
            Check("una variable de entorno Seccion__Clave pisa a la base",
                Build(store)["Storage:SubFolder"] == "SubFolderDelEntorno");
            Environment.SetEnvironmentVariable(environmentVariableName, null);
            Check("sin la variable de entorno, vuelve a mandar la base",
                Build(store)["Storage:SubFolder"] == "SubFolderDeLaBase");

            Console.WriteLine($"{Environment.NewLine}--- Secretos a traves de IConfiguration ---");

            store.SetAsync("Deepgram:ApiKey", "clave-secreta-de-prueba", isSecret: true).GetAwaiter().GetResult();
            Check("un secreto se lee descifrado a traves de IConfiguration",
                Build(store)["Deepgram:ApiKey"] == "clave-secreta-de-prueba");

            using (SqliteConnection connection = factory.Open())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "select value from setting where key = 'Deepgram:ApiKey';";
                string onDisk = command.ExecuteScalar()?.ToString() ?? string.Empty;
                Check("el secreto esta cifrado en disco, no en claro",
                    onDisk.Length > 0 && !onDisk.Contains("clave-secreta-de-prueba", StringComparison.Ordinal));
            }

            // Un secreto que no se puede descifrar es el caso real de "base copiada de
            // otro perfil". No puede lanzar en el arranque: tiene que desaparecer de
            // la capa y dejar ver la de abajo, para que el validador lo reporte como
            // faltante con un mensaje que se entiende.
            using (SqliteConnection connection = factory.Open())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "update setting set value = 'bm8tZXMtdW4tYmxvYi1kZS1EUEFQSQ==' where key = 'Deepgram:ApiKey';";
                command.ExecuteNonQuery();
            }

            bool undecryptableSurvived = true;
            string? fellThrough = null;
            try { fellThrough = Build(store)["Deepgram:ApiKey"]; }
            catch (Exception) { undecryptableSurvived = false; }

            Check("un secreto indescifrable no lanza al construir la configuracion", undecryptableSurvived);
            Check("y deja ver la capa de abajo en vez de un valor invalido",
                fellThrough == "clave-empaquetada");

            Console.WriteLine($"{Environment.NewLine}--- Base rota: la app tiene que seguir arrancando ---");

            var brokenFailures = new List<string>();
            IConfigurationRoot degraded = null!;
            bool builtAnyway = true;
            try
            {
                ISettingsStore brokenStore = new SqliteSettingsStore(
                    new SqliteConnectionFactory(@"\\?\Z:\no-existe\jamas\meetings.db"),
                    new DpapiSecretProtector());
                degraded = Build(brokenStore, (operation, _) => brokenFailures.Add(operation));
            }
            catch (Exception)
            {
                builtAnyway = false;
            }

            Check("una base en una ruta imposible no impide construir la configuracion", builtAnyway);
            Check("con la base rota, el empaquetado sigue visible",
                builtAnyway && degraded["Storage:VaultPath"] == @"C:\empaquetado\vault");
            Check("el fallo de base se reporto por el callback en vez de propagarse",
                brokenFailures.Contains("SqliteConfigurationProvider.Load"));

            Console.WriteLine($"{Environment.NewLine}--- Recarga ---");

            IConfigurationRoot reloadable = Build(store);
            store.SetAsync("Storage:VaultPath", @"E:\vault\despues").GetAwaiter().GetResult();
            Check("un valor escrito despues no aparece hasta recargar",
                reloadable["Storage:VaultPath"] == @"D:\base\vault");
            reloadable.Reload();
            Check("Reload() lo ve", reloadable["Storage:VaultPath"] == @"E:\vault\despues");

            // --- Migracion de una sola vez del archivo de T9 --------------------
            Console.WriteLine($"{Environment.NewLine}--- Import del appsettings.json de usuario (T9) ---");

            string userFilePath = Path.Combine(temporaryDirectory, "appsettings.json");
            // Anidamiento de tres niveles y una propiedad con ':' en el nombre a
            // proposito: es la forma que tiene la seccion Pricing del example real, y
            // es justo donde un recorrido del JSON escrito a mano se desviaria de lo
            // que produce AddJsonFile.
            File.WriteAllText(userFilePath,
                """
                {
                  "Storage": { "VaultPath": "C:\\usuario\\vault", "SubFolder": "ReportesDeUsuario" },
                  "Deepgram": { "ApiKey": "deepgram-de-usuario" },
                  "Gemini": { "ApiKey": "gemini-de-usuario", "Model": "gemini-de-prueba" },
                  "AzureFoundry": { "Endpoint": "https://ejemplo/openai/v1/", "ApiKey": "" },
                  "Api": { "Port": 5757, "AuthToken": "token-de-usuario" },
                  "Pricing": {
                    "Gemini:gemini-de-prueba": { "InputPerMillion": 0.30, "OutputPerMillion": 2.50 }
                  },
                  "Hotkey": { "Modifiers": "Control+Alt", "Key": "F9" },
                  "Marcador": { "ApiKey": "<pon-tu-clave-aca>" }
                }
                """);

            // Base limpia para el import: la de arriba ya tiene filas de las pruebas
            // de precedencia y ensuciaria los conteos.
            string importDatabasePath = Path.Combine(temporaryDirectory, "import.db");
            var importFactory = new SqliteConnectionFactory(importDatabasePath);
            new SqliteSchemaMigrator(importFactory).Migrate();
            ISettingsStore importStore = new SqliteSettingsStore(importFactory, new DpapiSecretProtector());

            UserSettingsImportResult result =
                new UserSettingsImporter(importStore).ImportOnceAsync(userFilePath).GetAwaiter().GetResult();
            Console.WriteLine($"        {result.Describe()}");

            Check("el import se declara hecho", result.Outcome == UserSettingsImportOutcome.Imported);
            Check("importo la clave anidada de tres niveles con ':' en el nombre",
                importStore.GetAsync("Pricing:Gemini:gemini-de-prueba:InputPerMillion").GetAwaiter().GetResult() == "0.30");
            Check("importo un valor numerico como texto",
                importStore.GetAsync("Api:Port").GetAwaiter().GetResult() == "5757");
            Check("importo las claves que la UI no sabe editar (Hotkey)",
                importStore.GetAsync("Hotkey:Key").GetAwaiter().GetResult() == "F9");
            Check("un valor no secreto vuelve tal cual",
                importStore.GetAsync("Storage:VaultPath").GetAwaiter().GetResult() == @"C:\usuario\vault");
            Check("un secreto vuelve descifrado",
                importStore.GetAsync("Deepgram:ApiKey").GetAwaiter().GetResult() == "deepgram-de-usuario");
            Check("omitio el marcador de posicion en vez de importarlo",
                importStore.GetAsync("Marcador:ApiKey").GetAwaiter().GetResult() is null &&
                result.SkippedKeys.Contains("Marcador:ApiKey"));
            Check("omitio la clave vacia (AzureFoundry:ApiKey)",
                importStore.GetAsync("AzureFoundry:ApiKey").GetAwaiter().GetResult() is null);

            // Lo que hace que el paso valga: ninguna credencial legible, ni en la base
            // ni en el archivo que queda. Api:AuthToken cuenta como credencial porque
            // enciende el microfono remotamente.
            using (SqliteConnection connection = importFactory.Open())
            {
                string RawValue(string key)
                {
                    using SqliteCommand command = connection.CreateCommand();
                    command.CommandText = "select value from setting where key = $key;";
                    command.Parameters.AddWithValue("$key", key);
                    return command.ExecuteScalar()?.ToString() ?? string.Empty;
                }

                Check("la API key de Deepgram quedo cifrada en la base",
                    !RawValue("Deepgram:ApiKey").Contains("deepgram-de-usuario", StringComparison.Ordinal));
                Check("la API key de Gemini quedo cifrada en la base",
                    !RawValue("Gemini:ApiKey").Contains("gemini-de-usuario", StringComparison.Ordinal));
                Check("el token del endpoint local tambien se trato como secreto",
                    RawValue("Api:AuthToken").Length > 0 &&
                    !RawValue("Api:AuthToken").Contains("token-de-usuario", StringComparison.Ordinal));
                Check("un valor no secreto NO se cifro (sigue legible a ojo en la base)",
                    RawValue("Storage:SubFolder") == "ReportesDeUsuario");
            }

            Check("el archivo original ya no esta en la ruta que lee IConfiguration",
                !File.Exists(userFilePath));

            string redactedPath = UserSettingsImporter.RedactedCopyPathFor(userFilePath);
            Check("quedo la copia redactada al lado", File.Exists(redactedPath));

            string redacted = File.Exists(redactedPath) ? File.ReadAllText(redactedPath) : string.Empty;
            Check("la copia redactada no contiene ninguna credencial en claro",
                !redacted.Contains("deepgram-de-usuario", StringComparison.Ordinal) &&
                !redacted.Contains("gemini-de-usuario", StringComparison.Ordinal) &&
                !redacted.Contains("token-de-usuario", StringComparison.Ordinal));
            Check("la copia redactada conserva los valores no secretos (para poder mirar que habia)",
                redacted.Contains("ReportesDeUsuario", StringComparison.Ordinal) &&
                redacted.Contains("Control+Alt", StringComparison.Ordinal));

            Check("quedo la marca de migrado en la base",
                importStore.GetAsync(UserSettingsImporter.MarkerKey).GetAwaiter().GetResult() is not null);

            // Idempotencia. Se vuelve a crear el archivo con OTRO valor: si el segundo
            // import lo leyera, el valor cambiaria — y eso significaria que cada
            // arranque puede volver a pisar lo que el usuario edito en la UI.
            File.WriteAllText(userFilePath, """{ "Storage": { "VaultPath": "C:\\no-deberia-importarse" } }""");
            UserSettingsImportResult second =
                new UserSettingsImporter(importStore).ImportOnceAsync(userFilePath).GetAwaiter().GetResult();

            Check("el segundo import no hace nada",
                second.Outcome == UserSettingsImportOutcome.AlreadyImported);
            Check("y no piso el valor que ya estaba",
                importStore.GetAsync("Storage:VaultPath").GetAwaiter().GetResult() == @"C:\usuario\vault");
            Check("el segundo import dejo el archivo intacto, no lo borro",
                File.Exists(userFilePath));

            // La comprobacion que justifica verificar la relectura antes de mover el
            // archivo: con el almacen roto, el import falla y el archivo del usuario
            // tiene que seguir ahi, porque mientras exista su capa sigue alimentando
            // a la app y no se perdio nada.
            Console.WriteLine($"{Environment.NewLine}--- Import contra una base rota ---");

            string survivorPath = Path.Combine(temporaryDirectory, "sobreviviente.json");
            File.WriteAllText(survivorPath, """{ "Storage": { "VaultPath": "C:\\intacto" } }""");
            ISettingsStore brokenImportStore = new SqliteSettingsStore(
                new SqliteConnectionFactory(@"\\?\Z:\no-existe\jamas\meetings.db"),
                new DpapiSecretProtector());
            UserSettingsImportResult failed =
                new UserSettingsImporter(brokenImportStore).ImportOnceAsync(survivorPath).GetAwaiter().GetResult();

            Check("un import contra una base rota se reporta como fallido, no lanza",
                failed.Outcome == UserSettingsImportOutcome.Failed);
            Check("y el archivo del usuario queda INTACTO", File.Exists(survivorPath));

            Check("una base limpia sin archivo de usuario no tiene nada que importar",
                new UserSettingsImporter(store)
                    .ImportOnceAsync(Path.Combine(temporaryDirectory, "no-existe.json"))
                    .GetAwaiter().GetResult().Outcome == UserSettingsImportOutcome.NothingToImport);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"El autotest reviento: {exception}");
            failures++;
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariableName, null);
            SqliteConnection.ClearAllPools();
            foreach (string leftover in new[] { temporaryPath, temporaryPath + "-wal", temporaryPath + "-shm" })
            {
                try { if (File.Exists(leftover)) File.Delete(leftover); } catch { /* best effort */ }
            }

            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true); }
            catch { /* best effort */ }
        }

        Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
        return failures == 0 ? 0 : 1;
    }

}
