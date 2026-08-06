using MeetingAssistant.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAssistant.App.Views;

public sealed partial class RecordPage : Page
{
    public RecordPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<RecordViewModel>();
    }
}
