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
        _restoringSelection = true;
        RecordNav.IsChecked = true;
        _restoringSelection = false;
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

    private async void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_restoringSelection) return;
        if (sender is not RadioButton { Tag: string tag }) return;

        Type page = tag switch
        {
            "record" => typeof(RecordPage),
            "history" => typeof(HistoryPage),
            "costs" => typeof(CostPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(RecordPage)
        };

        if (ContentFrame.CurrentSourcePageType == page)
        {
            _currentTag = tag;
            return;
        }

        if (!await ConfirmLeaveCurrentPageAsync())
        {
            // El radio ya quedó en el destino; se devuelve a la página en la que
            // el usuario se quedó. El evento de ese cambio no navega otra vez.
            _restoringSelection = true;
            try
            {
                RadioButton current = _currentTag switch
                {
                    "history" => HistoryNav,
                    "costs" => CostsNav,
                    "settings" => SettingsNav,
                    _ => RecordNav
                };
                current.IsChecked = true;
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
