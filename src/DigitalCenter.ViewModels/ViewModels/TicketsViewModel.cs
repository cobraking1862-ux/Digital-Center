using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalCenter.Core.Logging;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

public sealed class TicketSiteResultRow
{
    public TicketSiteResultRow(TicketSiteResult model)
    {
        Model = model;
    }

    public TicketSiteResult Model { get; }
    public string SiteName => Model.SiteName;
    public string Status => Model.Status;
    public string Url => Model.Url;
    public string Detail => Model.Detail;
    public bool IsResults => string.Equals(Model.Status, "Results", StringComparison.OrdinalIgnoreCase);
    public bool IsBlocked => string.Equals(Model.Status, "Blocked", StringComparison.OrdinalIgnoreCase);
    public bool CanOpen => !string.IsNullOrWhiteSpace(Model.Url) && (IsResults || IsBlocked);
}

public sealed class TicketTileViewModel
{
    public TicketTileViewModel(LookupTicket model)
    {
        Model = model;
        TicketNumber = $"#{model.Id}";
        TitleWithYear = model.Year.HasValue ? $"{model.Title} ({model.Year})" : model.Title;
        CreatedAtText = $"Filed: {model.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        CompletedAtText = model.CompletedAt.HasValue
            ? $"Completed: {model.CompletedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
            : string.Empty;
        HasCompletedAt = model.CompletedAt.HasValue;
        HasPoster = !string.IsNullOrWhiteSpace(model.PosterUrl);
        HasNotes = !string.IsNullOrWhiteSpace(model.Notes);
        SiteResults = model.SiteResults.Select(r => new TicketSiteResultRow(r)).ToList();
        HasSiteResults = SiteResults.Count > 0;
    }

    public LookupTicket Model { get; }
    public int Id => Model.Id;
    public string TicketNumber { get; }
    public string Title => Model.Title;
    public string TitleWithYear { get; }
    public string ImdbId => Model.ImdbId;
    public string MediaType => Model.MediaType;
    public string? PosterUrl => Model.PosterUrl;
    public bool HasPoster { get; }
    public string Status => Model.Status;
    public bool IsOpen => string.Equals(Model.Status, TicketStatuses.Open, StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => string.Equals(Model.Status, TicketStatuses.Completed, StringComparison.OrdinalIgnoreCase);
    public string AvailabilitySummary => Model.AvailabilitySummary;
    public string CreatedAtText { get; }
    public string CompletedAtText { get; }
    public bool HasCompletedAt { get; }
    public string Notes => Model.Notes ?? string.Empty;
    public bool HasNotes { get; }
    public IReadOnlyList<TicketSiteResultRow> SiteResults { get; }
    public bool HasSiteResults { get; }
}

/// <summary>
/// Tickets sidebar panel: displays movie/series tickets filed from Lookup with
/// title, created date, and saved per-site availability results, with Movies-style
/// search and an inline status pill row (Open, Completed, All).
/// </summary>
public partial class TicketsViewModel : ObservableObject
{
    private readonly TicketService _tickets;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IAppLogger _logger;
    private bool _suppressReload;

    public TicketsViewModel(
        TicketService tickets,
        IDialogService dialogs,
        IUiDispatcher uiDispatcher,
        IAppLogger logger)
    {
        _tickets = tickets;
        _dialogs = dialogs;
        _uiDispatcher = uiDispatcher;
        _logger = logger;

        StatusFilters = [TicketStatuses.Open, TicketStatuses.Completed, "All"];

        _suppressReload = true;
        try
        {
            SelectedStatusFilter = TicketStatuses.Open;
            SearchText = string.Empty;
            StatusText = string.Empty;
        }
        finally
        {
            _suppressReload = false;
        }

        _tickets.TicketsChanged += () => _uiDispatcher.Post(() => _ = RefreshAsync());
    }

    public IReadOnlyList<string> StatusFilters { get; }
    public ObservableCollection<TicketTileViewModel> Tickets { get; } = [];

    [ObservableProperty] public partial string SelectedStatusFilter { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; }
    [ObservableProperty] public partial int OpenTicketCount { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }

    public bool HasNoTickets => Tickets.Count == 0;
    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public bool IsOpenFilter
    {
        get => string.Equals(SelectedStatusFilter, TicketStatuses.Open, StringComparison.OrdinalIgnoreCase);
        set { if (value) SelectedStatusFilter = TicketStatuses.Open; }
    }

    public bool IsCompletedFilter
    {
        get => string.Equals(SelectedStatusFilter, TicketStatuses.Completed, StringComparison.OrdinalIgnoreCase);
        set { if (value) SelectedStatusFilter = TicketStatuses.Completed; }
    }

    public bool IsAllFilter
    {
        get => string.Equals(SelectedStatusFilter, "All", StringComparison.OrdinalIgnoreCase);
        set { if (value) SelectedStatusFilter = "All"; }
    }

    partial void OnSelectedStatusFilterChanged(string value)
    {
        OnPropertyChanged(nameof(IsOpenFilter));
        OnPropertyChanged(nameof(IsCompletedFilter));
        OnPropertyChanged(nameof(IsAllFilter));
        if (!_suppressReload) _ = RefreshAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (!_suppressReload) _ = RefreshAsync();
    }

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatusText));

    public Task OnNavigatedToAsync() => RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var filter = SelectedStatusFilter;
            var query = SearchText;
            var items = await Task.Run(() => _tickets.GetTicketsAsync(filter, query));
            var openCount = await Task.Run(() => _tickets.GetOpenCountAsync());

            Tickets.Clear();
            foreach (var item in items)
            {
                Tickets.Add(new TicketTileViewModel(item));
            }

            OpenTicketCount = openCount;
            OnPropertyChanged(nameof(HasNoTickets));
        }
        catch (Exception ex)
        {
            _logger.Error("Loading tickets failed.", ex);
            StatusText = $"Could not load tickets: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task MarkCompletedAsync(TicketTileViewModel? tile)
    {
        if (tile is null) return;
        await _tickets.SetStatusAsync(tile.Id, TicketStatuses.Completed);
        StatusText = $"Ticket {tile.TicketNumber} ('{tile.Title}') marked as Completed.";
    }

    [RelayCommand]
    private async Task ReopenTicketAsync(TicketTileViewModel? tile)
    {
        if (tile is null) return;
        await _tickets.SetStatusAsync(tile.Id, TicketStatuses.Open);
        StatusText = $"Ticket {tile.TicketNumber} ('{tile.Title}') reopened.";
    }

    [RelayCommand]
    private async Task DeleteTicketAsync(TicketTileViewModel? tile)
    {
        if (tile is null) return;
        if (!_dialogs.Confirm("Delete Ticket", $"Delete ticket {tile.TicketNumber} ('{tile.Title}')?"))
        {
            return;
        }

        await _tickets.DeleteTicketAsync(tile.Id);
        StatusText = $"Deleted ticket {tile.TicketNumber}.";
    }

    [RelayCommand]
    private async Task ClearCompletedAsync()
    {
        if (!_dialogs.Confirm("Clear Completed Tickets", "Remove all completed tickets from the list?"))
        {
            return;
        }

        var removed = await _tickets.ClearCompletedAsync();
        StatusText = removed > 0
            ? $"Cleared {removed} completed ticket(s)."
            : "No completed tickets to clear.";
    }

    [RelayCommand]
    private void OpenSiteResult(TicketSiteResultRow? site)
    {
        if (site is null || string.IsNullOrWhiteSpace(site.Url)) return;
        _dialogs.OpenUrl(site.Url);
    }
}
