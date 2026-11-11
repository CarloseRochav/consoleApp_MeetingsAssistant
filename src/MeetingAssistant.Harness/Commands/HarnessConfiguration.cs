using Microsoft.Extensions.Configuration;

/// <summary>
/// La configuración del harness, en un solo lugar. Abajo de todo, los valores de fábrica de la app
/// (<c>appsettings.defaults.json</c>, en git, sin secretos): de ahí sale la dirección de la API de
/// DevLog, una sola fuente para la app y el harness. Encima, <b>su</b> appsettings.json (opcional
/// desde el paso B: sólo lo piden la importación final y publicar el instalador) y las variables de
/// entorno.
/// </summary>
internal static class HarnessConfiguration
{
    public const string FileName = "appsettings.json";
    public const string AppDefaultsFileName = "appsettings.defaults.json";

    public static IConfiguration Load() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile(AppDefaultsFileName, optional: true, reloadOnChange: false)
        .AddJsonFile(FileName, optional: true, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();
}
