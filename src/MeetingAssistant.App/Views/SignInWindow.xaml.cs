using MeetingAssistant.App.Startup;
using MeetingAssistant.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAssistant.App.Views;

/// <summary>
/// El inicio de sesión del paso B: el login de DevLog una sola vez → el token de este dispositivo.
/// La contraseña sale del PasswordBox a un arreglo propio que <see cref="DeviceAuthService"/> borra
/// con ceros, y el PasswordBox se vacía. El string que guarda el propio PasswordBox no se puede
/// borrar desde .NET (los strings son inmutables): queda en memoria hasta que lo recoja el GC.
/// </summary>
public sealed partial class SignInWindow : Window
{
    private readonly DeviceAuthService _auth;

    /// <summary>La sesión quedó iniciada (llega en el hilo de UI).</summary>
    public event EventHandler? SignedIn;

    public SignInWindow(DeviceAuthService auth, string? reason, string? lastUsername)
    {
        InitializeComponent();
        _auth = auth;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(620, 640));

        if (!string.IsNullOrWhiteSpace(reason))
        {
            ReasonInfo.Message = reason;
            ReasonInfo.IsOpen = true;
        }

        UsernameBox.Text = lastUsername ?? string.Empty;
        DeviceText.Text = $"Este dispositivo se registra como «{auth.DeviceName}».";
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e) => await SignInAsync();

    private async void PasswordBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) await SignInAsync();
    }

    private async Task SignInAsync()
    {
        SignInButton.IsEnabled = false;
        Busy.IsActive = true;
        ResultText.Text = string.Empty;
        char[] password = PasswordBox.Password.ToCharArray();
        PasswordBox.Password = string.Empty;
        try
        {
            SignInResult result = await _auth.SignInAsync(UsernameBox.Text, password);
            ResultText.Text = result.Message;
            AppLog.Diagnostic($"Inicio de sesión: {result.Outcome}.");
            if (result.Outcome == SignInOutcome.Success)
            {
                SignedIn?.Invoke(this, EventArgs.Empty);
                Close();
            }
        }
        catch (Exception exception)
        {
            AppLog.Failure("SignInWindow.SignIn", exception);
            ResultText.Text = $"No se pudo iniciar sesión: {exception.Message}";
        }
        finally
        {
            Array.Clear(password);
            SignInButton.IsEnabled = true;
            Busy.IsActive = false;
        }
    }
}
