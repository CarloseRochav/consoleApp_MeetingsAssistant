namespace MeetingAssistant.App.Startup;

/// <summary>Dónde escribe la app lo suyo. Todo fuera del paquete, que es de sólo lectura.</summary>
internal static class AppPaths
{
    public static string MeetingOutputDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingAssistant",
        "meeting-output");

    /// <summary>
    /// Base local de reuniones (Fase 5). Mismo destino y mismo criterio que el
    /// log, el audio y la configuración de usuario: lo que la app necesita
    /// escribir no puede vivir dentro del paquete instalado, que es de sólo
    /// lectura.
    ///
    /// Ojo: <b>sobrevive a la desinstalación</b>, igual que el resto de la
    /// carpeta. Esa decisión se tomó cuando ahí sólo había un log y unos .wav;
    /// con la base pasa a haber además todos los transcripts.
    /// </summary>
    public static string DatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingAssistant",
        "meetings.db");

}
