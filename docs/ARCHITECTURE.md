# Architecture Overview

CBA Movie Explorer (`DigitalCenter.slnx`) follows a strict **3-layer architecture** that keeps business logic, data access, and presentation state completely decoupled from WPF and Windows-specific APIs.

## 1. Layer Separation

```text
+-------------------------------------------------------------------+
|  src/DigitalCenter.App  (net10.0-windows, WPF)                    |
|  - Composition Root (App.xaml.cs DI container)                    |
|  - Views, Controls (VirtualizingWrapPanel), Converters, Themes    |
|  - Platform Implementations (WpfUiDispatcher, DialogService)      |
+-------------------------------+-----------------------------------+
                                | depends on
                                v
+-------------------------------------------------------------------+
|  src/DigitalCenter.ViewModels  (net10.0, platform-independent)    |
|  - CommunityToolkit.Mvvm ViewModels (Main, Library, Details,      |
|    UsbCart, Management, Settings, Maintenance)                    |
|  - UI Abstractions (IUiDispatcher, IDialogService, IAppControl)   |
+-------------------------------+-----------------------------------+
                                | depends on
                                v
+-------------------------------------------------------------------+
|  src/DigitalCenter.Core  (net10.0, platform-independent)          |
|  - EF Core 10 + SQLite/FTS5 Data Layer & Migrations               |
|  - Domain Models, Configuration, Logging, TMDb Provider, Services |
+-------------------------------------------------------------------+
                                ^
                                | tested by
+-------------------------------------------------------------------+
|  tests/DigitalCenter.Tests  (net10.0, xUnit 2.9.3)                |
+-------------------------------------------------------------------+
```

### Dependency Rules
- **`DigitalCenter.Core`** has zero UI or Windows desktop dependencies. It targets `net10.0` and uses EF Core 10 (`Microsoft.EntityFrameworkCore.Sqlite`).
- **`DigitalCenter.ViewModels`** references only `DigitalCenter.Core` and `CommunityToolkit.Mvvm`. UI thread marshaling and dialogs are injected through `IUiDispatcher`, `IDialogService`, and `IAppControl`.
- **`DigitalCenter.App`** wires all singletons and factories in `App.xaml.cs` via `Microsoft.Extensions.DependencyInjection` and binds `MainWindow` to `MainViewModel`.

## 2. SQLite + FTS5 Data Flow

All runtime data is isolated under `%APPDATA%\DigitalCenter` (defined by `AppPaths`):
- `digitalcenter.db` — Primary SQLite database (with `-wal` / `-shm` sidecars in WAL mode)
- `settings.json` — Persisted operator settings (`AppSettings` / `SettingsService`)
- `Posters/` — Cached TMDb artwork (`PosterCache`)
- `Logs/` — Application log files (`FileLogger`)
- `Backups/` — Timestamped SQLite database backups (`BackupService`)

### Startup & Search Pipeline
1. **Migrations & WAL Mode:** Before `MainViewModel` or `LibraryQueryService` is resolved, `App.xaml.cs` runs `DatabaseInitializer.InitializeAsync()`. This applies pending EF Core migrations from `src/DigitalCenter.Core/Data/Migrations/` and enables WAL mode (`PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=15000;`) so background watch-folder and poster writes do not block UI reads.
2. **FTS5 Index & Triggers:** `FtsSupport.EnsureInstalledAsync()` checks `pragma_compile_options` for `ENABLE_FTS5`. When available, it idempotently creates the `MediaTitleFts` virtual table (`Title`, `People`, `MediaTitleId UNINDEXED`, tokenizer `unicode61 remove_diacritics 2`) along with `AFTER INSERT/UPDATE/DELETE` triggers on `MediaTitles` and `TitlePeople` to keep the index synchronized automatically.
3. **Default Seeding:** `DatabaseInitializer` idempotently seeds default movie and series categories (`Hollywood`, `Bollywood`, `Chinese`, `Madrasi`, `English`, `Indian`, `Pakistani`).
4. **Query Execution:** `LibraryQueryService` is lazily constructed with `DatabaseInitializer.FullTextSearchEnabled`. User search terms are converted into safe quoted prefix queries by `FtsSupport.BuildMatchExpression()`, falling back cleanly to `LIKE` filtering if FTS5 is unavailable.

## 3. Major Services & ViewModels

### Core Services (`src/DigitalCenter.Core/Services` & `Tmdb`)
| Service | Responsibility |
| --- | --- |
| `LibraryScanner` | Scans configured library roots (`AppSettings.GetConfiguredLibraryRoots()`) and reconciles files on disk with the catalog. |
| `FilenameParser` | Parses release filenames into structured title, year, season/episode, and language metadata. |
| `TmdbMetadataProvider` | HTTP client for TMDb v3 API metadata and artwork downloads, configured via `TmdbOptions`. |
| `MetadataMatcher` / `TitleSimilarity` / `MetadataMerge` | Scores TMDb candidates against parsed filenames and merges fetched metadata into `MediaTitle` entities. |
| `LanguageResolver` / `PathPlanner` | Maps ISO-639-1 codes, filename tags, and production countries into normalized language/collection destination paths. |
| `WatchFolderService` | Polls the configured watch folder, waits for file size stability (`StabilityDelaySeconds`), and triggers imports. |
| `ImportService` | Orchestrates end-to-end watch-folder ingestion, recovery of interrupted imports, TMDb matching, and file moves. |
| `LibraryQueryService` | Executes filtered, sorted, and FTS5/LIKE-searched catalog queries for the library grid and detail views. |
| `UsbCopyService` / `DriveProbe` | Enumerates drives, calculates required space, copies queued files into the target USB subfolder, handles conflicts, and optionally verifies SHA-256 hashes. |
| `BackupService` | Performs online SQLite backups (`SqliteConnection.BackupDatabase`) and schema-validated restores with automatic `.pre-restore-*` safety copies. |
| `MaintenanceService` | Runs catalog health checks, rescans, and cleanup routines. |
| `PosterCache` | Downloads and caches poster, backdrop, and cast profile artwork under `AppPaths.PostersDirectory`. |

### ViewModels (`src/DigitalCenter.ViewModels/ViewModels`)
- `MainViewModel`: Shell navigation (`Library`, `USB Cart`, `Watch Folder`, `Settings`), cart badge updates, activity feed, and details-view routing.
- `LibraryViewModel` & `LibraryItemViewModel`: Virtualized poster grid, search, filtering, and cart actions.
- `DetailsViewModel`: Full title metadata view, editing, and poster/cast presentation.
- `ManagementViewModel`: Watch-folder monitoring status, pending imports, and manual import actions.
- `UsbCartViewModel`: USB queue management, drive selection, copy options, and live transfer progress.
- `SettingsViewModel` & `MaintenanceViewModel`: Operator configuration, library root validation, backups, and maintenance triggers.
