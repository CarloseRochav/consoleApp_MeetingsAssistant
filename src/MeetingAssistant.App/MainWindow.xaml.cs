using MeetingAssistant.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAssistant.App;

public sealed partial class MainWindow : Window
{
    private bool _exitRequestedFromTray;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Closing += AppWindow_Closing;
        ContentFrame.Navigate(typeof(RecordPage));
    }

    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    public void BeginExitFromTray()
    {
        _exitRequestedFromTray = true;
        Close();
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_exitRequestedFromTray) return;

        args.Cancel = true;
        AppWindow.Hide();
    }

    private void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;

        Type page = tag switch
        {
            "record" => typeof(RecordPage),
            "history" => typeof(HistoryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(RecordPage)
        };

        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
    }
}
