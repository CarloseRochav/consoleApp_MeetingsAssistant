using System.Net;
using System.Text.Json;
using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.Infrastructure.Api;

/// <summary>
/// Expone IMeetingPipeline como dos endpoints HTTP locales:
///   POST /recording/start
///   POST /recording/stop  (síncrono — responde con transcript + reporte + ruta guardada)
///
/// Deliberadamente HttpListener, no Kestrel/ASP.NET Core: dos endpoints en una
/// app de escritorio no justifican un host web completo.
///
/// Requiere autenticación por token SIEMPRE, incluso en localhost — este
/// endpoint enciende el micrófono del usuario, no es un endpoint informativo
/// cualquiera. Solo escucha en localhost, nunca 0.0.0.0.
/// </summary>
public sealed class LocalRecordingApiServer : IDisposable
{
    private readonly IMeetingPipeline _pipeline;
    private readonly string _authToken;
    private readonly HttpListener _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenLoop;

    public LocalRecordingApiServer(IMeetingPipeline pipeline, IConfiguration configuration)
    {
        _pipeline = pipeline;

        _authToken = configuration["Api:AuthToken"]
            ?? throw new InvalidOperationException(
                "Falta configurar \"Api:AuthToken\" en appsettings.json. " +
                "Este endpoint enciende el micrófono — no debe exponerse sin token.");

        int port = configuration.GetValue<int?>("Api:Port") ?? 5757;

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        if (_listenLoop is not null)
        {
            throw new InvalidOperationException("LocalRecordingApiServer ya está corriendo.");
        }

        _cts = new CancellationTokenSource();
        _listener.Start();
        _listenLoop = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_listener.IsListening)
        {
            _listener.Stop();
        }
        _listenLoop = null;
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break; // Listener detenido externamente (Stop()).
            }

            // Fire-and-forget intencional: cada request se maneja en su propia
            // tarea para que /recording/stop (que tarda varios segundos por la
            // transcripción + extracción del LLM) no bloquee al listener de
            // aceptar la siguiente conexión.
            _ = HandleRequestAsync(context, cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        string? providedToken = request.Headers["X-Api-Token"];
        if (string.IsNullOrEmpty(providedToken) || providedToken != _authToken)
        {
            await WriteJsonAsync(response, 401, new { error = "Token inválido o ausente." });
            return;
        }

        try
        {
            switch (request.HttpMethod, request.Url?.AbsolutePath)
            {
                case ("POST", "/recording/start"):
                    await _pipeline.StartRecordingAsync(cancellationToken);
                    await WriteJsonAsync(response, 200, new { status = "recording" });
                    break;

                case ("POST", "/recording/stop"):
                    MeetingPipelineResult result = await _pipeline.StopRecordingAndProcessAsync(cancellationToken);
                    await WriteJsonAsync(response, 200, new
                    {
                        transcript = result.Transcription.Transcript,
                        report = result.Report,
                        savedReportPath = result.SavedReportPath
                    });
                    break;

                default:
                    await WriteJsonAsync(response, 404, new { error = "Ruta no encontrada." });
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            // Guard clauses de IMeetingPipeline (doble start / stop sin start) llegan aquí.
            await WriteJsonAsync(response, 409, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(response, 500, new { error = ex.Message });
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";

        // Reutiliza las mismas JsonSerializerOptions que MeetingReportParser usa
        // para deserializar (incluye el PriorityJsonConverter) — así el enum
        // Priority sale como "alta"/"media"/"baja" en la respuesta, consistente
        // con el resto del sistema, no como un número de enum crudo.
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, MeetingReportParser.SerializerOptions);

        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.OutputStream.Close();
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
        GC.SuppressFinalize(this);
    }
}
