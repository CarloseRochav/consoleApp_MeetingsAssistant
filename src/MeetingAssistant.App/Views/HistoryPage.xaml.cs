using System.ComponentModel;
using MeetingAssistant.App.Services;
using MeetingAssistant.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAssistant.App.Views;

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<HistoryViewModel>();
        Loaded += HistoryPage_Loaded;
        Unloaded += HistoryPage_Unloaded;
        ActualThemeChanged += (_, _) => UpdatePreview();
    }

    private HistoryViewModel ViewModel => (HistoryViewModel)DataContext;

    /// <summary>
    /// La lista se carga acá y no en el constructor: <c>MainWindow</c> navega con
    /// <c>ContentFrame.Navigate(typeof(HistoryPage))</c>, así que la página se
    /// reconstruye en cada visita y nada de su estado sobrevive a salir y volver
    /// a entrar. Cargar en <c>Loaded</c> también es lo que hace que al volver a
    /// la pestaña se vean las reuniones grabadas mientras no estabas mirando.
    /// </summary>
    private async void HistoryPage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        try
        {
            await ReportPreview.EnsureCoreWebView2Async();
        }
        catch (Exception exception)
        {
            // Mismo criterio que RecordPage: si el runtime de WebView2 no está,
            // la página tiene que seguir sirviendo — la pestaña "Markdown"
            // muestra el reporte igual.
            ViewModel.ErrorDetails = $"No se pudo iniciar la vista previa Markdown: {exception.Message}";
        }

        await ViewModel.RefreshCommand.ExecuteAsync(null);
        ApplyVisibility();
        UpdatePreview();
    }

    private void HistoryPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HistoryViewModel.SelectedReportMarkdown):
            case null:
                UpdatePreview();
                ApplyVisibility();
                break;

            case nameof(HistoryViewModel.IsLoading):
            case nameof(HistoryViewModel.IsEmpty):
            case nameof(HistoryViewModel.IsBusy):
            case nameof(HistoryViewModel.HasSelection):
                ApplyVisibility();
                break;
        }
    }

    /// <summary>
    /// La visibilidad se resuelve en code-behind, no con bindings a
    /// <c>bool</c>. WinUI no convierte <c>bool</c> a <c>Visibility</c> en un
    /// <c>{Binding}</c> clásico, y el proyecto ya resuelve esto así en
    /// <c>SettingsPage.UpdateProviderPanels()</c>: se sigue el precedente en vez
    /// de introducir un converter para cuatro paneles.
    /// </summary>
    private void ApplyVisibility()
    {
        LoadingBar.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = ViewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = ViewModel.HasSelection ? Visibility.Visible : Visibility.Collapsed;
        BusyBar.Visibility = ViewModel.IsBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReportViewer_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        // El guard no es defensivo por gusto: navegando rápido entre pestañas se
        // llega acá antes de que EnsureCoreWebView2Async haya terminado, y sin él
        // la página revienta.
        if (ReportPreview.CoreWebView2 is null) return;

        bool darkTheme = ActualTheme == ElementTheme.Dark;
        ReportPreview.NavigateToString(
            MarkdownPreviewRenderer.ToHtmlDocument(ViewModel.SelectedReportMarkdown, darkTheme));
    }
}
