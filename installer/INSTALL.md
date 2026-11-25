# Installing Meeting Assistant 1.0.4.0 (Phase 8 step B)

Owner's checklist for putting the step B release on each machine. The release
is already built and published (2026-11-11), so there's nothing to build here.

The install command contains a read-only SAS link. **It lives only in
`artifacts\release\1.0.4.0\install-command.txt` (git-ignored): never paste it
into this file or any other tracked file.**

## What you need

- Your DevLog username and password. The app asks once per machine; the
  password isn't stored.
- The vault folder where the `.md` reports should go on that machine.
- Administrator approval once, the first time a machine trusts the
  self-signed certificate.
- No provider key and no connection string: since step B the app has none.

## A. A machine that already has the app (1.0.3.0 or older)

1. Open Meeting Assistant. App Installer checks for a new version when the app
   opens and every 8 hours. If it doesn't update, close the app and open it
   again.
2. If the version check in section D still shows the old version, run the
   install command (section B).
   It updates in place and keeps your data.

## B. A new machine (or one where the update didn't apply)

1. Copy the contents of `artifacts\release\1.0.4.0\install-command.txt` from
   the dev PC to that machine. Use a private channel and don't save it in a
   shared place.
2. Open PowerShell as your normal user (not as administrator) and paste the
   command.
3. Approve the administrator prompt. It's only for trusting the certificate.
   If the machine already trusts it, there's no prompt.
4. The script ends with `Instalado: Meeting Assistant 1.0.4.0`.

On the **dev PC**, the script first removes the `dotnet run` development
registration, which has the same identity. Data in
`%LOCALAPPDATA%\MeetingAssistant` isn't touched.

## C. First run on each machine

1. **Configuración inicial** opens if the machine is missing its own values:
   pick the vault folder. The DevLog server address comes with the package.
2. **Iniciar sesión**: your DevLog username and password. The machine gets
   its own device token, encrypted for your Windows user.
3. The main window opens.

## D. Check that it works (about 2 minutes)

- [ ] The version is **1.0.4.0**. The app doesn't show it, so check in
      PowerShell:
      `(Get-AppxPackage -Name 962A0BC5-A1BC-432A-8A38-55011BFE3EE0).Version`
- [ ] Record about 30 seconds with some speech (button or hotkey), then stop.
- [ ] The `.md` shows up in the vault after about 1.5 to 2 minutes.
- [ ] History shows the new meeting, with its cost.

## If something fails

| Symptom | Likely cause | What to do |
| --- | --- | --- |
| The install command fails with an authorization error | The SAS link expired or its access policy was replaced | On the dev PC: `--publish-release artifacts\release\1.0.4.0 --apply` prints a new command |
| "No se pudo confiar el certificado" | The administrator prompt was cancelled | Run the command again and approve the prompt |
| `0x800711C7` or the app blocked on start | Smart App Control | Machine-specific; not solved by the installer |
| Sign-in says the session was closed by the server | That machine's device was revoked | Sign in again: it gets a new device |
| No `.md` after several minutes | No network or the API is down | It waits and resends on its own; the recording is safe on disk |

## Record it

Once a machine is done, add one line to the step B section of
`roadmap-meeting-ai-assistant.md`: the machine name, the date, and the job and
session ids of the test recording.
