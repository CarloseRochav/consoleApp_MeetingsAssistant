using MeetingAssistant.Core.Models;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.UI.Xaml;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Registra un hotkey de sistema y enruta WM_HOTKEY al mismo coordinador que
/// usan RecordPage y la bandeja. Todo el interop queda confinado al proyecto App.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x4D41;
    private const uint WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private static readonly UIntPtr SubclassId = new(0x4D41);

    private readonly IConfiguration _configuration;
    private readonly RecordingCoordinator _coordinator;
    private readonly TrayIconService _trayIcon;
    private readonly SubclassProc _subclassProc;
    private nint _windowHandle;
    private bool _isRegistered;
    private int _toggleInProgress;

    public GlobalHotkeyService(
        IConfiguration configuration,
        RecordingCoordinator coordinator,
        TrayIconService trayIcon)
    {
        _configuration = configuration;
        _coordinator = coordinator;
        _trayIcon = trayIcon;
        _subclassProc = WindowSubclassProc;
    }

    public bool Register(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_isRegistered) return true;

        try
        {
            (uint modifiers, uint key, string displayName) = ReadHotkey();
            _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);

            if (!SetWindowSubclass(_windowHandle, _subclassProc, SubclassId, UIntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "No se pudo conectar el manejador de mensajes del hotkey.");
            }

            if (!RegisterHotKey(_windowHandle, HotkeyId, modifiers | ModNoRepeat, key))
            {
                int error = Marshal.GetLastWin32Error();
                RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);
                _windowHandle = 0;
                throw new Win32Exception(error,
                    $"No se pudo registrar el hotkey global {displayName}; probablemente ya está en uso.");
            }

            _isRegistered = true;
            _trayIcon.SetStatus($"Meeting Assistant — hotkey {displayName}");
            return true;
        }
        catch (Exception exception)
        {
            App.LogStartupFailure("GlobalHotkeyService.Register", exception);
            _trayIcon.ShowError("Hotkey no disponible", exception.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (_windowHandle != 0)
        {
            if (_isRegistered && !UnregisterHotKey(_windowHandle, HotkeyId))
            {
                App.LogStartupFailure("GlobalHotkeyService.Unregister",
                    new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo liberar el hotkey global."));
            }

            RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);
        }

        _isRegistered = false;
        _windowHandle = 0;
        GC.SuppressFinalize(this);
    }

    private nint WindowSubclassProc(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == WmHotkey && wParam.ToInt64() == HotkeyId)
        {
            _ = ToggleRecordingAsync();
            return 0;
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private async Task ToggleRecordingAsync()
    {
        if (Interlocked.Exchange(ref _toggleInProgress, 1) != 0) return;

        try
        {
            if (_coordinator.IsRecording)
                await _coordinator.StopRecordingAndProcessAsync();
            else
                await _coordinator.StartRecordingAsync(SessionSource.Hotkey);
        }
        catch (Exception exception)
        {
            App.LogStartupFailure("GlobalHotkeyService.ToggleRecording", exception);
            _trayIcon.ShowError("Error de hotkey", exception.Message);
        }
        finally
        {
            Volatile.Write(ref _toggleInProgress, 0);
        }
    }

    private (uint Modifiers, uint Key, string DisplayName) ReadHotkey()
    {
        string modifiersText = ReadConfiguredValue("Hotkey:Modifiers") ?? "Control+Alt";
        string keyText = ReadConfiguredValue("Hotkey:Key") ?? "F9";
        uint modifiers = ParseModifiers(modifiersText);
        uint key = ParseKey(keyText);
        string displayName = $"{FormatModifiers(modifiers)}{keyText.Trim().ToUpperInvariant()}";
        return (modifiers, key, displayName);
    }

    private string? ReadConfiguredValue(string key)
    {
        string? value = _configuration[key];
        return string.IsNullOrWhiteSpace(value) || value.StartsWith('<') ? null : value;
    }

    private static uint ParseModifiers(string value)
    {
        uint result = 0;
        string[] parts = value.Split([',', '+', '|', ' '], StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            result |= part.Trim().ToLowerInvariant() switch
            {
                "alt" => ModAlt,
                "control" or "ctrl" => ModControl,
                "shift" => ModShift,
                "windows" or "win" => ModWin,
                _ => throw new FormatException($"Modificador de hotkey no reconocido: '{part}'.")
            };
        }

        if (result == 0)
            throw new FormatException("Hotkey:Modifiers debe contener al menos un modificador.");

        return result;
    }

    private static uint ParseKey(string value)
    {
        string key = value.Trim().ToUpperInvariant();
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
            return key[0];

        if (key.Length is 2 or 3 && key[0] == 'F' &&
            int.TryParse(key[1..], out int functionKey) && functionKey is >= 1 and <= 24)
            return (uint)(0x70 + functionKey - 1);

        throw new FormatException($"Tecla de hotkey no reconocida: '{value}'. Usa A-Z, 0-9 o F1-F24.");
    }

    private static string FormatModifiers(uint modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        return string.Join('+', parts) + "+";
    }

    private delegate nint SubclassProc(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProc subclassProc,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProc subclassProc,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint windowHandle, uint message, nint wParam, nint lParam);
}
