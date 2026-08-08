using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace MeetingAssistant.App;

/// <summary>
/// Runs a Cloudflare Quick Tunnel (<c>cloudflared tunnel --url ...</c>) pointed at
/// this app's localhost-only API. Quick Tunnels need no Cloudflare account, domain,
/// or token — cloudflared requests a random *.trycloudflare.com hostname on every
/// start and logs it to stderr. The connector is deliberately optional so a normal
/// desktop launch never publishes the recording endpoints by accident.
///
/// The URL is ephemeral and changes on every restart, so the resolved URL is written
/// to "cloudflare-tunnel-url.txt" next to the executable — there's no console to read
/// it from once the app is running as a packaged WinUI app.
/// </summary>
public sealed class CloudflareTunnelService : IDisposable
{
    private static readonly Regex QuickTunnelUrlPattern =
        new(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.Compiled);

    private readonly bool _enabled;
    private readonly string _cloudflaredPath;
    private readonly int _port;
    private readonly string _urlFilePath;
    private Process? _process;

    public CloudflareTunnelService(IConfiguration configuration)
    {
        _enabled = configuration.GetValue<bool?>("CloudflareTunnel:Enabled") ?? false;
        _cloudflaredPath = ReadConfiguredValue(configuration, "CloudflareTunnel:CloudflaredPath") ?? "cloudflared.exe";
        _port = configuration.GetValue<int?>("Api:Port") ?? 5757;
        _urlFilePath = Path.Combine(AppContext.BaseDirectory, "cloudflare-tunnel-url.txt");
    }

    public string? PublicUrl { get; private set; }

    public void Start()
    {
        if (!_enabled)
        {
            return;
        }

        if (_process is not null)
        {
            throw new InvalidOperationException("El túnel de Cloudflare ya está corriendo.");
        }

        PublicUrl = null;
        try
        {
            File.Delete(_urlFilePath);
        }
        catch (IOException)
        {
            // Ignorable: el archivo se sobreescribe apenas se resuelva la nueva URL.
        }

        var startInfo = new ProcessStartInfo(_cloudflaredPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("tunnel");
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add($"http://localhost:{_port}");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => TryCaptureUrl(e.Data);
        process.ErrorDataReceived += (_, e) => TryCaptureUrl(e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException("No se pudo iniciar cloudflared.");
        }

        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public void Stop()
    {
        Process? process = _process;
        _process = null;
        PublicUrl = null;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private void TryCaptureUrl(string? line)
    {
        if (PublicUrl is not null || line is null)
        {
            return;
        }

        Match match = QuickTunnelUrlPattern.Match(line);
        if (!match.Success)
        {
            return;
        }

        PublicUrl = match.Value;
        File.WriteAllText(_urlFilePath, PublicUrl);
    }

    private static string? ReadConfiguredValue(IConfiguration configuration, string key)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value) || value.StartsWith('<') ? null : value;
    }
}
