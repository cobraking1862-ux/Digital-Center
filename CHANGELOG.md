# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [14.0.0] - 2026-11-27

### Added
- Added the built-in BollyFlix Lookup Site with stable ID `builtin-bollyflix`; BigOne is available only when explicitly assigned to this site.
- Added the dedicated BigOne workflow for the captured BollyFlix listing, title-page quality sections, FastDLServer file-host page, and GDFlix cloud landing page.
- Added bounded, stage-aware redirect validation, approved-host checks, cloud-origin validation, final download-response verification, and browser hand-off outcomes.
- Added sanitized BigOne HTML fixtures derived from the four supplied page captures and focused xUnit coverage for parsing, routing, redirect safety, browser fallback, site isolation, and settings compatibility.

### Changed
- Replaced the generic download-provider registry and crawler with an explicit site/workflow dispatcher.
- Kept quality selection and downward fallback in `QualityPreferenceMatcher`; only FastDLServer Google Drive routes associated with their own quality section are candidates.
- Registered BigOne with an HTTP client that disables automatic redirects so every redirect target is validated before it is requested.
- Updated the Maintenance selector: ordinary sites are Lookup-only; the designated BollyFlix site offers Lookup-only or BigOne.
- Preserved IDM executable configuration, confirmation-dialog hand-off, preferred quality, Watch Folder routing, ticket auto-completion, Lookup availability checks, and media-library functionality.

### Removed
- Removed `DirectLinkDownloadWorkflow`, `MultiStepLinkDownloadWorkflow`, `WorkflowHttpHelpers`, `DownloadProviderRegistry`, and the unused provider interface/plugin architecture.
- Removed the obsolete Direct Link and Multi-Step workflow IDs/options. Existing assignments using those IDs are removed without migrating them to BigOne.

### Compatibility and safety
- Preserved `%APPDATA%\\DigitalCenter\\download-settings.json` and unrelated site assignments, including unimplemented/future workflow values. Existing IDM path, preferred quality, Watch Folder routing, and ticket auto-completion settings remain intact.
- Malformed settings files are not overwritten during compatibility loading.
- BigOne does not fetch Linksmod, follow Login/Instant/Telegram/GoFile alternatives, automate credentials or challenges, or treat a cloud landing page/filename as proof of a completed download.
- BigOne returns a browser hand-off to the original BollyFlix title page whenever a stage or final destination cannot be verified.
- No independent BigO implementation was present in the inspected repository. BigO is therefore not offered or executed; any unrelated stored assignment remains dormant.

## [1.0.0] - 2026-11-21

### Added
- Initial `.NET 10` WPF desktop application (`DigitalCenter.App`) with "Daylight Slate" light theme and virtualized poster grid (`VirtualizingWrapPanel`).
- Platform-independent domain and data layer (`DigitalCenter.Core`) using EF Core 10, SQLite WAL mode, trigger-synced FTS5 search (`MediaTitleFts`), and migrations (`InitialCreate`, `ImportWorkflowAndCopyQueue`, `V5WatchFolderAndDetails`).
- Platform-independent MVVM layer (`DigitalCenter.ViewModels`) powered by `CommunityToolkit.Mvvm`.
- Library scanning, filename parsing, TMDb metadata matching and artwork caching, language-based Watch Folder auto-organization, USB copy queue with optional SHA-256 verification, and online SQLite backup/restore.
- xUnit test suite (`tests/DigitalCenter.Tests`) covering core services and database behavior.
- Inno Setup installer script (`pakage.iss`).
