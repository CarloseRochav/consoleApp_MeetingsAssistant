using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace MeetingAssistant.App.Views;

/// <summary>
/// Ventana de último recurso para reportar un fallo de arranque.
///
/// Se construye enteramente en código, sin XAML ni InitializeComponent, a
/// propósito: esta ventana existe para mostrar errores que pueden haber
/// ocurrido justamente al cargar XAML o al resolver el contenedor de DI. Si
/// dependiera de un .xaml compilado o de App.Services, podría fallar por la
/// misma causa que intenta reportar y volveríamos al crash silencioso.
/// </summary>
public sealed class StartupErrorWindow : Window
{
    private readonly string _details;

    public StartupErrorWindow(string context, Exception exception)
    {
        _details = BuildDetails(context, exception);

        Title = "MeetingAssistant — error al iniciar";

        var heading = new TextBlock
        {
            Text = "La aplicación no pudo iniciarse",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };

        var summary = new TextBlock
        {
            Text = $"Falló en: {context}\n{exception.GetType().Name}: {exception.Message}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var detailBox = new TextBox
        {
            Text = _details,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Height = 260
        };

        // En WinUI las barras de scroll de un TextBox son propiedades adjuntas
        // de ScrollViewer, no propiedades propias del control.
        ScrollViewer.SetHorizontalScrollBarVisibility(detailBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(detailBox, ScrollBarVisibility.Auto);

        var copyButton = new Button { Content = "Copiar detalles" };
        copyButton.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(_details);
            Clipboard.SetContent(package);
        };

        var closeButton = new Button { Content = "Cerrar" };
        closeButton.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { copyButton, closeButton }
        };

        var logHint = new TextBlock
        {
            Text = $"Copia guardada en: {App.StartupErrorLogPath}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 8, 0, 0)
        };

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children = { heading, summary, detailBox, logHint, buttons }
        };
    }

    private static string BuildDetails(string context, Exception exception) =>
        $"""
         Contexto : {context}
         Fecha    : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}
         Log      : {App.StartupErrorLogPath}

         {exception}
         """;
}
