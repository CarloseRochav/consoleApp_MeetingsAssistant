using System.ComponentModel;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAssistant.App.Views;

public sealed partial class PromptEditorPanel : UserControl
{
    /// <summary>Mientras el code-behind devuelve el desplegable a su valor, su SelectionChanged no cuenta.</summary>
    private bool _revertingSelection;

    public PromptEditorPanel()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<PromptEditorViewModel>();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.ConfirmDiscard = ShowDiscardDialogAsync;
        _revertingSelection = true;
        PromptCombo.ItemsSource = ViewModel.Prompts;
        _revertingSelection = false;
        SelectPromptInCombo();
        ApplyVisibility();
    }

    private PromptEditorViewModel ViewModel => (PromptEditorViewModel)DataContext;

    public bool HasUnsavedEdits => ViewModel.HasUnsavedEdits;

    /// <summary>Para quien saca al usuario de la página: true si se puede seguir.</summary>
    public Task<bool> ConfirmLeaveAsync() => ViewModel.ConfirmLeaveAsync();

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PromptEditorViewModel.HasStructuredOutputWarning) or null)
        {
            ApplyVisibility();
        }
    }

    private async void PromptCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_revertingSelection || PromptCombo.SelectedItem is not PromptDefinition next) return;

        if (!await ViewModel.TrySelectPromptAsync(next))
        {
            SelectPromptInCombo();
        }
    }

    private async void Versions_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PromptVersionItem item)
        {
            await ViewModel.LoadVersionAsync(item);
        }
    }

    /// <summary>Deja el desplegable en el prompt que tiene el ViewModel, sin disparar otra pregunta.</summary>
    private void SelectPromptInCombo()
    {
        _revertingSelection = true;
        try
        {
            PromptCombo.SelectedItem = ViewModel.Prompts.FirstOrDefault(prompt =>
                string.Equals(prompt.Id, ViewModel.SelectedPrompt?.Id, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _revertingSelection = false;
        }
    }

    /// <summary>
    /// Ante la duda, no se pierde nada: si el diálogo no se puede mostrar (otro
    /// ContentDialog abierto, panel sin XamlRoot) la respuesta es "seguir
    /// editando".
    /// </summary>
    private async Task<bool> ShowDiscardDialogAsync(string promptName)
    {
        if (XamlRoot is null) return false;

        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Hay cambios sin guardar",
                Content = $"Los cambios en «{promptName}» no se guardaron. Si sigues, se pierden.",
                PrimaryButtonText = "Descartar cambios",
                CloseButtonText = "Seguir editando",
                DefaultButton = ContentDialogButton.Close
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception exception)
        {
            App.LogStartupFailure("PromptEditorPanel.ShowDiscardDialog", exception);
            return false;
        }
    }

    /// <summary>Mismo criterio que el resto de las páginas: visibilidad en code-behind, sin converters.</summary>
    private void ApplyVisibility()
    {
        StructuredWarning.IsOpen = ViewModel.HasStructuredOutputWarning;
        StructuredWarning.Visibility = ViewModel.HasStructuredOutputWarning ? Visibility.Visible : Visibility.Collapsed;
    }
}
