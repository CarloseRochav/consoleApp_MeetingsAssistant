using MeetingAssistant.App.Startup;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace MeetingAssistant.App.Views;

/// <summary>
/// Muestra un error: el mensaje arriba, la traza plegada y un botón para copiar
/// todo. Se usa igual en todas las páginas:
/// <c>&lt;views:ErrorPanel Details="{Binding ErrorDetails}" /&gt;</c>.
///
/// <see cref="Details"/> admite tanto un mensaje suelto como el
/// <c>ToString()</c> de una excepción. Con texto vacío el panel se cierra.
/// </summary>
public sealed partial class ErrorPanel : UserControl
{
    public static readonly DependencyProperty DetailsProperty = DependencyProperty.Register(
        nameof(Details),
        typeof(string),
        typeof(ErrorPanel),
        new PropertyMetadata(null, (sender, _) => ((ErrorPanel)sender).Render()));

    public ErrorPanel()
    {
        InitializeComponent();
    }

    public string? Details
    {
        get => (string?)GetValue(DetailsProperty);
        set => SetValue(DetailsProperty, value);
    }

    private void Render()
    {
        string details = Details?.Trim() ?? string.Empty;
        CopiedText.Text = string.Empty;

        if (details.Length == 0)
        {
            Bar.IsOpen = false;
            return;
        }

        SummaryText.Text = Summarize(details);
        DetailsBox.Text = details;

        // El desplegable sólo aporta si hay algo más que el mensaje: una traza,
        // o excepciones internas. Para un mensaje de una línea sería un
        // desplegable que repite lo que ya se lee arriba.
        bool hasMore = details.Contains('\n');
        DetailsExpander.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
        DetailsExpander.IsExpanded = false;

        // Si el usuario cerró el panel y llega otro error, tiene que volver a verse.
        Bar.IsOpen = true;
    }

    /// <summary>
    /// La primera línea, sin el nombre del tipo de excepción adelante:
    /// <c>"System.InvalidOperationException: Falta la clave"</c> se lee
    /// <c>"Falta la clave"</c>. El nombre del tipo sigue en el detalle.
    /// </summary>
    internal static string Summarize(string details)
    {
        string firstLine = details.Split('\n', 2)[0].Trim();
        string withoutType = ExceptionTypePrefix().Replace(firstLine, string.Empty);
        return withoutType.Length > 0 ? withoutType : firstLine;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            // Con fecha y versión adelante: lo que se pega en un chat o en un
            // ticket tiene que decir de cuándo es y de qué build salió.
            package.SetText(
                $"Meeting Assistant {typeof(ErrorPanel).Assembly.GetName().Version} — {DateTime.Now:yyyy-MM-dd HH:mm:ss}" +
                $"{Environment.NewLine}{Environment.NewLine}{Details?.Trim()}");
            Clipboard.SetContent(package);
            // Sin esto el contenido se pierde al cerrar la app.
            Clipboard.Flush();

            CopiedText.Text = $"Copiado a las {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception exception)
        {
            // El portapapeles puede estar tomado por otra app. El texto sigue
            // seleccionable a mano en el detalle.
            AppLog.Failure("ErrorPanel.Copy", exception);
            CopiedText.Text = "No se pudo copiar. Puedes seleccionar el texto a mano.";
        }
    }

    [GeneratedRegex(@"^(?:[A-Za-z_][\w`]*\.)*[A-Za-z_][\w`]*(?:Exception|Error)(?:\s*\(0x[0-9A-Fa-f]+\))?:\s*")]
    private static partial Regex ExceptionTypePrefix();
}
