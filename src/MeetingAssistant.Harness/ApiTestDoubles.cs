using System.Net;
using System.Text;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

/// <summary>Las comprobaciones de un autotest: imprime [OK]/[MAL] (la CI cuenta los [MAL]) y lleva la cuenta.</summary>
internal sealed class Checks(string title)
{
    public int Failed { get; private set; }

    public int Passed { get; private set; }

    public void Check(string description, bool passed)
    {
        if (passed) Passed++;
        else Failed++;
        Console.WriteLine($"  [{(passed ? "OK" : "MAL")}] {description}");
    }

    public int Finish()
    {
        Console.WriteLine();
        Console.WriteLine($"{title}: {Passed} OK, {Failed} MAL.");
        return Failed == 0 ? 0 : 1;
    }
}

/// <summary>Un transporte HTTP falso: contesta lo que diga <see cref="Respond"/> y guarda cada petición.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, Dictionary<string, string> Headers, byte[] Body)> Requests { get; } = [];

    public Func<HttpRequestMessage, byte[], HttpResponseMessage> Respond { get; set; } =
        (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add((request.Method, request.RequestUri!.ToString(), headers, body));
        return Respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>
/// Una API de DevLog en memoria, con lo mínimo para la cola y la sesión: trabajos que avanzan cuando
/// el test lo dice, reportes por sesión, y cada respuesta reemplazable para forzar un fallo.
/// </summary>
internal sealed class FakeDevLogApi : IDevLogApi
{
    public const string GoodToken = "dlg_good";
    public const string Password = "correct horse";

    public Dictionary<long, JobRow> Jobs { get; } = [];

    public Dictionary<long, List<ApiReport>> Reports { get; } = [];

    public List<string> Calls { get; } = [];

    public List<string> Sources { get; } = [];

    public string ValidToken { get; set; } = GoodToken;

    public long MaxBytes { get; set; } = 1L << 30;

    /// <summary>Si no es null, la próxima llamada a ese método devuelve este error (una vez).</summary>
    public Dictionary<string, ApiError> FailNext { get; } = [];

    /// <summary>Lo que hace el servidor con un trabajo al confirmarlo: por defecto, queda en cola.</summary>
    public Func<JobRow, string> StatusAfterConfirm { get; set; } = _ => "queued";

    /// <summary>La próxima confirmación se hace en el servidor pero su respuesta se pierde.</summary>
    public bool LoseConfirmAnswerOnce { get; set; }

    public int DevicesCreated { get; private set; }

    public string? LastDeviceName { get; private set; }

    private long _nextJob = 100;
    private long _nextReport = 500;
    private long _nextSession = 900;

    /// <summary>El worker termina el trabajo: sesión, reporte, done.</summary>
    public long CompleteJob(long idJob, string promptId = "assignment-meeting", string markdown = "# Resumen\n\nTexto del reporte.")
    {
        JobRow job = Jobs[idJob];
        long idSession = job.IdSession ?? _nextSession++;
        var report = new ApiReport(_nextReport++, idSession, promptId, "v1", markdown, "AzureFoundry", "grok-4.6", 1200, 300, 4321, DateTimeOffset.UtcNow);
        if (!Reports.TryGetValue(idSession, out List<ApiReport>? list)) Reports[idSession] = list = [];
        list.Add(report);
        Jobs[idJob] = job with { Status = "done", IdSession = idSession };
        return idSession;
    }

    public void FailJob(long idJob, string error) => Jobs[idJob] = Jobs[idJob] with { Status = "failed", Error = error };

    private bool Fails<T>(string method, out ApiResult<T> failure)
    {
        Calls.Add(method);
        if (FailNext.Remove(method, out ApiError? error))
        {
            failure = new ApiResult<T>.Failure(error);
            return true;
        }

        failure = null!;
        return false;
    }

    private ApiResult<T>? Unauthorized<T>(string token) =>
        token == ValidToken ? null : new ApiResult<T>.Failure(new ApiError.Unauthorized());

    public Task<ApiResult<LoginResult>> LoginAsync(string username, ReadOnlyMemory<char> password, CancellationToken cancellationToken = default)
    {
        if (Fails(nameof(LoginAsync), out ApiResult<LoginResult> failure)) return Task.FromResult(failure);
        ApiResult<LoginResult> result = username == "carlos" && password.Span.SequenceEqual(Password)
            ? new ApiResult<LoginResult>.Success(new LoginResult("jwt_user_token", new LoginUser(1, "carlos", "admin")))
            : new ApiResult<LoginResult>.Failure(new ApiError.Unauthorized());
        return Task.FromResult(result);
    }

    public Task<ApiResult<DeviceCreated>> CreateDeviceAsync(string userToken, string name, CancellationToken cancellationToken = default)
    {
        if (Fails(nameof(CreateDeviceAsync), out ApiResult<DeviceCreated> failure)) return Task.FromResult(failure);
        DevicesCreated++;
        LastDeviceName = name;
        return Task.FromResult<ApiResult<DeviceCreated>>(new ApiResult<DeviceCreated>.Success(new DeviceCreated(7, ValidToken)));
    }

    public Task<ApiResult<DeviceIdentity>> MeAsync(string deviceToken, CancellationToken cancellationToken = default)
    {
        if (Fails(nameof(MeAsync), out ApiResult<DeviceIdentity> failure)) return Task.FromResult(failure);
        return Task.FromResult(Unauthorized<DeviceIdentity>(deviceToken) ??
            new ApiResult<DeviceIdentity>.Success(new DeviceIdentity("1", "carlos", "7", "TEST")));
    }

    public Task<ApiResult<JobUpload>> CreateJobAsync(string deviceToken, string source, CancellationToken cancellationToken = default)
    {
        if (Fails(nameof(CreateJobAsync), out ApiResult<JobUpload> failure)) return Task.FromResult(failure);
        if (Unauthorized<JobUpload>(deviceToken) is { } denied) return Task.FromResult(denied);
        Sources.Add(source);
        long id = _nextJob++;
        Jobs[id] = new JobRow(id, "awaiting_upload", null, null, null, 0, "transcribe", null, source, DateTimeOffset.UtcNow);
        return Task.FromResult<ApiResult<JobUpload>>(new ApiResult<JobUpload>.Success(
            new JobUpload(id, $"https://storage.test/uploads/u1/{id}.wav?sv=x&sig=SECRET{id}", DateTimeOffset.UtcNow.AddHours(1), "audio/wav", MaxBytes)));
    }

    public Task<ApiResult<JobRow>> ConfirmJobAsync(string deviceToken, long idJob, CancellationToken cancellationToken = default)
    {
        if (Fails(nameof(ConfirmJobAsync), out ApiResult<JobRow> failure)) return Task.FromResult(failure);
        if (Unauthorized<JobRow>(deviceToken) is { } denied) return Task.FromResult(denied);
        if (!Jobs.TryGetValue(idJob, out JobRow? job)) return Task.FromResult<ApiResult<JobRow>>(new ApiResult<JobRow>.Failure(new ApiError.NotFound()));
        if (job.Status != "awaiting_upload") return Task.FromResult<ApiResult<JobRow>>(new ApiResult<JobRow>.Failure(new ApiError.Rejected(409, "not awaiting")));
        Jobs[idJob] = job with { Status = StatusAfterConfirm(job) };
        if (LoseConfirmAnswerOnce)
        {
            // El servidor confirmó, pero la respuesta no llegó: el cliente reintenta y recibe 409.
            LoseConfirmAnswerOnce = false;
            return Task.FromResult<ApiResult<JobRow>>(new ApiResult<JobRow>.Failure(new ApiError.Network("lost")));
        }

        return Task.FromResult<ApiResult<JobRow>>(new ApiResult<JobRow>.Success(Jobs[idJob]));
    }

    public Task<ApiResult<JobRow>> ReextractAsync(string deviceToken, long idSession, string promptId, string source, CancellationToken cancellationToken = default)
    {
        if (Fails(nameof(ReextractAsync), out ApiResult<JobRow> failure)) return Task.FromResult(failure);
        if (Unauthorized<JobRow>(deviceToken) is { } denied) return Task.FromResult(denied);
        Sources.Add(source);
        long id = _nextJob++;
        Jobs[id] = new JobRow(id, "queued", null, null, idSession, 0, "reextract", promptId, source, DateTimeOffset.UtcNow);
        return Task.FromResult<ApiResult<JobRow>>(new ApiResult<JobRow>.Success(Jobs[id]));
    }

    public Task<ApiResult<JobRow>> GetJobAsync(string deviceToken, long idJob, CancellationToken cancellationToken = default)
    {
        if (Fails(nameof(GetJobAsync), out ApiResult<JobRow> failure)) return Task.FromResult(failure);
        if (Unauthorized<JobRow>(deviceToken) is { } denied) return Task.FromResult(denied);
        return Task.FromResult<ApiResult<JobRow>>(Jobs.TryGetValue(idJob, out JobRow? job)
            ? new ApiResult<JobRow>.Success(job)
            : new ApiResult<JobRow>.Failure(new ApiError.NotFound()));
    }

    public Task<ApiResult<IReadOnlyList<ApiReport>>> GetReportsAsync(string deviceToken, long idSession, CancellationToken cancellationToken = default)
    {
        if (Fails(nameof(GetReportsAsync), out ApiResult<IReadOnlyList<ApiReport>> failure)) return Task.FromResult(failure);
        if (Unauthorized<IReadOnlyList<ApiReport>>(deviceToken) is { } denied) return Task.FromResult(denied);
        IReadOnlyList<ApiReport> reports = Reports.TryGetValue(idSession, out List<ApiReport>? list) ? list.ToList() : [];
        return Task.FromResult<ApiResult<IReadOnlyList<ApiReport>>>(new ApiResult<IReadOnlyList<ApiReport>>.Success(reports));
    }

    // Lo que la cola y la sesión no usan: lo prueban los autotests del cliente HTTP y --verify-api.
    public Task<ApiResult<IReadOnlyList<ApiSession>>> GetSessionsAsync(string deviceToken, int limit, int offset, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ApiResult<ApiTranscript>> GetTranscriptAsync(string deviceToken, long idSession, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ApiResult<IReadOnlyList<ApiSearchHit>>> SearchAsync(string deviceToken, string query, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ApiResult<IReadOnlyList<ApiCostRow>>> GetCostsAsync(string deviceToken, int option, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ApiResult<IReadOnlyList<CatalogPrompt>>> GetPromptCatalogAsync(string deviceToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ApiResult<IReadOnlyList<PromptVersionRow>>> GetPromptVersionsAsync(string deviceToken, int option, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ApiResult<long>> InsertPromptVersionAsync(string deviceToken, string promptId, string baseVersion, string? version, string? systemPrompt, string? note, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ApiResult<IReadOnlyList<ApiSetting>>> GetSettingsAsync(string deviceToken, int option, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ApiResult<int>> SetSettingAsync(string deviceToken, string key, string? value, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ApiResult<AudioLink>> GetAudioLinkAsync(string deviceToken, long idSession, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>Un subidor falso: "sube" bloque a bloque, y puede cortarse después de N bloques o contestar un 403.</summary>
internal sealed class FakeUploader : IAudioUploader
{
    public List<int> BlocksSent { get; } = [];

    public List<IReadOnlySet<int>> AlreadySentSeen { get; } = [];

    public int? InterruptAfterBlocks { get; set; }

    public bool RejectLinkOnce { get; set; }

    public int? FailWithCodeOnce { get; set; }

    public async Task<BlockUploadOutcome> UploadAsync(string uploadUrl, string filePath, int blockBytes, IReadOnlySet<int> alreadySent, Func<int, Task> onBlockSent, CancellationToken cancellationToken = default)
    {
        AlreadySentSeen.Add(alreadySent.ToHashSet());
        if (RejectLinkOnce)
        {
            RejectLinkOnce = false;
            return new BlockUploadOutcome.LinkRejected();
        }

        if (FailWithCodeOnce is { } code)
        {
            FailWithCodeOnce = null;
            return new BlockUploadOutcome.Failed(code);
        }

        int count = (int)((new FileInfo(filePath).Length + blockBytes - 1) / blockBytes);
        int sentThisRun = 0;
        for (int index = 0; index < count; index++)
        {
            if (alreadySent.Contains(index)) continue;
            if (InterruptAfterBlocks is { } limit && sentThisRun >= limit)
            {
                InterruptAfterBlocks = null;
                return new BlockUploadOutcome.Interrupted("simulated");
            }

            BlocksSent.Add(index);
            sentThisRun++;
            await onBlockSent(index);
        }

        return new BlockUploadOutcome.Committed();
    }
}

/// <summary>Un almacén de sesión en memoria.</summary>
internal sealed class MemoryDeviceSessionStore : IDeviceSessionStore
{
    public DeviceSession? Stored { get; set; }

    public DeviceSession? Load() => Stored;

    public void Save(DeviceSession session) => Stored = session;

    public void Clear() => Stored = null;
}
