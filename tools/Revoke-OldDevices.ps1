<#
.SYNOPSIS
    Revoca en DevLog los dispositivos viejos de esta máquina (Fase 8, paso B).

.DESCRIPTION
    Cada inicio de sesión de la app crea un dispositivo nuevo con el nombre de la máquina, y cerrar
    sesión no lo revoca (un dispositivo no puede revocar: sólo el login del usuario). Este script
    inicia sesión con tu usuario de DevLog, lista tus dispositivos y revoca los que:
      - siguen activos (sin revoked_at),
      - se llaman como esta máquina (o "harness-" + la máquina), o como -Name si lo pasas,
      - y NO son el que usa la app ahora (el de %LOCALAPPDATA%\MeetingAssistant\device.json) ni uno de -Keep.
    Nunca toca otros nombres (el teléfono, otras máquinas) salvo que los pidas con -Name.

    Sin -Apply sólo muestra lo que revocaría. La contraseña se pide en el diálogo de Windows, viaja
    sólo en el login y no se guarda; el token del login (8 h) se borra de la sesión al terminar.

.EXAMPLE
    tools\Revoke-OldDevices.ps1                      # muestra qué revocaría
    tools\Revoke-OldDevices.ps1 -Apply               # revoca
    tools\Revoke-OldDevices.ps1 -Name 'OTRA-PC' -Apply
#>
[CmdletBinding()]
param(
    [string] $Username = 'tormentorXXVI',
    [string[]] $Name = @($env:COMPUTERNAME, "harness-$env:COMPUTERNAME"),
    [int[]] $Keep = @(),
    [switch] $Apply
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$api = ((Get-Content (Join-Path $root 'src\MeetingAssistant.App\appsettings.defaults.json') -Raw | ConvertFrom-Json).DevLog.BaseUrl).TrimEnd('/')

# El dispositivo que usa la app en esta máquina nunca se revoca (el id no es secreto; el token sí, y no se lee).
$deviceFile = Join-Path $env:LOCALAPPDATA 'MeetingAssistant\device.json'
if (Test-Path $deviceFile) {
    $current = (Get-Content $deviceFile -Raw | ConvertFrom-Json).IdDevice
    if ($current) { $Keep += [int]$current; "La app usa el dispositivo ${current}: se conserva." }
}

$cred = Get-Credential -UserName $Username -Message "DevLog ($api)"
if (-not $cred) { throw 'Cancelado.' }
try {
    $body = @{ username = $cred.UserName; password = $cred.GetNetworkCredential().Password } | ConvertTo-Json
    $login = Invoke-RestMethod -Method Post "$api/Auth/Login" -ContentType 'application/json' -Body $body
}
finally {
    Remove-Variable body, cred -ErrorAction SilentlyContinue
}

$headers = @{ Authorization = "Bearer $($login.token)" }
try {
    $devices = @(Invoke-RestMethod -Method Post "$api/Devices/Select_Devices" -Headers $headers)
    $devices | Sort-Object idDevice | Format-Table idDevice, name, created_at, last_seen_at, revoked_at -AutoSize

    $targets = @($devices | Where-Object {
        -not $_.revoked_at -and ($Name -contains $_.name) -and ($Keep -notcontains [int]$_.idDevice)
    })

    if ($targets.Count -eq 0) { 'Nada que revocar.'; return }

    "A revocar ($($targets.Count)): " + (($targets | ForEach-Object { "$($_.idDevice) $($_.name)" }) -join ', ')
    if (-not $Apply) { 'Simulación: no se revocó nada. Agrega -Apply para revocar.'; return }

    foreach ($device in $targets) {
        $answer = Invoke-RestMethod -Method Post "$api/Devices/InsertUpdate_Devices" -Headers $headers `
            -ContentType 'application/json' -Body (@{ option = 1; idDevice = [int]$device.idDevice } | ConvertTo-Json)
        if ([int]$answer.v_id -eq [int]$device.idDevice) { "Revocado $($device.idDevice) ($($device.name))." }
        else { Write-Warning "No se revocó $($device.idDevice): el servidor contestó v_id = $($answer.v_id)." }
    }
}
finally {
    Remove-Variable login, headers -ErrorAction SilentlyContinue
}
