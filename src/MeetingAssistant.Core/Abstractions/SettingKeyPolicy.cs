namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Decide qué claves de configuración son secretas, o sea cuáles hay que cifrar
/// antes de escribirlas.
///
/// Vive en <c>Core</c> y no en el llamador porque tiene que haber <b>una sola</b>
/// respuesta: <c>SettingsPage</c> decide el flag al guardar y el importador de
/// una sola vez lo decide al migrar el archivo de T9. Si discreparan, una clave
/// entraría en claro por un camino y cifrada por el otro, y el bug sería
/// invisible — la app funcionaría igual, con la credencial legible en disco.
///
/// La regla es por <b>nombre de hoja</b>, no por sección: la sección la elige
/// quien agrega el proveedor (<c>Gemini</c>, <c>AzureFoundry</c>, mañana otro) y
/// una lista de secciones habría que recordar ampliarla. El nombre de la hoja lo
/// impone el consumidor y ya es consistente en todo el appsettings.
/// </summary>
public static class SettingKeyPolicy
{
    /// <summary>
    /// Nombres de hoja que se tratan como credenciales. <c>AuthToken</c> está
    /// acá por la misma razón que existe: <b>enciende el micrófono
    /// remotamente</b>, así que no es menos sensible que una clave de API.
    /// </summary>
    private static readonly string[] SecretLeafNames =
    [
        "ApiKey",
        "AuthToken",
        "Token",
        "Secret",
        "Password",
        "ConnectionString"
    ];

    /// <summary>
    /// <c>true</c> si la clave — en formato <c>IConfiguration</c>, p. ej.
    /// <c>"Deepgram:ApiKey"</c> — guarda una credencial.
    /// </summary>
    public static bool IsSecret(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        int separator = key.LastIndexOf(':');
        string leaf = separator < 0 ? key : key[(separator + 1)..];

        return SecretLeafNames.Any(name => leaf.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Secciones cuyos ajustes se pueden guardar en la base compartida en la
    /// nube. Es una lista de lo que <b>sí</b> va, no de lo que no: un ajuste
    /// nuevo se queda local hasta que alguien decida lo contrario, que es el
    /// error barato de los dos.
    ///
    /// Lo que queda afuera, y por qué:
    /// <list type="bullet">
    /// <item><c>Storage</c>, <c>Api</c>, <c>Hotkey</c>: son de <b>esta máquina</b>
    /// — la ruta del vault, un puerto, un atajo. Compartirlos haría que una
    /// máquina le pise la ruta a la otra.</item>
    /// <item><c>Configuration</c>, <c>Database</c>, <c>Postgres</c>: son lo que
    /// dice <b>dónde está</b> la base en la nube. No se pueden leer de ella.</item>
    /// </list>
    /// </summary>
    private static readonly string[] CloudSections =
    [
        "Pricing",
        "Llm",
        "Gemini",
        "AzureFoundry"
    ];

    /// <summary>
    /// <c>true</c> si la clave se puede guardar en la base en la nube y leer de
    /// ella. <b>Un secreto nunca</b>, esté en la sección que esté: la nube no
    /// tiene con qué cifrarlo de forma que otra máquina lo pueda leer.
    ///
    /// Se aplica al escribir <b>y al leer</b>: una fila que alguien haya metido
    /// a mano en la tabla no puede cambiarle a la app una credencial ni la
    /// dirección de su base.
    /// </summary>
    public static bool CanLiveInCloud(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || IsSecret(key)) return false;

        int separator = key.IndexOf(':');
        if (separator <= 0) return false;

        string section = key[..separator];
        return CloudSections.Any(name => section.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
