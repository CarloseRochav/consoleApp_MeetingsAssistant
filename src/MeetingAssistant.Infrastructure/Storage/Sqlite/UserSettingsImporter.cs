using System.Text.Json;
using System.Text.Json.Nodes;
using MeetingAssistant.Core.Abstractions;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.Infrastructure.Storage.Sqlite;

public enum UserSettingsImportOutcome
{
    /// <summary>Ya se hizo antes: la marca está en la base. No se toca nada.</summary>
    AlreadyImported,

    /// <summary>No hay archivo de usuario que importar. Instalación limpia.</summary>
    NothingToImport,

    /// <summary>Importado y verificado; el archivo original ya no existe.</summary>
    Imported,

    /// <summary>
    /// Algo falló. <b>El archivo original sigue intacto</b>, que es lo que
    /// importa: mientras exista, su capa de configuración sigue alimentando a la
    /// app y no se perdió nada.
    /// </summary>
    Failed
}

/// <summary>
/// Resultado del import. Lleva los <b>nombres</b> de las claves, nunca los
/// valores: esto termina en el log de diagnóstico, que es texto plano en el
/// perfil del usuario — el mismo archivo donde ya se revisan los arranques.
/// </summary>
public sealed record UserSettingsImportResult(
    UserSettingsImportOutcome Outcome,
    IReadOnlyList<string> ImportedKeys,
    IReadOnlyList<string> SkippedKeys,
    string? RedactedCopyPath = null,
    string? Detail = null)
{
    /// <summary>Una línea para el log de arranque.</summary>
    public string Describe() => Outcome switch
    {
        UserSettingsImportOutcome.AlreadyImported =>
            "ajustes de usuario: ya migrados a la base en un arranque anterior",
        UserSettingsImportOutcome.NothingToImport =>
            "ajustes de usuario: no hay archivo que migrar",
        UserSettingsImportOutcome.Imported =>
            $"ajustes de usuario: {ImportedKeys.Count} clave(s) migradas a la base " +
            $"({string.Join(", ", ImportedKeys)})" +
            (SkippedKeys.Count > 0 ? $"; {SkippedKeys.Count} omitida(s): {string.Join(", ", SkippedKeys)}" : string.Empty) +
            (RedactedCopyPath is null ? string.Empty : $"; copia redactada en {RedactedCopyPath}"),
        _ => $"ajustes de usuario: la migración FALLO y el archivo quedó intacto — {Detail}"
    };
}

/// <summary>
/// Migra una sola vez el <c>appsettings.json</c> de usuario que creó T9
/// (<c>%LOCALAPPDATA%\MeetingAssistant\appsettings.json</c>) a la tabla
/// <c>setting</c> de la base, cifrando las credenciales por el camino.
///
/// Es el paso que hace verdadero el criterio de salida de la fase — <b>las API
/// keys ya no están en texto plano</b> —, porque hasta acá el archivo de T9 las
/// guardaba en claro en el perfil del usuario y eso estaba anotado
/// explícitamente como decisión diferida, no como olvido.
///
/// Tres decisiones que no son accidentales:
///
/// 1. <b>Se importa todo el archivo, no las nueve claves que conoce
///    <c>SettingsPage</c>.</b> El archivo es editable a mano y ya se documentó
///    que <c>Hotkey</c> y <c>Api</c> quedaron fuera de la UI a propósito.
///    Importar sólo lo que la UI sabe editar habría dejado esas claves atrás sin
///    que nada avise.
/// 2. <b>El aplanado lo hace <c>IConfiguration</c>, no un recorrido propio.</b>
///    Esta clase sustituye una capa de <c>AddJsonFile</c>: usar el mismo lector
///    garantiza que las claves salgan idénticas a las que esa capa producía,
///    anidamiento y arrays incluidos. Un walker escrito a mano es exactamente
///    donde aparecería una diferencia de una clave que nadie nota.
/// 3. <b>El original se reemplaza por una copia redactada, no se borra a
///    secas.</b> Borrarlo sería destruir la configuración del usuario apoyándose
///    en que la base quedó bien; dejarlo sería conservar las credenciales en
///    claro y, peor, mantener viva una capa por debajo de SQLite que resucitaría
///    valores viejos al vaciar un campo en la UI. La copia redactada resuelve
///    las tres cosas: deja rastro de qué había, no deja ningún secreto legible,
///    y saca el archivo de la ruta que <c>IConfiguration</c> lee.
/// </summary>
public sealed class UserSettingsImporter
{
    /// <summary>
    /// Marca de "esto ya se hizo", guardada en la propia base. Va en la tabla
    /// <c>setting</c> y no en un archivo aparte porque tiene que compartir el
    /// destino de los datos que describe: si alguien borra la base para empezar
    /// de cero, la marca se va con ella y el import vuelve a estar disponible.
    /// </summary>
    public const string MarkerKey = "Migration:UserSettingsImportedAtUtc";

    /// <summary>
    /// Valor con el que se reemplaza cada credencial en la copia redactada.
    /// Empieza con <c>&lt;</c> a propósito: es la misma forma que un marcador de
    /// posición del example, y <b>todos</b> los lectores de la app
    /// (<c>App.ReadSetting</c>, <c>StartupConfigurationValidator</c>,
    /// <c>UserSettingsService</c>) ya tratan un valor así como ausente. Si algún
    /// día alguien renombra la copia de vuelta al nombre original, no reintroduce
    /// una credencial inventada.
    /// </summary>
    private const string RedactedValue = "<migrado a meetings.db>";

    /// <summary>
    /// Formato de la copia redactada. El codificador relajado no es un adorno: el
    /// de fábrica escapa a notación <c>\uXXXX</c> todo carácter que pudiera ser
    /// peligroso incrustado en HTML, y esta copia existe <b>para poderse leer a
    /// ojo</b> — con el escapado por defecto, el <c>+</c> de
    /// <c>"Control+Alt"</c> sale como una secuencia numérica y el propio marcador
    /// de redacción, que empieza con un signo de menor, queda
    /// ilegible. Es seguro acá porque este archivo no se sirve nunca como HTML ni
    /// como JavaScript: se escribe en el perfil del usuario para que lo mire una
    /// persona.
    /// </summary>
    private static readonly JsonSerializerOptions RedactedCopyFormat = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ISettingsStore _settingsStore;

    public UserSettingsImporter(ISettingsStore settingsStore) => _settingsStore = settingsStore;

    /// <summary>
    /// Ruta de la copia redactada que queda en lugar del original.
    /// </summary>
    public static string RedactedCopyPathFor(string userSettingsFilePath) =>
        Path.Combine(
            Path.GetDirectoryName(userSettingsFilePath) ?? string.Empty,
            "appsettings.pre-sqlite.json");

    /// <summary>
    /// Importa el archivo si hace falta. Idempotente: la segunda vez devuelve
    /// <see cref="UserSettingsImportOutcome.AlreadyImported"/> sin escribir nada.
    ///
    /// No lanza por un fallo de importación — devuelve
    /// <see cref="UserSettingsImportOutcome.Failed"/> con el detalle. Corre en el
    /// arranque, y ahí una excepción no es un error que se reporta: es la app que
    /// no abre.
    /// </summary>
    public async Task<UserSettingsImportResult> ImportOnceAsync(
        string userSettingsFilePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (await _settingsStore.GetAsync(MarkerKey, cancellationToken) is not null)
            {
                return new UserSettingsImportResult(UserSettingsImportOutcome.AlreadyImported, [], []);
            }

            if (!File.Exists(userSettingsFilePath))
            {
                // Sin marca: si el usuario crea el archivo a mano más adelante, el
                // import sigue disponible. No se marca "hecho" lo que no se hizo.
                return new UserSettingsImportResult(UserSettingsImportOutcome.NothingToImport, [], []);
            }

            (List<string> importedKeys, List<string> skippedKeys) = await CopyIntoStoreAsync(
                userSettingsFilePath, cancellationToken);

            // Verificación antes de tocar el original. Es la única condición bajo
            // la que vale mover el archivo: que cada clave se pueda volver a leer
            // con el valor correcto, secretos incluidos — o sea que el ciclo
            // completo de cifrado y descifrado funcionó en ESTE perfil, no que la
            // escritura no dio error.
            string? mismatch = await FindMismatchAsync(userSettingsFilePath, cancellationToken);
            if (mismatch is not null)
            {
                return new UserSettingsImportResult(
                    UserSettingsImportOutcome.Failed, importedKeys, skippedKeys,
                    Detail: $"la relectura no coincide en '{mismatch}'; el archivo original NO se movió");
            }

            await _settingsStore.SetAsync(
                MarkerKey,
                DateTimeOffset.UtcNow.ToString("O"),
                isSecret: false,
                cancellationToken);

            string redactedCopyPath = WriteRedactedCopyAndRemoveOriginal(userSettingsFilePath);

            return new UserSettingsImportResult(
                UserSettingsImportOutcome.Imported, importedKeys, skippedKeys, redactedCopyPath);
        }
        catch (Exception exception)
        {
            return new UserSettingsImportResult(
                UserSettingsImportOutcome.Failed, [], [], Detail: exception.Message);
        }
    }

    /// <summary>
    /// Lee el archivo con el mismo lector que la capa que sustituye y escribe
    /// cada valor en el almacén. Devuelve las claves importadas y las omitidas.
    /// </summary>
    private async Task<(List<string> Imported, List<string> Skipped)> CopyIntoStoreAsync(
        string userSettingsFilePath,
        CancellationToken cancellationToken)
    {
        var imported = new List<string>();
        var skipped = new List<string>();

        foreach ((string key, string? value) in ReadFlattened(userSettingsFilePath))
        {
            // Un vacío o un marcador de posición no es un valor: importarlo
            // crearía una fila que pisa al appsettings empaquetado con nada, y el
            // síntoma sería una clave que "está configurada" y no funciona.
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('<'))
            {
                skipped.Add(key);
                continue;
            }

            await _settingsStore.SetAsync(key, value, SettingKeyPolicy.IsSecret(key), cancellationToken);
            imported.Add(key);
        }

        return (imported, skipped);
    }

    /// <summary>
    /// Devuelve la primera clave cuyo valor releído no coincide con el archivo, o
    /// <c>null</c> si todas cuadran.
    /// </summary>
    private async Task<string?> FindMismatchAsync(
        string userSettingsFilePath,
        CancellationToken cancellationToken)
    {
        foreach ((string key, string? value) in ReadFlattened(userSettingsFilePath))
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('<')) continue;

            if (await _settingsStore.GetAsync(key, cancellationToken) != value) return key;
        }

        return null;
    }

    /// <summary>
    /// Aplana el JSON a pares <c>Seccion:Clave</c> usando el propio
    /// <c>IConfiguration</c>. Las claves intermedias (las de sección, que vienen
    /// con valor nulo) se descartan.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string?>> ReadFlattened(string userSettingsFilePath) =>
        new ConfigurationBuilder()
            .AddJsonFile(userSettingsFilePath, optional: false, reloadOnChange: false)
            .Build()
            .AsEnumerable()
            .Where(pair => pair.Value is not null && !string.Equals(pair.Key, MarkerKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Escribe la copia con las credenciales redactadas y borra el original.
    ///
    /// El orden importa y es el mismo patrón que ya usaba <c>Save</c> en T9:
    /// primero existe el reemplazo, después desaparece el original. Al revés, un
    /// corte de luz en el medio dejaría al usuario sin ninguno de los dos.
    /// </summary>
    private static string WriteRedactedCopyAndRemoveOriginal(string userSettingsFilePath)
    {
        string redactedCopyPath = RedactedCopyPathFor(userSettingsFilePath);

        JsonNode? root = JsonNode.Parse(File.ReadAllText(userSettingsFilePath));
        if (root is not null) Redact(root, prefix: string.Empty);

        File.WriteAllText(redactedCopyPath, root?.ToJsonString(RedactedCopyFormat) ?? "{}");

        File.Delete(userSettingsFilePath);

        // T9 escribía con un temporal al lado; si quedó uno de una escritura
        // interrumpida, tiene las mismas credenciales en claro que el original.
        string leftoverTemporary = userSettingsFilePath + ".tmp";
        if (File.Exists(leftoverTemporary)) File.Delete(leftoverTemporary);

        return redactedCopyPath;
    }

    /// <summary>
    /// Reemplaza en el árbol JSON el valor de cada hoja secreta. Recorre el
    /// árbol, y no la lista de claves aplanadas, porque hay que preservar la
    /// forma del archivo — el sentido de la copia es poder mirar qué había.
    /// </summary>
    private static void Redact(JsonNode node, string prefix)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                // Sobre una copia de los nombres: se reasignan valores mientras se
                // recorre, y modificar la colección que se está enumerando lanza.
                foreach (string name in jsonObject.Select(property => property.Key).ToList())
                {
                    string key = prefix.Length == 0 ? name : $"{prefix}:{name}";

                    if (jsonObject[name] is JsonObject or JsonArray)
                    {
                        Redact(jsonObject[name]!, key);
                        continue;
                    }

                    if (SettingKeyPolicy.IsSecret(key)) jsonObject[name] = RedactedValue;
                }

                break;

            case JsonArray jsonArray:
                for (int index = 0; index < jsonArray.Count; index++)
                {
                    if (jsonArray[index] is { } element) Redact(element, $"{prefix}:{index}");
                }

                break;
        }
    }
}
