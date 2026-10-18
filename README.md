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
2. **Management** — the one place the single default Watch Folder is
   configured and where its activity is reviewed. The workspace is deliberately
   small: one **Open Folder** action in the header, start/stop monitoring,
   **Rescan Now**, one **Import Summary** card (Total Detected, Imported,
   Awaiting Destination, No Match, Skipped, Failed) and the activity list with
   the file preview. Editing the Watch Folder itself is just the path field,
   **Browse…**, **Save Watch Folder** and the file-stability delay. Monitoring
   is polling + file-stability checks: the whole folder shares ONE stability
   window per pass (size and write time must stay identical and the file must
   be readable), so half-written downloads are never imported and a folder full
   of new files no longer takes one delay per file. Every detection
   lands in the review list with the parsed title/year/season/episode. The
   operator sees the detected file with its parsed fields and the automatic
   TMDb match, chooses a **destination folder**, and **confirms** the import
   (full metadata + poster + move into the library) or **skips** it (the file
   stays in the Watch Folder). Files without a reliable match are marked
   "No Match" and left untouched. Every decision is
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
5. **Settings** — library roots (movies and series), metadata/artwork
   preferences with a TMDb status indicator and connection test,
   file-operation defaults (clean file names, hash
   verification, collision policy), USB layout and naming templates, and the
   local data folder location. The Watch Folder is deliberately **not** here:
   it is owned by Management, so there is exactly one saved path and one
   monitoring state.

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
| Tests            | xUnit (177 test cases, all offline)          |

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
    └── DigitalCenter.Tests/        Unit + integration tests (177 cases, all offline)
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

There is exactly **one** `settings.json` and exactly one Watch Folder. The
Settings screen owns the library roots and the application preferences; the
Management workspace owns `WatchFolderPath`, `StabilityDelaySeconds` and the
monitoring flags (`WatchFolderEnabled`, `ScanOnStartup`,
`WatchFolderIncludeSubfolders`). The three monitoring switches and the
minimum-match-confidence slider are no longer surfaced in the UI — the stored
values keep working exactly as before and are written back unchanged, so an
existing `settings.json` stays compatible. Both screens save through
`SettingsService.Update(load → apply → save)`, so neither can clobber the
other's values. Validation rules: every configured path must be absolute,
library roots must exist, and the Watch Folder must exist and must not be
inside either library root. Changing the Watch Folder path never moves media
and never erases catalog or import records.

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
Watch Folder poll (every 15 s, or "Rescan Now" in Management)
   └─ new video file + stable size + readable  →  MetadataImportLog (Pending)
      parsed title/year/season/episode (S01E01, S01.E01, 1x01, "Season 1
      Episode 1", folder season hints) decide series vs movie
Management workspace
   ├─ automatic TMDb match: identifiers first, then title/year
   │    (parsed episodes search /search/tv only — never /search/movie)
   │    → Matched (awaiting destination) or No Match (file stays in place)
   └─ CONFIRM (destination folder chosen)        SKIP
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

## Design system (Obsidian Aurora 2026)

All shared geometry, color and icon decisions live in
`src/DigitalCenter.App/Themes/Styles.xaml`. Screens reference the tokens;
they never invent their own values, which is what keeps Home, details, USB
Cart, Management and Settings visually identical.

| Token group | Values | Used for |
|-------------|--------|----------|
| `Radius.Xs` | 2 | hairlines, counters/badges, slider · scrollbar · progress thumbs, switch knob |
| `Radius.Sm` | 4 | tabs, segmented navigation, filter chips, episode rows, switch track |
| `Radius.Control` | 6 | buttons, text boxes, combo boxes, search field, nav rows |
| `Radius.Tile` / `Radius.Art` | 8 | icon tiles and chips, posters, backdrops, empty-state medallion |
| `Radius.Card` / `Radius.Popup` | 10 | cards, panels, floating panels, popups |

There is **no pill radius any more** (`Radius.Pill` was removed together with
every use of it). The largest radius in the application is 10 px, well below
half of the smallest control height, so nothing can read as a capsule or an
oval whatever its label is — a 999 px radius on a long label ("Awaiting
destination") is exactly the distortion this scale exists to prevent. Only the
small 8 px status dots stay circular; they are indicators, not containers.

* **Controls vs. panels** — inputs and secondary buttons use `ControlBrush` /
  `ControlHoverBrush` / `ControlPressedBrush` and a darker, more opaque
  `InputBackground`, so a control always reads as something pressable sitting
  *on* a glass panel rather than as part of it.
* **Control metrics** — `ControlPadding`, `ControlPaddingCompact` and
  `ControlMinHeight` (34 px) keep buttons and fields properly proportioned
  whatever the label length.
* **Icons** — one family (Segoe Fluent Icons) behind named `Icon.*` resources
  (no raw codepoints in screens, no emoji) and one size scale: `GlyphXs` 12,
  `GlyphSmall` 14, `GlyphIcon` 16, `GlyphMedium`/`NavGlyph` 20, `GlyphLarge`
  26, `GlyphHero` 34. Icon containers (`IconTile`, `IconChip`) are square with
  the tile radius.
* **Details screen layout** — three stacked regions with no nested scrolling:
  a fixed action bar, a fixed hero (backdrop, poster, title, overview, hero
  actions) and one `ScrollViewer` below them holding the editor or the
  cast / seasons / episodes / files sections. The hero never scrolls away and
  the two regions never fight over the mouse wheel. Episodes that have local
  files are tinted and marked; episodes that are metadata-only stay neutral.
* **Shell navigation** — clicking a sidebar row always navigates, including the
  row that is already selected (while the details screen is open the selection
  does not change, so a plain `SelectionChanged` never fires). Returning from
  the details screen reloads the destination, and library reloads are
  coalesced: overlapping refreshes used to clear and refill the same
  `ObservableCollection` twice, which doubled filter options and lost the
  selection.
* **Card activation** — `Controls/CardActivation` is the one attached
  behaviour that makes a poster card selectable: one click selects, a second
  click (or Enter/Space on the focused card) opens the details screen, and a
  click that starts on an in-card control ("Add to Cart") never navigates.

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

177 test cases cover the highest-risk behavior: filename parsing (movies with
years; every supported series marker format — `S01E01`, `S01.E01`, `S01 E01`,
`S01_E01`, `1x01`, `Season 1 Episode 1`, `E01`, folder season hints — with
quality tags, release years and extra punctuation; ambiguous/malformed names;
folder-derived show titles), database rules (type CHECK, file XOR association, case-insensitive
unique paths, cascades, FTS trigger sync), scanner end-to-end, the full
import workflow with a scripted fake TMDb provider (confirm movie/series,
skip, rename-on-collision, duplicate rejection), the series import path end
to end (`Reacher.S01.E01.mkv` and the other marker formats are matched as TV
episodes, never searched as movies, linked to the right Series/Season/Episode
rows, and repeated scans never duplicate them; an unavailable season, no
match and being offline all leave the library intact), Watch Folder
ownership (a Settings save keeps what Management owns and vice versa, a path
change moves the single monitor without touching files or records), crash
recovery (crash after copy / after move / source vanished), Watch Folder
detection (stability, growth in progress, dedupe by path), USB copying
(skip/overwrite/rename, insufficient space, missing source, deterministic
cancel mid-copy + resume, retry, queue persistence, space estimation
accounting for skips, throttled progress reporting, SHA-256 verification of a
multi-chunk copy, a job queued without a known size), maintenance (missing files,
duplicates, index rebuild, CSV round-trip incl. quoting) and TMDb response
parsing from canned JSON (no network). Tests use temporary directories and
databases; the TMDb provider is always mocked in workflow tests.
