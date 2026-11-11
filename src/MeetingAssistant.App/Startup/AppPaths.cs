namespace MeetingAssistant.App.Startup;

/// <summary>Las carpetas y archivos de esta máquina. Todo bajo %LOCALAPPDATA%\MeetingAssistant.</summary>
internal static class AppPaths
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingAssistant");

    /// <summary>Donde la captura deja cada WAV original (44,1 kHz). Se conserva, como antes del paso B.</summary>
    public static string MeetingOutputDirectory { get; } = Path.Combine(Root, "meeting-output");

    /// <summary>La base local: desde el paso B sólo guarda los ajustes de esta máquina (vault, hotkey, token del endpoint).</summary>
    public static string DatabasePath { get; } = Path.Combine(Root, "meetings.db");

    /// <summary>La cola de subidas: un JSON por reunión (paso B).</summary>
    public static string QueueDirectory { get; } = Path.Combine(Root, "upload-queue");

    /// <summary>Las copias convertidas a 16 kHz que se suben; cada una se borra cuando su reporte llega al vault.</summary>
    public static string UploadDirectory { get; } = Path.Combine(Root, "upload-queue", "audio");

    /// <summary>El dispositivo de esta máquina en la API, con el token cifrado (DPAPI).</summary>
    public static string DeviceFile { get; } = Path.Combine(Root, "device.json");
}
