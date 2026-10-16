namespace MeetingAssistant.Infrastructure.Transcription;

/// <summary>
/// La lista de términos que Deepgram tiene que reconocer bien (Keyterm
/// Prompting): jerga técnica que mezcla español e inglés, nombres de
/// productos y de gente. Existe por un error real visto en Fase 0: "stored
/// procedure" salía como "auth restore procedure", y el LLM lo pasaba al
/// reporte como si fuera una indicación oficial.
///
/// Vive en una sola clave, <c>Deepgram:Keyterms</c>, con los términos
/// separados por <c>;</c> o por saltos de línea: así cabe igual en la tabla
/// de ajustes, en la nube y en una variable de entorno. Cada término viaja a
/// Deepgram como su propio parámetro <c>keyterm=</c>; una lista con comas
/// dentro de un solo parámetro Deepgram la toma como UN término literal, sin
/// error, y no sirve de nada.
///
/// Límites de Deepgram (developers.deepgram.com/docs/keyterm, 2026-10-15):
/// hasta 100 términos y 500 tokens por petición; recomienda 20–50. Cobra
/// aparte: <c>Pricing:Deepgram:KeytermPerMinute</c>.
/// </summary>
public static class DeepgramKeyterms
{
    public const string SettingKey = "Deepgram:Keyterms";
    public const int MaximumTerms = 100;

    private static readonly char[] Separators = [';', '\n', '\r'];

    /// <summary>
    /// Separa, recorta, descarta vacíos y repetidos (sin distinguir
    /// mayúsculas; se queda el primero, con su forma) y conserva el orden.
    /// Pasados <see cref="MaximumTerms"/> se corta: Deepgram rechazaría la
    /// petición entera y la reunión quedaría sin transcribir.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<string>();
        foreach (string part in raw.Split(Separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(part)) terms.Add(part);
            if (terms.Count == MaximumTerms) break;
        }

        return terms;
    }

    /// <summary>
    /// La forma en que se guarda: en una línea, separada por <c>;</c>, para que
    /// se lea igual en <c>--list-cloud-settings</c> que en la tabla. La página
    /// de Configuración la muestra un término por línea.
    /// </summary>
    public static string? Format(IReadOnlyList<string> terms) =>
        terms.Count == 0 ? null : string.Join("; ", terms);
}
