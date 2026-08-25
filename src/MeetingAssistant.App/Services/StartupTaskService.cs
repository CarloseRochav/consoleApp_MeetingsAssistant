using Windows.ApplicationModel;

namespace MeetingAssistant.App.Services;

public sealed class StartupTaskService
{
    public const string TaskId = "MeetingAssistantStartup";

    public async Task<StartupTaskState> GetStateAsync()
    {
        StartupTask task = await GetTaskAsync();
        return task.State;
    }

    public async Task<StartupTaskState> EnableAsync()
    {
        StartupTask task = await GetTaskAsync();
        return task.State == StartupTaskState.Disabled
            ? await task.RequestEnableAsync()
            : task.State;
    }

    public async Task<StartupTaskState> DisableAsync()
    {
        StartupTask task = await GetTaskAsync();
        if (task.State == StartupTaskState.Enabled)
        {
            task.Disable();
        }

        return task.State;
    }

    private static async Task<StartupTask> GetTaskAsync()
    {
        try
        {
            return await StartupTask.GetAsync(TaskId);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"No se encontró la tarea de inicio '{TaskId}' en el manifiesto del paquete.",
                exception);
        }
    }
}
