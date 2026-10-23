# DigitalCenter

A single-user Windows desktop application (WPF, .NET 10) for maintaining a
private movie/series catalog, importing metadata from TMDb with operator
approval, and copying selected media files to customers' USB drives.

DigitalCenter is **not** a streaming server. It is a local catalog and file
management tool for one operator on one computer. It works fully offline —
only metadata search and poster downloads touch the network, and only when
a TMDb token is configured.

## Feature overview (complete)

1. **Library** — **Home** shows both categories at once: the dashboard loads
   the newest **Movies** and the newest **TV Series** in two labelled sections
   (each with its own count and a *Show all movies* / *Show all series*
   shortcut), so nothing has to be clicked twice to reach the collection. The
   Movies / Series / Genres tabs then narrow the view to one category (the
   former "Collections" tab was a placeholder and is gone). Virtualized poster
   grid of the catalog,
   debounced free-text search (title and actor/director names via FTS5 with
   automatic LIKE fallback), type filter, sorting, multi-select (Ctrl/Shift)
   with **"Add N to USB queue"**, cached poster artwork, live file
   availability dots, details window with editable metadata and per-episode
   file overviews.
2. **Management** — the one place the single default Watch Folder is
   configured and where its activity is reviewed. New files are detected,
   matched against TMDb and then **filed automatically**: the app downloads the
   metadata, reads the title's original language and moves the file into
   `<Library>/Movies|Series/<Language>/`, creating that language folder when it
   does not exist yet. A file that cannot be matched with confidence stays
   exactly where it is and is listed for the operator instead.
   The workspace is deliberately small: one **Open Folder** action in the
   header, start/stop monitoring, **Rescan Now**, one **Import Summary** card
   (Total Detected, Imported, Awaiting Destination, No Match, Skipped, Failed)
   and the
   activity list with the file preview. Editing the Watch Folder itself is just
   the path field, **Browse…**, **Save Watch Folder** and the file-stability
   delay. Monitoring is polling + file-stability checks: the whole folder shares
   ONE stability window per pass (size and write time must stay identical and
   the file must be readable), so half-written downloads are never imported and
   a folder full of new files no longer takes one delay per file. Every
   detection lands in the review list with the parsed title/year/season/episode
   and the automatic TMDb match. **Every row has its own Browse / Move button**:
   it opens the standard folder picker for that one file and moves that file
   (metadata + artwork + catalog row included) — that is also how the planned
   language folder is overridden. Cancelling the picker changes nothing, and a
   row without a reliable match explains why it cannot be moved instead of
   guessing a destination. The preview pane **confirms** the planned move or
   **skips** the file (it stays in the Watch Folder). Files without a reliable
   match are marked "No Match" and left untouched. Every decision is persisted
   in the import history, and **interrupted imports are recovered automatically
   at the next startup** (checkpointed state machine). Automatic filing can be
   switched off in Settings, which returns to reviewing every match by hand.
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
5. **Current Activity & Status** — one consolidated runtime feed in the
   sidebar. Navigation (*Browsing Movies*, *Opened Movie “Title”*), cart work
   (*Movie added to cart successfully*, *removed from the cart*), background
   tasks (*Downloading metadata…*, *Fetching files from the Watch Folder…*),
   file operations (*Moving file to USB…*, *Moving file to Library…*) and
   system events (*Settings saved successfully*, scan/backup/import results)
   all land here instead of being scattered over the individual screens.
   Newest entry first, colour-coded by kind, capped at 200 entries, with a
   **Clear** action; publishing is marshalled onto the UI thread, so a
   background service can report without touching a control. Because this feed
   is the single running log, the screens no longer carry their own status
   line; a failure still opens a dialog where the operator has to act.
6. **Settings** — library roots (movies and series), metadata/artwork
   preferences with a TMDb status indicator and connection test,
   file-operation defaults (clean file names, hash verification, collision
   policy, **import matched files automatically**, **organize the library by
   language**), USB layout and naming templates, and the local data folder
   location. The Watch Folder is deliberately **not** here:
   it is owned by Management, so there is exactly one saved path and one
   monitoring state. The two automatic-filing switches live here rather than in
   the Watch Folder card, which keeps that screen to the actions that are used
   while watching a folder.

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
| Tests            | xUnit (203 test cases, all offline)          |

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
│   │   │                           WatchFolderService, LanguageFolder, ImportService,
│   │   │                           UsbCopyService, MaintenanceService, PosterCache,
│   │   │                           PathPlanner, DriveProbe, BackupService
│   │   └── Tmdb/                   ITmdbMetadataProvider + REST client (testable, mockable)
│   ├── DigitalCenter.ViewModels/   View models (CommunityToolkit.Mvvm) + UI service interfaces
│   └── DigitalCenter.App/          WPF application (views, DI composition root, dialogs)
├── tests/
│   └── DigitalCenter.Tests/        Unit + integration tests (203 cases, all offline)
└── tools/                          Offline checkers used while working on the repo
    ├── xaml_lint.py                XAML well-formedness, resource keys, glyph codepoints
    ├── csharp_literal_lint.py      unterminated string/char literals
    ├── member_map.py               lists a class member by member (round-trip checked)
    ├── split_partial.py            splits a class into partial files, content-verified
    └── dead_member_scan.py         reports members nothing in src/ or tests/ references
```

The three biggest types are split into partial files by responsibility instead
of one long file: `ImportService` (+ `.Matching`, `.Destination`, `.Confirm`,
`.Registration`), `UsbCopyService` (+ `.Transfer`), `DetailsViewModel`
(+ `.Editing`, `.Actions`) and `ManagementViewModel` (+ `.WatchFolder`,
`.FileActions`).

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
Settings screen owns the library roots, the application preferences and the two
automatic-filing switches (`AutoImportMatchedFiles` and
`OrganizeLibraryByLanguage`, both on by default); the Management workspace owns `WatchFolderPath`, `StabilityDelaySeconds` and the
monitoring flags (`WatchFolderEnabled`, `ScanOnStartup`,
`WatchFolderIncludeSubfolders`). The three monitoring switches and the
minimum-match-confidence slider are no longer surfaced in the UI — the stored
values keep working exactly as before and are written back unchanged, so an
existing `settings.json` stays compatible. The unused `AskForDestination` key
was dropped from `AppSettings`: a settings file that still carries it is read
normally (unknown keys are skipped) and rewritten without it. Both screens save through
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
Watch Folder poll (every 15 s, "Rescan Now", or application startup)
   └─ new video file + stable size + readable  →  MetadataImportLog (Pending)
      parsed title/year/season/episode (S01E01, S01.E01, 1x01, "Season 1
      Episode 1", folder season hints) decide series vs movie
Automatic pass (monitoring, "Rescan Now" and startup all run the same one)
   ├─ TMDb match: identifiers first, then title/year
   │    (parsed episodes search /search/tv only — never /search/movie)
   │    → Matched (including the title's original language)
   │      or No Match (the file stays in place and is reported)
   └─ automatic organization: every matched file is planned into
        <Library>/Movies|Series/<Language>/   (folder created when missing,
        library root when TMDb reports no language)
        │  Settings › "Import matched files automatically" = off → the row
        │  waits and the operator moves it with Browse / Move or Move to Library
        ▼
   metadata upsert (title/seasons/episodes/cast/genres/countries/category)
        ▼   checkpoint: Approved
   plan the target path inside the destination folder — name collisions get
   " (n)" suffixes, nothing is ever overwritten
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
├── Movies/<Language>/<files>       e.g. Movies/Korean/Parasite (2019).mkv
└── Series/<Language>/<files>       e.g. Series/Japanese/Breaking.Bad.S01E02.mkv
```

The language folder comes from the title's `original_language` in the TMDb
response (`Services/LanguageFolder.cs`): the ISO code is turned into its
English culture name (`ko` → `Korean`, `ur` → `Urdu`, `pt-BR` → `Portuguese`),
sanitized against the characters Windows rejects, and created under the movies
or series root when it is missing. A code the runtime does not know still
becomes a folder (`xx` → `Xx`), because grouping by language is the point of
the feature; when TMDb reports no language at all the file lands in the root
instead of an invented one. Turning off "Organize the library by language"
files everything directly into `Movies/` and `Series/`.

Per-title folders (`Movies/Hollywood/Inception (2010)/…`,
`Series/English/Breaking Bad (2008)/Season 01/…`) are the **USB copy** layout,
which mirrors the naming templates in Settings; library imports do not create
them. The scanner classifies by top-level folder (`Movies`/`Series`), reads the
second level as the category and parses file names for
title/year/season/episode, so a library organized either way is registered
correctly. Recognized series markers: `S01E02`, `1x02`,
`E02`/`Episode 02`/trailing number (with a `Season NN` folder hint; the show
title can also be taken from the enclosing folder). Release noise (quality
tags, codecs, release groups) is stripped; unparsable names are reported,
never guessed into the wrong place.

## Design system (Aurora Light 2026)

The application is a **light** theme: a violet-tinted white canvas
(`WindowBackground` over a faint gradient with two very soft glows), opaque
white cards and hairline borders. All shared geometry,
color and icon decisions live in `src/DigitalCenter.App/Themes/Styles.xaml`
(166 resources, 44 icon glyphs). Screens reference the tokens; they never
invent their own values, which is what keeps Home, details, USB Cart,
Management, Maintenance and Settings visually identical.

| Token group | Values | Used for |
|-------------|--------|----------|
| `PrimaryBrush` / `PrimaryDarkBrush` | `#6D28D9` / `#5B21B6` | filled buttons, active navigation, links, focus |
| `CyanBrush` | `#0E7490` | secondary accent, progress values, series badges |
| `OnAccentBrush` | `#FFFFFF` | the only white text in the app — on filled accent surfaces |
| `TextBrush` / `SubTextBrush` / `FaintTextBrush` | `#1B1726` / `#554F6B` / `#7A7490` | body (~16:1), secondary (~7.6:1), captions (~4.6:1) on white |
| `OkBrush` / `WarnBrush` / `DangerBrush` | `#15803D` / `#B45309` / `#B91C1C` | status dots, chips and destructive actions (`OkSoftBrush` / `DangerSoftBrush` are the chip backgrounds) |
| `WindowBackground` / `CardBrush` / `PanelBrush` / `ControlBrush` | `#F3F1F9` / `#FFFFFF` / `#FCFBFE` / `#F2EFF9` | canvas, cards, inset panels, inputs and secondary buttons |
| `DividerBrush` / `BorderLowBrush` / `BorderHighBrush` | `#1A1B1726` / `#241B1726` / `#3D1B1726` | hairlines that separate without darkening the layout |

Contrast was chosen for a bright room: body text sits at roughly 16:1, the
quietest caption still clears WCAG AA (4.6:1), and white is only ever placed on
a filled accent (`OnAccentBrush`) — never on a light surface. Shadows are
violet-tinted and low-opacity (0.10–0.18) so cards lift without graying the
page.

| Radius | Values | Used for |
|--------|--------|----------|
| `Radius.Xs` | 2 | hairlines, counters/badges, slider · scrollbar · progress thumbs, switch knob |
| `Radius.Sm` | 4 | tabs, segmented navigation, filter chips, episode rows, switch track |
| `Radius.Control` | 6 | buttons, text boxes, combo boxes, search field, nav rows |
| `Radius.Tile` / `Radius.Art` | 8 | icon tiles and chips, posters, backdrops, empty-state medallion |
| `Radius.Card` / `Radius.Popup` | 10 | cards, panels, popups |

There is **no pill radius any more** (`Radius.Pill` was removed together with
every use of it). The largest radius in the application is 10 px, well below
half of the smallest control height, so nothing can read as a capsule or an
oval whatever its label is — a 999 px radius on a long label ("Awaiting
destination") is exactly the distortion this scale exists to prevent. Only the
small 8 px status dots stay circular; they are indicators, not containers.

* **Controls vs. panels** — inputs and secondary buttons use `ControlBrush` /
  `ControlHoverBrush` / `ControlPressedBrush` over the white card, with a
  hairline border, so a control always reads as something pressable sitting
  *on* a surface rather than as part of it.
* **Control metrics** — `ControlPadding`, `ControlPaddingCompact` and
  `ControlMinHeight` (34 px) keep buttons and fields properly proportioned
  whatever the label length.
* **Icons** — one family (Segoe Fluent Icons) behind named `Icon.*` resources
  (no raw codepoints in screens, no emoji) and one size scale: `GlyphXs` 12,
  `GlyphSmall` 14, `GlyphIcon` 16, `GlyphMedium`/`NavGlyph` 20, `GlyphLarge`
  26, `GlyphHero` 34. Icon containers (`IconTile`, `IconChip`) are square with
  the tile radius.
* **One status surface** — the sidebar's *Current Activity & Status* feed is the
  only running log, so the screens do not repeat it: the page-level status
  lines, the details screen's "Library status" rail, the placeholder
  *Collections* tab, the sidebar version card and the preview pane's *Choose
  Destination…* button (the per-row *Browse / Move* already does that) were all
  removed. What is left on a screen is what that screen acts on.
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
- **Nothing moves without a decision** — a file leaves the Watch Folder either
  because TMDb matched it with confidence (the app then files it under its
  detected language automatically) or because the operator picked a destination
  by hand. Unmatched files are never guessed into place, "Import matched files
  automatically" can be switched off in Settings to review every match first,
  and an explicit folder choice always wins over the planned one.
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
- **Language folders are derived, never invented** — the sub-folder name comes
  from the runtime's culture table for TMDb's `original_language`, is sanitized
  against the Windows-invalid character set, and is created on demand; a title
  with no reported language is filed in the library root instead of a guessed
  folder. Existing folders are reused, and a title that already exists in the
  catalog is never duplicated because the same TMDb id arrived in another
  language.

## Testing

203 test cases cover the highest-risk behavior: filename parsing (movies with
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
change moves the single monitor without touching files or records), automatic
language organization (folder names for `en`, `ur`, `hi`, `ko`, `ja`, `fr`,
`ar`, `pt-BR`, `zh-Hant` and an unknown code, sanitization, movies and series
landing in `<root>/<Language>/`, a missing language landing in the root, the
organization switch off, the auto-move switch off — the folder is planned and
the file waits for a hand confirm — unmatched files never moving, the reported
activity messages, folder reuse and no duplicate catalog row when the same
TMDb id arrives in another language), crash
recovery (crash after copy / after move / source vanished), Watch Folder
detection (stability, growth in progress, dedupe by path), USB copying
(skip/overwrite/rename, insufficient space, missing source, deterministic
cancel mid-copy + resume, retry, queue persistence, space estimation
accounting for skips, throttled progress reporting, SHA-256 verification of a
multi-chunk copy, a job queued without a known size), the consolidated
activity reporting (an automatic match announces its batch and stays silent
when there is no work, and an automatic organization pass reports what it
moved), maintenance (missing files,
duplicates, index rebuild, CSV round-trip incl. quoting) and TMDb response
parsing from canned JSON (no network). Tests use temporary directories and
databases; the TMDb provider is always mocked in workflow tests.
