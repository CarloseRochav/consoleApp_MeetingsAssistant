using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Core.Services;
using MeetingAssistant.Infrastructure.Api;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using Microsoft.Extensions.Configuration;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

/// <summary>
/// Autotests offline del paso B (escritorio como cliente de la API): sin red, sin claves, con un
/// transporte HTTP falso o una API falsa en memoria. Los corre la CI. Lo que va contra la API de
/// verdad está en <see cref="ApiCommands"/>.
/// </summary>
internal static class ApiSelfTests
{
    private const string BaseUrl = "https://api.test";

    // ------------------------------------------------------------------ cliente HTTP

    public static async Task<int> VerifyClientAsync()
    {
        Console.WriteLine("=== Cliente HTTP de la API de DevLog (transporte falso) ===");
        var checks = new Checks("--verify-api-client");
        var handler = new FakeHttpHandler();
        var api = new DevLogApiClient(new HttpClient(handler), BaseUrl + "/");

        // Forma de las peticiones.
        handler.Respond = (_, _) => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"v_id":41,"uploadUrl":"https://s/u.wav?sig=SECRET","expiresAt":"2026-11-10T20:00:00Z","contentType":"audio/wav","maxBytes":1073741824}""");
        ApiResult<JobUpload> created = await api.CreateJobAsync("dlg_token", "desktop");
        var request = handler.Requests[^1];
        using (JsonDocument body = JsonDocument.Parse(request.Body))
        {
            checks.Check("crear trabajo: POST a Jobs/InsertUpdate_Jobs", request.Method == HttpMethod.Post && request.Url == $"{BaseUrl}/Jobs/InsertUpdate_Jobs");
            checks.Check("crear trabajo: option 0 y source \"desktop\"",
                body.RootElement.GetProperty("option").GetInt32() == 0 && body.RootElement.GetProperty("source").GetString() == "desktop");
        }

        checks.Check("el token del dispositivo va como Bearer", request.Headers.GetValueOrDefault("Authorization") == "Bearer dlg_token");
        checks.Check("la respuesta se lee (v_id, maxBytes, expiresAt)",
            created is ApiResult<JobUpload>.Success { Value: { IdJob: 41, MaxBytes: 1073741824 } job } && job.ExpiresAt.Hour == 20);
        checks.Check("el ToString del trabajo no muestra el enlace", created is ApiResult<JobUpload>.Success s && !s.Value.ToString().Contains("SECRET"));

        handler.Respond = (_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, """{"idUser":"1","username":"carlos","idDevice":"7","deviceName":"PC"}""");
        await api.MeAsync("dlg_token");
        checks.Check("Select_Me es GET", handler.Requests[^1].Method == HttpMethod.Get && handler.Requests[^1].Url.EndsWith("/Devices/Select_Me"));

        handler.Respond = (_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, """{"v_id":3}""");
        await api.SetSettingAsync("dlg_token", "Llm:Provider", "Gemini");
        using (JsonDocument body = JsonDocument.Parse(handler.Requests[^1].Body))
        {
            checks.Check("guardar un ajuste: option 0, shared false (mi capa)",
                body.RootElement.GetProperty("option").GetInt32() == 0 && !body.RootElement.GetProperty("shared").GetBoolean());
        }

        await api.SetSettingAsync("dlg_token", "Llm:Provider", null);
        using (JsonDocument body = JsonDocument.Parse(handler.Requests[^1].Body))
        {
            checks.Check("borrar un ajuste: option 1", body.RootElement.GetProperty("option").GetInt32() == 1);
        }

        await api.InsertPromptVersionAsync("dlg_token", "assignment-meeting", "v1", "v1-mine-1", "texto", null);
        using (JsonDocument body = JsonDocument.Parse(handler.Requests[^1].Body))
        {
            checks.Check("una edición de prompt va a mi capa (shared false) con los nombres de DevLog",
                !body.RootElement.GetProperty("shared").GetBoolean() && body.RootElement.GetProperty("prompt_id").GetString() == "assignment-meeting" &&
                body.RootElement.GetProperty("base_version").GetString() == "v1");
        }

        await api.ReextractAsync("dlg_token", 54, "functional-spec", "desktop");
        using (JsonDocument body = JsonDocument.Parse(handler.Requests[^1].Body))
        {
            checks.Check("re-extraer: option 2 con idSession y prompt_id",
                body.RootElement.GetProperty("option").GetInt32() == 2 && body.RootElement.GetProperty("idSession").GetInt64() == 54);
        }

        // Login: la contraseña va en el cuerpo, una sola vez, y no en el ToString del resultado.
        handler.Respond = (_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, """{"token":"jwt_SECRET","expiresAt":"2026-11-10T20:00:00Z","user":{"idUser":1,"username":"carlos","name":"","lastname":"","role":"admin"}}""");
        char[] password = "pa\"ss wörd".ToCharArray();
        ApiResult<LoginResult> login = await api.LoginAsync("carlos", password);
        using (JsonDocument body = JsonDocument.Parse(handler.Requests[^1].Body))
        {
            checks.Check("login: la contraseña llega entera (con comillas y acentos escapados bien)",
                body.RootElement.GetProperty("password").GetString() == "pa\"ss wörd" && handler.Requests[^1].Headers.GetValueOrDefault("Authorization") is null);
        }

        checks.Check("login: el ToString del resultado no muestra el token",
            login is ApiResult<LoginResult>.Success l && !l.Value.ToString().Contains("SECRET") && l.Value.User.IdUser == 1);

        // Errores.
        async Task<ApiError?> ErrorFor(HttpStatusCode status, string body)
        {
            handler.Respond = (_, _) => FakeHttpHandler.Json(status, body);
            return await api.CreateJobAsync("dlg_token", "desktop") is ApiResult<JobUpload>.Failure failure ? failure.Error : null;
        }

        checks.Check("401 → Unauthorized", await ErrorFor(HttpStatusCode.Unauthorized, "") is ApiError.Unauthorized);
        checks.Check("403 → Forbidden", await ErrorFor(HttpStatusCode.Forbidden, "") is ApiError.Forbidden);
        checks.Check("404 → NotFound", await ErrorFor(HttpStatusCode.NotFound, "") is ApiError.NotFound);
        ApiError? cap = await ErrorFor(HttpStatusCode.TooManyRequests,
            """{"message":"Daily cap","spentUsd":2.01,"capUsd":2,"unpriced":1,"inFlight":1,"maxInFlight":3,"resetsAt":"2026-11-11T08:00:00Z"}""");
        checks.Check("429 de Jobs → CapReached con los números del servidor",
            cap is ApiError.CapReached { Refusal: { CapUsd: 2m, InFlight: 1, MaxInFlight: 3, Unpriced: 1 } refusal } && !refusal.IsInFlightLimit);
        checks.Check("el aviso del tope lleva lo gastado y el tope", cap?.Describe().Contains("2,01") == true || cap?.Describe().Contains("2.01") == true);
        ApiError? inFlight = await ErrorFor(HttpStatusCode.TooManyRequests, """{"message":"x","spentUsd":0.1,"capUsd":2,"unpriced":0,"inFlight":3,"maxInFlight":3}""");
        checks.Check("429 por trabajos en curso se reconoce y dice 3 de 3", inFlight is ApiError.CapReached { Refusal.IsInFlightLimit: true } && inFlight.Describe().Contains("3 de 3"));
        checks.Check("429 sin cuerpo (login) → RateLimited", await ErrorFor(HttpStatusCode.TooManyRequests, "") is ApiError.RateLimited);
        checks.Check("503 → Server", await ErrorFor(HttpStatusCode.ServiceUnavailable, "Uploads are not configured") is ApiError.Server { Code: 503 });
        checks.Check("400 con texto JSON → Rejected con el motivo",
            await ErrorFor(HttpStatusCode.BadRequest, "\"key not allowed\"") is ApiError.Rejected { Code: 400, Message: "key not allowed" });
        checks.Check("400 de validación (ProblemDetails) → el motivo, no el JSON",
            await ErrorFor(HttpStatusCode.BadRequest, """{"title":"One or more validation errors occurred.","errors":{"query":["The query field is required."]}}""")
                is ApiError.Rejected { Message: var problem } && problem.Contains("query field is required") && !problem.Contains('{'));
        checks.Check("409 → Rejected 409", await ErrorFor(HttpStatusCode.Conflict, "\"exists\"") is ApiError.Rejected { Code: 409 });

        handler.Respond = (_, _) => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (api.test:443) https://s/u?sig=SECRET");
        ApiError? offline = await api.CreateJobAsync("dlg_token", "desktop") is ApiResult<JobUpload>.Failure f1 ? f1.Error : null;
        checks.Check("sin red → Network, sin el texto de la excepción", offline is ApiError.Network { Detail: var detail } && !detail.Contains("SECRET"));

        handler.Respond = (_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, "<html>proxy</html>");
        checks.Check("una respuesta que no es JSON → Unexpected",
            await api.MeAsync("dlg_token") is ApiResult<DeviceIdentity>.Failure { Error: ApiError.Unexpected });

        handler.Respond = (_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, "[]");
        checks.Check("un trabajo que no está (lista vacía) → NotFound",
            await api.GetJobAsync("dlg_token", 9) is ApiResult<JobRow>.Failure { Error: ApiError.NotFound });

        // Sólo HTTPS.
        bool refused = false;
        try
        {
            _ = new DevLogApiClient(new HttpClient(handler), "http://api.example.com");
        }
        catch (ArgumentException)
        {
            refused = true;
        }

        checks.Check("una dirección http:// (no localhost) se rechaza: el token viaja en cada llamada", refused);

        // Lo que se imprime nunca lleva un secreto.
        var session = new DeviceSession("dlg_SECRET", 7, 1, "carlos", "PC");
        var link = new AudioLink(54, 41, "https://s/a.wav?sig=SECRET", DateTimeOffset.UtcNow, "audio/wav", 10, DateTimeOffset.UtcNow);
        var queued = new QueuedMeeting { Id = "x", CreatedAtUtc = DateTimeOffset.UtcNow, UploadUrl = "https://s/u?sig=SECRET" };
        checks.Check("ToString de DeviceSession, AudioLink y QueuedMeeting sin secretos",
            !session.ToString().Contains("SECRET") && !link.ToString().Contains("SECRET") && !queued.ToString().Contains("SECRET"));

        // Las fechas de la API vienen con y sin desfase (DateTime de .NET): sin desfase son UTC.
        ApiSession? parsed = JsonSerializer.Deserialize<ApiSession>(
            """{"idSession":54,"started_at":"2026-11-11T03:48:00Z","ended_at":null,"duration_seconds":null,"source":"android","has_transcript":true,"report_count":1,"transcript_cost_micro_usd":null,"reports_cost_micro_usd":1200,"reports_cost_unknown":0,"audio_expires_at":"2026-12-11T03:51:00Z"}""");
        checks.Check("una sesión se lee: costos nulos quedan null (no 0), audio_expires_at presente",
            parsed is { TranscriptCostMicroUsd: null, ReportsCostMicroUsd: 1200, AudioExpiresAt: not null });

        return checks.Finish();
    }

    // ------------------------------------------------------------------ subida por bloques

    public static async Task<int> VerifyBlockUploadAsync()
    {
        Console.WriteLine("=== Subida por bloques a Storage (transporte falso) ===");
        var checks = new Checks("--verify-block-upload");
        string file = Path.Combine(Path.GetTempPath(), $"blocks-{Guid.NewGuid():N}.bin");
        byte[] content = new byte[10_500];
        new Random(42).NextBytes(content);
        await File.WriteAllBytesAsync(file, content);
        const string url = "https://acct.blob.core.windows.net/uploads/u1/x.wav?sv=2021&sp=cw&sig=SECRET";

        try
        {
            var stored = new Dictionary<string, byte[]>();
            var handler = new FakeHttpHandler();
            handler.Respond = (request, body) =>
            {
                string query = request.RequestUri!.Query;
                if (query.Contains("comp=block&")) stored[Uri.UnescapeDataString(query.Split("blockid=")[1])] = body;
                return new HttpResponseMessage(HttpStatusCode.Created);
            };
            var uploader = new AzureBlockUploader(new HttpClient(handler));
            var sentLog = new List<int>();

            BlockUploadOutcome outcome = await uploader.UploadAsync(url, file, 4096, new HashSet<int>(), index => { sentLog.Add(index); return Task.CompletedTask; });
            checks.Check("10500 bytes en bloques de 4096 = 3 bloques y la lista", outcome is BlockUploadOutcome.Committed && sentLog.SequenceEqual([0, 1, 2]));
            checks.Check("los bloques juntos son el archivo exacto",
                stored.OrderBy(pair => pair.Key, StringComparer.Ordinal).SelectMany(pair => pair.Value).SequenceEqual(content));
            checks.Check("todos los ids de bloque miden lo mismo (regla de Azure)", stored.Keys.Select(key => key.Length).Distinct().Count() == 1);
            checks.Check("cada PUT lleva x-ms-version", handler.Requests.All(r => r.Headers.ContainsKey("x-ms-version")));
            var commit = handler.Requests[^1];
            string xml = Encoding.UTF8.GetString(commit.Body);
            checks.Check("la lista de bloques confirma los 3 en orden, como Latest",
                commit.Url.EndsWith("&comp=blocklist") && xml.Split("<Latest>").Length - 1 == 3 && xml.IndexOf(AzureBlockUploader.BlockId(0)) < xml.IndexOf(AzureBlockUploader.BlockId(2)));
            checks.Check("la confirmación dice que es audio/wav", commit.Headers.GetValueOrDefault("x-ms-blob-content-type") == "audio/wav");
            checks.Check("los ids son los del teléfono (block-%08d en base64)",
                AzureBlockUploader.BlockId(3) == Convert.ToBase64String(Encoding.ASCII.GetBytes("block-00000003")));

            handler.Requests.Clear();
            sentLog.Clear();
            await uploader.UploadAsync(url, file, 4096, new HashSet<int> { 0, 1 }, index => { sentLog.Add(index); return Task.CompletedTask; });
            checks.Check("al reanudar con 0 y 1 ya enviados, sube sólo el 2 (y la lista)",
                sentLog.SequenceEqual([2]) && handler.Requests.Count == 2);

            handler.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden);
            checks.Check("403 (enlace vencido) → LinkRejected",
                await uploader.UploadAsync(url, file, 4096, new HashSet<int>(), _ => Task.CompletedTask) is BlockUploadOutcome.LinkRejected);

            handler.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError);
            checks.Check("500 → Failed con el código",
                await uploader.UploadAsync(url, file, 4096, new HashSet<int>(), _ => Task.CompletedTask) is BlockUploadOutcome.Failed { Code: 500 });

            int calls = 0;
            handler.Respond = (_, _) => ++calls == 2
                ? throw new HttpRequestException(HttpRequestError.ConnectionError, "cut " + url)
                : new HttpResponseMessage(HttpStatusCode.Created);
            sentLog.Clear();
            BlockUploadOutcome cut = await uploader.UploadAsync(url, file, 4096, new HashSet<int>(), index => { sentLog.Add(index); return Task.CompletedTask; });
            checks.Check("un corte → Interrupted, con el bloque 0 ya marcado como enviado",
                cut is BlockUploadOutcome.Interrupted && sentLog.SequenceEqual([0]));
            checks.Check("el detalle del corte no lleva el enlace", cut is BlockUploadOutcome.Interrupted { Detail: var detail } && !detail.Contains("SECRET"));
        }
        finally
        {
            File.Delete(file);
        }

        return checks.Finish();
    }

    // ------------------------------------------------------------------ conversión a 16 kHz

    public static async Task<int> VerifyConversionAsync()
    {
        Console.WriteLine("=== Conversión a 16 kHz 16 bits mono ===");
        var checks = new Checks("--verify-wav-convert");
        string folder = Path.Combine(Path.GetTempPath(), $"convert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var converter = new UploadAudioConverter();

            // Como graba la captura del escritorio: 44,1 kHz, 16 bits, mono.
            string captured = Path.Combine(folder, "captured.wav");
            WriteTone(captured, WaveFormat.CreateCustomFormat(WaveFormatEncoding.Pcm, 44100, 1, 88200, 2, 16), TimeSpan.FromSeconds(10));
            AudioConversion result = await converter.ConvertForUploadAsync(captured, Path.Combine(folder, "out", "captured-16k.wav"));
            using (var reader = new WaveFileReader(result.TargetPath))
            {
                checks.Check("sale a 16000 Hz, 16 bits, 1 canal, PCM",
                    reader.WaveFormat is { SampleRate: 16000, BitsPerSample: 16, Channels: 1, Encoding: WaveFormatEncoding.Pcm });
                checks.Check("dura lo mismo (±50 ms)", Math.Abs((reader.TotalTime - TimeSpan.FromSeconds(10)).TotalMilliseconds) <= 50);
            }

            double ratio = (double)result.SourceBytes / result.TargetBytes;
            checks.Check($"pesa 2,76 veces menos que el original (medido: {ratio:F2})", ratio is > 2.7 and < 2.8);
            checks.Check("dice el formato de origen", result.SourceFormat.StartsWith("44100 Hz, 16 bits, 1 canal"));

            // Un audio de otra fuente: estéreo, 48 kHz, float.
            string stereo = Path.Combine(folder, "stereo.wav");
            WriteTone(stereo, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), TimeSpan.FromSeconds(3));
            AudioConversion fromStereo = await converter.ConvertForUploadAsync(stereo, Path.Combine(folder, "stereo-16k.wav"));
            using (var reader = new WaveFileReader(fromStereo.TargetPath))
            {
                checks.Check("estéreo 48 kHz float también sale 16 kHz mono 16 bits",
                    reader.WaveFormat is { SampleRate: 16000, Channels: 1, BitsPerSample: 16 } && Math.Abs((reader.TotalTime - TimeSpan.FromSeconds(3)).TotalMilliseconds) <= 50);
            }

            // Un archivo que no es audio: la conversión falla (y el grabador sube el original).
            string junk = Path.Combine(folder, "junk.wav");
            await File.WriteAllTextAsync(junk, "no soy audio");
            bool threw = false;
            try
            {
                await converter.ConvertForUploadAsync(junk, Path.Combine(folder, "junk-16k.wav"));
            }
            catch (Exception)
            {
                threw = true;
            }

            checks.Check("un archivo que no es audio lanza (no deja un WAV vacío como si nada)", threw);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        return checks.Finish();
    }

    /// <summary>
    /// Una grabación larga en el formato de la captura (44,1 kHz 16 bits mono), sintética y en disco,
    /// convertida como la app: mide tamaño y tiempo y comprueba que entra en el límite de 1 GiB de la
    /// API. No va en la CI (escribe cientos de MB). La captura real de 100+ min se mide aparte.
    /// </summary>
    public static async Task<int> VerifyLongAudioAsync(IReadOnlyList<string> arguments)
    {
        int minutes = arguments.Count > 0 && int.TryParse(arguments[0], out int parsed) && parsed > 0 ? parsed : 110;
        Console.WriteLine($"=== Grabación larga sintética: {minutes} min a 44,1 kHz 16 bits mono ===");
        var checks = new Checks("--verify-long-audio");
        string folder = Path.Combine(Path.GetTempPath(), $"long-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "long.wav");
            var clock = Stopwatch.StartNew();
            WriteTone(source, WaveFormat.CreateCustomFormat(WaveFormatEncoding.Pcm, 44100, 1, 88200, 2, 16), TimeSpan.FromMinutes(minutes));
            Console.WriteLine($"  generado en {clock.Elapsed.TotalSeconds:F0} s");
            clock.Restart();
            AudioConversion result = await new UploadAudioConverter().ConvertForUploadAsync(source, Path.Combine(folder, "long-16k.wav"));
            Console.WriteLine($"  convertido en {clock.Elapsed.TotalSeconds:F0} s: {result.SourceBytes / 1048576.0:F0} MB → {result.TargetBytes / 1048576.0:F0} MB");
            checks.Check($"dura {minutes} min (±1 s)", Math.Abs((result.Duration - TimeSpan.FromMinutes(minutes)).TotalSeconds) <= 1);
            checks.Check($"el convertido entra en 1 GiB ({result.TargetBytes / 1048576.0:F0} MB)", result.TargetBytes < 1L << 30);
            checks.Check($"y entra en bloques de 4 MiB ({AzureBlockUploader.BlockCount(result.TargetBytes, MeetingUploadQueue.DefaultBlockBytes)} de {AzureBlockUploader.MaxBlocks} posibles)",
                AzureBlockUploader.BlockCount(result.TargetBytes, MeetingUploadQueue.DefaultBlockBytes) <= AzureBlockUploader.MaxBlocks);
            double hoursToLimit = (1L << 30) / (16000.0 * 2) / 3600;
            Console.WriteLine($"  a 16 kHz el límite de 1 GiB son ≈{hoursToLimit:F1} h; a 44,1 kHz 16 bits eran ≈{(1L << 30) / 88200.0 / 3600:F1} h.");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        return checks.Finish();
    }

    /// <summary>Un tono de 440 Hz en el formato pedido.</summary>
    internal static void WriteTone(string path, WaveFormat format, TimeSpan duration)
    {
        var tone = new SignalGenerator(format.SampleRate, format.Channels) { Frequency = 440, Gain = 0.2, Type = SignalGeneratorType.Sin };
        ISampleProvider limited = tone.Take(duration);
        IWaveProvider provider = format.Encoding == WaveFormatEncoding.IeeeFloat ? limited.ToWaveProvider() : limited.ToWaveProvider16();
        WaveFileWriter.CreateWaveFile(path, provider);
    }

    // ------------------------------------------------------------------ sesión del dispositivo

    public static async Task<int> VerifyDeviceAuthAsync()
    {
        Console.WriteLine("=== Inicio de sesión del dispositivo (API falsa, DPAPI real) ===");
        var checks = new Checks("--verify-device-auth");
        string file = Path.Combine(Path.GetTempPath(), $"device-{Guid.NewGuid():N}.json");
        try
        {
            var api = new FakeDevLogApi();
            var store = new DpapiDeviceSessionStore(file, new DpapiSecretProtector());
            var auth = new DeviceAuthService(api, store, "MAQUINA-TEST");
            var signedOut = new List<SignedOutReason>();
            auth.SignedOut += (_, reason) => signedOut.Add(reason);

            char[] wrong = "nope".ToCharArray();
            SignInResult bad = await auth.SignInAsync("carlos", wrong);
            checks.Check("contraseña mal → WrongCredentials, sin dispositivo", bad.Outcome == SignInOutcome.WrongCredentials && api.DevicesCreated == 0);
            checks.Check("el arreglo de la contraseña quedó en ceros también al fallar", wrong.All(c => c == '\0'));

            api.FailNext[nameof(IDevLogApi.LoginAsync)] = new ApiError.Network("x");
            checks.Check("sin red → Offline", (await auth.SignInAsync("carlos", "x".ToCharArray())).Outcome == SignInOutcome.Offline);
            api.FailNext[nameof(IDevLogApi.LoginAsync)] = new ApiError.RateLimited();
            checks.Check("429 del login → TooManyAttempts", (await auth.SignInAsync("carlos", "x".ToCharArray())).Outcome == SignInOutcome.TooManyAttempts);
            checks.Check("vacío → MissingFields", (await auth.SignInAsync(" ", "x".ToCharArray())).Outcome == SignInOutcome.MissingFields);

            char[] password = FakeDevLogApi.Password.ToCharArray();
            SignInResult good = await auth.SignInAsync("carlos", password);
            checks.Check("login → dispositivo con el nombre de la máquina", good.Outcome == SignInOutcome.Success && api.LastDeviceName == "MAQUINA-TEST");
            checks.Check("la contraseña quedó en ceros", password.All(c => c == '\0'));
            checks.Check("el archivo existe y NO tiene el token en claro",
                File.Exists(file) && !File.ReadAllText(file).Contains(FakeDevLogApi.GoodToken));
            checks.Check("el archivo tampoco tiene la contraseña ni el token del login",
                !File.ReadAllText(file).Contains(FakeDevLogApi.Password) && !File.ReadAllText(file).Contains("jwt_user_token"));

            var restarted = new DeviceAuthService(api, store, "MAQUINA-TEST");
            checks.Check("otra instancia (un reinicio) recupera la sesión sin red", restarted.Restore() is { Token: FakeDevLogApi.GoodToken, IdUser: 1, IdDevice: 7 });
            checks.Check("Select_Me con el token bueno → Valid", await restarted.VerifyAsync() == VerifyOutcome.Valid);

            api.FailNext[nameof(IDevLogApi.MeAsync)] = new ApiError.Network("x");
            checks.Check("Select_Me sin red → Unknown, la sesión sigue", await restarted.VerifyAsync() == VerifyOutcome.Unknown && restarted.IsSignedIn);

            // Revocado: el servidor deja de aceptar el token.
            api.ValidToken = "dlg_other";
            checks.Check("revocado → Revoked", await auth.VerifyAsync() == VerifyOutcome.Revoked);
            checks.Check("se avisó Revoked y se borró el archivo", signedOut.SequenceEqual([SignedOutReason.Revoked]) && !File.Exists(file));

            bool threwSignedOut = false;
            try
            {
                await auth.CallAsync(token => api.MeAsync(token));
            }
            catch (DeviceSignedOutException)
            {
                threwSignedOut = true;
            }

            checks.Check("una llamada sin sesión lanza DeviceSignedOutException", threwSignedOut);

            // Un 401 en una llamada de la UI también cierra.
            api.ValidToken = FakeDevLogApi.GoodToken;
            await auth.SignInAsync("carlos", FakeDevLogApi.Password.ToCharArray());
            api.ValidToken = "dlg_revoked";
            bool threwApi = false;
            try
            {
                await auth.CallAsync(token => api.MeAsync(token));
            }
            catch (ApiException exception) when (exception.Error is ApiError.Unauthorized)
            {
                threwApi = true;
            }

            checks.Check("un 401 en CallAsync cierra la sesión y lanza ApiException", threwApi && !auth.IsSignedIn && signedOut.Count == 2);

            // Una respuesta vieja no cierra una sesión nueva.
            api.ValidToken = FakeDevLogApi.GoodToken;
            await auth.SignInAsync("carlos", FakeDevLogApi.Password.ToCharArray());
            var stale = new DeviceSession("dlg_old", 1, 1, "carlos", "X");
            checks.Check("un 401 de una sesión vieja no cierra la actual", !auth.HandleUnauthorized(stale) && auth.IsSignedIn);

            auth.SignOut();
            checks.Check("cerrar sesión borra el archivo y avisa ByUser", !File.Exists(file) && signedOut[^1] == SignedOutReason.ByUser);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }

        return checks.Finish();
    }

    // ------------------------------------------------------------------ la cola de subidas

    public static async Task<int> VerifyQueueAsync()
    {
        Console.WriteLine("=== Cola de subidas (API falsa, disco real) ===");
        var checks = new Checks("--verify-upload-queue");
        string root = Path.Combine(Path.GetTempPath(), $"queue-{Guid.NewGuid():N}");
        string queueDir = Path.Combine(root, "queue");
        string vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(root);

        try
        {
            var api = new FakeDevLogApi();
            var sessions = new MemoryDeviceSessionStore();
            var auth = new DeviceAuthService(api, sessions, "MAQUINA-TEST");
            await auth.SignInAsync("carlos", FakeDevLogApi.Password.ToCharArray());
            var protector = new DpapiSecretProtector();
            IReportStorage reports = new MarkdownReportStorage(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:VaultPath"] = vault, ["Storage:SubFolder"] = "MeetingReports" })
                .Build());
            var uploader = new FakeUploader();
            const int block = 1024;

            MeetingUploadQueue NewQueue() => new(api, auth, uploader, new JsonQueuedMeetingStore(queueDir, protector), reports, blockBytes: block);

            string Audio(string name, int bytes)
            {
                string path = Path.Combine(root, name);
                File.WriteAllBytes(path, new byte[bytes]);
                return path;
            }

            // 1. El camino feliz.
            var queue = NewQueue();
            var ready = new List<QueuedMeeting>();
            queue.ReportReady += (_, meeting) => ready.Add(meeting);
            string original = Audio("original.wav", 9000);
            string converted = Audio("converted.wav", 3000);
            QueuedMeeting meeting = queue.EnqueueAudio(converted, original, 60, SessionSource.Hotkey);
            checks.Check("encolada: espera, con el tamaño del archivo a subir", meeting is { Status: QueuedMeetingStatus.Waiting, TotalBytes: 3000 });
            QueueStep step = await queue.AdvanceAsync(meeting.Id);
            QueuedMeeting? current = queue.Items.Single(item => item.Id == meeting.Id);
            checks.Check("subida y confirmada: el servidor procesa", step == QueueStep.Waiting && current.Status == QueuedMeetingStatus.Processing);
            checks.Check("el trabajo se creó con source \"desktop\"", api.Sources.SequenceEqual(["desktop"]));
            checks.Check("3000 bytes en bloques de 1024 = 3 bloques", uploader.BlocksSent.SequenceEqual([0, 1, 2]));
            checks.Check("el enlace no queda guardado después de confirmar", current.UploadUrl is null);
            api.CompleteJob(current.IdJob!.Value);
            step = await queue.AdvanceAsync(meeting.Id);
            current = queue.Items.Single(item => item.Id == meeting.Id);
            checks.Check("trabajo terminado → el .md en el vault", step == QueueStep.Done && current.Status == QueuedMeetingStatus.Ready && File.Exists(current.ReportPath));
            string md = File.ReadAllText(current.ReportPath!);
            checks.Check("el .md tiene frontmatter con modelo, costo y la sesión de DevLog",
                md.StartsWith("﻿---") || md.StartsWith("---") ? md.Contains("llm-model: grok-4.6") && md.Contains("cost-usd: 0.004321") && md.Contains($"devlog-session: {current.IdSession}") : false);
            checks.Check("el .md tiene el Markdown del servidor", md.Contains("# Resumen"));
            checks.Check("se avisó ReportReady una vez", ready.Count == 1);
            checks.Check("la copia convertida se borró; el original se conserva", !File.Exists(converted) && File.Exists(original));

            // 2. Sin red: graba igual, espera y sigue.
            uploader.BlocksSent.Clear();
            string offlineAudio = Audio("offline.wav", 2048);
            QueuedMeeting offline = queue.EnqueueAudio(offlineAudio, offlineAudio, 30, SessionSource.Window);
            api.FailNext[nameof(IDevLogApi.CreateJobAsync)] = new ApiError.Network("x");
            step = await queue.AdvanceAsync(offline.Id);
            current = queue.Items.Single(item => item.Id == offline.Id);
            checks.Check("sin red → Retry, sigue esperando (no falla)", step == QueueStep.Retry && current.Status == QueuedMeetingStatus.Waiting);
            checks.Check("dice por qué y cuándo reintenta", current.Notice?.Contains("No se pudo llegar") == true && current.Notice.Contains("Próximo intento"));
            step = await queue.AdvanceAsync(offline.Id);
            checks.Check("vuelve la red → sube", step == QueueStep.Waiting && queue.Items.Single(item => item.Id == offline.Id).Status == QueuedMeetingStatus.Processing);
            api.CompleteJob(queue.Items.Single(item => item.Id == offline.Id).IdJob!.Value);
            checks.Check("y termina", await queue.AdvanceAsync(offline.Id) == QueueStep.Done);

            // 3. Reinicio a mitad de la subida: otra cola sobre la misma carpeta sigue donde quedó.
            uploader.BlocksSent.Clear();
            uploader.AlreadySentSeen.Clear();
            string longAudio = Audio("long.wav", 5 * block);
            QueuedMeeting interrupted = queue.EnqueueAudio(longAudio, longAudio, 300, SessionSource.Tray);
            uploader.InterruptAfterBlocks = 2;
            step = await queue.AdvanceAsync(interrupted.Id);
            current = queue.Items.Single(item => item.Id == interrupted.Id);
            checks.Check("corte tras 2 de 5 bloques → Retry, Uploading, 2 bloques guardados",
                step == QueueStep.Retry && current.Status == QueuedMeetingStatus.Uploading && current.SentBlocks.SequenceEqual([0, 1]) && current.SentBytes == 2 * block);
            string stored = File.ReadAllText(Path.Combine(queueDir, $"{interrupted.Id}.json"));
            checks.Check("en disco el enlace de subida está cifrado (DPAPI), no en claro", !stored.Contains("SECRET") && stored.Contains("UploadUrl"));
            long jobBefore = current.IdJob!.Value;

            queue.Dispose();
            var afterRestart = NewQueue();
            QueuedMeeting resumed = afterRestart.Items.Single(item => item.Id == interrupted.Id);
            checks.Check("tras el reinicio el enlace se descifra y el estado está", resumed.UploadUrl?.Contains("SECRET") == true && resumed.Status == QueuedMeetingStatus.Uploading);
            step = await afterRestart.AdvanceAsync(interrupted.Id);
            checks.Check("sigue con el mismo trabajo y manda sólo los 3 que faltaban",
                step == QueueStep.Waiting && uploader.AlreadySentSeen[^1].SetEquals([0, 1]) && uploader.BlocksSent.SequenceEqual([0, 1, 2, 3, 4]) &&
                afterRestart.Items.Single(item => item.Id == interrupted.Id).IdJob == jobBefore);
            api.CompleteJob(jobBefore);
            checks.Check("y termina", await afterRestart.AdvanceAsync(interrupted.Id) == QueueStep.Done);
            queue = afterRestart;

            // 4. El tope (429) con los números del servidor.
            string capAudio = Audio("cap.wav", 1000);
            QueuedMeeting capped = queue.EnqueueAudio(capAudio, capAudio, 10, SessionSource.Window);
            api.FailNext[nameof(IDevLogApi.CreateJobAsync)] = new ApiError.CapReached(new CapRefusal("x", 0.4m, 2m, 0, 3, 3, DateTimeOffset.UtcNow.AddHours(5)));
            step = await queue.AdvanceAsync(capped.Id);
            current = queue.Items.Single(item => item.Id == capped.Id);
            checks.Check("429 por trabajos en curso → Retry con «3 de 3»", step == QueueStep.Retry && current.Notice?.Contains("3 de 3") == true);
            api.FailNext[nameof(IDevLogApi.CreateJobAsync)] = new ApiError.CapReached(new CapRefusal("x", 2.05m, 2m, 1, 0, 3, DateTimeOffset.UtcNow.AddHours(5)));
            await queue.AdvanceAsync(capped.Id);
            current = queue.Items.Single(item => item.Id == capped.Id);
            checks.Check("429 por el tope diario → lo gastado, el tope y lo sin precio",
                current.Notice?.Contains("tope de gasto") == true && current.Notice.Contains("1 sin precio") && current.Status == QueuedMeetingStatus.Waiting);

            // 5. El servidor falla el trabajo: se dice su motivo.
            await queue.AdvanceAsync(capped.Id);
            current = queue.Items.Single(item => item.Id == capped.Id);
            api.FailJob(current.IdJob!.Value, "Deepgram: 400 empty audio");
            var failures = new List<QueuedMeeting>();
            queue.MeetingFailed += (_, failed) => failures.Add(failed);
            step = await queue.AdvanceAsync(capped.Id);
            current = queue.Items.Single(item => item.Id == capped.Id);
            checks.Check("trabajo fallido → Failed con el motivo del servidor",
                step == QueueStep.Failed && current.Status == QueuedMeetingStatus.Failed && current.Error?.Contains("Deepgram: 400 empty audio") == true && failures.Count == 1);
            checks.Check("reintentar una fallida sin sesión del servidor vuelve a subir", queue.Retry(capped.Id) &&
                queue.Items.Single(item => item.Id == capped.Id) is { Status: QueuedMeetingStatus.Waiting, IdJob: null, Error: null });

            // 6. Revocado a mitad: la reunión no falla, espera a la nueva sesión.
            string revokedAudio = Audio("revoked.wav", 1000);
            QueuedMeeting revoked = queue.EnqueueAudio(revokedAudio, revokedAudio, 10, SessionSource.Http);
            await queue.AdvanceAsync(revoked.Id);
            api.ValidToken = "dlg_new";
            step = await queue.AdvanceAsync(revoked.Id);
            current = queue.Items.Single(item => item.Id == revoked.Id);
            checks.Check("401 → SignedOut, sesión cerrada, la reunión sigue en Processing",
                step == QueueStep.SignedOut && !auth.IsSignedIn && current.Status == QueuedMeetingStatus.Processing && current.Notice?.Contains("inicia sesión") == true);
            api.CompleteJob(current.IdJob!.Value);
            await auth.SignInAsync("carlos", FakeDevLogApi.Password.ToCharArray());
            checks.Check("con la sesión nueva (otro token) termina", await queue.AdvanceAsync(revoked.Id) == QueueStep.Done);

            // 7. Enlace vencido (403): trabajo nuevo, desde el bloque 0.
            uploader.BlocksSent.Clear();
            string expiredAudio = Audio("expired.wav", 2048);
            QueuedMeeting expired = queue.EnqueueAudio(expiredAudio, expiredAudio, 10, SessionSource.Window);
            uploader.RejectLinkOnce = true;
            step = await queue.AdvanceAsync(expired.Id);
            current = queue.Items.Single(item => item.Id == expired.Id);
            long firstJob = api.Jobs.Keys.Max();
            checks.Check("403 → vuelve a Waiting sin trabajo, para pedir otro", step == QueueStep.Retry && current is { Status: QueuedMeetingStatus.Waiting, IdJob: null });
            await queue.AdvanceAsync(expired.Id);
            current = queue.Items.Single(item => item.Id == expired.Id);
            checks.Check("el siguiente intento usa un trabajo nuevo y sube todo", current.IdJob is { } newJob && newJob > firstJob && uploader.BlocksSent.SequenceEqual([0, 1]));

            // 8. Confirmación perdida: 409 pero el trabajo ya está en cola.
            string lostAudio = Audio("lost.wav", 1000);
            QueuedMeeting lost = queue.EnqueueAudio(lostAudio, lostAudio, 10, SessionSource.Window);
            api.LoseConfirmAnswerOnce = true;
            step = await queue.AdvanceAsync(lost.Id);
            current = queue.Items.Single(item => item.Id == lost.Id);
            checks.Check("confirmación sin respuesta → Retry, todavía Uploading", step == QueueStep.Retry && current.Status == QueuedMeetingStatus.Uploading);
            step = await queue.AdvanceAsync(lost.Id);
            current = queue.Items.Single(item => item.Id == lost.Id);
            checks.Check("el reintento recibe 409, ve el trabajo en cola y pasa a Processing (sin subir otra vez)",
                step == QueueStep.Waiting && current.Status == QueuedMeetingStatus.Processing && api.Calls.Count(call => call == nameof(IDevLogApi.ConfirmJobAsync)) >= 2);

            // 9. Demasiado grande para el límite del servidor.
            api.MaxBytes = 500;
            string bigAudio = Audio("big.wav", 1000);
            QueuedMeeting big = queue.EnqueueAudio(bigAudio, bigAudio, 10, SessionSource.Window);
            step = await queue.AdvanceAsync(big.Id);
            checks.Check("más grande que maxBytes → Failed, sin subir", step == QueueStep.Failed &&
                queue.Items.Single(item => item.Id == big.Id).Error?.Contains("demasiado grande") == true);
            api.MaxBytes = 1L << 30;

            // 10. El servidor sigue: en cola con un error = reintenta (017).
            string retryAudio = Audio("retry.wav", 1000);
            QueuedMeeting retrying = queue.EnqueueAudio(retryAudio, retryAudio, 10, SessionSource.Window);
            await queue.AdvanceAsync(retrying.Id);
            current = queue.Items.Single(item => item.Id == retrying.Id);
            api.Jobs[current.IdJob!.Value] = api.Jobs[current.IdJob.Value] with { Status = "queued", Error = "DeploymentNotFound", Attempts = 1 };
            step = await queue.AdvanceAsync(retrying.Id);
            current = queue.Items.Single(item => item.Id == retrying.Id);
            checks.Check("en cola con error → Waiting, «El servidor reintenta (intento 1)»",
                step == QueueStep.Waiting && current.Notice?.Contains("intento 1") == true && current.Notice.Contains("DeploymentNotFound"));

            // 11. Re-extracción: trabajo opción 2, el reporte de su prompt.
            long idSession = api.CompleteJob(current.IdJob!.Value);
            await queue.AdvanceAsync(retrying.Id);
            QueuedMeeting reextract = queue.EnqueueReextract(idSession, "functional-spec", SessionSource.Window);
            step = await queue.AdvanceAsync(reextract.Id);
            current = queue.Items.Single(item => item.Id == reextract.Id);
            checks.Check("re-extracción: trabajo creado, el servidor procesa", step == QueueStep.Waiting && current.Status == QueuedMeetingStatus.Processing);
            api.CompleteJob(current.IdJob!.Value, "functional-spec", "# Especificación\n");
            step = await queue.AdvanceAsync(reextract.Id);
            current = queue.Items.Single(item => item.Id == reextract.Id);
            checks.Check("toma el reporte de su prompt (no el anterior de la sesión) y escribe el .md",
                step == QueueStep.Done && File.ReadAllText(current.ReportPath!).Contains("# Especificación") && Path.GetFileName(current.ReportPath!).StartsWith("functional-spec-"));
            api.FailNext[nameof(IDevLogApi.ReextractAsync)] = new ApiError.Rejected(409, "no transcript");
            QueuedMeeting noTranscript = queue.EnqueueReextract(idSession, "functional-spec", SessionSource.Window);
            checks.Check("re-extraer sin transcripción (409) → Failed, dicho en palabras",
                await queue.AdvanceAsync(noTranscript.Id) == QueueStep.Failed && queue.Items.Single(item => item.Id == noTranscript.Id).Error!.Contains("no tiene transcripción"));

            // 12. Sin sesión no se toca la red.
            auth.SignOut();
            int calls = api.Calls.Count;
            string waitingAudio = Audio("waiting.wav", 1000);
            QueuedMeeting waiting = queue.EnqueueAudio(waitingAudio, waitingAudio, 10, SessionSource.Window);
            checks.Check("sin sesión → SignedOut y ninguna llamada", await queue.AdvanceAsync(waiting.Id) == QueueStep.SignedOut && api.Calls.Count == calls);

            // 13. Un archivo de la cola roto no frena el resto.
            await File.WriteAllTextAsync(Path.Combine(queueDir, "broken.json"), "{ esto no es json");
            checks.Check("un JSON roto se aparta (.bad) y el resto se lee", queue.Items.Count > 0 && File.Exists(Path.Combine(queueDir, "broken.json.bad")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        return checks.Finish();
    }

    // ------------------------------------------------------------------ servicios de la UI

    public static int VerifyServices()
    {
        Console.WriteLine("=== Costos, prompts y ajustes por la API (reglas de presentación) ===");
        var checks = new Checks("--verify-api-services");

        checks.Check("costo desconocido nunca es 0: null con 2 sin precio → «desconocido»", new CostAmount(null, 2).Text().StartsWith("desconocido"));
        checks.Check("nada que costear → «nada», no «US$0»", new CostAmount(null, 0).Text() == "nada");
        checks.Check("conocido + desconocidos → «US$… + 1 sin precio»", new CostAmount(12_300, 1).Text() == "US$0,0123 + 1 sin precio");
        checks.Check("desde un dólar, centavos", new CostAmount(1_234_567).Text() == "US$1,23");
        checks.Check("la suma de null y null sigue siendo null", (new CostAmount(null, 1) + new CostAmount(null, 0)).KnownMicroUsd is null);
        checks.Check("la suma de null y un monto es el monto", (new CostAmount(null, 1) + new CostAmount(500)) is { KnownMicroUsd: 500, UnknownCount: 1 });
        CostPeriod period = CostPeriod.From(new ApiCostRow(null, 3, null, 2, 4000, 0, 10, 20));
        checks.Check("el total de un período con la transcripción sin precio lo dice", period.Total.Text() == "US$0,0040 + 2 sin precio");

        var catalog = new CatalogPrompt("assignment-meeting", "v1", "Reunión de asignación", "", "StructuredMeetingReport", "TEXTO DEL CÓDIGO");
        DateTimeOffset t0 = DateTimeOffset.UtcNow.AddDays(-2);
        var baseEdit = new PromptVersionRow(4, "assignment-meeting", "v1", "v1.1", "TEXTO BASE", null, t0, null);
        var mine = new PromptVersionRow(6, "Assignment-Meeting", "v1", "v1-mine-1", "TEXTO MÍO", null, t0.AddDays(1), 1);
        var revert = new PromptVersionRow(7, "assignment-meeting", "v1", null, null, null, t0.AddDays(1.5), 1);
        checks.Check("sin ediciones vale el texto del catálogo", PromptLibraryService.EntryOf(catalog, [], []) is { Text: "TEXTO DEL CÓDIGO", CanUseBuiltIn: false });
        checks.Check("la edición de la base vale si no tengo una mía", PromptLibraryService.EntryOf(catalog, [baseEdit], []).Text == "TEXTO BASE");
        PromptEntry withMine = PromptLibraryService.EntryOf(catalog, [mine], [mine]);
        checks.Check("la mía vale (ids sin mayúsculas) y se puede volver al original", withMine is { Text: "TEXTO MÍO", Version: "v1-mine-1", CanUseBuiltIn: true });
        checks.Check("el nombre sugerido sigue el del teléfono: v1-mine-2", withMine.SuggestedVersion() == "v1-mine-2");
        PromptEntry reverted = PromptLibraryService.EntryOf(catalog, [revert, mine], [revert, mine]);
        checks.Check("una vuelta al original más nueva → vale el catálogo", reverted is { Text: "TEXTO DEL CÓDIGO", BuiltInChosen: true, CanUseBuiltIn: false });
        checks.Check("la vuelta al original no cuenta para el nombre sugerido", reverted.SuggestedVersion() == "v1-mine-2");

        checks.Check("nunca un precio en mi capa", !ServerSettingsService.IsAllowed("Pricing:Gemini:gemini-3.5-flash-lite:InputPerMillion"));
        checks.Check("nunca un secreto", !ServerSettingsService.IsAllowed("Deepgram:ApiKey") && !ServerSettingsService.IsAllowed("AzureFoundry:ApiKey"));
        checks.Check("nunca un Endpoint", !ServerSettingsService.IsAllowed("AzureFoundry:Endpoint"));
        checks.Check("los editables sí", ServerSettingsService.Editable.All(item => ServerSettingsService.IsAllowed(item.Key)));
        checks.Check("la retención del audio (018) es editable", ServerSettingsService.Editable.Any(item => item.Key == "Audio:RetentionDays"));

        return checks.Finish();
    }
}
