using System.Text.Json;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// La cola de subidas en disco: un JSON por reunión en una carpeta de esta máquina (como el
/// teléfono). Se escribe a un temporal y se mueve, para que un corte a mitad nunca deje un archivo
/// roto. El enlace de subida (una credencial de 60 minutos para un solo blob) se guarda cifrado con
/// <see cref="ISecretProtector"/> (DPAPI); el resto no es secreto.
/// </summary>
public sealed class JsonQueuedMeetingStore : IQueuedMeetingStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly ISecretProtector _protector;
    private readonly Action<string, Exception> _failure;

    public JsonQueuedMeetingStore(string directory, ISecretProtector protector, Action<string, Exception>? failure = null)
    {
        _directory = directory;
        _protector = protector;
        _failure = failure ?? ((_, _) => { });
        Directory.CreateDirectory(directory);
    }

    public IReadOnlyList<QueuedMeeting> All()
    {
        var meetings = new List<QueuedMeeting>();
        foreach (string file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            if (Read(file) is { } meeting) meetings.Add(meeting);
        }

        return meetings;
    }

    public QueuedMeeting? Get(string id)
    {
        string file = PathOf(id);
        return File.Exists(file) ? Read(file) : null;
    }

    public void Save(QueuedMeeting meeting)
    {
        QueuedMeeting stored = meeting.UploadUrl is { } url ? meeting with { UploadUrl = _protector.Protect(url) } : meeting;
        string file = PathOf(meeting.Id);
        string temporary = file + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(stored, Json));
        File.Move(temporary, file, overwrite: true);
    }

    public void Delete(string id)
    {
        string file = PathOf(id);
        if (File.Exists(file)) File.Delete(file);
    }

    private QueuedMeeting? Read(string file)
    {
        try
        {
            QueuedMeeting? meeting = JsonSerializer.Deserialize<QueuedMeeting>(File.ReadAllBytes(file), Json);
            if (meeting?.UploadUrl is { } protectedUrl)
            {
                // Sin poder descifrarlo (otro perfil de Windows), el enlace se pierde y la cola pide otro.
                meeting = meeting with { UploadUrl = _protector.TryUnprotect(protectedUrl) };
            }

            return meeting;
        }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
        {
            // Un archivo que no se puede leer no puede frenar el resto de la cola: se aparta y se dice.
            _failure($"JsonQueuedMeetingStore.Read({Path.GetFileName(file)})", exception);
            try
            {
                File.Move(file, file + ".bad", overwrite: true);
            }
            catch (IOException)
            {
            }

            return null;
        }
    }

    private string PathOf(string id)
    {
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Id de reunión no válido.", nameof(id));
        }

        return Path.Combine(_directory, $"{id}.json");
    }
}
