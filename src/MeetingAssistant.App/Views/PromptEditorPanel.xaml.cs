using System.ComponentModel;
using MeetingAssistant.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAssistant.App.Views;

public sealed partial class PromptEditorPanel : UserControl
{
    public PromptEditorPanel()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<PromptEditorViewModel>();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ApplyVisibility();
    }

    private PromptEditorViewModel ViewModel => (PromptEditorViewModel)DataContext;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PromptEditorViewModel.HasStructuredOutputWarning) or null)
        {
            ApplyVisibility();
        }
    }

    /// <summary>Mismo criterio que el resto de las páginas: visibilidad en code-behind, sin converters.</summary>
    private void ApplyVisibility()
    {
        StructuredWarning.IsOpen = ViewModel.HasStructuredOutputWarning;
        StructuredWarning.Visibility = ViewModel.HasStructuredOutputWarning ? Visibility.Visible : Visibility.Collapsed;
    }
}
