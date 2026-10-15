using System.IO.Compression;
using System.Xml.Linq;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Publica una version del instalador en Azure Blob para que otras maquinas la
/// instalen y se actualicen solas (.appinstaller).
///
/// El contenedor es PRIVADO: la cuenta tiene el acceso publico apagado, y es la
/// misma que guarda el audio de las reuniones. Windows descarga con un enlace
/// firmado (SAS) de solo lectura, atado a una directiva de acceso del
/// contenedor: borrar esa directiva revoca todos los enlaces sin rotar la clave
/// de la cuenta. Cada publicacion renueva el vencimiento de la directiva, y los
/// enlaces no cambian.
///
/// La firma del enlace no puede llevar '+' ni '/'. App Installer decodifica el
/// %2B de vuelta a '+' antes de pedir el archivo, Azure lo lee como espacio y
/// responde 403 (visto en la primera instalacion real, 2026-10-15; Invoke-
/// WebRequest lo manda tal cual y por eso el script y el certificado si
/// bajaban). La firma depende del nombre de la directiva, asi que se prueba
/// <c>meeting-assistant-install</c>, <c>-2</c>, <c>-3</c>... y se queda la
/// primera limpia: la eleccion es determinista para una misma clave de cuenta,
/// y volver a publicar no cambia el enlace.
/// </summary>
internal static class ReleaseCommands
{
    public const string ContainerName = "releases";
    public const string AccessPolicyIdPrefix = "meeting-assistant-install";
    private const int MaxPolicyCandidates = 100;
    public const string AppInstallerBlobName = "MeetingAssistant.appinstaller";
    public const string CertificateBlobName = "MeetingAssistant.cer";
    public const string InstallScriptBlobName = "Install.ps1";
    private static readonly TimeSpan PolicyLifetime = TimeSpan.FromDays(730);

    private static readonly XNamespace AppInstallerNamespace = "http://schemas.microsoft.com/appx/appinstaller/2021";
    private static readonly XNamespace ManifestNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    /// <summary>Lo que identifica al paquete. Sale del AppxManifest.xml del .msix, no se escribe a mano.</summary>
    public sealed record PackageIdentity(string Name, string Publisher, string Version, string Architecture);

    public static async Task<int> PublishFromArgumentsAsync(IConfiguration configuration, IReadOnlyList<string> arguments)
    {
        string folder = arguments[0];
        bool apply = arguments.Skip(1).Contains("--apply", StringComparer.OrdinalIgnoreCase);

        string? msixPath = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.msix").SingleOrDefault() : null;
        string certificatePath = Path.Combine(folder, CertificateBlobName);
        string installScriptPath = Path.Combine(folder, InstallScriptBlobName);
        if (msixPath is null || !File.Exists(certificatePath) || !File.Exists(installScriptPath))
        {
            Console.Error.WriteLine(
                $"La carpeta tiene que tener exactamente un .msix, {CertificateBlobName} e {InstallScriptBlobName}: " +
                "es la que deja installer/Build-Release.ps1.");
            return 1;
        }

        PackageIdentity identity = ReadIdentity(msixPath);
        string msixBlobName = $"MeetingAssistant_{identity.Version}_{identity.Architecture}.msix";

        Console.WriteLine("=== Publicar instalador ===");
        Console.WriteLine($"Paquete: {identity.Name} {identity.Version} ({identity.Architecture}), firmado por {identity.Publisher}");
        Console.WriteLine($"Contenedor: {ContainerName} (privado; descarga con enlace SAS de solo lectura)");
        Console.WriteLine($"Se suben, en este orden: {msixBlobName}, {CertificateBlobName}, {InstallScriptBlobName}, {AppInstallerBlobName}");

        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("Simulacion: no se subio nada. Agrega --apply para publicar.");
            return 0;
        }

        string? connectionString = configuration["AzureStorage:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.StartsWith('<'))
        {
            Console.Error.WriteLine("Falta AzureStorage:ConnectionString en el appsettings.json del harness.");
            return 1;
        }

        var container = new BlobContainerClient(connectionString, ContainerName);
        await container.CreateIfNotExistsAsync(PublicAccessType.None);

        // Con Identifier, permisos y vencimiento salen de la directiva: el
        // enlace no los lleva, y por eso no cambia entre publicaciones.
        (string policyId, string sas) = ChooseAppInstallerSafeSas(container);

        // Renovar el vencimiento en cada publicacion: mientras se siga
        // publicando, los enlaces de las maquinas ya instaladas no caducan.
        // Reemplaza TODAS las directivas del contenedor: si el nombre cambio,
        // los enlaces de la directiva anterior dejan de funcionar.
        DateTimeOffset expiresOn = DateTimeOffset.UtcNow.Add(PolicyLifetime);
        await container.SetAccessPolicyAsync(permissions:
        [
            new BlobSignedIdentifier
            {
                Id = policyId,
                AccessPolicy = new BlobAccessPolicy
                {
                    PolicyStartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
                    PolicyExpiresOn = expiresOn,
                    Permissions = "r"
                }
            }
        ]);

        string UrlOf(string blobName) => $"{container.Uri.AbsoluteUri.TrimEnd('/')}/{blobName}?{sas}";

        // El .appinstaller va al final: es el puntero que ven las maquinas
        // instaladas, y no puede apuntar a un paquete que todavia no esta.
        await UploadAsync(container, msixBlobName, msixPath, "application/msix");
        await UploadAsync(container, CertificateBlobName, certificatePath, "application/pkix-cert");
        await UploadAsync(container, InstallScriptBlobName, installScriptPath, "text/plain");

        string appInstallerXml = BuildAppInstaller(identity, UrlOf(AppInstallerBlobName), UrlOf(msixBlobName));
        string appInstallerPath = Path.Combine(folder, AppInstallerBlobName);
        await File.WriteAllTextAsync(appInstallerPath, appInstallerXml);
        await UploadAsync(container, AppInstallerBlobName, appInstallerPath, "application/appinstaller");

        // Lleva el enlace firmado: queda en la carpeta de artefactos, que esta
        // fuera de git, y en la consola. No va a ningun archivo rastreado.
        string installCommand = BuildInstallCommand(UrlOf(InstallScriptBlobName), UrlOf(AppInstallerBlobName), UrlOf(CertificateBlobName));
        string installCommandPath = Path.Combine(folder, "install-command.txt");
        await File.WriteAllTextAsync(installCommandPath, installCommand);

        Console.WriteLine();
        Console.WriteLine($"Publicado. La directiva de acceso '{policyId}' vence el {expiresOn:yyyy-MM-dd} (se renueva en cada publicacion).");
        Console.WriteLine("Azure puede tardar hasta 30 s en aplicar una directiva nueva.");
        Console.WriteLine($"Comando para instalar en otra maquina (PowerShell, usuario normal), guardado en {installCommandPath}:");
        Console.WriteLine();
        Console.WriteLine(installCommand);
        return 0;
    }

    private static async Task UploadAsync(BlobContainerClient container, string blobName, string path, string contentType)
    {
        Console.WriteLine($"  subiendo {blobName} ({new FileInfo(path).Length / 1048576.0:F1} MB)...");
        await container.GetBlobClient(blobName).UploadAsync(path, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            // En bloques de 8 MB: el .msix (~130 MB) en una sola peticion
            // excede el plazo de 100 s del SDK con una subida lenta (2026-10-15),
            // y cada reintento volvia a empezar desde cero.
            TransferOptions = new Azure.Storage.StorageTransferOptions
            {
                InitialTransferSize = 8 * 1024 * 1024,
                MaximumTransferSize = 8 * 1024 * 1024,
                MaximumConcurrency = 4
            }
        });
    }

    /// <summary>
    /// El primer nombre de directiva cuyo enlace sobrevive a App Installer.
    /// Solo firma en local (HMAC con la clave de la cuenta): no llama a Azure.
    /// </summary>
    public static (string PolicyId, string Sas) ChooseAppInstallerSafeSas(BlobContainerClient container)
    {
        for (int candidate = 1; candidate <= MaxPolicyCandidates; candidate++)
        {
            string policyId = candidate == 1 ? AccessPolicyIdPrefix : $"{AccessPolicyIdPrefix}-{candidate}";
            string sas = container.GenerateSasUri(new BlobSasBuilder
            {
                BlobContainerName = container.Name,
                Resource = "c",
                Identifier = policyId
            }).Query.TrimStart('?');

            if (IsSafeForAppInstaller(sas)) return (policyId, sas);
        }

        throw new InvalidOperationException(
            $"Ninguno de {MaxPolicyCandidates} nombres de directiva dio una firma sin '+' ni '/'. No se publica un enlace que App Installer rompe.");
    }

    /// <summary>La firma no lleva '+' ni '/', ni escapados ni sin escapar.</summary>
    public static bool IsSafeForAppInstaller(string sasQuery)
    {
        string signature = sasQuery.Split('&')
            .FirstOrDefault(part => part.StartsWith("sig=", StringComparison.Ordinal))?["sig=".Length..]
            ?? throw new InvalidOperationException("El enlace SAS no tiene firma (sig=).");

        return !signature.Contains("%2B", StringComparison.OrdinalIgnoreCase)
            && !signature.Contains("%2F", StringComparison.OrdinalIgnoreCase)
            && !signature.Contains('+')
            && !signature.Contains('/');
    }

    public static PackageIdentity ReadIdentity(string msixPath)
    {
        using ZipArchive archive = ZipFile.OpenRead(msixPath);
        ZipArchiveEntry manifestEntry = archive.GetEntry("AppxManifest.xml")
            ?? throw new InvalidOperationException($"{msixPath} no tiene AppxManifest.xml.");

        using Stream stream = manifestEntry.Open();
        XElement identity = XDocument.Load(stream).Root?.Element(ManifestNamespace + "Identity")
            ?? throw new InvalidOperationException("El AppxManifest.xml no tiene Identity.");

        string Attribute(string name) => identity.Attribute(name)?.Value
            ?? throw new InvalidOperationException($"Identity no tiene {name}.");

        return new PackageIdentity(Attribute("Name"), Attribute("Publisher"), Attribute("Version"), Attribute("ProcessorArchitecture"));
    }

    /// <summary>
    /// El archivo que Windows consulta para instalar y para buscar
    /// actualizaciones. Con XElement y no con texto: el enlace SAS lleva
    /// <c>&amp;</c>, que en XML hay que escapar.
    /// </summary>
    public static string BuildAppInstaller(PackageIdentity identity, string appInstallerUri, string msixUri)
    {
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(AppInstallerNamespace + "AppInstaller",
                new XAttribute("Version", identity.Version),
                new XAttribute("Uri", appInstallerUri),
                new XElement(AppInstallerNamespace + "MainPackage",
                    new XAttribute("Name", identity.Name),
                    new XAttribute("Publisher", identity.Publisher),
                    new XAttribute("Version", identity.Version),
                    new XAttribute("ProcessorArchitecture", identity.Architecture),
                    new XAttribute("Uri", msixUri)),
                new XElement(AppInstallerNamespace + "UpdateSettings",
                    // Al abrir la app, si hay version nueva, pregunta; no bloquea
                    // el arranque. Y una tarea de Windows revisa cada 8 h aunque
                    // la app viva dias en la bandeja.
                    new XElement(AppInstallerNamespace + "OnLaunch",
                        new XAttribute("HoursBetweenUpdateChecks", "0"),
                        new XAttribute("ShowPrompt", "true"),
                        new XAttribute("UpdateBlocksActivation", "false")),
                    new XElement(AppInstallerNamespace + "AutomaticBackgroundTask"))));

        using var writer = new Utf8StringWriter();
        document.Save(writer);
        return writer.ToString();
    }

    /// <summary>
    /// Se pega en PowerShell en la maquina nueva. Install.ps1 pide permisos de
    /// administrador solo para confiar el certificado; la app se instala para
    /// el usuario que lo corre.
    /// </summary>
    public static string BuildInstallCommand(string installScriptUri, string appInstallerUri, string certificateUri) =>
        $"$script = Join-Path $env:TEMP 'MeetingAssistant-Install.ps1'; " +
        $"Invoke-WebRequest '{installScriptUri}' -OutFile $script; " +
        $"powershell -NoProfile -ExecutionPolicy Bypass -File $script -AppInstallerUri '{appInstallerUri}' -CertificateUri '{certificateUri}'";

    /// <summary>Offline: arma un .msix falso y un .appinstaller, y los vuelve a leer.</summary>
    public static int Verify()
    {
        int failures = 0;
        void Check(string description, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
            if (!passed) failures++;
        }

        Console.WriteLine("=== Publicacion del instalador ===");
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ma-release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            // --- La identidad sale del manifiesto del paquete -----------------
            Console.WriteLine($"{Environment.NewLine}--- Identidad del paquete ---");
            string msixPath = Path.Combine(temporaryDirectory, "fake.msix");
            using (ZipArchive archive = ZipFile.Open(msixPath, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("AppxManifest.xml").Open());
                writer.Write(
                    """
                    <?xml version="1.0" encoding="utf-8"?>
                    <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                      <Identity Name="Test.Package" Publisher="CN=Test Publisher" Version="1.2.3.0" ProcessorArchitecture="x64" />
                    </Package>
                    """);
            }

            PackageIdentity identity = ReadIdentity(msixPath);
            Check("lee nombre, editor, version y arquitectura del AppxManifest.xml",
                identity == new PackageIdentity("Test.Package", "CN=Test Publisher", "1.2.3.0", "x64"));

            // --- El .appinstaller ---------------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Archivo .appinstaller ---");
            const string sas = "sv=2025-01-05&si=policy&sr=c&sig=abc%2Bdef";
            string appInstallerUri = $"https://example.blob.core.windows.net/releases/MeetingAssistant.appinstaller?{sas}";
            string msixUri = $"https://example.blob.core.windows.net/releases/MeetingAssistant_1.2.3.0_x64.msix?{sas}";
            string xml = BuildAppInstaller(identity, appInstallerUri, msixUri);

            XDocument parsed = XDocument.Parse(xml);
            XElement root = parsed.Root!;
            XElement? main = root.Element(AppInstallerNamespace + "MainPackage");
            Check("es XML valido con el esquema 2021 de App Installer", root.Name == AppInstallerNamespace + "AppInstaller");
            Check("declara utf-8 (App Installer rechaza utf-16)", xml.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", StringComparison.Ordinal));
            Check("el & del enlace firmado va escapado en el texto", xml.Contains("&amp;si=policy", StringComparison.Ordinal) && !xml.Contains("&si=", StringComparison.Ordinal));
            Check("y al leerlo vuelve a ser el enlace exacto", root.Attribute("Uri")?.Value == appInstallerUri && main?.Attribute("Uri")?.Value == msixUri);
            Check("la version del archivo y la del paquete son la del manifiesto",
                root.Attribute("Version")?.Value == "1.2.3.0" && main?.Attribute("Version")?.Value == "1.2.3.0");
            Check("el paquete principal lleva nombre, editor y arquitectura",
                main?.Attribute("Name")?.Value == "Test.Package" &&
                main.Attribute("Publisher")?.Value == "CN=Test Publisher" &&
                main.Attribute("ProcessorArchitecture")?.Value == "x64");
            XElement? updates = root.Element(AppInstallerNamespace + "UpdateSettings");
            Check("busca actualizaciones al abrir, sin bloquear el arranque",
                updates?.Element(AppInstallerNamespace + "OnLaunch") is { } onLaunch &&
                onLaunch.Attribute("UpdateBlocksActivation")?.Value == "false");
            Check("y en segundo plano, aunque la app no se abra",
                updates?.Element(AppInstallerNamespace + "AutomaticBackgroundTask") is not null);

            // --- El comando de instalacion ------------------------------------
            Console.WriteLine($"{Environment.NewLine}--- Comando de instalacion ---");
            string command = BuildInstallCommand("https://x/Install.ps1?" + sas, appInstallerUri, "https://x/MeetingAssistant.cer?" + sas);
            Check("los enlaces van entre comillas simples (el & no corta el comando)",
                command.Contains($"'{appInstallerUri}'", StringComparison.Ordinal));
            Check("corre Install.ps1 con los dos enlaces", command.Contains("-AppInstallerUri", StringComparison.Ordinal) && command.Contains("-CertificateUri", StringComparison.Ordinal));

            // --- La firma sobrevive a App Installer ---------------------------
            Console.WriteLine($"{Environment.NewLine}--- Firma del enlace (App Installer decodifica %2B) ---");
            Check("rechaza una firma con %2B (el 403 de la primera instalacion)", !IsSafeForAppInstaller("sv=2026-10-06&si=p&sr=c&sig=tHWc7kR%2BZg1K%3D"));
            Check("rechaza una firma con %2F", !IsSafeForAppInstaller("sv=2026-10-06&si=p&sr=c&sig=abc%2fdef%3D"));
            Check("acepta una firma sin + ni / (el %3D del final no molesta)", IsSafeForAppInstaller("sv=2026-10-06&si=p&sr=c&sig=abcDEF123%3D"));

            // Clave inventada: firmar es local, nada sale a la red.
            byte[] fakeKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64);
            var fakeContainer = new BlobContainerClient(
                $"DefaultEndpointsProtocol=https;AccountName=fakeaccount;AccountKey={Convert.ToBase64String(fakeKey)};EndpointSuffix=core.windows.net",
                ContainerName);
            (string policyId, string chosenSas) = ChooseAppInstallerSafeSas(fakeContainer);
            Check($"elige una directiva con firma limpia ({policyId})",
                IsSafeForAppInstaller(chosenSas) && chosenSas.Contains($"si={policyId}", StringComparison.Ordinal));
            Check("y la misma clave da siempre la misma directiva (republicar no cambia el enlace)",
                ChooseAppInstallerSafeSas(fakeContainer) == (policyId, chosenSas));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }

        Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>StringWriter declara utf-16 por defecto; App Installer quiere utf-8.</summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(false);
    }
}
