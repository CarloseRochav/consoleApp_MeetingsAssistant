using Microsoft.Extensions.Configuration;

/// <summary>
/// La configuración del harness, en un solo lugar: antes se armaba igual en
/// nueve sitios. Es <b>su</b> appsettings.json, no el de la app, más las
/// variables de entorno encima.
/// </summary>
internal static class HarnessConfiguration
{
    public const string FileName = "appsettings.json";

    public static IConfiguration Load() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile(FileName, optional: false, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();
}
