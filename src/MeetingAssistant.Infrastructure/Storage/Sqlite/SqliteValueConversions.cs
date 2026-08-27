using System.Globalization;

namespace MeetingAssistant.Infrastructure.Storage.Sqlite;

/// <summary>
/// Conversiones entre los tipos de C# y cómo se guardan en SQLite. Están juntas
/// y en un solo lugar porque son exactamente donde se cuelan los errores de
/// persistencia que después no se ven: dinero que deriva y fechas que cambian de
/// huso a mitad del viaje.
/// </summary>
internal static class SqliteValueConversions
{
    /// <summary>
    /// Formato de fecha: ISO-8601 en UTC, con la Z explícita. Ordena bien
    /// lexicográficamente — que es lo que hace que <c>order by started_at_utc</c>
    /// funcione sin trucos — y se lee a ojo en DB Browser.
    /// </summary>
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    /// <summary>
    /// El costo se guarda en micro-dólares enteros. Guardar dinero en punto
    /// flotante es pedir deriva justo en la métrica que Fase 4 quiere sumar, y
    /// 1e-6 no pierde nada: es la precisión que el frontmatter ya muestra (F6).
    /// </summary>
    private const decimal MicroUsdPerUsd = 1_000_000m;

    public static string ToText(DateTimeOffset value) =>
        value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset ToTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public static long? ToMicroUsd(decimal? value) =>
        value is null ? null : (long)Math.Round(value.Value * MicroUsdPerUsd, MidpointRounding.AwayFromZero);

    public static decimal? ToUsd(long? microUsd) =>
        microUsd is null ? null : microUsd.Value / MicroUsdPerUsd;

    public static decimal ToUsd(long microUsd) => microUsd / MicroUsdPerUsd;

    /// <summary>
    /// Convierte lo que escribe una persona en una consulta FTS5 válida.
    ///
    /// Hace falta porque la sintaxis de FTS5 tiene operadores (<c>AND</c>,
    /// <c>NEAR</c>, <c>*</c>, comillas, paréntesis) y un término a medias
    /// <b>lanza una excepción</b>. Eso es intolerable en una caja de búsqueda:
    /// el usuario escribe letra a letra y pasaría por muchos estados inválidos
    /// antes de llegar a lo que quiere.
    ///
    /// La regla es simple: cada palabra se entrecomilla como literal y se le
    /// agrega <c>*</c> para que busque por prefijo. Se pierde poder de consulta
    /// y se gana que nunca falle.
    /// </summary>
    public static string ToFts5Query(string userInput)
    {
        IEnumerable<string> terms = userInput
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.Replace("\"", string.Empty))
            .Where(term => term.Length > 0)
            .Select(term => $"\"{term}\"*");

        return string.Join(" ", terms);
    }
}
