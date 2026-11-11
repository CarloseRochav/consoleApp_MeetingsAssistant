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
            ViewModel.ErrorDetails = $"No se pudo iniciar la vista previa Markdown: {exception.Message}";
        }
    }

    private void RecordPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Detach();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecordViewModel.LastGeneratedReport) or null) UpdatePreview();
    }

    private void ReportViewer_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdatePreview();

    private void UpdatePreview()
    {
        if (ReportPreview.CoreWebView2 is null) return;
        ReportPreview.NavigateToString(
            MarkdownPreviewRenderer.ToHtmlDocument(ViewModel.LastGeneratedReport, ActualTheme == ElementTheme.Dark));
    }

    private async void ProcessExistingAudio_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".m4a");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        StorageFile? selectedFile = await picker.PickSingleFileAsync();
        if (selectedFile is not null) await ViewModel.ProcessExistingAudioAsync(selectedFile.Path);
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QueueItemViewModel item) RecordViewModel.ShowInExplorer(item);
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QueueItemViewModel item) ViewModel.Retry(item);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QueueItemViewModel item) ViewModel.Remove(item);
    }
}
