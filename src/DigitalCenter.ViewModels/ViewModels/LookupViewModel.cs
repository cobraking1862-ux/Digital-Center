using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Configuration;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

public sealed class LookupResultRowViewModel
{
    public LookupResultRowViewModel(ImdbLookupResult model)
    {
        Model = model;
        YearText = model.Year.HasValue ? model.Year.Value.ToString() : string.Empty;
        HeaderSubtitle = string.Join(" · ", new[] { YearText, model.MediaType, model.ImdbId }.Where(s => !string.IsNullOrWhiteSpace(s)));
        HasPoster = !string.IsNullOrWhiteSpace(model.PosterUrl);
        HasCast = !string.IsNullOrWhiteSpace(model.CastSummary);
    }

    public ImdbLookupResult Model { get; }
    public string ImdbId => Model.ImdbId;
    public string Title => Model.Title;
    public int? Year => Model.Year;
    public string YearText { get; }
    public string MediaType => Model.MediaType;
    public string CastSummary => Model.CastSummary ?? string.Empty;
    public string? PosterUrl => Model.PosterUrl;
    public bool HasPoster { get; }
    public bool HasCast { get; }
    public bool InLocalLibrary => Model.InLocalLibrary;
    public int? LocalTitleId => Model.LocalTitleId;
    public string HeaderSubtitle { get; }
}

/// <summary>Per-site background availability check and optional one-click download row shown in Lookup.</summary>
public sealed partial class LookupCheckRowViewModel : ObservableObject
{
    public LookupCheckRowViewModel(LookupSiteEntry site, string? providerId = null)
    {
        Site = site;
        ProviderId = string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim();
        Status = LookupCheckStatus.Pending;
        Detail = "Ready to check";
        CheckedUrl = string.Empty;
        DownloadStatusText = string.Empty;
    }

    public LookupSiteEntry Site { get; }
    public string SiteId => Site.Id;
    public string SiteName => Site.Name;
    public string? ProviderId { get; }
    public bool HasDownloadProvider => !string.IsNullOrWhiteSpace(ProviderId);

    [ObservableProperty] public partial LookupCheckStatus Status { get; set; }
    [ObservableProperty] public partial string Detail { get; set; }
    [ObservableProperty] public partial string CheckedUrl { get; set; }
    [ObservableProperty] public partial long ElapsedMs { get; set; }
    [ObservableProperty] public partial bool IsResolvingDownload { get; set; }
    [ObservableProperty] public partial bool NeedsBrowserAttention { get; set; }
    [ObservableProperty] public partial string DownloadStatusText { get; set; }

    public string StatusLabel => Status switch
    {
        LookupCheckStatus.Checking => "Checking…",
        LookupCheckStatus.Results => "Results",
        LookupCheckStatus.NoResults => "No Results",
        LookupCheckStatus.Blocked => "Blocked",
        LookupCheckStatus.Error => "Error",
        _ => "Ready"
    };

    public bool IsResults => Status == LookupCheckStatus.Results;
    public bool IsNoResults => Status == LookupCheckStatus.NoResults;
    public bool IsBlocked => Status == LookupCheckStatus.Blocked;
    public bool IsError => Status == LookupCheckStatus.Error;
    public bool IsChecking => Status == LookupCheckStatus.Checking;

    /// <summary>Show the Open button on every checked row so interactive or blocked sites can be opened in one click.</summary>
    public bool CanOpen => !string.IsNullOrWhiteSpace(CheckedUrl);

    /// <summary>Show the Download button only when this site has a registered workflow provider and a checked URL.</summary>
    public bool CanDownload => HasDownloadProvider && !string.IsNullOrWhiteSpace(CheckedUrl) && !IsResolvingDownload;

    public bool HasDownloadStatus => !string.IsNullOrWhiteSpace(DownloadStatusText);

    public string OpenButtonLabel => (Status == LookupCheckStatus.Blocked || NeedsBrowserAttention)
        ? "Open in Browser"
        : "Open";

    public string DownloadButtonLabel => IsResolvingDownload ? "Resolving…" : "Download";

    public string DetailWithTiming => ElapsedMs > 0 ? $"{Detail} · {ElapsedMs} ms" : Detail;

    partial void OnStatusChanged(LookupCheckStatus value)
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(IsResults));
        OnPropertyChanged(nameof(IsNoResults));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(OpenButtonLabel));
    }

    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(DetailWithTiming));
    partial void OnElapsedMsChanged(long value) => OnPropertyChanged(nameof(DetailWithTiming));

    partial void OnCheckedUrlChanged(string value)
    {
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(CanDownload));
    }

    partial void OnIsResolvingDownloadChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(DownloadButtonLabel));
    }

    partial void OnNeedsBrowserAttentionChanged(bool value) => OnPropertyChanged(nameof(OpenButtonLabel));
    partial void OnDownloadStatusTextChanged(string value) => OnPropertyChanged(nameof(HasDownloadStatus));
}

/// <summary>
/// Output-focused Lookup workspace: search IMDb to resolve a movie's IMDb ID (tt...),
/// select a match, run background availability checks across enabled sites,
/// launch one-click workflow downloads to IDM (using the default quality configured
/// in Maintenance → Lookup Sites → Download Manager), or file Tickets.
/// </summary>
public partial class LookupViewModel : ObservableObject
{
    private readonly ImdbLookupService _lookupService;
    private readonly TicketService _ticketService;
    private readonly SettingsService _settingsService;
    private readonly DownloadSettingsService _downloadSettings;
    private readonly DownloadProviderRegistry _providerRegistry;
    private readonly IdmLauncherService _idmLauncher;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IAppLogger _logger;
    private readonly ActivityFeedService _activityFeed;

    private CancellationTokenSource? _checkCts;
    private CancellationTokenSource? _downloadCts;
    private List<LookupSiteEntry> _enabledSites = [];

    public event Action<int>? OpenLocalTitleRequested;

    public LookupViewModel(
        ImdbLookupService lookupService,
        TicketService ticketService,
        SettingsService settingsService,
        AppPaths paths,
        IDialogService dialogs,
        IUiDispatcher uiDispatcher,
        IAppLogger logger,
        ActivityFeedService activityFeed)
    {
        _lookupService = lookupService;
        _ticketService = ticketService;
        _settingsService = settingsService;
        _downloadSettings = new DownloadSettingsService(paths);
        _providerRegistry = DownloadProviderRegistry.CreateDefault(logger: logger);
        _idmLauncher = new IdmLauncherService(logger);
        _dialogs = dialogs;
        _uiDispatcher = uiDispatcher;
        _logger = logger;
        _activityFeed = activityFeed;

        SearchQuery = string.Empty;
        StatusText = "Search any movie or series title (or paste an IMDb ID like tt1272878) to resolve its IMDb ID first.";
        CheckSummaryText = string.Empty;

        ReloadSites();
    }

    public ObservableCollection<LookupResultRowViewModel> Results { get; } = [];
    public ObservableCollection<LookupCheckRowViewModel> AvailabilityChecks { get; } = [];

    [ObservableProperty] public partial string SearchQuery { get; set; }
    [ObservableProperty] public partial LookupResultRowViewModel? SelectedResult { get; set; }
    [ObservableProperty] public partial bool IsSearching { get; set; }
    [ObservableProperty] public partial bool IsCheckingAvailability { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial string CheckSummaryText { get; set; }

    public bool HasSelectedResult => SelectedResult is not null;
    public bool HasNoResults => !IsSearching && Results.Count == 0;
    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);
    public bool HasCheckSummary => !string.IsNullOrWhiteSpace(CheckSummaryText);
    public bool HasNoEnabledSites => _enabledSites.Count == 0;

    public string SelectedSummaryText => SelectedResult is null
        ? "Select an IMDb match on the left, then click Check Availability."
        : $"{SelectedResult.Title}{(SelectedResult.Year.HasValue ? $" ({SelectedResult.Year})" : "")} — IMDb ID: {SelectedResult.ImdbId}";

    partial void OnSelectedResultChanged(LookupResultRowViewModel? value)
    {
        _checkCts?.Cancel();
        _downloadCts?.Cancel();
        OnPropertyChanged(nameof(HasSelectedResult));
        OnPropertyChanged(nameof(SelectedSummaryText));
        CheckAvailabilityCommand.NotifyCanExecuteChanged();
        FileTicketCommand.NotifyCanExecuteChanged();
        OpenInLocalLibraryCommand.NotifyCanExecuteChanged();
        ResetAvailabilityRows();
    }

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoResults));
        SearchImdbCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCheckingAvailabilityChanged(bool value)
    {
        CheckAvailabilityCommand.NotifyCanExecuteChanged();
        CancelCheckCommand.NotifyCanExecuteChanged();
    }

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatusText));
    partial void OnCheckSummaryTextChanged(string value) => OnPropertyChanged(nameof(HasCheckSummary));

    public void ReloadSites()
    {
        var settings = _settingsService.Load();
        var all = settings.LookupSites is { Count: > 0 }
            ? settings.LookupSites
            : ImdbLookupService.GetDefaultSites().ToList();

        _enabledSites = all.Where(s => s.IsEnabled).ToList();
        OnPropertyChanged(nameof(HasNoEnabledSites));
        CheckAvailabilityCommand.NotifyCanExecuteChanged();

        if (!IsCheckingAvailability)
        {
            ResetAvailabilityRows();
        }
    }

    private string? ResolveRegisteredProviderId(LookupSiteEntry site, DownloadSettings dlSettings)
    {
        if (dlSettings.SiteWorkflowBindings.TryGetValue(site.Id, out var pid) &&
            _providerRegistry.HasProvider(pid))
        {
            return pid;
        }

        return null;
    }

    private void ResetAvailabilityRows()
    {
        AvailabilityChecks.Clear();
        CheckSummaryText = string.Empty;
        var dlSettings = _downloadSettings.Load();
        foreach (var site in _enabledSites)
        {
            AvailabilityChecks.Add(new LookupCheckRowViewModel(site, ResolveRegisteredProviderId(site, dlSettings)));
        }
    }

    private bool CanSearch() => !IsSearching;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchImdbAsync()
    {
        var query = (SearchQuery ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            StatusText = "Enter a movie title or IMDb ID (e.g. tt1272878) to search.";
            return;
        }

        _checkCts?.Cancel();
        _downloadCts?.Cancel();
        IsSearching = true;
        StatusText = $"Searching IMDb for '{query}'…";
        try
        {
            var matches = await Task.Run(() => _lookupService.SearchImdbAsync(query));
            Results.Clear();
            foreach (var match in matches)
            {
                Results.Add(new LookupResultRowViewModel(match));
            }

            SelectedResult = Results.FirstOrDefault();
            OnPropertyChanged(nameof(HasNoResults));

            StatusText = Results.Count > 0
                ? $"Found {Results.Count} IMDb match(es). Select one and click Check Availability."
                : $"No IMDb titles found for '{query}'.";
        }
        catch (Exception ex)
        {
            _logger.Error("IMDb lookup search failed.", ex);
            StatusText = ex.Message;
            _dialogs.Error("Lookup", ex.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }

    private bool CanCheckAvailability() => !IsCheckingAvailability && SelectedResult is not null && _enabledSites.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCheckAvailability))]
    private async Task CheckAvailabilityAsync()
    {
        if (SelectedResult is null)
        {
            return;
        }

        ReloadSites();
        if (_enabledSites.Count == 0)
        {
            _dialogs.Info("Lookup", "No lookup sites are enabled. Go to Maintenance → Lookup Sites to add or enable sites.");
            return;
        }

        _checkCts?.Cancel();
        _checkCts?.Dispose();
        _checkCts = new CancellationTokenSource();
        var ct = _checkCts.Token;

        var target = SelectedResult;
        var dlSettings = _downloadSettings.Load();
        AvailabilityChecks.Clear();
        var rows = new List<LookupCheckRowViewModel>(_enabledSites.Count);
        foreach (var site in _enabledSites)
        {
            var row = new LookupCheckRowViewModel(site, ResolveRegisteredProviderId(site, dlSettings))
            {
                Status = LookupCheckStatus.Checking,
                Detail = "Checking in background…"
            };
            rows.Add(row);
            AvailabilityChecks.Add(row);
        }

        IsCheckingAvailability = true;
        CheckSummaryText = $"Checking {rows.Count} enabled site(s) for {target.ImdbId}…";

        try
        {
            using var gate = new SemaphoreSlim(4, 4);
            var tasks = rows.Select(async row =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var result = await _lookupService.CheckSiteAvailabilityAsync(
                        row.Site,
                        target.ImdbId,
                        target.Title,
                        target.Year,
                        ct);

                    _uiDispatcher.Post(() =>
                    {
                        row.Status = result.Status;
                        row.Detail = result.Detail;
                        row.CheckedUrl = result.CheckedUrl;
                        row.ElapsedMs = result.ElapsedMs;
                    });
                }
                catch (OperationCanceledException)
                {
                    _uiDispatcher.Post(() =>
                    {
                        row.Status = LookupCheckStatus.Pending;
                        row.Detail = "Cancelled";
                    });
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);

            var withResults = rows.Count(r => r.Status == LookupCheckStatus.Results);
            var noResults = rows.Count(r => r.Status == LookupCheckStatus.NoResults);
            var blocked = rows.Count(r => r.Status == LookupCheckStatus.Blocked);
            var errors = rows.Count(r => r.Status == LookupCheckStatus.Error);

            var parts = new List<string>
            {
                $"{withResults} with results",
                $"{noResults} no results"
            };
            if (blocked > 0) parts.Add($"{blocked} blocked");
            if (errors > 0) parts.Add($"{errors} error(s)");

            CheckSummaryText = ct.IsCancellationRequested
                ? "Availability check cancelled."
                : string.Join(" · ", parts);
        }
        catch (OperationCanceledException)
        {
            CheckSummaryText = "Availability check cancelled.";
        }
        finally
        {
            IsCheckingAvailability = false;
        }
    }

    private bool CanCancelCheck() => IsCheckingAvailability;

    [RelayCommand(CanExecute = nameof(CanCancelCheck))]
    private void CancelCheck()
    {
        _checkCts?.Cancel();
        _downloadCts?.Cancel();
    }

    [RelayCommand]
    private async Task DownloadFromSiteAsync(LookupCheckRowViewModel? row)
    {
        if (row is null || SelectedResult is null || !row.HasDownloadProvider || row.IsResolvingDownload)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.CheckedUrl))
        {
            row.DownloadStatusText = "Run Check Availability first.";
            return;
        }

        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        var dlSettings = _downloadSettings.Load();
        var appSettings = _settingsService.Load();
        var target = SelectedResult;

        var request = new DownloadRequest(
            SourceId: row.SiteId,
            SourceName: row.SiteName,
            SourcePageUrl: row.CheckedUrl,
            ImdbId: target.ImdbId,
            MovieTitle: target.Year.HasValue ? $"{target.Title} ({target.Year})" : target.Title,
            Year: target.Year,
            PreferredQuality: dlSettings.PreferredQuality);

        row.IsResolvingDownload = true;
        row.NeedsBrowserAttention = false;
        row.DownloadStatusText = $"Resolving {dlSettings.PreferredQuality} (auto-fallback enabled)…";

        var progress = new Progress<string>(msg => _uiDispatcher.Post(() => row.DownloadStatusText = msg));

        try
        {
            var outcome = await Task.Run(() => _providerRegistry.ResolveForSiteAsync(row.ProviderId, request, progress, ct), ct);

            if (outcome.IsSuccess && outcome.Download is not null)
            {
                var launch = _idmLauncher.LaunchDownload(outcome.Download, dlSettings, appSettings);
                row.DownloadStatusText = launch.Message;
                row.NeedsBrowserAttention = !launch.Launched;
                if (launch.Launched)
                {
                    _activityFeed.Publish($"Sent to IDM: {outcome.Download.MovieTitle} [{outcome.Download.Quality}]");
                }
            }
            else if (outcome.Status == DownloadResolveStatus.NeedsBrowser)
            {
                // Preserve row.CheckedUrl so clicking 'Open in Browser' always opens the original matched search page.
                row.NeedsBrowserAttention = true;
                row.DownloadStatusText = outcome.Message;
            }
            else
            {
                row.DownloadStatusText = outcome.Message;
            }
        }
        catch (OperationCanceledException)
        {
            row.DownloadStatusText = "Download resolution cancelled.";
        }
        catch (Exception ex)
        {
            _logger.Warn($"One-click download failed on '{row.SiteName}': {ex.Message}");
            row.DownloadStatusText = $"Download failed: {ex.Message}";
        }
        finally
        {
            row.IsResolvingDownload = false;
        }
    }

    private bool CanFileTicket() => SelectedResult is not null;

    [RelayCommand(CanExecute = nameof(CanFileTicket))]
    private async Task FileTicketAsync()
    {
        if (SelectedResult is null)
        {
            return;
        }

        var target = SelectedResult;
        var checkedRows = AvailabilityChecks
            .Where(r => r.Status is LookupCheckStatus.Results or LookupCheckStatus.NoResults or LookupCheckStatus.Blocked or LookupCheckStatus.Error)
            .ToList();

        var siteResults = checkedRows.Select(r => new TicketSiteResult
        {
            SiteName = r.SiteName,
            Status = r.StatusLabel,
            Url = r.CheckedUrl,
            Detail = r.Detail
        }).ToList();

        string summary;
        if (checkedRows.Count == 0)
        {
            summary = "Filed before availability check";
        }
        else
        {
            var foundNames = checkedRows.Where(r => r.Status == LookupCheckStatus.Results).Select(r => r.SiteName).ToList();
            var noResCount = checkedRows.Count(r => r.Status == LookupCheckStatus.NoResults);
            var blockedCount = checkedRows.Count(r => r.Status == LookupCheckStatus.Blocked);

            var parts = new List<string>();
            if (foundNames.Count > 0)
            {
                parts.Add($"Results on: {string.Join(", ", foundNames)}");
            }
            else
            {
                parts.Add("0 sites with results");
            }

            if (noResCount > 0) parts.Add($"{noResCount} no results");
            if (blockedCount > 0) parts.Add($"{blockedCount} blocked");
            summary = string.Join(" · ", parts);
        }

        try
        {
            var ticket = await _ticketService.CreateTicketAsync(
                title: target.Title,
                year: target.Year,
                mediaType: target.MediaType,
                imdbId: target.ImdbId,
                posterUrl: target.PosterUrl,
                availabilitySummary: summary,
                siteResults: siteResults);

            StatusText = $"Ticket #{ticket.Id} filed for '{target.Title}' — view it in the Tickets panel.";
            _activityFeed.Publish($"Ticket #{ticket.Id} filed: {target.Title}");
        }
        catch (Exception ex)
        {
            _logger.Error("Filing ticket failed.", ex);
            _dialogs.Error("File Ticket", $"Could not file ticket: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenCheckedSite(LookupCheckRowViewModel? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.CheckedUrl))
        {
            return;
        }

        _dialogs.OpenUrl(row.CheckedUrl);
    }

    private bool CanOpenInLocalLibrary() => SelectedResult?.InLocalLibrary == true && SelectedResult.LocalTitleId.HasValue;

    [RelayCommand(CanExecute = nameof(CanOpenInLocalLibrary))]
    private void OpenInLocalLibrary()
    {
        if (SelectedResult?.LocalTitleId is { } id)
        {
            OpenLocalTitleRequested?.Invoke(id);
        }
    }
}
