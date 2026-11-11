using System.Diagnostics;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;
using MeetingAssistant.Infrastructure.Api;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using Microsoft.Extensions.Configuration;

/// <summary>
/// El harness contra la API de DevLog de verdad (paso B; el paso A dejó "los autotests del harness
/// contra la API" para éste). El harness tiene su propio dispositivo (<c>harness-MAQUINA</c>), aparte
/// del de la app: se inicia sesión con <c>--api-signin</c> y su token queda cifrado con DPAPI en
/// <see cref="DeviceFile"/>. Nada de esto imprime un token, un enlace ni contenido de reuniones.
/// </summary>
internal static class ApiCommands
{
    public static string DeviceFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingAssistant", "harness-device.json");

    /// <summary>El dispositivo de la app (AppPaths.DeviceFile), si la app inició sesión en esta máquina.</summary>
    private static string AppDeviceFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingAssistant", "device.json");

    private static string QueueDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingAssistant", "harness-queue");

    private static string DeviceName => $"harness-{Environment.MachineName}";

    // ------------------------------------------------------------------ sesión

    public static async Task<int> SignInAsync(IConfiguration configuration, string username)
    {
        (IDevLogApi api, string baseUrl) = CreateApi(configuration);
        Console.WriteLine($"API: {baseUrl}. Dispositivo: {DeviceName}.");
        Console.Write($"Contraseña de {username} (no se muestra ni se guarda): ");
        char[] password = ReadPassword();
        var auth = new DeviceAuthService(api, new DpapiDeviceSessionStore(DeviceFile, new DpapiSecretProtector()), DeviceName);
        SignInResult result = await auth.SignInAsync(username, password);
        Console.WriteLine(result.Message);
        if (result.Outcome == SignInOutcome.Success) Console.WriteLine($"Token guardado cifrado (DPAPI) en {DeviceFile}.");
        return result.Outcome == SignInOutcome.Success ? 0 : 1;
    }

    public static int SignOut()
    {
        if (File.Exists(DeviceFile)) File.Delete(DeviceFile);
        Console.WriteLine("Se borró el token de esta máquina. Sigue valiendo en el servidor hasta revocarlo en DevLog.");
        return 0;
    }

    // ------------------------------------------------------------------ autotest de sólo lectura

    /// <summary>
    /// Lee todo lo que la app lee, con el token del harness, sin escribir nada: identidad, sesiones,
    /// transcript, reportes, búsqueda, costos, catálogo, versiones de prompt, ajustes y el enlace
    /// de escucha. Más un token inventado → 401. Sólo cuenta y comprueba formas; no imprime contenido.
    /// </summary>
    public static async Task<int> VerifyAsync(IConfiguration configuration)
    {
        if (!TrySession(configuration, out IDevLogApi api, out DeviceAuthService auth, out string baseUrl)) return 1;
        Console.WriteLine($"=== La API de verdad, sólo lectura ({baseUrl}) ===");
        var checks = new Checks("--verify-api");
        string token = auth.RequireToken();

        checks.Check($"Devices/Select_Me acepta el token ({auth.DeviceName})", await api.MeAsync(token) is ApiResult<DeviceIdentity>.Success { Value.DeviceName: var name } && name == auth.DeviceName);
        checks.Check("un token inventado → 401", await api.MeAsync("dlg_" + new string('0', 43)) is ApiResult<DeviceIdentity>.Failure { Error: ApiError.Unauthorized });
        checks.Check("sin token, las reuniones → 401",
            await api.GetSessionsAsync("", 1, 0) is ApiResult<IReadOnlyList<ApiSession>>.Failure { Error: ApiError.Unauthorized });

        var history = new MeetingHistoryService(api, auth);
        IReadOnlyList<ApiSession> sessions = await history.ListSessionsAsync(200);
        checks.Check($"Select_Sessions: {sessions.Count} sesiones, la más nueva primero",
            sessions.Count > 0 && sessions.Zip(sessions.Skip(1)).All(pair => pair.First.StartedAt >= pair.Second.StartedAt));
        Console.WriteLine($"      orígenes: {string.Join(", ", sessions.GroupBy(s => s.Source).Select(g => $"{g.Key} {g.Count()}"))}");

        if (sessions.FirstOrDefault(s => s.HasTranscript) is { } withTranscript)
        {
            ApiTranscript? transcript = await history.GetTranscriptAsync(withTranscript.IdSession);
            checks.Check($"Select_Transcripts de la sesión {withTranscript.IdSession}: {transcript?.Text.Length ?? 0} caracteres", transcript is { Text.Length: > 0 });
            IReadOnlyList<ApiReport> reports = await history.GetReportsAsync(withTranscript.IdSession);
            checks.Check($"Select_Reports: {reports.Count} reporte(s), el número coincide con la lista", reports.Count == withTranscript.ReportCount);
        }

        if (sessions.FirstOrDefault(s => !s.HasTranscript) is { } without)
        {
            checks.Check($"una sesión sin transcript ({without.IdSession}) → null, no un error", await history.GetTranscriptAsync(without.IdSession) is null);
        }

        IReadOnlyList<ApiSearchHit> hits = await history.SearchAsync("reunion", 20);
        checks.Check($"Select_Search «reunion» (sin acento): {hits.Count} resultado(s), con fragmento", hits.All(hit => hit.Snippet.Length > 0));
        bool searchSurvived;
        try
        {
            await history.SearchAsync("\"'(*: AND OR", 5);
            searchSurvived = true;
        }
        catch (ApiException)
        {
            searchSurvived = false;
        }

        checks.Check("una búsqueda con comillas y operadores no rompe", searchSurvived);

        CostOverview costs = await new MeetingCostsService(api, auth).LoadAsync();
        Console.WriteLine($"      costos: {costs.Totals.Sessions} sesiones; transcripción {costs.Totals.Transcripts.Text()}; reportes {costs.Totals.Reports.Text()}; total {costs.Totals.Total.Text()}");
        checks.Check("Select_Costs: totales y meses", costs.Totals.Sessions == sessions.Count && costs.Months.Count > 0);
        checks.Check("los meses suman las sesiones de los totales", costs.Months.Sum(month => month.Sessions) == costs.Totals.Sessions);

        IReadOnlyList<PromptEntry> prompts = await new PromptLibraryService(api, auth).LoadAsync();
        checks.Check($"catálogo de prompts: {prompts.Count} ({string.Join(", ", prompts.Select(p => $"{p.PromptId} {p.Version}"))})",
            prompts.Count > 0 && prompts.All(p => p.Text.Length > 0));

        IReadOnlyList<ServerSetting> settings = await new ServerSettingsService(api, auth).LoadAsync();
        checks.Check("ajustes: ninguno es secreto ni un precio de mi capa",
            settings.All(s => ServerSettingsService.IsAllowed(s.Key)));
        Console.WriteLine($"      ajustes con valor: {string.Join(", ", settings.Where(s => s.Effective is not null).Select(s => s.Key + (s.OwnValue is null ? " (base)" : " (mío)")))}");

        if (sessions.FirstOrDefault(s => s.AudioExpiresAt is not null && s.AudioExpiresAt > DateTimeOffset.UtcNow) is { } archived)
        {
            AudioLink? link = await history.GetAudioLinkAsync(archived.IdSession);
            checks.Check($"Select_Audio_Link de la sesión {archived.IdSession}: enlace de sólo lectura, vence en ≤ 15 min",
                link is not null && link.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(16) && link.Url.StartsWith("https://"));
            if (link is not null)
            {
                using var http = new HttpClient();
                using var head = new HttpRequestMessage(HttpMethod.Head, link.Url);
                using HttpResponseMessage answer = await http.SendAsync(head);
                checks.Check($"el enlace se puede leer ({(int)answer.StatusCode}, {answer.Content.Headers.ContentLength ?? 0} bytes)", answer.IsSuccessStatusCode);
            }
        }
        else
        {
            Console.WriteLine("      (ninguna sesión con audio archivado vigente: no se probó el enlace de escucha)");
        }

        if (sessions.FirstOrDefault(s => s.AudioExpiresAt is null) is { } notArchived)
        {
            checks.Check($"sesión sin archivo ({notArchived.IdSession}) → sin enlace (404)", await history.GetAudioLinkAsync(notArchived.IdSession) is null);
        }

        return checks.Finish();
    }

    // ------------------------------------------------------------------ un trabajo de verdad (cobra)

    /// <summary>
    /// Un WAV por el mismo camino que la app (convertir a 16 kHz → cola → servidor → .md), con el
    /// dispositivo del harness. Cobra Deepgram y el LLM. Espera hasta Ready o Failed.
    /// </summary>
    public static async Task<int> ProcessFileAsync(IConfiguration configuration, IReadOnlyList<string> arguments)
    {
        string audio = Path.GetFullPath(arguments[0]);
        if (!File.Exists(audio))
        {
            Console.Error.WriteLine($"No existe: {audio}");
            return 1;
        }

        string vault = VaultOf(arguments);
        if (!TrySession(configuration, out IDevLogApi api, out DeviceAuthService auth, out string baseUrl)) return 1;

        (MeetingUploadQueue queue, MeetingRecorder recorder) = CreatePipeline(api, auth, vault);
        Console.WriteLine($"API {baseUrl}. Vault de prueba: {vault}. Cobra Deepgram y el LLM.");
        var clock = Stopwatch.StartNew();
        QueuedMeeting meeting = await recorder.QueueAudioFileAsync(audio, SessionSource.Harness);
        Console.WriteLine($"Convertido y encolado en {clock.Elapsed.TotalSeconds:F1} s: {meeting.TotalBytes / 1048576.0:F1} MB a subir (original {new FileInfo(audio).Length / 1048576.0:F1} MB).");
        return await FollowAsync(queue, meeting.Id, clock);
    }

    /// <summary>Retoma lo pendiente de la cola del harness (p. ej. tras matar el proceso a mitad de una subida).</summary>
    public static async Task<int> ResumeAsync(IConfiguration configuration, IReadOnlyList<string> arguments)
    {
        if (!TrySession(configuration, out IDevLogApi api, out DeviceAuthService auth, out _)) return 1;
        (MeetingUploadQueue queue, _) = CreatePipeline(api, auth, VaultOf(arguments));
        int failures = 0;
        foreach (QueuedMeeting pending in queue.Items.Where(item => !item.IsFinished))
        {
            Console.WriteLine($"Retomando {pending.Id}: {pending.Status}, trabajo {pending.IdJob}, bloques ya enviados [{string.Join(",", pending.SentBlocks)}] de {AzureBlockUploader.BlockCount(pending.TotalBytes, pending.BlockBytes > 0 ? pending.BlockBytes : MeetingUploadQueue.DefaultBlockBytes)}.");
            if (await FollowAsync(queue, pending.Id, Stopwatch.StartNew()) != 0) failures++;
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>Re-extraer una sesión de verdad con otro prompt (Jobs opción 2). Cobra el LLM.</summary>
    public static async Task<int> ReextractAsync(IConfiguration configuration, IReadOnlyList<string> arguments)
    {
        if (!long.TryParse(arguments[0], out long idSession))
        {
            Console.Error.WriteLine("El primer argumento es el idSession.");
            return 1;
        }

        string vault = VaultOf(arguments);
        if (!TrySession(configuration, out IDevLogApi api, out DeviceAuthService auth, out _)) return 1;
        (MeetingUploadQueue queue, _) = CreatePipeline(api, auth, vault);
        var clock = Stopwatch.StartNew();
        QueuedMeeting meeting = queue.EnqueueReextract(idSession, arguments[1], SessionSource.Harness);
        return await FollowAsync(queue, meeting.Id, clock);
    }

    /// <summary>Graba N segundos (altavoz + micrófono) y lo manda como la app. Cobra.</summary>
    public static async Task<int> RecordAsync(IConfiguration configuration, IReadOnlyList<string> arguments)
    {
        int seconds = arguments.Count > 0 && int.TryParse(arguments[0], out int parsed) && parsed > 0 ? parsed : 15;
        string vault = VaultOf(arguments);
        if (!TrySession(configuration, out IDevLogApi api, out DeviceAuthService auth, out _)) return 1;
        (MeetingUploadQueue queue, MeetingRecorder recorder) = CreatePipeline(api, auth, vault);

        Console.WriteLine($"Grabando {seconds} s (altavoz + micrófono)...");
        await recorder.StartRecordingAsync(SessionSource.Harness);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var clock = Stopwatch.StartNew();
        QueuedMeeting meeting = await recorder.StopRecordingAndQueueAsync();
        Console.WriteLine($"Grabado y encolado: {meeting.OriginalAudioPath} → {meeting.TotalBytes / 1048576.0:F2} MB a subir.");
        return await FollowAsync(queue, meeting.Id, clock);
    }

    private static async Task<int> FollowAsync(MeetingUploadQueue queue, string id, Stopwatch clock)
    {
        QueuedMeetingStatus? last = null;
        long lastPercent = -1;
        queue.Progress += (_, progress) =>
        {
            long percent = progress.TotalBytes == 0 ? 0 : progress.SentBytes * 100 / progress.TotalBytes;
            if (percent / 10 == lastPercent / 10) return;
            lastPercent = percent;
            Console.WriteLine($"  [{clock.Elapsed.TotalSeconds,6:F1} s] enviando {percent}%");
        };

        while (true)
        {
            QueueStep step = await queue.AdvanceAsync(id);
            QueuedMeeting meeting = queue.Items.Single(item => item.Id == id);
            if (meeting.Status != last)
            {
                Console.WriteLine($"  [{clock.Elapsed.TotalSeconds,6:F1} s] {meeting.Status}" +
                    (meeting.IdJob is { } job ? $" (trabajo {job}" + (meeting.IdSession is { } session ? $", sesión {session})" : ")") : string.Empty));
                last = meeting.Status;
            }

            if (meeting.Notice is { } notice) Console.WriteLine($"           {notice}");
            switch (step)
            {
                case QueueStep.Done:
                    Console.WriteLine($"Listo en {clock.Elapsed.TotalSeconds:F0} s: {meeting.ReportPath}");
                    return 0;
                case QueueStep.Failed:
                    Console.WriteLine($"Falló: {meeting.Error}");
                    return 1;
                case QueueStep.SignedOut:
                    Console.WriteLine("Sin sesión (¿revocado?). Corre --api-signin.");
                    return 1;
                default:
                    await Task.Delay(TimeSpan.FromSeconds(step == QueueStep.Retry ? 15 : 5));
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ armado

    private static (IDevLogApi Api, string BaseUrl) CreateApi(IConfiguration configuration)
    {
        string baseUrl = configuration["DevLog:BaseUrl"] is { Length: > 0 } value && !value.StartsWith('<')
            ? value
            : throw new InvalidOperationException("Falta DevLog:BaseUrl (viene de appsettings.defaults.json de la app, o la variable DevLog__BaseUrl).");
        return (new DevLogApiClient(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, baseUrl), baseUrl);
    }

    private static bool TrySession(IConfiguration configuration, out IDevLogApi api, out DeviceAuthService auth, out string baseUrl)
    {
        (api, baseUrl) = CreateApi(configuration);
        auth = new DeviceAuthService(api, new DpapiDeviceSessionStore(DeviceFile, new DpapiSecretProtector()), DeviceName);
        if (auth.Restore() is not null) return true;

        // Sin dispositivo propio, el de la app de esta máquina (mismo usuario de Windows, DPAPI): así
        // se prueba sin escribir la contraseña otra vez. Se dice, porque las llamadas quedan a su nombre.
        if (File.Exists(AppDeviceFile))
        {
            auth = new DeviceAuthService(api, new DpapiDeviceSessionStore(AppDeviceFile, new DpapiSecretProtector()), Environment.MachineName);
            if (auth.Restore() is { } session)
            {
                Console.WriteLine($"(Sin dispositivo propio del harness: se usa el de la app, «{session.DeviceName}».)");
                return true;
            }
        }

        Console.Error.WriteLine($"El harness no tiene sesión en {baseUrl}. Corre primero: --api-signin <usuario>, o inicia sesión en la app.");
        return false;
    }

    private static (MeetingUploadQueue Queue, MeetingRecorder Recorder) CreatePipeline(IDevLogApi api, DeviceAuthService auth, string vault)
    {
        IReportStorage reports = new MarkdownReportStorage(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:VaultPath"] = vault, ["Storage:SubFolder"] = "MeetingReports" })
            .Build());
        var queue = new MeetingUploadQueue(
            api, auth, new AzureBlockUploader(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }),
            new JsonQueuedMeetingStore(QueueDirectory, new DpapiSecretProtector()), reports,
            diagnostic: message => Console.WriteLine($"           {message}"),
            failure: (context, exception) => Console.Error.WriteLine($"           {context}: {exception.GetType().Name}: {exception.Message}"));
        string output = Path.Combine(QueueDirectory, "audio");
        var recorder = new MeetingRecorder(new AudioCaptureService(), new UploadAudioConverter(), queue, output, Path.Combine(QueueDirectory, "upload"),
            message => Console.WriteLine($"           {message}"),
            (context, exception) => Console.Error.WriteLine($"           {context}: {exception.GetType().Name}: {exception.Message}"));
        return (queue, recorder);
    }

    /// <summary>
    /// <c>--vault &lt;carpeta&gt;</c>, o la carpeta de pruebas <c>local-vault/</c> de la raíz del repo
    /// (ignorada por git): las corridas del harness no escriben en el vault real salvo que se pida.
    /// </summary>
    private static string VaultOf(IReadOnlyList<string> arguments)
    {
        int index = arguments.ToList().FindIndex(argument => argument.Equals("--vault", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < arguments.Count) return Path.GetFullPath(arguments[index + 1]);

        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "MeetingAssistant.sln"))) directory = Path.GetDirectoryName(directory);
        return Path.Combine(directory ?? Environment.CurrentDirectory, "local-vault");
    }

    private static char[] ReadPassword()
    {
        var buffer = new List<char>();
        if (Console.IsInputRedirected)
        {
            int c;
            while ((c = Console.In.Read()) is not -1 and not '\n' and not '\r') buffer.Add((char)c);
        }
        else
        {
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace && buffer.Count > 0) buffer.RemoveAt(buffer.Count - 1);
                else if (!char.IsControl(key.KeyChar)) buffer.Add(key.KeyChar);
            }
        }

        Console.WriteLine();
        char[] password = buffer.ToArray();
        // La lista creció copiando; se pisa lo que queda en ella (las copias viejas no se alcanzan).
        for (int i = 0; i < buffer.Count; i++) buffer[i] = '\0';
        return password;
    }
}

/// <summary>--verify-capture: graba N segundos con la captura de la app y dice cuánto audio salió. No sube nada.</summary>
internal static class CaptureCheck
{
    public static async Task<int> RunAsync(IReadOnlyList<string> arguments)
    {
        int seconds = arguments.Count > 0 && int.TryParse(arguments[0], out int parsed) && parsed > 0 ? parsed : 5;
        string folder = Path.Combine(Path.GetTempPath(), "capture-check");
        var capture = new AudioCaptureService();
        Console.WriteLine($"Grabando {seconds} s (altavoz + micrófono)...");
        await capture.StartAsync(folder);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        AudioCaptureResult result = await capture.StopAsync();
        long bytes = new FileInfo(result.AudioPath).Length;
        Console.WriteLine($"Altavoz: {result.LoopbackDevice}. Micrófono: {result.MicrophoneDevice}.");
        Console.WriteLine($"{result.AudioPath}: {bytes} bytes en {result.Duration.TotalSeconds:F1} s (esperado ≈ {88200L * seconds} a 44,1 kHz 16 bits mono).");
        File.Delete(result.AudioPath);
        return bytes > 44 + 88200L * seconds / 2 ? 0 : 1;
    }
}
