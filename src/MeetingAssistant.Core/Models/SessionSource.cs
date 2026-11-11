namespace MeetingAssistant.Core.Models;

/// <summary>
/// Qué disparó una grabación en esta máquina: el hotkey, la bandeja, el endpoint HTTP, la ventana o
/// una importación de archivo. Desde el paso B se muestra en la cola de subidas; a la API va
/// siempre "desktop" (<c>MeetingUploadQueue.Source</c>).
/// </summary>
public static class SessionSource
{
    public const string Hotkey = "hotkey";
    public const string Tray = "tray";
    public const string Http = "http";
    public const string Window = "window";
    public const string Import = "import";
    public const string Harness = "harness";
}
