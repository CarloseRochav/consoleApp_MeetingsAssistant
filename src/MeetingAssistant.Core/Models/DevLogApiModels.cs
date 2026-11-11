using System.Text.Json.Serialization;

namespace MeetingAssistant.Core.Models;

// Las formas de la API de DevLog (C:\Projects\DevLog_Backend, su CLAUDE.md manda), con sus nombres
// de campo (idSession, started_at...). Paso B de la Fase 8: el escritorio sólo habla con ella, con
// el token de este dispositivo, nunca con una clave de proveedor. El dinero viene en micro-dólares
// enteros y null quiere decir "precio desconocido", nunca cero.
//
// Lo que lleva una credencial (token, enlace SAS) o contenido de reuniones tiene un ToString que no
// lo muestra: así nunca llega a un log por accidente. Las atributos son de System.Text.Json, que es
// parte del runtime (no un paquete): la regla de Core se mantiene.

/// <summary>
/// El dispositivo con el que esta máquina habla con la API. <see cref="Token"/> es una credencial
/// (<c>dlg_...</c>): se guarda cifrado con DPAPI y nunca se escribe en un log.
/// </summary>
public sealed record DeviceSession(string Token, int IdDevice, int IdUser, string Username, string DeviceName)
{
    public override string ToString() =>
        $"DeviceSession(idDevice={IdDevice}, idUser={IdUser}, username={Username}, deviceName={DeviceName}, token=[redacted])";
}

/// <summary>Auth/Login: el token de 8 horas del usuario (se usa una vez, para crear el dispositivo).</summary>
public sealed record LoginResult(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("user")] LoginUser User)
{
    public override string ToString() => $"LoginResult(user={User}, token=[redacted])";
}

public sealed record LoginUser(
    [property: JsonPropertyName("idUser")] int IdUser,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("role")] string? Role = null);

/// <summary>Devices/InsertUpdate_Devices opción 0: el dispositivo y su token, que el servidor muestra sólo esta vez.</summary>
public sealed record DeviceCreated(
    [property: JsonPropertyName("v_id")] int IdDevice,
    [property: JsonPropertyName("token")] string Token)
{
    public override string ToString() => $"DeviceCreated(idDevice={IdDevice}, token=[redacted])";
}

/// <summary>Devices/Select_Me: quién es el dispositivo que llama (el servidor manda los ids como texto).</summary>
public sealed record DeviceIdentity(
    [property: JsonPropertyName("idUser")] string? IdUser,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("idDevice")] string? IdDevice,
    [property: JsonPropertyName("deviceName")] string? DeviceName);

/// <summary>Jobs/InsertUpdate_Jobs opción 0: el trabajo y un enlace de sólo escritura a su blob. El enlace es una credencial.</summary>
public sealed record JobUpload(
    [property: JsonPropertyName("v_id")] long IdJob,
    [property: JsonPropertyName("uploadUrl")] string UploadUrl,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("maxBytes")] long MaxBytes)
{
    public override string ToString() => $"JobUpload(idJob={IdJob}, expiresAt={ExpiresAt:o}, maxBytes={MaxBytes}, uploadUrl=[redacted])";
}

/// <summary>Una fila de Jobs/Select_Jobs. status: awaiting_upload, queued, processing, done, failed.</summary>
public sealed record JobRow(
    [property: JsonPropertyName("idJob")] long IdJob,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("idSession")] long? IdSession,
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("prompt_id")] string? PromptId,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt);

/// <summary>Una fila de Meetings/Select_Sessions: las reuniones de todos mis dispositivos.</summary>
public sealed record ApiSession(
    [property: JsonPropertyName("idSession")] long IdSession,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("ended_at")] DateTimeOffset? EndedAt,
    [property: JsonPropertyName("duration_seconds")] double? DurationSeconds,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("has_transcript")] bool HasTranscript,
    [property: JsonPropertyName("report_count")] long ReportCount,
    [property: JsonPropertyName("transcript_cost_micro_usd")] long? TranscriptCostMicroUsd,
    [property: JsonPropertyName("reports_cost_micro_usd")] long? ReportsCostMicroUsd,
    [property: JsonPropertyName("reports_cost_unknown")] long ReportsCostUnknown,
    [property: JsonPropertyName("audio_expires_at")] DateTimeOffset? AudioExpiresAt);

/// <summary>Meetings/Select_Transcripts. Contenido de una reunión: ToString no lo muestra.</summary>
public sealed record ApiTranscript(
    [property: JsonPropertyName("idSession")] long IdSession,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("cost_micro_usd")] long? CostMicroUsd,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
{
    public override string ToString() => $"ApiTranscript(idSession={IdSession}, {Text.Length} chars)";
}

/// <summary>Una fila de Meetings/Select_Reports. Contenido de una reunión: ToString no lo muestra.</summary>
public sealed record ApiReport(
    [property: JsonPropertyName("idReport")] long IdReport,
    [property: JsonPropertyName("idSession")] long IdSession,
    [property: JsonPropertyName("prompt_id")] string PromptId,
    [property: JsonPropertyName("prompt_version")] string? PromptVersion,
    [property: JsonPropertyName("markdown")] string Markdown,
    [property: JsonPropertyName("llm_provider")] string? LlmProvider,
    [property: JsonPropertyName("llm_model")] string? LlmModel,
    [property: JsonPropertyName("tokens_input")] int? TokensInput,
    [property: JsonPropertyName("tokens_output")] int? TokensOutput,
    [property: JsonPropertyName("cost_micro_usd")] long? CostMicroUsd,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
{
    public override string ToString() => $"ApiReport(idReport={IdReport}, idSession={IdSession}, promptId={PromptId}, {Markdown.Length} chars)";
}

/// <summary>Un resultado de Meetings/Select_Search: hasta dos fragmentos, lo buscado entre [ y ].</summary>
public sealed record ApiSearchHit(
    [property: JsonPropertyName("idSession")] long IdSession,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("snippet")] string Snippet,
    [property: JsonPropertyName("rank")] float Rank)
{
    public override string ToString() => $"ApiSearchHit(idSession={IdSession}, rank={Rank})";
}

/// <summary>
/// Una fila de Meetings/Select_Costs: los totales (opción 0, <see cref="Month"/> null) o un mes UTC
/// (opción 1). Un <c>*CostMicroUsd</c> es la suma de los precios conocidos (null si no se conoce
/// ninguno); un <c>*CostUnknown</c> cuenta lo que no tiene precio.
/// </summary>
public sealed record ApiCostRow(
    [property: JsonPropertyName("month")] DateTimeOffset? Month,
    [property: JsonPropertyName("sessions")] long Sessions,
    [property: JsonPropertyName("transcripts_cost_micro_usd")] long? TranscriptsCostMicroUsd,
    [property: JsonPropertyName("transcripts_cost_unknown")] long TranscriptsCostUnknown,
    [property: JsonPropertyName("reports_cost_micro_usd")] long? ReportsCostMicroUsd,
    [property: JsonPropertyName("reports_cost_unknown")] long ReportsCostUnknown,
    [property: JsonPropertyName("tokens_input")] long? TokensInput,
    [property: JsonPropertyName("tokens_output")] long? TokensOutput);

/// <summary>Una fila de Meetings/Select_Prompt_Catalog: un prompt del código, como lo corre el worker.</summary>
public sealed record CatalogPrompt(
    [property: JsonPropertyName("prompt_id")] string PromptId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("output_kind")] string OutputKind,
    [property: JsonPropertyName("system_prompt")] string SystemPrompt)
{
    public override string ToString() => $"CatalogPrompt(promptId={PromptId}, version={Version}, {SystemPrompt.Length} chars)";
}

/// <summary>Una fila de Meetings/Select_Prompt_Versions: una edición. Version y SystemPrompt null = vuelta al original.</summary>
public sealed record PromptVersionRow(
    [property: JsonPropertyName("idPromptVersion")] long IdPromptVersion,
    [property: JsonPropertyName("prompt_id")] string PromptId,
    [property: JsonPropertyName("base_version")] string BaseVersion,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("system_prompt")] string? SystemPrompt,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("idUser")] int? IdUser)
{
    public bool IsRevert => Version is null && SystemPrompt is null;

    /// <summary>Null = una edición compartida (la base); si no, la del usuario.</summary>
    public bool IsMine => IdUser is not null;

    public override string ToString() => $"PromptVersionRow(id={IdPromptVersion}, promptId={PromptId}, version={Version}, mine={IsMine})";
}

/// <summary>Una fila de Meetings/Select_Settings. IdUser null = la base compartida.</summary>
public sealed record ApiSetting(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("idUser")] int? IdUser);

/// <summary>Meetings/Select_Audio_Link: un enlace de sólo lectura, 15 minutos. Es una credencial.</summary>
public sealed record AudioLink(
    [property: JsonPropertyName("idSession")] long IdSession,
    [property: JsonPropertyName("idJob")] long IdJob,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("sizeBytes")] long? SizeBytes,
    [property: JsonPropertyName("audioExpiresAt")] DateTimeOffset AudioExpiresAt)
{
    public override string ToString() => $"AudioLink(idSession={IdSession}, expiresAt={ExpiresAt:o}, url=[redacted])";
}

/// <summary>
/// El cuerpo del 429 de Jobs/InsertUpdate_Jobs (migración 011): el tope diario de gasto o el
/// límite de trabajos sin terminar. Montos en dólares; <see cref="ResetsAt"/> es cuándo termina el "hoy".
/// </summary>
public sealed record CapRefusal(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("spentUsd")] decimal SpentUsd,
    [property: JsonPropertyName("capUsd")] decimal CapUsd,
    [property: JsonPropertyName("unpriced")] int Unpriced,
    [property: JsonPropertyName("inFlight")] int InFlight,
    [property: JsonPropertyName("maxInFlight")] int MaxInFlight,
    [property: JsonPropertyName("resetsAt")] DateTimeOffset? ResetsAt)
{
    /// <summary>Lo que frenó es el límite de trabajos en curso, no la plata.</summary>
    public bool IsInFlightLimit => MaxInFlight > 0 && InFlight >= MaxInFlight;

    /// <summary>En palabras para el usuario, con los números del servidor.</summary>
    public string Describe() => IsInFlightLimit
        ? $"Hay otras reuniones procesándose ({InFlight} de {MaxInFlight} a la vez). Se reintenta solo."
        : $"Se alcanzó el tope de gasto de hoy (US${SpentUsd:0.00} de US${CapUsd:0.00}" +
          (Unpriced > 0 ? $", {Unpriced} sin precio" : string.Empty) +
          (ResetsAt is { } resets ? $"; se renueva {resets.ToLocalTime():dd/MM HH:mm}" : string.Empty) +
          "). Se reintenta solo.";
}
