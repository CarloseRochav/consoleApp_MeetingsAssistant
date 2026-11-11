namespace MeetingAssistant.Core.Abstractions;

/// <summary>
/// Cifra y descifra valores sensibles antes de que toquen el disco.
///
/// Existe como abstracción, y no como una llamada directa a DPAPI, por dos
/// razones: <c>Core</c> no puede referenciar nada específico de plataforma, y
/// <b>sin cifrado real mover las API keys a la base no mejoraría nada</b> —
/// SQLite no cifra, así que sería pasarlas de un archivo en claro a otro.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    /// <summary>
    /// Descifra. Devuelve <c>null</c> si el valor no se puede descifrar en este
    /// perfil — cosa que pasa de verdad: DPAPI ata el secreto al usuario y la
    /// máquina, así que copiar el archivo a otro perfil deja los valores
    /// ilegibles. Devolver null en vez de lanzar es deliberado: eso ocurre
    /// durante el arranque, y una excepción ahí ya se llevó puesta la app entera
    /// una vez (T4.4).
    /// </summary>
    string? TryUnprotect(string protectedText);
}

/// <summary>Un ajuste guardado.</summary>
public sealed record SettingEntry(string Key, string? Value, bool IsSecret, DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Ajustes persistidos, con las claves en el mismo formato que usa
/// <c>IConfiguration</c> (<c>"Storage:VaultPath"</c>). Esa coincidencia no es
/// casual: es lo que permite exponer esto como un
/// <c>IConfigurationProvider</c> más adelante, en vez de reemplazar
/// <c>IConfiguration</c> — y así conservar intactos <c>ReadRequiredSetting</c>,
/// el validador de arranque, el estimador de costo y, sobre todo, las variables
/// de entorno como capa de arriba.
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// Todos los ajustes, ya descifrados. Un secreto que no se pudo descifrar
    /// viene con <c>Value = null</c>, no explota: el usuario tiene que poder
    /// abrir Configuración y volver a escribir la clave.
    /// </summary>
    Task<IReadOnlyList<SettingEntry>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Guarda un ajuste. Con <paramref name="isSecret"/> el valor se cifra antes
    /// de escribirse. Un <paramref name="value"/> nulo o vacío <b>borra</b> la
    /// clave, que es como se vuelve al valor empaquetado — mismo criterio que ya
    /// usa el archivo de usuario de T9.
    /// </summary>
    Task SetAsync(string key, string? value, bool isSecret = false, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
