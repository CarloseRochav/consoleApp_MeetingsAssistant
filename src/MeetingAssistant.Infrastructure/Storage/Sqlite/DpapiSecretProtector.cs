using System.Security.Cryptography;
using System.Text;
using MeetingAssistant.Core.Abstractions;

namespace MeetingAssistant.Infrastructure.Storage.Sqlite;

/// <summary>
/// Cifra secretos con DPAPI, atados al usuario de Windows actual.
///
/// Es específico de Windows, y eso está bien: la app es de Windows por diseño
/// (WinUI 3, WASAPI, StartupTask, HttpListener) y `AGENTS.md` dice explícitamente
/// que no se intente hacerla portable.
///
/// Con ámbito <see cref="DataProtectionScope.CurrentUser"/>, el valor sólo se
/// puede descifrar desde el mismo perfil de la misma máquina. Copiar
/// <c>meetings.db</c> a otro lado deja los secretos ilegibles — que es
/// justamente lo que se quiere, porque el archivo sobrevive a la desinstalación
/// y no está en ningún respaldo cifrado.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>
    /// Entropía adicional. No es una clave — DPAPI ya deriva la suya del
    /// usuario —, pero evita que otra app del mismo perfil pueda descifrar estos
    /// valores por accidente pasándole el blob a <c>Unprotect</c>.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MeetingAssistant.Settings.v1");

    public string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);

        // El guard no es defensa contra un caso que pueda pasar — la app es de
        // Windows y no arranca en otro lado. Está porque Infrastructure compila
        // como net10.0 (sin sufijo de plataforma) y el analizador, con razón, no
        // puede saberlo. Guardar es más honesto que anotar la clase: si algún
        // día alguien reusa Infrastructure fuera de Windows, recibe un mensaje
        // que se entiende en vez de una excepción de plataforma.
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "El cifrado de secretos usa DPAPI y sólo funciona en Windows.");
        }

        byte[] encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainText),
            Entropy,
            DataProtectionScope.CurrentUser);

        return Convert.ToBase64String(encrypted);
    }

    public string? TryUnprotect(string protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText) || !OperatingSystem.IsWindows()) return null;

        try
        {
            byte[] decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedText),
                Entropy,
                DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            // Pasa de verdad y no es excepcional: base copiada de otro perfil,
            // perfil recreado, o un valor que quedó a medio escribir. Devolver
            // null deja que la UI pida la clave de nuevo; lanzar acá reventaría
            // el arranque, que es el error que ya costó nueve días en T4.4.
            return null;
        }
    }
}
