namespace MeetingAssistant.App.Views;

/// <summary>
/// Una página que puede tener trabajo sin guardar. <see cref="MainWindow"/> le
/// pregunta antes de navegar a otra página o de salir de la app.
///
/// Es una pregunta aparte y no <c>Page.OnNavigatingFrom</c>: ese evento exige
/// decidir la cancelación en el acto, y confirmar con un diálogo es asíncrono.
/// Además, cuando el Frame se entera, el NavigationView ya marcó el destino.
/// </summary>
public interface IConfirmNavigation
{
    bool HasUnsavedChanges { get; }

    /// <summary>true si se puede salir: no hay nada que perder, o el usuario aceptó perderlo.</summary>
    Task<bool> ConfirmLeaveAsync();
}
