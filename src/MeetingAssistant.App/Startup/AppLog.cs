namespace MeetingAssistant.App.Startup;

/// <summary>
/// El log de texto de la app: fallos y trazas de diagnóstico, en el mismo
/// archivo. Existe antes que cualquier UI y que el contenedor de servicios, por
/// eso es estático y no lanza nunca.
/// </summary>
internal static class AppLog
{
    /// <summary>
    /// Ruta del log de diagnóstico. Deliberadamente NO usa
    /// <see cref="AppContext.BaseDirectory"/>: cuando la app corre empaquetada
    /// el directorio base queda bajo WindowsApps, donde la escritura falla o se
    /// redirige de forma opaca, y el log se perdía justo en el escenario que
    /// más importa depurar.
    ///
    /// Ojo con dónde buscarlo, porque depende de CÓMO esté registrada la app y
    /// no de esta ruta. Hay que mirar los dos lados antes de concluir que no hay
    /// log:
    ///
    ///   - MSIX firmado e instalado: %LOCALAPPDATA%\MeetingAssistant\
    ///     (la ruta plana, tal cual la arma este Path.Combine). Medido en T6a y
    ///     confirmado de nuevo en T5.
    ///   - Registro de desarrollo (`dotnet run`): LocalApplicationData está
    ///     redirigido y el archivo aparece bajo
    ///     %LOCALAPPDATA%\Packages\{PackageFamilyName}\LocalCache\Local\MeetingAssistant.
    ///
    /// Buscar en el lado equivocado ya costó caro dos veces: el 2026-08-23 hizo
    /// dar por bueno un Register() que llevaba fallando en cada arranque desde
    /// que se implementó T4. Este comentario decía que la ruta plana nunca se
    /// usaba, lo cual dejó de ser cierto al instalar el paquete firmado en T6a.
    /// </summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingAssistant",
        "startup-errors.log");

    /// <summary>Un fallo con su excepción completa. Lo usa toda la app, no sólo el arranque.</summary>
    public static void Failure(string context, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{context}] {exception}\n\n";
            File.AppendAllText(FilePath, entry);
        }
        catch
        {
            // Si ni siquiera se puede escribir el log de diagnóstico, no hay
            // nada más que hacer aquí sin arriesgar un segundo crash.
        }
    }

    /// <summary>
    /// Traza de diagnóstico, en el mismo archivo que
    /// <see cref="Failure"/>. Existe porque una notificación no tiene
    /// ninguna superficie donde depurarse: cuando no aparece un toast, sin esto
    /// no hay forma de distinguir "el evento nunca llegó al servicio" de "Show
    /// se llamó y Windows no lo mostró", que son problemas opuestos.
    /// </summary>
    public static void Diagnostic(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(
                FilePath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [diag] {message}\n");
        }
        catch
        {
            // Mismo criterio que Failure: si no se puede escribir el
            // diagnóstico, no vale arriesgar un segundo fallo por dejar rastro.
        }
    }
}
