using MeetingAssistant.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace MeetingAssistant.Infrastructure.Storage.Sqlite;

/// <summary>
/// Implementación SQLite de <see cref="IArchiveUploadStore"/>, sobre la tabla
/// <c>cloud_upload</c> del esquema v2.
/// </summary>
public sealed class SqliteArchiveUploadStore : IArchiveUploadStore
{
    private const string Columns =
        "id, session_id, kind, local_path, remote_name, state, attempts, remote_url, last_error";

    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteArchiveUploadStore(SqliteConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<ArchiveUpload> EnqueueAsync(
        long sessionId,
        ArchiveKind kind,
        string localPath,
        string remoteName,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        // "do nothing" y no "do update": si la fila ya existe se devuelve tal
        // cual, con su nombre remoto original. Cambiarlo dejaría un blob
        // huérfano con el nombre viejo.
        command.CommandText =
            $"""
            insert into cloud_upload(session_id, kind, local_path, remote_name, state, created_at_utc, updated_at_utc)
            values ($session, $kind, $path, $remote, 'pending', $now, $now)
            on conflict(kind, local_path) do nothing;

            select {Columns} from cloud_upload where kind = $kind and local_path = $path;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$kind", ToText(kind));
        command.Parameters.AddWithValue("$path", localPath);
        command.Parameters.AddWithValue("$remote", remoteName);
        command.Parameters.AddWithValue("$now", SqliteValueConversions.ToText(DateTimeOffset.UtcNow));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return Read(reader);
    }

    public async Task MarkUploadedAsync(long uploadId, string remoteUrl, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            update cloud_upload
               set state = 'uploaded', attempts = attempts + 1, remote_url = $url,
                   last_error = null, updated_at_utc = $now
             where id = $id;
            """;
        command.Parameters.AddWithValue("$url", remoteUrl);
        command.Parameters.AddWithValue("$now", SqliteValueConversions.ToText(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", uploadId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(long uploadId, string error, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            update cloud_upload
               set state = 'failed', attempts = attempts + 1, last_error = $error, updated_at_utc = $now
             where id = $id;
            """;
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$now", SqliteValueConversions.ToText(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", uploadId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArchiveUpload>> ListUnfinishedAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"select {Columns} from cloud_upload where state <> 'uploaded' order by id;";

        var results = new List<ArchiveUpload>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(Read(reader));

        return results;
    }

    public async Task<IReadOnlyList<ArchiveUpload>> ListUploadedAsync(
        ArchiveKind kind,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.Open();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"select {Columns} from cloud_upload where state = 'uploaded' and kind = $kind order by id;";
        command.Parameters.AddWithValue("$kind", ToText(kind));

        var results = new List<ArchiveUpload>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(Read(reader));

        return results;
    }

    private static ArchiveUpload Read(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        SessionId: reader.GetInt64(1),
        Kind: reader.GetString(2) == "audio" ? ArchiveKind.Audio : ArchiveKind.Report,
        LocalPath: reader.GetString(3),
        RemoteName: reader.GetString(4),
        State: ToState(reader.GetString(5)),
        Attempts: reader.GetInt32(6),
        RemoteUrl: reader.IsDBNull(7) ? null : reader.GetString(7),
        LastError: reader.IsDBNull(8) ? null : reader.GetString(8));

    internal static string ToText(ArchiveKind kind) => kind == ArchiveKind.Audio ? "audio" : "report";

    internal static ArchiveUploadState ToState(string text) => text switch
    {
        "uploaded" => ArchiveUploadState.Uploaded,
        "failed" => ArchiveUploadState.Failed,
        _ => ArchiveUploadState.Pending
    };
}
