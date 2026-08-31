# Device Service installer (Windows)

1. Publish BOTH exes into one folder as self-contained ReadyToRun folders (no
   single-file bundling — RTR folders start faster and avoid temp extraction):

   ```
   dotnet publish windows/src/GuardPulse.Agent.Service -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o windows/installer/publish
   dotnet publish windows/src/GuardPulse.Agent.Session -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o windows/installer/publish
   ```

   (Exe names stay identical; the shared output folder ends up with both apps'
   files. If a file collision is reported, publish each into its own folder and
   merge, keeping either copy of shared runtimes.)
2. Copy `windows/installer` to the target machine.
3. In an elevated PowerShell run:
   `.\install.ps1 -ApiKey <web-api-key> -ProjectId <project-id> -DatabaseUrl https://<project>-default-rtdb.firebaseio.com`
   Optional: `-InstallDir "C:\Program Files\Device Service"`, `-SourceDir <publish folder>`.
4. install.ps1 copies the binaries, writes `agent-config.json` (file log level
   defaults to `warning`; set `"logLevel": "information"` for diagnostics),
   creates/starts the `GuardPulseDeviceService` service (display name
   "Device Service") with restart-on-failure and Safe Boot start flags, sets a
   SYSTEM/Admin-only security descriptor, locks down the state directory
   (Users get directory read only; secrets/ledger files are SYSTEM-only) and
   adds the `DeviceServiceAgent` Run key.
5. Log off / log on (or reboot) once so the per-user agent starts.
6. Uninstall (elevated): `.\uninstall.ps1` — add `-RemoveData` to also delete `%ProgramData%\GuardPulse`.
