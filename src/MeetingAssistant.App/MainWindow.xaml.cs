using MeetingAssistant.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAssistant.App;

public sealed partial class MainWindow : Window
{
    private bool _exitRequestedFromTray;
    private bool _restoringSelection;
    private string _currentTag = "record";

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

    /// <summary>
    /// Si la página actual tiene trabajo sin guardar, pregunta antes de dejarla.
    /// Trae la ventana al frente primero: al salir desde la bandeja puede estar
    /// oculta, y un diálogo en una ventana oculta esperaría para siempre.
    /// </summary>
    public async Task<bool> ConfirmLeaveCurrentPageAsync()
    {
        if (ContentFrame.Content is not IConfirmNavigation page || !page.HasUnsavedChanges) return true;

        ShowFromTray();
        return await page.ConfirmLeaveAsync();
    }

    private async void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_restoringSelection) return;
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;

        Type page = tag switch
        {
            "record" => typeof(RecordPage),
            "history" => typeof(HistoryPage),
            "costs" => typeof(CostPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(RecordPage)
        };

        if (ContentFrame.CurrentSourcePageType == page) return;

        if (!await ConfirmLeaveCurrentPageAsync())
        {
            // El NavigationView ya marcó el destino; se lo devuelve a la página
            // en la que el usuario se quedó.
            _restoringSelection = true;
            try
            {
                sender.SelectedItem = sender.MenuItems
                    .OfType<NavigationViewItem>()
                    .FirstOrDefault(menuItem => menuItem.Tag is string itemTag && itemTag == _currentTag);
            }
            finally
            {
                _restoringSelection = false;
            }

            return;
        }

        ContentFrame.Navigate(page);
        _currentTag = tag;
    }
}
