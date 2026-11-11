using System.Text.Json;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// El dispositivo de esta máquina en un archivo propio, con el token cifrado por DPAPI (atado a este
/// usuario de Windows; copiado a otro perfil no se puede leer y hay que iniciar sesión otra vez).
/// Archivo aparte y no en la tabla de ajustes a propósito: así el token nunca entra en
/// <c>IConfiguration</c>, y cerrar sesión es borrar un archivo.
/// </summary>
public sealed class DpapiDeviceSessionStore(string filePath, ISecretProtector protector) : IDeviceSessionStore
{
    public DeviceSession? Load()
    {
        if (!File.Exists(filePath)) return null;

        try
        {
            StoredDevice? stored = JsonSerializer.Deserialize<StoredDevice>(File.ReadAllBytes(filePath));
            if (stored is null || protector.TryUnprotect(stored.ProtectedToken) is not { Length: > 0 } token) return null;
            return new DeviceSession(token, stored.IdDevice, stored.IdUser, stored.Username, stored.DeviceName);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    public void Save(DeviceSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var stored = new StoredDevice(session.IdDevice, session.IdUser, session.Username, session.DeviceName, protector.Protect(session.Token));
        string temporary = filePath + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(stored));
        File.Move(temporary, filePath, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(filePath)) File.Delete(filePath);
    }

    private sealed record StoredDevice(int IdDevice, int IdUser, string Username, string DeviceName, string ProtectedToken)
    {
        public override string ToString() => $"StoredDevice(idDevice={IdDevice}, token=[redacted])";
    }
}
