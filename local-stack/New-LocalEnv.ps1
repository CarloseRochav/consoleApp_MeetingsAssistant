# Writes local-stack/.env (git-ignored) for docker-compose.yml. Prints names only, never a value.
#  - JWT_KEY: a fresh random key for the local API (local tokens never work against production).
#  - STORAGE: Azurite's public development account (a documented, non-secret key).
#  - DEEPGRAM_KEY / GEMINI_KEY: copied from the worker's untracked appsettings.json, so local
#    meetings can be transcribed and extracted. Leave them empty to run without paying providers.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$workerSettings = 'C:\Projects\MeetingAssistant_Worker\src\MeetingAssistant.Worker\appsettings.json'

$bytes = New-Object byte[] 48
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$jwt = [Convert]::ToBase64String($bytes)
$azurite = 'DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10010/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10011/devstoreaccount1;'

$deepgram = ''; $gemini = ''
if (Test-Path $workerSettings) {
    $w = Get-Content $workerSettings -Raw | ConvertFrom-Json
    $deepgram = "$($w.Deepgram.ApiKey)"; $gemini = "$($w.Gemini.ApiKey)"
}
$lines = @(
    "JWT_KEY=$jwt",
    "STORAGE=$azurite",
    "DEEPGRAM_KEY=$deepgram",
    "GEMINI_KEY=$gemini")
[IO.File]::WriteAllLines((Join-Path $here '.env'), $lines, (New-Object System.Text.UTF8Encoding $false))
"wrote .env: JWT_KEY (new), STORAGE (Azurite), DEEPGRAM_KEY ($(if ($deepgram) {'set'} else {'empty'})), GEMINI_KEY ($(if ($gemini) {'set'} else {'empty'}))"
