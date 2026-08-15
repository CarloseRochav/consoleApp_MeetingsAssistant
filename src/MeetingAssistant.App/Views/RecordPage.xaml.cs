using System.ComponentModel;
using MeetingAssistant.App.Services;
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
        Loaded += RecordPage_Loaded;
        Unloaded += RecordPage_Unloaded;
        ActualThemeChanged += (_, _) => UpdatePreview();
    }

    private RecordViewModel ViewModel => (RecordViewModel)DataContext;

    private async void RecordPage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        try
        {
            await ReportPreview.EnsureCoreWebView2Async();
            UpdatePreview();
        }
        catch (Exception exception)
        {
            ViewModel.ErrorDetails =
                $"No se pudo iniciar la vista previa Markdown: {exception.Message}";
        }
    }

    private void RecordPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecordViewModel.LastGeneratedReport) or null)
        {
            UpdatePreview();
        }
    }

    private void ReportViewer_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (ReportPreview.CoreWebView2 is null)
        {
            return;
        }

        bool darkTheme = ActualTheme == ElementTheme.Dark;
        ReportPreview.NavigateToString(
            MarkdownPreviewRenderer.ToHtmlDocument(ViewModel.LastGeneratedReport, darkTheme));
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
        if (selectedFile is not null)
        {
            await ViewModel.ProcessExistingAudioAsync(selectedFile.Path);
        }
    }

    private async void AttachTranscriptFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".txt");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        StorageFile? selectedFile = await picker.PickSingleFileAsync();
        if (selectedFile is not null)
        {
            await ViewModel.LoadTranscriptFileAsync(selectedFile.Path);
        }
    }
}
