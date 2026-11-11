namespace MeetingAssistant.Core.Models;

/// <summary>
/// El resultado de una llamada a la API: el valor, o por qué no. Un fallo esperado (sin red, 404,
/// 429) nunca es una excepción: quien llama decide qué hacer con cada caso, como en el teléfono.
/// </summary>
public abstract record ApiResult<T>
{
    private ApiResult()
    {
    }

    public sealed record Success(T Value) : ApiResult<T>;

    public sealed record Failure(ApiError Error) : ApiResult<T>;

    public bool IsSuccess => this is Success;

    /// <summary>El valor, o <see cref="ApiException"/> con el error.</summary>
    public T GetOrThrow() => this switch
    {
        Success success => success.Value,
        Failure failure => throw new ApiException(failure.Error),
        _ => throw new InvalidOperationException()
    };

    public static implicit operator ApiResult<T>(ApiError error) => new Failure(error);
}

/// <summary>Por qué falló una llamada, en los términos a los que reacciona la app.</summary>
public abstract record ApiError
{
    /// <summary>401: sin token, contraseña mal, o un dispositivo revocado.</summary>
    public sealed record Unauthorized : ApiError
    {
        public override string Describe() => "El servidor no acepta las credenciales (401).";
    }

    /// <summary>403: con sesión, pero sin permiso.</summary>
    public sealed record Forbidden : ApiError
    {
        public override string Describe() => "El servidor no lo permite (403).";
    }

    /// <summary>404: no existe, o no es tuyo (la API contesta igual en los dos casos).</summary>
    public sealed record NotFound : ApiError
    {
        public override string Describe() => "El servidor no lo encontró (404).";
    }

    /// <summary>429 sin los números del tope (p. ej. el login: 10 intentos cada 15 minutos).</summary>
    public sealed record RateLimited : ApiError
    {
        public override string Describe() => "Demasiados intentos (429). Espera un rato.";
    }

    /// <summary>429 de Jobs con los números del servidor: el tope diario o los trabajos en curso. Pasajero.</summary>
    public sealed record CapReached(CapRefusal Refusal) : ApiError
    {
        public override string Describe() => Refusal.Describe();
    }

    /// <summary>Otro 4xx; <see cref="Message"/> es la explicación corta del servidor.</summary>
    public sealed record Rejected(int Code, string Message) : ApiError
    {
        public override string Describe() =>
            string.IsNullOrWhiteSpace(Message) ? $"El servidor lo rechazó (HTTP {Code})." : $"El servidor lo rechazó (HTTP {Code}): {Message}";
    }

    /// <summary>5xx: falló el servidor.</summary>
    public sealed record Server(int Code) : ApiError
    {
        public override string Describe() => $"El servidor tuvo un problema (HTTP {Code}).";
    }

    /// <summary>Sin respuesta: sin red, plazo vencido, conexión cortada. <see cref="Detail"/> nunca lleva un enlace ni un token.</summary>
    public sealed record Network(string Detail) : ApiError
    {
        public override string Describe() => "No se pudo llegar al servidor (sin conexión o no responde).";
    }

    /// <summary>Una respuesta que la app no pudo leer.</summary>
    public sealed record Unexpected(string Detail) : ApiError
    {
        public override string Describe() => $"Respuesta inesperada del servidor: {Detail}";
    }

    /// <summary>En palabras para el usuario.</summary>
    public abstract string Describe();

    /// <summary>Pasajero: reintentar más tarde puede funcionar sin que nadie haga nada.</summary>
    public bool IsTransient => this is Network or Server or RateLimited or CapReached;
}

/// <summary>Un <see cref="ApiError"/> como excepción, para los caminos de la UI que ya tienen try/catch.</summary>
public sealed class ApiException(ApiError error) : Exception(error.Describe())
{
    public ApiError Error { get; } = error;
}
