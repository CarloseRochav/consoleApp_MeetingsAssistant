using MeetingAssistant.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MeetingAssistant.App.Views;

public sealed partial class RecordPage : Page
{
    public RecordPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<RecordViewModel>();
    }

    private async void ProcessExistingAudio_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".m4a");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        StorageFile? selectedFile = await picker.PickSingleFileAsync();
        if (selectedFile is not null && DataContext is RecordViewModel viewModel)
        {
            await viewModel.ProcessExistingAudioAsync(selectedFile.Path);
        }
    }
}
