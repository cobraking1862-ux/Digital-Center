# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- Multi-Step Host Workflow now follows the real download chain end-to-end: search page → movie page → `dl.fastdlserver.site` (preferred over `linksmod.top` "Download Links" mirrors) → gdflix `/file/` page → `/cloud/` page (rebuilt on `new10.gdflix.io`) → direct file handed to IDM. Previously it died after 3 hops and followed the wrong (linksmod) host.
- `dl.fastdlserver.site/?id=...&type=file` anchors are no longer misclassified as navigation taxonomy (root path with a query is a parameterized download endpoint), which previously made them invisible as candidates.
- Relative gdflix `/cloud/<id>/<token>` links are pinned to `new10.gdflix.io` instead of whichever mirror served the intermediate page.
- `WorkflowHttpHelpers.FetchAsync` follows 3xx redirects itself (capped at 5), so the workflow works with `HttpClient` handlers that disable auto-redirect.
- Download anchors without a quality label (e.g. "🚀 Google Drive") now inherit the quality tier of the `<h5>` heading above them, so the 480p/720p/1080p/2160p sections are ranked correctly.
- `/login`, `/signin`, `/register`, `/signup` paths are ignored during hop selection so the workflow never follows a gdflix "Login To DL" button.
- Raised `MaxHops` from 3 to 5 (the real chain needs 4) and added login/auth paths to the ignored navigation list.
- Recreated the `tests/DigitalCenter.Tests` project with 8 xUnit tests, including a mock-HTTP end-to-end replay of the captured fastdlserver → gdflix cloud chain.

### Added
- `docs/ARCHITECTURE.md` documenting the 3-layer separation, SQLite + FTS5 data flow, and core services/ViewModels.
- `CHANGELOG.md` initialized in Keep a Changelog format.
- `GEMINI.md` with concise AI agent instructions, layer boundaries, commands, guardrails.
- `.geminiignore` to exclude build artifacts, binaries, and local database/cache files from AI agent context.

### Changed
- Reorganized and hardened `.gitignore` into `.NET / Visual Studio`, `Inno Setup / Packaging`, `Local Databases & Runtime Caches`, and `OS / Editor Files` sections, removing duplicate test-result rules and ignoring `Output/`, `*.sqlite*`, `*.db*`, and `appsettings.*.json` overrides.
- Overhauled `README.md` to reflect the current `.NET 10` codebase, 4-project solution structure, PowerShell build/test/run commands, secure TMDb configuration (`dotnet user-secrets` / `Tmdb__ReadAccessToken`), and Inno Setup packaging workflow.

## [1.0.0] - 2026-11-21

### Added
- Initial `.NET 10` WPF desktop application (`DigitalCenter.App`) with "Daylight Slate" light theme and virtualized poster grid (`VirtualizingWrapPanel`).
- Platform-independent domain and data layer (`DigitalCenter.Core`) using EF Core 10, SQLite WAL mode, trigger-synced FTS5 search (`MediaTitleFts`), and migrations (`InitialCreate`, `ImportWorkflowAndCopyQueue`, `V5WatchFolderAndDetails`).
- Platform-independent MVVM layer (`DigitalCenter.ViewModels`) powered by `CommunityToolkit.Mvvm`.
- Library scanning, filename parsing, TMDb metadata matching and artwork caching, language-based Watch Folder auto-organization, USB copy queue with optional SHA-256 verification, and online SQLite backup/restore.
- xUnit test suite (`tests/DigitalCenter.Tests`) covering core services and database behavior.
- Inno Setup installer script (`pakage.iss`).
