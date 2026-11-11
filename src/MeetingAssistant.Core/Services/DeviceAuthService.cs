using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;

namespace MeetingAssistant.Core.Services;

public enum SignInOutcome
{
    Success,
    MissingFields,
    WrongCredentials,
    TooManyAttempts,
    Offline,
    CouldNotSave,
    Failed
}

public sealed record SignInResult(SignInOutcome Outcome, string Message, DeviceSession? Session = null);

public enum VerifyOutcome
{
    /// <summary>El servidor aceptó el token.</summary>
    Valid,

    /// <summary>El token fue revocado (o el usuario desactivado): esta máquina quedó sin sesión.</summary>
    Revoked,

    /// <summary>Sin respuesta (sin red, servidor caído): no cambió nada.</summary>
    Unknown,

    /// <summary>No había sesión que comprobar.</summary>
    SignedOut
}

public enum SignedOutReason
{
    /// <summary>El usuario cerró la sesión desde Configuración.</summary>
    ByUser,

    /// <summary>El servidor contestó 401 con el token del dispositivo: revocado.</summary>
    Revoked
}

/// <summary>Sin sesión de dispositivo: hay que iniciar sesión otra vez.</summary>
public sealed class DeviceSignedOutException()
    : InvalidOperationException("Esta máquina no tiene sesión en el servidor. Inicia sesión otra vez.");

/// <summary>
/// La sesión de esta máquina con la API (Fase 8, paso B). Iniciar sesión = el login del usuario de
/// DevLog una sola vez (la contraseña no se guarda y su arreglo se borra) → un token de dispositivo
/// con el nombre de la máquina → guardado con DPAPI. El resto de la app sólo usa ese token. Un 401
/// con él quiere decir revocado: se borra y se avisa con <see cref="SignedOut"/>, y la app vuelve al
/// inicio de sesión. Referencia: el <c>AuthRepository</c> del teléfono.
/// </summary>
public sealed class DeviceAuthService
{
    private readonly IDevLogApi _api;
    private readonly IDeviceSessionStore _store;
    private readonly object _sync = new();
    private DeviceSession? _current;

    public DeviceAuthService(IDevLogApi api, IDeviceSessionStore store, string deviceName)
    {
        _api = api;
        _store = store;
        DeviceName = deviceName;
    }

    /// <summary>El nombre con que esta máquina se registra (el de la máquina).</summary>
    public string DeviceName { get; }

    public DeviceSession? Current
    {
        get { lock (_sync) return _current; }
    }

    public bool IsSignedIn => Current is not null;

    /// <summary>Se levanta al cerrarse la sesión, por el usuario o por un 401 (revocado). Puede llegar en cualquier hilo.</summary>
    public event EventHandler<SignedOutReason>? SignedOut;

    /// <summary>Se levanta al iniciar sesión. Puede llegar en cualquier hilo.</summary>
    public event EventHandler<DeviceSession>? SignedIn;

    /// <summary>Carga la sesión guardada, sin red, para que la app abra aunque no haya conexión.</summary>
    public DeviceSession? Restore()
    {
        DeviceSession? session = _store.Load();
        lock (_sync) _current = session;
        return session;
    }

    /// <summary>Devices/Select_Me. Un revocado cierra la sesión; sin respuesta no cambia nada.</summary>
    public async Task<VerifyOutcome> VerifyAsync(CancellationToken cancellationToken = default)
    {
        if (Current is not { } session) return VerifyOutcome.SignedOut;

        ApiResult<DeviceIdentity> answer = await _api.MeAsync(session.Token, cancellationToken);
        return answer switch
        {
            ApiResult<DeviceIdentity>.Success => VerifyOutcome.Valid,
            ApiResult<DeviceIdentity>.Failure { Error: ApiError.Unauthorized } => HandleUnauthorized(session) ? VerifyOutcome.Revoked : VerifyOutcome.Valid,
            _ => VerifyOutcome.Unknown
        };
    }

    /// <summary>
    /// Login → dispositivo → guardado. <paramref name="password"/> se borra (ceros) antes de volver,
    /// pase lo que pase. El token del login (8 horas) se usa sólo para crear el dispositivo y se
    /// descarta.
    /// </summary>
    public async Task<SignInResult> SignInAsync(string username, char[] password, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(username) || password.Length == 0)
            {
                return new SignInResult(SignInOutcome.MissingFields, "Escribe el usuario y la contraseña.");
            }

            LoginResult login;
            switch (await _api.LoginAsync(username.Trim(), password, cancellationToken))
            {
                case ApiResult<LoginResult>.Success success:
                    login = success.Value;
                    break;
                case ApiResult<LoginResult>.Failure failure:
                    return FailureOf(failure.Error, wrongCredentials: true);
                default:
                    throw new InvalidOperationException();
            }

            DeviceCreated device;
            switch (await _api.CreateDeviceAsync(login.Token, DeviceName, cancellationToken))
            {
                case ApiResult<DeviceCreated>.Success success:
                    device = success.Value;
                    break;
                case ApiResult<DeviceCreated>.Failure failure:
                    return FailureOf(failure.Error, wrongCredentials: false);
                default:
                    throw new InvalidOperationException();
            }

            var session = new DeviceSession(device.Token, device.IdDevice, login.User.IdUser, login.User.Username, DeviceName);
            try
            {
                _store.Save(session);
            }
            catch (Exception exception)
            {
                return new SignInResult(SignInOutcome.CouldNotSave,
                    $"Se creó el dispositivo pero no se pudo guardar en esta máquina ({exception.GetType().Name}). " +
                    "Revócalo en DevLog y vuelve a intentar.");
            }

            lock (_sync) _current = session;
            Raise(SignedIn, session);
            return new SignInResult(SignInOutcome.Success, $"Sesión iniciada como {session.Username} en «{DeviceName}».", session);
        }
        finally
        {
            Array.Clear(password);
        }
    }

    /// <summary>Olvida el token en esta máquina. Sigue valiendo en el servidor hasta revocarlo en DevLog.</summary>
    public void SignOut()
    {
        lock (_sync)
        {
            if (_current is null) return;
            _current = null;
        }

        _store.Clear();
        Raise(SignedOut, SignedOutReason.ByUser);
    }

    /// <summary>El token de la sesión actual, o <see cref="DeviceSignedOutException"/>.</summary>
    public string RequireToken() => Current?.Token ?? throw new DeviceSignedOutException();

    /// <summary>
    /// Una llamada con el token del dispositivo, para los caminos de la UI: el valor, o
    /// <see cref="ApiException"/>. Un 401 cierra la sesión (revocado) antes de lanzar.
    /// </summary>
    public async Task<T> CallAsync<T>(Func<string, Task<ApiResult<T>>> call)
    {
        DeviceSession session = Current ?? throw new DeviceSignedOutException();
        ApiResult<T> answer = await call(session.Token);
        if (answer is ApiResult<T>.Failure { Error: ApiError.Unauthorized }) HandleUnauthorized(session);
        return answer.GetOrThrow();
    }

    /// <summary>
    /// El servidor rechazó el token de <paramref name="session"/>: revocado. Sólo cierra si sigue
    /// siendo la sesión actual (una respuesta vieja no puede cerrar una sesión nueva). True si cerró.
    /// </summary>
    public bool HandleUnauthorized(DeviceSession session)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_current, session)) return false;
            _current = null;
        }

        _store.Clear();
        Raise(SignedOut, SignedOutReason.Revoked);
        return true;
    }

    private static SignInResult FailureOf(ApiError error, bool wrongCredentials) => error switch
    {
        ApiError.Unauthorized when wrongCredentials => new SignInResult(SignInOutcome.WrongCredentials, "Usuario o contraseña incorrectos."),
        ApiError.RateLimited => new SignInResult(SignInOutcome.TooManyAttempts, "Demasiados intentos. Espera unos minutos y vuelve a probar."),
        ApiError.Network => new SignInResult(SignInOutcome.Offline, "No se pudo llegar al servidor. Revisa la conexión."),
        _ => new SignInResult(SignInOutcome.Failed, error.Describe())
    };

    private void Raise<T>(EventHandler<T>? handlers, T args)
    {
        foreach (EventHandler<T> handler in handlers?.GetInvocationList().Cast<EventHandler<T>>() ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Un suscriptor que falla no puede dejar la sesión a medias.
            }
        }
    }
}
