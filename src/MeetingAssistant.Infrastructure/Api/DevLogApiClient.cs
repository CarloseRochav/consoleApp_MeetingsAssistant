using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Infrastructure.Api;

/// <summary>
/// <see cref="IDevLogApi"/> por HTTP (Fase 8, paso B). Todas las acciones son POST con un JSON de
/// nombres de DevLog (<c>option</c>, <c>idSession</c>...), salvo <c>Devices/Select_Me</c> (GET). Nada
/// se escribe en un log: ni el token, ni la contraseña, ni un enlace, ni el contenido. Los mensajes
/// de error de red llevan sólo el tipo de la excepción, no su texto (un texto de .NET puede traer
/// la URL; las de esta API no llevan secretos, pero no hay por qué arriesgarlo).
/// </summary>
public sealed class DevLogApiClient : IDevLogApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    private readonly HttpClient _http;
    private readonly string _base;

    /// <param name="baseUrl">La dirección de la API (<c>DevLog:BaseUrl</c>), p. ej. https://ca-devlog-api....azurecontainerapps.io.</param>
    public DevLogApiClient(HttpClient http, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            // Sólo HTTPS (o localhost, para pruebas contra una API local): el token viaja en cada llamada.
            throw new ArgumentException("DevLog:BaseUrl tiene que ser una dirección https:// (o http://localhost para pruebas).", nameof(baseUrl));
        }

        _http = http;
        _base = uri.ToString().TrimEnd('/');
    }

    public async Task<ApiResult<LoginResult>> LoginAsync(string username, ReadOnlyMemory<char> password, CancellationToken cancellationToken = default)
    {
        // El cuerpo se arma en un buffer propio de tamaño fijo (nada de copias por crecer) y se
        // borra con ceros al terminar. Lo que HttpClient copie por dentro queda fuera de alcance.
        byte[] buffer = new byte[1024 + Encoding.UTF8.GetMaxByteCount(username.Length + password.Length) * 6];
        try
        {
            int length;
            using (var stream = new MemoryStream(buffer, writable: true))
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("username", username);
                    writer.WriteString("password", password.Span);
                    writer.WriteEndObject();
                }

                length = (int)stream.Position;
            }

            using var content = new ByteArrayContent(buffer, 0, length);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return await SendAsync<LoginResult>(HttpMethod.Post, "Auth/Login", content, token: null, cancellationToken);
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    public Task<ApiResult<DeviceCreated>> CreateDeviceAsync(string userToken, string name, CancellationToken cancellationToken = default) =>
        PostAsync<DeviceCreated>("Devices/InsertUpdate_Devices", new { option = 0, name }, userToken, cancellationToken);

    public Task<ApiResult<DeviceIdentity>> MeAsync(string deviceToken, CancellationToken cancellationToken = default) =>
        SendAsync<DeviceIdentity>(HttpMethod.Get, "Devices/Select_Me", content: null, deviceToken, cancellationToken);

    public Task<ApiResult<JobUpload>> CreateJobAsync(string deviceToken, string source, CancellationToken cancellationToken = default) =>
        PostAsync<JobUpload>("Jobs/InsertUpdate_Jobs", new { option = 0, source }, deviceToken, cancellationToken);

    public Task<ApiResult<JobRow>> ConfirmJobAsync(string deviceToken, long idJob, CancellationToken cancellationToken = default) =>
        PostAsync<JobRow>("Jobs/InsertUpdate_Jobs", new { option = 1, idJob }, deviceToken, cancellationToken);

    public Task<ApiResult<JobRow>> ReextractAsync(string deviceToken, long idSession, string promptId, string source, CancellationToken cancellationToken = default) =>
        PostAsync<JobRow>("Jobs/InsertUpdate_Jobs", new { option = 2, idSession, prompt_id = promptId, source }, deviceToken, cancellationToken);

    public async Task<ApiResult<JobRow>> GetJobAsync(string deviceToken, long idJob, CancellationToken cancellationToken = default) =>
        First(await PostAsync<List<JobRow>>("Jobs/Select_Jobs", new { option = 1, idJob }, deviceToken, cancellationToken));

    public async Task<ApiResult<IReadOnlyList<ApiSession>>> GetSessionsAsync(string deviceToken, int limit, int offset, CancellationToken cancellationToken = default) =>
        AsReadOnly(await PostAsync<List<ApiSession>>("Meetings/Select_Sessions", new { option = 0, limit, offset }, deviceToken, cancellationToken));

    public Task<ApiResult<ApiTranscript>> GetTranscriptAsync(string deviceToken, long idSession, CancellationToken cancellationToken = default) =>
        PostAsync<ApiTranscript>("Meetings/Select_Transcripts", new { idSession }, deviceToken, cancellationToken);

    public async Task<ApiResult<IReadOnlyList<ApiReport>>> GetReportsAsync(string deviceToken, long idSession, CancellationToken cancellationToken = default) =>
        AsReadOnly(await PostAsync<List<ApiReport>>("Meetings/Select_Reports", new { option = 0, idSession }, deviceToken, cancellationToken));

    public async Task<ApiResult<IReadOnlyList<ApiSearchHit>>> SearchAsync(string deviceToken, string query, int limit, CancellationToken cancellationToken = default) =>
        AsReadOnly(await PostAsync<List<ApiSearchHit>>("Meetings/Select_Search", new { query, limit }, deviceToken, cancellationToken));

    public async Task<ApiResult<IReadOnlyList<ApiCostRow>>> GetCostsAsync(string deviceToken, int option, CancellationToken cancellationToken = default) =>
        AsReadOnly(await PostAsync<List<ApiCostRow>>("Meetings/Select_Costs", new { option }, deviceToken, cancellationToken));

    public async Task<ApiResult<IReadOnlyList<CatalogPrompt>>> GetPromptCatalogAsync(string deviceToken, CancellationToken cancellationToken = default) =>
        AsReadOnly(await PostAsync<List<CatalogPrompt>>("Meetings/Select_Prompt_Catalog", new { option = 0 }, deviceToken, cancellationToken));

    public async Task<ApiResult<IReadOnlyList<PromptVersionRow>>> GetPromptVersionsAsync(string deviceToken, int option, CancellationToken cancellationToken = default) =>
        AsReadOnly(await PostAsync<List<PromptVersionRow>>("Meetings/Select_Prompt_Versions", new { option }, deviceToken, cancellationToken));

    public async Task<ApiResult<long>> InsertPromptVersionAsync(
        string deviceToken, string promptId, string baseVersion, string? version, string? systemPrompt, string? note, CancellationToken cancellationToken = default) =>
        await PostAsync<IdResult>("Meetings/Insert_Prompt_Versions",
            new { shared = false, prompt_id = promptId, base_version = baseVersion, version, system_prompt = systemPrompt, note },
            deviceToken, cancellationToken) switch
        {
            ApiResult<IdResult>.Success success => new ApiResult<long>.Success(success.Value.Id),
            ApiResult<IdResult>.Failure failure => new ApiResult<long>.Failure(failure.Error),
            _ => throw new InvalidOperationException()
        };

    public async Task<ApiResult<IReadOnlyList<ApiSetting>>> GetSettingsAsync(string deviceToken, int option, CancellationToken cancellationToken = default) =>
        AsReadOnly(await PostAsync<List<ApiSetting>>("Meetings/Select_Settings", new { option }, deviceToken, cancellationToken));

    public async Task<ApiResult<int>> SetSettingAsync(string deviceToken, string key, string? value, CancellationToken cancellationToken = default) =>
        await PostAsync<SettingWritten>("Meetings/InsertUpdate_Settings",
            new { option = value is null ? 1 : 0, shared = false, key, value },
            deviceToken, cancellationToken) switch
        {
            ApiResult<SettingWritten>.Success success => new ApiResult<int>.Success(success.Value.Id),
            ApiResult<SettingWritten>.Failure failure => new ApiResult<int>.Failure(failure.Error),
            _ => throw new InvalidOperationException()
        };

    public Task<ApiResult<AudioLink>> GetAudioLinkAsync(string deviceToken, long idSession, CancellationToken cancellationToken = default) =>
        PostAsync<AudioLink>("Meetings/Select_Audio_Link", new { idSession }, deviceToken, cancellationToken);

    private Task<ApiResult<T>> PostAsync<T>(string path, object body, string? token, CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return SendAsync<T>(HttpMethod.Post, path, content, token, cancellationToken);
    }

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, HttpContent? content, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{_base}/{path}") { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) return ErrorOf(response.StatusCode, body);

            T? value = JsonSerializer.Deserialize<T>(body, Json);
            return value is null
                ? new ApiResult<T>.Failure(new ApiError.Unexpected($"respuesta vacía de {path}"))
                : new ApiResult<T>.Success(value);
        }
        catch (HttpRequestException exception)
        {
            return new ApiResult<T>.Failure(new ApiError.Network(exception.HttpRequestError.ToString()));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ApiResult<T>.Failure(new ApiError.Network("timeout"));
        }
        catch (IOException exception)
        {
            return new ApiResult<T>.Failure(new ApiError.Network(exception.GetType().Name));
        }
        catch (JsonException)
        {
            return new ApiResult<T>.Failure(new ApiError.Unexpected($"no se pudo leer la respuesta de {path}"));
        }
        finally
        {
            content?.Dispose();
        }
    }

    /// <summary>Un 429 con los números del tope (Jobs) o sin ellos (el login).</summary>
    internal static ApiError ErrorOf(HttpStatusCode status, string body)
    {
        int code = (int)status;
        return code switch
        {
            401 => new ApiError.Unauthorized(),
            403 => new ApiError.Forbidden(),
            404 => new ApiError.NotFound(),
            429 => CapRefusalOf(body) is { } refusal ? new ApiError.CapReached(refusal) : new ApiError.RateLimited(),
            >= 500 and <= 599 => new ApiError.Server(code),
            _ => new ApiError.Rejected(code, ShortMessage(body))
        };
    }

    private static CapRefusal? CapRefusalOf(string body)
    {
        try
        {
            CapRefusal? refusal = JsonSerializer.Deserialize<CapRefusal>(body, Json);
            return refusal is not null && (refusal.MaxInFlight > 0 || refusal.CapUsd > 0) ? refusal : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// El texto de un 4xx: un string JSON ("..."), un ProblemDetails (title / errors) o texto. Corto,
    /// porque se muestra en una fila de la cola.
    /// </summary>
    private static string ShortMessage(string body)
    {
        string text = body.Trim();
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String) text = root.GetString() ?? string.Empty;
            else if (root.ValueKind == JsonValueKind.Object)
            {
                var parts = new List<string>();
                if (root.TryGetProperty("title", out JsonElement title) && title.ValueKind == JsonValueKind.String) parts.Add(title.GetString()!);
                if (root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty field in errors.EnumerateObject())
                    {
                        if (field.Value.ValueKind != JsonValueKind.Array) continue;
                        parts.AddRange(field.Value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!));
                    }
                }

                if (parts.Count > 0) text = string.Join(" ", parts);
            }
        }
        catch (JsonException)
        {
            // Texto plano.
        }

        return text.Length <= 200 ? text : text[..200];
    }

    private static ApiResult<T> First<T>(ApiResult<List<T>> rows) => rows switch
    {
        ApiResult<List<T>>.Success { Value.Count: > 0 } success => new ApiResult<T>.Success(success.Value[0]),
        ApiResult<List<T>>.Success => new ApiResult<T>.Failure(new ApiError.NotFound()),
        ApiResult<List<T>>.Failure failure => new ApiResult<T>.Failure(failure.Error),
        _ => throw new InvalidOperationException()
    };

    private static ApiResult<IReadOnlyList<T>> AsReadOnly<T>(ApiResult<List<T>> rows) => rows switch
    {
        ApiResult<List<T>>.Success success => new ApiResult<IReadOnlyList<T>>.Success(success.Value),
        ApiResult<List<T>>.Failure failure => new ApiResult<IReadOnlyList<T>>.Failure(failure.Error),
        _ => throw new InvalidOperationException()
    };

    private sealed record IdResult([property: System.Text.Json.Serialization.JsonPropertyName("v_id")] long Id);

    private sealed record SettingWritten([property: System.Text.Json.Serialization.JsonPropertyName("v_id")] int Id);
}
