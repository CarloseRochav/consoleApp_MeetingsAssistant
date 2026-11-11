using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace MeetingAssistant.App.Themes;

/// <summary>
/// Pinta una pastilla de estado (esperando, enviando, en el servidor, listo, falló)
/// con el pincel del tema activo. El tono lo pone el ViewModel; el color vive en
/// <c>AppTheme.xaml</c>.
/// </summary>
public sealed class StatusToneBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string tone = value as string ?? "waiting";
        string role = parameter as string ?? "Foreground";
        string name = tone switch
        {
            "uploading" => "Uploading",
            "processing" => "Processing",
            "ready" => "Ready",
            "failed" => "Failed",
            _ => "Waiting"
        };

        if (Application.Current?.Resources[$"Status{name}{role}Brush"] is Brush brush)
            return brush;

        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
