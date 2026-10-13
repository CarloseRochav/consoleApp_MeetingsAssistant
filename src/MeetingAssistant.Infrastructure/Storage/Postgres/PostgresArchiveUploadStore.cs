using MeetingAssistant.Core.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace MeetingAssistant.Infrastructure.Storage.Postgres;

/// <summary>
/// Implementación Postgres de <see cref="IArchiveUploadStore"/>.
///
/// La diferencia con la de SQLite es <c>machine_name</c>. Las rutas locales son
/// de una máquina, y esta base la comparten varias: cada una sólo ve y retoma
/// <b>sus</b> subidas. Sin eso, la segunda máquina marcaría como fallidas las
/// pendientes de la primera por no encontrar el archivo.
/// </summary>
public sealed class PostgresArchiveUploadStore : IArchiveUploadStore
{
    private const string Columns =
        "id, session_id, kind, local_path, remote_name, state, attempts, remote_url, last_error";

    private readonly PostgresDatabase _database;
    private readonly string _machineName;

    public PostgresArchiveUploadStore(PostgresDatabase database, string? machineName = null)
    {
        _database = database;
        _machineName = machineName ?? Environment.MachineName;
    }

    public async Task<ArchiveUpload> EnqueueAsync(
        long sessionId,
        ArchiveKind kind,
        string localPath,
        string remoteName,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);

        await using (var insert = new NpgsqlCommand(
            """
            insert into cloud_upload(session_id, machine_name, kind, local_path, remote_name, state, created_at_utc, updated_at_utc)
            values (@session, @machine, @kind, @path, @remote, 'pending', now(), now())
            on conflict (machine_name, kind, local_path) do nothing;
            """,
            connection))
        {
            insert.Parameters.Add("session", NpgsqlDbType.Bigint).Value = sessionId;
            insert.Parameters.Add("machine", NpgsqlDbType.Text).Value = _machineName;
            insert.Parameters.Add("kind", NpgsqlDbType.Text).Value = ToText(kind);
            insert.Parameters.Add("path", NpgsqlDbType.Text).Value = localPath;
            insert.Parameters.Add("remote", NpgsqlDbType.Text).Value = remoteName;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = new NpgsqlCommand(
            $"select {Columns} from cloud_upload where machine_name = @machine and kind = @kind and local_path = @path;",
            connection);
        select.Parameters.Add("machine", NpgsqlDbType.Text).Value = _machineName;
        select.Parameters.Add("kind", NpgsqlDbType.Text).Value = ToText(kind);
        select.Parameters.Add("path", NpgsqlDbType.Text).Value = localPath;

        await using NpgsqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return Read(reader);
    }

    public async Task MarkUploadedAsync(long uploadId, string remoteUrl, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update cloud_upload
               set state = 'uploaded', attempts = attempts + 1, remote_url = @url,
                   last_error = null, updated_at_utc = now()
             where id = @id;
            """,
            connection);
        command.Parameters.Add("url", NpgsqlDbType.Text).Value = remoteUrl;
        command.Parameters.Add("id", NpgsqlDbType.Bigint).Value = uploadId;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(long uploadId, string error, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update cloud_upload
               set state = 'failed', attempts = attempts + 1, last_error = @error, updated_at_utc = now()
             where id = @id;
            """,
            connection);
        command.Parameters.Add("error", NpgsqlDbType.Text).Value = error;
        command.Parameters.Add("id", NpgsqlDbType.Bigint).Value = uploadId;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArchiveUpload>> ListUnfinishedAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"select {Columns} from cloud_upload where machine_name = @machine and state <> 'uploaded' order by id;",
            connection);
        command.Parameters.Add("machine", NpgsqlDbType.Text).Value = _machineName;

        var results = new List<ArchiveUpload>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(Read(reader));

        return results;
    }

    private static ArchiveUpload Read(NpgsqlDataReader reader) => new(
        Id: reader.GetInt64(0),
        SessionId: reader.GetInt64(1),
        Kind: reader.GetString(2) == "audio" ? ArchiveKind.Audio : ArchiveKind.Report,
        LocalPath: reader.GetString(3),
        RemoteName: reader.GetString(4),
        State: reader.GetString(5) switch
        {
            "uploaded" => ArchiveUploadState.Uploaded,
            "failed" => ArchiveUploadState.Failed,
            _ => ArchiveUploadState.Pending
        },
        Attempts: reader.GetInt32(6),
        RemoteUrl: reader.IsDBNull(7) ? null : reader.GetString(7),
        LastError: reader.IsDBNull(8) ? null : reader.GetString(8));

    private static string ToText(ArchiveKind kind) => kind == ArchiveKind.Audio ? "audio" : "report";
}
