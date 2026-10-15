using Microsoft.Extensions.Configuration;

/// <summary>Lo que recibe un comando: sus argumentos (sin el nombre) y lo global.</summary>
/// <param name="Arguments">Los argumentos que siguen al nombre del comando.</param>
/// <param name="PromptId">El valor de <c>--prompt</c>, si se pasó.</param>
internal sealed record CommandContext(IReadOnlyList<string> Arguments, string? PromptId)
{
    private readonly Lazy<IConfiguration> _configuration = new(HarnessConfiguration.Load);

    /// <summary>
    /// El appsettings.json del harness más las variables de entorno. Se carga
    /// recién al pedirla: los autotests offline no la necesitan, y el archivo
    /// puede no existir.
    /// </summary>
    public IConfiguration Configuration => _configuration.Value;
}

/// <summary>
/// Un comando del harness (patrón Command): un nombre, cuántos argumentos
/// acepta y qué hacer. Agregar uno es agregar una entrada a
/// <see cref="CommandCatalog"/>, sin tocar el despacho.
/// </summary>
/// <param name="Name">Lo que se escribe, p. ej. <c>--verify-retry</c>.</param>
/// <param name="Usage">Cómo se usa, para la ayuda y los errores de uso.</param>
/// <param name="Description">Qué hace, y si toca algo real o cobra.</param>
/// <param name="MinArguments">Mínimo de argumentos después del nombre.</param>
/// <param name="MaxArguments">Máximo de argumentos después del nombre.</param>
/// <param name="RunAsync">La acción. Devuelve el código de salida.</param>
internal sealed record HarnessCommand(
    string Name,
    string Usage,
    string Description,
    int MinArguments,
    int MaxArguments,
    Func<CommandContext, Task<int>> RunAsync)
{
    public bool AcceptsArgumentCount(int count) => count >= MinArguments && count <= MaxArguments;

    public static HarnessCommand Offline(string name, string description, Func<Task<int>> run) =>
        new(name, name, description, 0, 0, _ => run());

    public static HarnessCommand Offline(string name, string description, Func<int> run) =>
        new(name, name, description, 0, 0, _ => Task.FromResult(run()));
}
