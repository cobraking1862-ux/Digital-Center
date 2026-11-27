# GEMINI.md — AI Agent Instructions

## Stack & Solution
- **Solution:** `DigitalCenter.slnx` (.NET 10, C# latest, nullable + implicit usings enabled via `Directory.Build.props`).
- **`src/DigitalCenter.Core` (`net10.0`):** EF Core 10 + SQLite (WAL mode, FTS5), TMDb client, configuration, domain models, and services.
- **`src/DigitalCenter.ViewModels` (`net10.0`):** MVVM ViewModels using `CommunityToolkit.Mvvm` 8.4.2.
- **`src/DigitalCenter.App` (`net10.0-windows`):** WPF desktop shell, DI root (`App.xaml.cs`), views, controls, converters, and styles.
- **`tests/DigitalCenter.Tests` (`net10.0`):** xUnit 2.9.3 test suite targeting `DigitalCenter.Core`.
- **Installer:** Inno Setup script at `pakage.iss`.

## Strict Layer Boundaries
1. **Keep `Core` and `ViewModels` platform-independent:** Never add WPF (`System.Windows.*`) or `net10.0-windows` references to `DigitalCenter.Core` or `DigitalCenter.ViewModels`.
2. **UI abstractions:** Route UI thread marshaling, dialogs, and app control from ViewModels through `IUiDispatcher`, `IDialogService`, and `IAppControl`.
3. **Startup order:** `DatabaseInitializer.InitializeAsync()` must run before resolving `MainViewModel` or `LibraryQueryService` so migrations, WAL mode, and FTS5 detection complete first.

## Build, Test, and Migration Commands
```powershell
dotnet restore DigitalCenter.slnx
dotnet build DigitalCenter.slnx
dotnet test DigitalCenter.slnx
dotnet run --project src/DigitalCenter.App/DigitalCenter.App.csproj
```
EF Core migrations (uses local tool in `dotnet-tools.json`):
```powershell
dotnet tool restore
dotnet ef migrations add <MigrationName> --project src/DigitalCenter.Core --startup-project src/DigitalCenter.App
```

## Guardrails
- **Branch isolation:** Always work on a dedicated branch; never commit directly to `main`.
- **Protected files:** Never delete `Directory.Build.props`, `dotnet-tools.json`, or any file in `src/DigitalCenter.Core/Data/Migrations/`.
- **No secrets or personal paths:** Never commit real TMDb tokens (`Tmdb:ReadAccessToken`) or local paths with personal usernames. Use `dotnet user-secrets` or `Tmdb__ReadAccessToken` env vars.
- **Data isolation:** Runtime data belongs under `%APPDATA%\DigitalCenter` (`AppPaths`), never inside the media library or repo tree.
- **Verify changes:** Run `dotnet build DigitalCenter.slnx` and `dotnet test DigitalCenter.slnx` before finishing code changes.
