using MeetingAssistant.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAssistant.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ContentFrame.Navigate(typeof(RecordPage));
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
