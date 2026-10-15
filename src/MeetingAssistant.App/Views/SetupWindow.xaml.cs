using MeetingAssistant.App.Startup;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace MeetingAssistant.App.Views;

/// <summary>
/// Lo que se abre en vez de la ventana principal cuando faltan ajustes
/// obligatorios: el caso normal de una instalación nueva, cuyo paquete no trae
/// ninguna clave. Antes ese caso mostraba la ventana de error de arranque, que
/// sólo podía decir qué faltaba, no dejar escribirlo.
///
/// Al guardar reinicia la app: los clientes de transcripción y LLM se
/// construyen una sola vez al arrancar, y la capa de la nube se lee al
/// arrancar. Si después de reiniciar todavía falta algo (por ejemplo, la nube
/// eligió un proveedor cuya clave no está), esta ventana vuelve a aparecer con
/// la lista nueva.
/// </summary>
public sealed partial class SetupWindow : Window
{
    public SetupWindow(IReadOnlyList<string> missingSettings)
    {
        InitializeComponent();
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 900));

        MissingInfo.Message = string.Join(Environment.NewLine, missingSettings);
        Form.Saved += (_, _) => Restart();
    }

    private void Restart()
    {
        AppLog.Diagnostic("Primer arranque: ajustes guardados, se reinicia la app.");

        // Si reinicia, esta llamada no vuelve: el proceso termina.
        Windows.ApplicationModel.Core.AppRestartFailureReason reason = AppInstance.Restart(string.Empty);

        AppLog.Diagnostic($"Primer arranque: AppInstance.Restart no pudo reiniciar ({reason}).");
        MissingInfo.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
        MissingInfo.Title = "Guardado";
        MissingInfo.Message = "No se pudo reiniciar sola: cierra esta ventana y vuelve a abrir Meeting Assistant.";
    }
}
