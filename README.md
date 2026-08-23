# EAVdrop v0.1

EAVdrop is a read-only Emby activity viewer for Windows 11, Android phones, and Android TV.

## v0.1 scope

- Dashboard showing active playback sessions.
- Current user, media title, device/client, playback progress, and playback method/status.
- General Emby Activity Log viewer.
- Filter the Activity Log by Emby user and search text.
- User list with a per-user activity screen and current playback.
- Local LAN and remote HTTPS server URLs.
- Auto / Local / Remote connection mode.
- API key stored with .NET MAUI SecureStorage.
- Android TV Leanback launcher declaration and no touchscreen requirement.
- Read-only calls only.

Playback Reporting is intentionally not queried yet. The first milestone is to verify the standard Emby 4.9.5.0 API against the real server. Once that works, detailed Playback Reporting history can be added without destabilizing the base viewer.

## First test

1. Build/install EAVdrop.
2. Open Settings.
3. Local URL is prefilled as `http://192.168.1.188:19096`.
4. Enter the EAVdrop API key locally. Do not commit the key to the project.
5. Leave connection mode on Auto.
6. Tap/click **Test Connection**.
7. Confirm the status reports the Emby server name/version and that the Sessions endpoint is OK.
8. Open Dashboard, Activity, and Users.

For remote access, enter the Emby HTTPS URL in Settings. Auto mode tries the local URL first, then the remote URL.

## Build locally on Windows 11

Install Visual Studio with the .NET MAUI workload, or install .NET 10 and the MAUI workload. From a Developer PowerShell:

```powershell
dotnet workload install maui
dotnet restore EAVdrop.csproj
```

Android APK:

```powershell
dotnet publish EAVdrop.csproj -f net10.0-android -c Release -p:AndroidPackageFormats=apk
```

Windows x64 unpackaged build:

```powershell
dotnet publish EAVdrop.csproj -f net10.0-windows10.0.19041.0 -c Release -p:RuntimeIdentifierOverride=win-x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true
```

## GitHub Actions

The included `.github/workflows/build.yml` builds both targets when run manually or when `main` is updated. The workflow uploads:

- `EAVdrop-Android` containing the APK.
- `EAVdrop-Windows-x64` containing a zip of the Windows build.

## Emby API calls used in v0.1

All authenticated requests use `X-Emby-Token` and the `/emby/` API prefix.

- `GET /System/Info`
- `GET /Sessions`
- `GET /System/ActivityLog/Entries`
- `GET /Users/Query`

The Activity Log and Users Query endpoints require administrator authentication, which is why EAVdrop uses the dedicated admin API key created for it.
