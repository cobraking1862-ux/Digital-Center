# DigitalCenter

A single-user Windows desktop application (WPF, .NET 10) for maintaining a
private movie/series catalog, importing metadata from TMDb with operator
approval, and copying selected media files to customers' USB drives.

DigitalCenter is **not** a streaming server. It is a local catalog and file
management tool for one operator on one computer. It works fully offline —
only metadata search and poster downloads touch the network, and only when
a TMDb token is configured.

## Feature overview (complete)

1. **Library** — virtualized poster grid + table view of the catalog,
   debounced free-text search (title and actor/director names via FTS5 with
   automatic LIKE fallback), type filter, sorting, multi-select (Ctrl/Shift)
   with **"Add N to USB queue"**, cached poster artwork, live file
   availability dots, details window with editable metadata and per-episode
   file overviews.
2. **Import Review** — Watch Folder monitoring (polling + file-stability
   checks: size must stay identical across checks and the file must be
   readable, so half-written downloads are never imported). Every detection
   lands in the review list with the parsed title/year/season/episode. The
   operator can correct every field, search TMDb, pick a candidate, and
   **approve** (full metadata + poster + move into the library) or
   **reject** (file stays in the Watch Folder). Offline "approve with file
   names only" keeps the workflow working without TMDb. Every decision is
   persisted in the import history, and **interrupted imports are recovered
   automatically at the next startup** (checkpointed state machine).
3. **USB Copy Queue** — persistent queue (the table doubles as the copy
   log). Sequential background copying with preflight checks (source
   present, drive ready, sufficient space), Skip/Overwrite/Rename conflict
   handling, temp-file-then-rename (a crash never leaves a half-written
   file under its final name), per-file exception isolation, size
   verification (optional SHA-256), cancellation (the current file is
   cleaned up and re-queued), retry of failed jobs, and a persistent log of
   every outcome. **Sources are never deleted.**
4. **Library Management** — library scan (additive registration), missing
   files report, duplicate candidates report (identical name+size in
   multiple locations, multiple versions per title/episode), FTS5 index
   rebuild, CSV export (RFC 4180, one row per file) and additive CSV import,
   database backup/restore with validation and safety copies.
5. **Settings** — library root, Watch Folder (validated to be outside the
   library), USB subfolder, default categories, hash-verification flag,
   TMDb status indicator with connection test, and the local data folder
   location.

## Technology stack

| Component        | Choice                                       |
|------------------|----------------------------------------------|
| Language         | C# (.NET 10 LTS)                             |
| UI               | WPF + XAML, MVVM                             |
| MVVM toolkit     | CommunityToolkit.Mvvm 8.4.2                  |
| Database         | SQLite via Microsoft.Data.Sqlite             |
| ORM              | Entity Framework Core 10 (SQLite provider)   |
| Full-text search | SQLite FTS5 (bundled SQLite has it built in) |
| TMDb             | Official REST API v3 via HttpClient (bearer token) |
| Tests            | xUnit (93 tests, all offline)                |

Note: CommunityToolkit.Mvvm is pinned to **8.4.2** — 8.4.0 source generators
do not emit members under the .NET 10 SDK's Roslyn 5.0 compiler.

## Solution layout

```
DigitalCenter.slnx
├── src/
│   ├── DigitalCenter.Core/         Platform-independent domain + data + services
│   │   ├── Configuration/          AppPaths, AppSettings, SettingsService, TmdbOptions
│   │   ├── Data/                   DbContext, migrations, FTS5 support, initializer
│   │   ├── Logging/                Simple daily file logger
│   │   ├── Models/                 Entities (MediaTitle, MediaFile, CopyJob, MetadataImportLog, ...)
│   │   ├── Services/               FilenameParser, LibraryScanner, LibraryQueryService,
│   │   │                           WatchFolderService, ImportService, UsbCopyService,
│   │   │                           MaintenanceService, PosterCache, PathPlanner, DriveProbe, BackupService
│   │   └── Tmdb/                   ITmdbMetadataProvider + REST client (testable, mockable)
│   ├── DigitalCenter.ViewModels/   View models (CommunityToolkit.Mvvm) + UI service interfaces
│   └── DigitalCenter.App/          WPF application (views, DI composition root, dialogs)
└── tests/
    └── DigitalCenter.Tests/        Unit + integration tests (93 tests, all offline)
```

Why three libraries: the MVVM source generator does not run inside the WPF
markup-compilation temp project, and testable logic must not depend on WPF.
Core and ViewModels are plain `net10.0` class libraries; only the App
project is `net10.0-windows`.

## Build and run

Prerequisite: .NET 10 SDK.

```bash
dotnet build DigitalCenter.slnx
dotnet test  tests/DigitalCenter.Tests/DigitalCenter.Tests.csproj
dotnet run   --project src/DigitalCenter.App
```

Release deployment (on any OS thanks to `<EnableWindowsTargeting>`; run on Windows):

```bash
dotnet publish src/DigitalCenter.App -c Release -r win-x64 --self-contained true
```

No separate .NET runtime installation is needed on the target machine.

## Configuration and local data

All application data lives under `%APPDATA%\DigitalCenter`:

| Item            | Location                        |
|-----------------|---------------------------------|
| Database        | `digitalcenter.db`              |
| Settings        | `settings.json`                 |
| Cached posters  | `Posters/` (local image files, referenced by relative path) |
| Logs            | `Logs/digitalcenter-YYYYMMDD.log` |
| Backups         | `Backups/`                      |

Settings (library root, Watch Folder, USB defaults, verification flags) are
edited in the Settings screen and stored as JSON with an atomic write.
Validation rules: both paths must be absolute and the Watch Folder must not
be inside the library root.

### TMDb credentials

The metadata read access token uses the configuration key
`Tmdb:ReadAccessToken` and is **never** stored in the catalog, in
`settings.json`, in the UI, or in logs. It is read from (in order):

1. `appsettings.json` next to the executable — for deployments:

   ```json
   { "Tmdb": { "ReadAccessToken": "<your-token>" } }
   ```

2. Environment variable `Tmdb__ReadAccessToken`.
3. .NET user secrets — for development:

   ```bash
   dotnet user-secrets set "Tmdb:ReadAccessToken" "<your-token>" --project src/DigitalCenter.App
   ```

Replace `<your-token>` with the real "Read Access Token" from the TMDb API
settings page. The application remains fully usable without any token:
scanning, browsing, editing, Watch Folder imports (file names only) and USB
copying all work offline.

## Database schema (summary)

- `MediaTitles` — movies and series (`Type` CHECK constrained to
  'Movie'/'Series'), title/year/synopsis/poster/TMDb id (unique), timestamps.
- `SeriesDetails` → `Seasons` → `Episodes` — series structure, all cascading.
- `MediaFiles` — exactly one owner: a movie title **or** an episode
  (`CK_MediaFiles_Association`); unique case-insensitive `FilePath`
  (COLLATE NOCASE) prevents duplicate registration on Windows.
- `People`/`TitlePeople`, `Genres`/`TitleGenres`, `Countries`/`TitleCountries`,
  `Categories`/`TitleCategories` — many-to-many descriptive data.
- `MetadataImportLogs` — per-file import history including parsed values,
  operator category choice, import stage (checkpoint state machine) and
  error details. Unique per source path; history survives title deletion.
- `CopyJobs` — the persistent USB copy queue and log: source, library-
  relative target, sizes, status (Queued/Copying/Completed/Failed/
  Cancelled/Skipped), error/conflict notes, verification flag, timestamps.
- `MediaTitleFts` — FTS5 index over titles + people, synchronized by SQL
  triggers on every relevant change (plus a manual rebuild command).

Default categories (Hollywood, Bollywood, Chinese, Madrasi / English,
Indian, Pakistani) are seeded on first start.

## The import workflow in detail

```
Watch Folder poll (every 15 s, or "Check now")
   └─ new video file + stable size + readable  →  MetadataImportLog (Pending)
Import Review screen
   ├─ operator corrects parsed fields (title/year/season/episode/type/category)
   ├─ "Search TMDb" → candidate list → operator selects one
   └─ APPROVE                                   REJECT
        │                                          └─ file stays in Watch Folder
        ▼
   metadata upsert (title/seasons/episodes/cast/genres/countries/category)
        ▼   checkpoint: Approved
   plan destination: <Library>/Movies|Series/<Category>/<Title (Year)>[/Season NN]/
        file-name collisions get " (n)" suffixes — nothing is ever overwritten
        ▼   checkpoint: MoveStarted
   copy → size verify → rename into place → delete source
        ▼   checkpoint: MovedToLibrary
   register MediaFile (movie → title, episode → episode row)
        ▼   checkpoint: Imported
   poster download + cache (best effort)
```

A crash between two checkpoints is healed at the next startup: completed
copies are registered, missing sources fail the log with a clear message,
and nothing is ever silently dropped.

## Library folder conventions

```
<LibraryRoot>/
├── Movies/<Category>/<Title (Year)>/<files>      e.g. Movies/Hollywood/Inception (2010)/Inception (2010).mkv
└── Series/<Category>/<Series (Year)>/Season NN/<files>
                                                   e.g. Series/English/Breaking Bad (2008)/Season 01/Breaking.Bad.S01E02.mkv
```

The scanner classifies by top-level folder (`Movies`/`Series`), derives the
category from the second-level folder, and parses file names for
title/year/season/episode. Recognized series markers: `S01E02`, `1x02`,
`E02`/`Episode 02`/trailing number (with a `Season NN` folder hint; the show
title can also be taken from the enclosing folder). Release noise (quality
tags, codecs, release groups) is stripped; unparsable names are reported,
never guessed into the wrong place. The USB destination mirrors this layout
under the chosen drive (e.g. `E:\DigitalCenter\Movies\...`).

## Engineering decisions worth knowing

- **Scans are additive and safe** — the scanner only reads the filesystem;
  registration failures are logged per file and reported, never silent.
- **The catalog always stores absolute, normalized file paths** so links
  survive restarts; availability is checked live on disk.
- **Approval is mandatory** — nothing enters the catalog from the Watch
  Folder without an explicit operator decision, even on perfect matches.
- **Copies are transactional where possible** — temp file, size (and
  optional hash) verification, then rename; failures clean up their partial
  files; the queue/log survives restarts.
- **Backups are validated** (`MediaTitles` + `__EFMigrationsHistory` must
  exist) before a restore; the current database is always copied aside first
  (`*.pre-restore-*`) and connection pools are cleared so the file replace
  is clean. Restart after restore.
- **Schema management** uses EF Core migrations (`dotnet ef` is configured
  as a local dotnet tool via the manifest). FTS5 objects are installed
  idempotently at startup, separate from migrations.
- **Reports never delete** — missing files and duplicate candidates are
  listed for the operator to decide.

## Testing

93 tests cover the highest-risk behavior: filename parsing (movies with
years, series markers, ambiguous/malformed names, folder-derived show
titles), database rules (type CHECK, file XOR association, case-insensitive
unique paths, cascades, FTS trigger sync), scanner end-to-end, the full
import workflow with a scripted fake TMDb provider (approve movie/series,
filename-only offline approve, reject, rename-on-collision, duplicate
rejection), crash recovery (crash after copy / after move / source
vanished), Watch Folder detection (stability, growth in progress, dedupe by
path), USB copying (skip/overwrite/rename, insufficient space, missing
source, deterministic cancel mid-copy + resume, retry, queue persistence,
space estimation accounting for skips), maintenance (missing files,
duplicates, index rebuild, CSV round-trip incl. quoting) and TMDb response
parsing from canned JSON (no network). Tests use temporary directories and
databases; the TMDb provider is always mocked in workflow tests.
