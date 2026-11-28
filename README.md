# CBA Movie Explorer (DigitalCenter)

A Windows desktop application built with **.NET 10** and **WPF** for managing a local movie and TV series library, automatically matching metadata from TMDb, organizing watch-folder imports by language and collection, and copying selected titles to USB drives.

## Key Features

- **Library Scanning & Catalog Management:** Scans configured movie, collection, and series root folders (`LibraryScanner`) and keeps the local catalog synchronized (`MaintenanceService`).
- **Filename Parsing:** Extracts titles, release years, season/episode numbers, and language/quality tags from media filenames (`FilenameParser`).
- **TMDb Metadata Matching & Artwork Caching:** Queries the TMDb API for movie/series details, cast, genres, trailers, posters, and backdrops (`TmdbMetadataProvider`, `MetadataMatcher`, `MetadataMerge`, `PosterCache`, `LanguageResolver`).
- **Fast Full-Text Search (SQLite + FTS5):** Uses a trigger-synchronized SQLite FTS5 virtual table (`MediaTitleFts`) over titles and people with automatic fallback to `LIKE` queries (`DatabaseInitializer`, `FtsSupport`, `LibraryQueryService`).
- **Watch Folders & Automated Imports:** Monitors a configured watch folder for newly stabilized video files, matches TMDb metadata, and organizes files into language or collection folders (`WatchFolderService`, `ImportService`, `PathPlanner`).
- **USB Copy Queue:** Queues titles from the library, detects removable/fixed drives (`DriveProbe`), estimates capacity, cleans filenames, handles conflicts (`Skip`, `Overwrite`, `Rename`), and optionally verifies SHA-256 hashes (`UsbCopyService`).
- **Backup & Restore:** Creates consistent database backups using the SQLite Online Backup API and validates backups before restoring with an automatic pre-restore safety copy (`BackupService`).

## Solution Architecture

The solution (`DigitalCenter.slnx`) is split into four projects (see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for full details):

| Project | Target Framework | Responsibility |
| --- | --- | --- |
| `src/DigitalCenter.Core` | `net10.0` | Platform-independent domain models, EF Core 10 + SQLite/FTS5 data layer, configuration (`AppPaths`, `AppSettings`, `TmdbOptions`), TMDb client, and core services. |
| `src/DigitalCenter.ViewModels` | `net10.0` | Platform-independent MVVM ViewModels (`CommunityToolkit.Mvvm` 8.4.2) and UI service abstractions (`IUiDispatcher`, `IDialogService`, `IAppControl`). |
| `src/DigitalCenter.App` | `net10.0-windows` | WPF desktop host, DI composition root (`App.xaml.cs`), views, custom `VirtualizingWrapPanel`, converters, and the "Daylight Slate" light theme. |
| `tests/DigitalCenter.Tests` | `net10.0` | xUnit 2.9.3 unit and integration test suite covering `DigitalCenter.Core` (database, FTS5 search, parser, scanner, importer, TMDb, USB copy, watch folders, backup/restore, and maintenance). |

## Prerequisites

- **Windows 11** (64-bit / x64 or ARM64)
- **.NET 10 SDK**
- **Visual Studio 2026** (optional, for IDE development and XAML designer)
- **Inno Setup** (optional, required only to build the Windows installer from `pakage.iss`)

## Build, Test, and Run (PowerShell)

```powershell
# Restore NuGet packages and local dotnet tools
dotnet restore DigitalCenter.slnx

# Build the entire solution
dotnet build DigitalCenter.slnx

# Run all xUnit tests
dotnet test DigitalCenter.slnx

# Launch the WPF desktop application
dotnet run --project src/DigitalCenter.App/DigitalCenter.App.csproj
```

Local runtime data (SQLite database, cached posters, logs, backups, and `settings.json`) is stored under `%APPDATA%\DigitalCenter` so the app never writes inside your media folders or next to the executable.

## BigOne Visible Browser Workflow

The BollyFlix BigOne provider opens a visible Microsoft Edge window (Playwright Chromium is the fallback), uses AngleSharp to parse the live DOM, and hands the final static Cloud Resume URL to IDM. The browser uses its own persistent profile and pauses for manual human verification when a site requires it.

Install the fallback browser once after building:

```powershell
.\scripts\Install-PlaywrightBrowsers.ps1
```

Assign **BigOne — Visible Browser (BollyFlix)** to the BollyFlix site in Maintenance → Lookup Sites. The browser stage trace is shown in Lookup and written to `%APPDATA%\DigitalCenter\Logs`.

See [BigOne Browser Automation](docs/BIGONE_BROWSER_AUTOMATION.md) for the route and diagnostics.

## Configuring the TMDb API Token Securely

`DigitalCenter.App` reads the TMDb v4 Read Access Token from configuration key `Tmdb:ReadAccessToken` in the following precedence order (later sources override earlier ones):

1. `appsettings.json` next to the executable
2. Environment variable `Tmdb__ReadAccessToken`
3. `.NET User Secrets` (recommended for local development)

**Never commit real TMDb API keys or tokens to source control.**

### Option 1: .NET User Secrets (Recommended for Development)

```powershell
dotnet user-secrets set "Tmdb:ReadAccessToken" "<YOUR_TMDB_READ_ACCESS_TOKEN>" --project src/DigitalCenter.App/DigitalCenter.App.csproj
```

### Option 2: Environment Variable (PowerShell)

```powershell
# Current PowerShell session
$env:Tmdb__ReadAccessToken = "<YOUR_TMDB_READ_ACCESS_TOKEN>"

# Or persist for the current Windows user
[Environment]::SetEnvironmentVariable("Tmdb__ReadAccessToken", "<YOUR_TMDB_READ_ACCESS_TOKEN>", "User")
```

## Building the Windows Installer (Inno Setup)

1. Publish a Release build of the WPF application for `win-x64`:
   ```powershell
   dotnet publish src/DigitalCenter.App/DigitalCenter.App.csproj -c Release -r win-x64
   ```
2. Open `pakage.iss` and ensure the `[Setup]` and `[Files]` paths (`LicenseFile`, `InfoBeforeFile`, `InfoAfterFile`, `SetupIconFile`, `OutputDir`, and `Source`) point to your local publish directory and desired output directory.
3. Compile the installer using the Inno Setup Compiler IDE or CLI:
   ```powershell
   iscc pakage.iss
   ```
   This produces `CBAExplorerSetup.exe` in the configured `OutputDir`.

## Documentation

- [Architecture Overview](docs/ARCHITECTURE.md)
- [BigOne Browser Automation](docs/BIGONE_BROWSER_AUTOMATION.md)
- [Changelog](CHANGELOG.md)
- [AI Agent Guidelines](GEMINI.md)
