<#
.SYNOPSIS
    Instala Meeting Assistant en esta maquina desde Azure Blob, con
    actualizaciones automaticas.

.DESCRIPTION
    Dos pasos:
      1. Confia el certificado con que se firmo el paquete (autofirmado). Es lo
         unico que necesita administrador: Windows solo acepta el certificado
         en LocalMachine\TrustedPeople. Se pide elevacion solo para eso.
      2. Instala la app para el usuario que corre este script, desde el
         .appinstaller. Instalada asi, Windows busca versiones nuevas solo.

    No lleva ninguna clave: al abrir la app por primera vez aparece la ventana
    de configuracion inicial.

    Lo genera y lo sube harness --publish-release, que tambien imprime el
    comando exacto con los enlaces.
#>
param(
    [Parameter(Mandatory)] [string] $AppInstallerUri,
    [Parameter(Mandatory)] [string] $CertificateUri
)

$ErrorActionPreference = 'Stop'
$packageName = '962A0BC5-A1BC-432A-8A38-55011BFE3EE0'

# --- 1. Certificado -----------------------------------------------------------
$certificatePath = Join-Path $env:TEMP 'MeetingAssistant.cer'
Invoke-WebRequest $CertificateUri -OutFile $certificatePath
$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $certificatePath
Write-Host "Certificado: $($certificate.Subject), huella $($certificate.Thumbprint), vence $($certificate.NotAfter.ToString('yyyy-MM-dd'))"

if (Test-Path "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)") {
    Write-Host 'Ya es de confianza en esta maquina.'
}
else {
    Write-Host 'Se pide permiso de administrador para confiar el certificado (solo este paso)...'
    $import = "Import-Certificate -FilePath '$certificatePath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    $process = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile', '-Command', $import
    if ($process.ExitCode -ne 0 -or -not (Test-Path "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)")) {
        throw 'No se pudo confiar el certificado (se cancelo la elevacion o fallo la importacion).'
    }
    Write-Host 'Certificado de confianza.'
}

# --- 2. App ---------------------------------------------------------------------
# Una registracion de desarrollo (dotnet run) tiene la misma identidad y bloquea
# la instalacion. Quitarla no borra los datos: viven en
# %LOCALAPPDATA%\MeetingAssistant, fuera del paquete.
$existing = Get-AppxPackage -Name $packageName
if ($existing -and $existing.IsDevelopmentMode) {
    Write-Host "Hay una registracion de desarrollo ($($existing.Version)). Se quita para instalar la firmada; los datos no se tocan."
    Remove-AppxPackage $existing.PackageFullName
}

Write-Host 'Instalando desde el .appinstaller...'
Add-AppxPackage -AppInstallerFile $AppInstallerUri

$installed = Get-AppxPackage -Name $packageName
Write-Host "Instalado: Meeting Assistant $($installed.Version). Abrela desde el menu Inicio."
