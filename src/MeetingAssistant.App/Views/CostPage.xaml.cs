using System.ComponentModel;
using MeetingAssistant.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAssistant.App.Views;

public sealed partial class CostPage : Page
{
    public CostPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<CostViewModel>();
        Loaded += CostPage_Loaded;
        Unloaded += CostPage_Unloaded;
    }

    private CostViewModel ViewModel => (CostViewModel)DataContext;

    /// <summary>
    /// Se carga en <c>Loaded</c> por el mismo motivo que <c>HistoryPage</c>: el
    /// <c>Frame</c> reconstruye la página en cada visita, y así al volver se ven
    /// los reportes generados mientras no estabas mirando.
    /// </summary>
    private async void CostPage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        await ViewModel.RefreshCommand.ExecuteAsync(null);
        ApplyVisibility();
    }

    private void CostPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CostViewModel.IsLoading) or nameof(CostViewModel.HasData) or null)
        {
            ApplyVisibility();
        }
    }

    /// <summary>
    /// En code-behind, siguiendo a <c>HistoryPage.ApplyVisibility()</c>. Se
    /// vuelve a evaluar al terminar cada carga (<c>IsLoading</c> pasa a false
    /// después de llenar las colecciones), que es cuando <c>Caveats</c> ya tiene
    /// su contenido definitivo.
    /// </summary>
    private void ApplyVisibility()
    {
        bool showEmpty = !ViewModel.IsLoading && !ViewModel.HasData && ViewModel.ErrorDetails is null;

        LoadingBar.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        DataPanel.Visibility = ViewModel.HasData ? Visibility.Visible : Visibility.Collapsed;
        CaveatsPanel.Visibility = ViewModel.Caveats.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
