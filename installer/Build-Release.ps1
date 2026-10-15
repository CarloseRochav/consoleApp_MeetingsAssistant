<#
.SYNOPSIS
    Arma una version firmada de Meeting Assistant para instalar en otras
    maquinas y, con -Publish, la sube a Azure Blob.

.DESCRIPTION
    1. Sube la version del Package.appxmanifest (1.0.N.0 -> 1.0.N+1.0), salvo
       que se pase -Version. Las maquinas instaladas solo se actualizan a una
       version mayor. El cambio queda en git para commitear.
    2. Compila Release x64, autocontenido (el paquete trae .NET y el Windows
       App SDK), y lo firma con el certificado de CurrentUser\My.
    3. Comprueba que el .msix NO lleva appsettings.json (las claves) y que la
       firma es valida. Si algo falla, no sigue.
    4. Deja en artifacts\release\<version>\ el .msix, el .cer (solo la parte
       publica) e Install.ps1.
    5. Con -Publish corre harness --publish-release --apply sobre esa carpeta.

.EXAMPLE
    .\installer\Build-Release.ps1            # arma y deja listo, sin subir
    .\installer\Build-Release.ps1 -Publish   # arma y publica
#>
param(
    [string] $Version,
    [string] $CertificateSubject = 'CN=MeetingAssistant Local Publisher',
    [switch] $Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $root 'src\MeetingAssistant.App\MeetingAssistant.App.csproj'
$manifestPath = Join-Path $root 'src\MeetingAssistant.App\Package.appxmanifest'

# --- Certificado ------------------------------------------------------------------
$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $CertificateSubject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $certificate) {
    throw "No hay un certificado vigente '$CertificateSubject' con clave privada en Cert:\CurrentUser\My. " +
          "Se crea con: New-SelfSignedCertificate -Type Custom -Subject '$CertificateSubject' -KeyUsage DigitalSignature " +
          "-FriendlyName 'Meeting Assistant' -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3) " +
          "-TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')"
}
Write-Host "Certificado: $($certificate.Subject) ($($certificate.Thumbprint)), vence $($certificate.NotAfter.ToString('yyyy-MM-dd'))"

# --- Version ----------------------------------------------------------------------
$manifestText = [IO.File]::ReadAllText($manifestPath)
$identityPattern = '(<Identity\b[^>]*\bVersion=")(\d+\.\d+\.\d+\.\d+)(")'
$current = [regex]::Match($manifestText, $identityPattern).Groups[2].Value
if (-not $current) { throw "No se encontro la version en $manifestPath." }

if (-not $Version) {
    $parts = $current.Split('.')
    $Version = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1).0"
}
if ([version]$Version -le [version]$current) {
    throw "La version nueva ($Version) tiene que ser mayor que la actual ($current): Windows no actualiza a una igual o menor."
}

$manifestText = [regex]::Replace($manifestText, $identityPattern, "`${1}$Version`${3}")
[IO.File]::WriteAllText($manifestPath, $manifestText, (New-Object Text.UTF8Encoding($true)))
Write-Host "Version: $current -> $Version (Package.appxmanifest, para commitear)"

# --- Compilar y firmar --------------------------------------------------------------
$outputDirectory = Join-Path $root "artifacts\release\$Version"
$packageDirectory = Join-Path $outputDirectory 'pkg\'
if (Test-Path $outputDirectory) { Remove-Item $outputDirectory -Recurse -Force }

# Un appsettings.json viejo en la salida Release terminaria dentro del paquete.
# El csproj ya corta la compilacion si lo encuentra; esto lo evita de entrada.
Get-ChildItem (Join-Path $root 'src\MeetingAssistant.App\bin') -Recurse -Filter appsettings.json -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\Release\\' } |
    Remove-Item -Force

dotnet build $appProject -c Release -p:Platform=x64 `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=true `
    -p:PackageCertificateThumbprint=$($certificate.Thumbprint) `
    -p:AppxBundle=Never `
    -p:UapAppxPackageBuildMode=SideloadOnly `
    -p:AppxSymbolPackageEnabled=false `
    -p:AppxPackageDir=$packageDirectory
if ($LASTEXITCODE -ne 0) { throw "La compilacion fallo (codigo $LASTEXITCODE)." }

$msix = Get-ChildItem $packageDirectory -Recurse -Filter *.msix | Select-Object -First 1
if (-not $msix) { throw "La compilacion no dejo ningun .msix en $packageDirectory." }

# --- Comprobaciones: nada de esto se publica si falla ---------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($msix.FullName)
try {
    $entries = @($zip.Entries | ForEach-Object FullName)
}
finally {
    $zip.Dispose()
}
if ($entries -contains 'appsettings.json') { throw 'El paquete lleva appsettings.json, con claves. No se publica.' }
if ($entries -notcontains 'appsettings.defaults.json') { throw 'El paquete no lleva appsettings.defaults.json: la app no arrancaria.' }

$signature = Get-AuthenticodeSignature $msix.FullName
if ($signature.Status -ne 'Valid') { throw "La firma del paquete no es valida: $($signature.Status) $($signature.StatusMessage)" }

Copy-Item $msix.FullName (Join-Path $outputDirectory "MeetingAssistant_$($Version)_x64.msix")
Export-Certificate -Cert $certificate -FilePath (Join-Path $outputDirectory 'MeetingAssistant.cer') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Install.ps1') $outputDirectory
Remove-Item $packageDirectory -Recurse -Force

Write-Host ''
Write-Host "Listo en $outputDirectory ($('{0:N1}' -f ($msix.Length / 1MB)) MB): sin appsettings.json, firma valida."

# --- Publicar -------------------------------------------------------------------------
$harness = Join-Path $root 'src\MeetingAssistant.Harness'
if ($Publish) {
    dotnet run --project $harness -- --publish-release $outputDirectory --apply
    if ($LASTEXITCODE -ne 0) { throw "La publicacion fallo (codigo $LASTEXITCODE)." }
}
else {
    Write-Host "Para publicar: dotnet run --project $harness -- --publish-release `"$outputDirectory`" --apply"
}
