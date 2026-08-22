using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace MeetingAssistant.App;

/// <summary>
/// Punto de entrada propio, en lugar del Main que genera XAML
/// (por eso el csproj define DISABLE_XAML_GENERATED_MAIN).
///
/// Existe por una sola razon: esta app tiene que ser de instancia unica.
/// T4 registra el activador COM de AppNotificationManager, asi que un clic en
/// un toast activa la app por COM; sin redireccion Windows lanza un proceso
/// nuevo. Como cerrar la ventana solo la oculta (T2), el proceso viejo sigue
/// vivo e invisible, y el segundo proceso termina peleando por lo mismo: dos
/// iconos de bandeja, un RegisterHotKey que falla (T3) y el bind del puerto de
/// LocalRecordingApiServer que revienta con HttpListenerException 183 — el
/// mismo choque que ya se documento en T2.2. Lo mismo aplica a volver a lanzar
/// la app a mano teniendola oculta en la bandeja.
/// </summary>
public static class Program
{
    private const string InstanceKey = "MeetingAssistant.App.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        AppInstance mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!mainInstance.IsCurrent)
        {
            // Otra instancia ya es la dueña: le pasamos la activacion y salimos
            // sin arrancar UI. El proceso vivo se encarga de mostrarse.
            AppActivationArguments activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            mainInstance.RedirectActivationToAsync(activationArgs).AsTask().GetAwaiter().GetResult();
            return 0;
        }

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });

        return 0;
    }
}
