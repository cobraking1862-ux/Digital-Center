# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- BigOne visible browser workflow (`big-one-browser`) for BollyFlix: a real, visible Microsoft Edge window (Playwright Chromium fallback) automates the BollyFlix → FastDLServer → GDFlix route, auto-clicks FAST CLOUD / ZIPDISK and Cloud Resume Download, captures the actual browser download event across GDFlix's rotated `cloud-dl.<id>.workers.dev` final hosts, and hands the captured URL and suggested filename to IDM. See `docs/BIGONE_BROWSER_AUTOMATION.md`.
- Built-in BollyFlix Lookup site plus automatic settings migration (v11) that adds it and moves legacy BollyFlix workflow bindings to the visible browser workflow.
- `.github/workflows/dotnet-ci.yml` CI pipeline (restore, build, and test on Windows).
- `scripts/Install-PlaywrightBrowsers.ps1` for the one-time Playwright Chromium fallback browser install.
- Recreated the `tests/DigitalCenter.Tests` project with xUnit coverage of the BigOne browser workflow (route parsing, final-host policy, challenge detection), quality preference matching, and IDM argument building.
- `docs/ARCHITECTURE.md` documenting the 3-layer separation, SQLite + FTS5 data flow, and core services/ViewModels.
- `CHANGELOG.md` initialized in Keep a Changelog format.
- `GEMINI.md` with concise AI agent instructions, layer boundaries, commands, guardrails.
- `.geminiignore` to exclude build artifacts, binaries, and local database/cache files from AI agent context.

### Removed
- Removed the `direct-link` (Direct Link Workflow) and `multi-step-link` (Multi-Step Host Workflow) HTTP-crawler providers, their shared `WorkflowHttpHelpers` plumbing, and their mock-HTTP tests; the BigOne visible browser workflow is now the only registered download workflow. BollyFlix sites still bound to a legacy workflow (`direct-link`, `multi-step-link`, or the old `big-one` HTTP workflow) migrate automatically to `big-one-browser`; stale legacy bindings on other sites are ignored gracefully (the site stays Lookup-only) until reassigned in Maintenance → Lookup Sites.

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
