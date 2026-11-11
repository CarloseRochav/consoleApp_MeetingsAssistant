using MeetingAssistant.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAssistant.App.Views;

/// <summary>Mis ajustes en el servidor (la capa propia de la API). Se cargan al abrir el panel.</summary>
public sealed partial class ServerSettingsPanel : UserControl
{
    public ServerSettingsPanel()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<ServerSettingsViewModel>();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    private ServerSettingsViewModel ViewModel => (ServerSettingsViewModel)DataContext;

    public bool HasUnsavedChanges => ViewModel.HasUnsavedChanges;
}
