# VardyParty Local Service

**For local development setup**, see [DEVELOPMENT.md](./DEVELOPMENT.md).

**V2/MP pages** use a different API than **V1/FB**: [docs/MP_CHROME.md](docs/MP_CHROME.md) (run it on your desktop) and [docs/MP_PROTOCOL.md](docs/MP_PROTOCOL.md) (`POST /mp`).

## Install as a background service

### Windows (Windows Service installer)

Run from an elevated PowerShell prompt:

```powershell
./scripts/install-service-windows.ps1
```

What it does:

- Publishes `VardyParty.LocalService`
- Installs files to `C:\Program Files\VardyParty\LocalService`
- Creates/recreates the Windows service
- Starts the service

Optional custom install directory:

```powershell
./scripts/install-service-windows.ps1 -InstallDirectory "C:\VardyParty\LocalService"
```

If you already published and only want to reinstall the service:

```powershell
./scripts/install-service-windows.ps1 -SkipPublish
```

### Linux (systemd)

First publish:

```bash
dotnet publish ./VardyParty.LocalService/VardyParty.LocalService.csproj -c Release
```

Then install:

```bash
chmod +x ./local-scripts/install-service-linux.sh
./local-scripts/install-service-linux.sh
```

Optional custom publish directory (for example an extracted self-contained release tarball):

```bash
./local-scripts/install-service-linux.sh /path/to/publish
```

The script starts the native `VardyParty.LocalService` executable only when the directory is a **self-contained** publish (its `VardyParty.LocalService.runtimeconfig.json` lists `includedFrameworks`, or `libcoreclr.so` is next to the dll), which needs no .NET runtime. For a framework-dependent publish such as the one above it runs `dotnet VardyParty.LocalService.dll` with the `dotnet` found on your `PATH`, even though that publish also contains an apphost; this needs the ASP.NET Core 11 runtime.

### macOS (launchd)

First publish:

```bash
dotnet publish ./VardyParty.LocalService/VardyParty.LocalService.csproj -c Release
```

Then install:

```bash
chmod +x ./local-scripts/install-service-macos.sh
./local-scripts/install-service-macos.sh
```

Optional custom publish directory (for example the extracted `VardyParty.LocalService-osx-x64-<version>.tar.gz` release asset, which is self-contained):

```bash
./local-scripts/install-service-macos.sh /path/to/publish
```

Like the Linux script, it launches the native `VardyParty.LocalService` executable only for a **self-contained** publish (`includedFrameworks` in the runtimeconfig, or `libcoreclr.dylib` next to the dll), which needs no .NET runtime. For a framework-dependent publish it runs `dotnet VardyParty.LocalService.dll` with the `dotnet` on your `PATH`, which needs the ASP.NET Core 11 runtime.

A tarball downloaded with a browser carries the Gatekeeper quarantine attribute, and launchd may refuse to start the (not notarized) executable until you clear it on the extracted folder:

```bash
xattr -dr com.apple.quarantine /path/to/publish
```

## Runtime notes

- Linux service unit name: `vardyparty-local-service`
- macOS launchd label: `com.vardyparty.localservice`
- Windows service name default: `VardyPartyLocalService`
- Service host integration is enabled in app startup:
  - `UseWindowsService()` on Windows
  - `UseSystemd()` on Linux

## Installer Bootstrapper

The project provides a Windows installer helper, the InstallerBootstrapper. See [docs/BOOTSTRAPPER.md](./docs/BOOTSTRAPPER.md) for details about what the bootstrapper does, how it affects the MSIX and build, firewall rule lifecycle, and guidance on why we keep it.

## Packages

This repo builds and tests the host. After CI succeeds on `main`, **Publish runtime NuGets** pushes `VardyParty.LocalService.Runtime.<rid>` to this repo's GitHub Packages feed. Those packages do not include Strategy plugin assemblies. This repo does not create installer releases, and Strategies does not start a build here.

[Vardy-Party/M3U8-resolver](https://github.com/Vardy-Party/M3U8-resolver) builds the installer release from these packages and the Strategies packages.
