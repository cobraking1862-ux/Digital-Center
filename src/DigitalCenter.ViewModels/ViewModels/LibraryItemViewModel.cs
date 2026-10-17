using CommunityToolkit.Mvvm.ComponentModel;
using DigitalCenter.Core.Models;
using DigitalCenter.Core.Services;
using DigitalCenter.ViewModels.Services;

namespace DigitalCenter.ViewModels;

/// <summary>
/// Display row for one catalog title on the Library screen. A row is observable
/// so the grid can show which card is selected: one click selects, two clicks
/// open the details screen.
/// </summary>
public sealed partial class LibraryItemViewModel : ObservableObject
{
    public LibraryItemViewModel(LibraryPageItem item)
    {
        Id = item.Id;
        Title = item.Title;
        Type = item.Type;
        YearDisplay = item.ReleaseYear?.ToString() ?? "—";
        TypeDisplay = item.Type.ToString();
        PosterPath = item.PosterPath;
        Initials = UiFormat.Initials(item.Title);
        Categories = item.Categories;
        Genres = item.Genres;
        FileSummary = item.FileCount == 1 ? "1 file" : $"{item.FileCount} files";
        SizeSummary = UiFormat.FormatBytes(item.TotalSize);
        FilesOnDisk = item.FilesOnDisk;
        FileCount = item.FileCount;

        if (item.FileCount == 0)
        {
            Availability = "No file";
            AvailabilityState = "Missing";
        }
        else if (item.FilesOnDisk == 0)
        {
            Availability = "Missing";
            AvailabilityState = "Missing";
        }
        else if (item.FilesOnDisk < item.FileCount)
        {
            Availability = "Partial";
            AvailabilityState = "Partial";
        }
        else
        {
            Availability = "Available";
            AvailabilityState = "Available";
        }
    }

    public int Id { get; }
    public string Title { get; }
    public MediaKind Type { get; }
    public string YearDisplay { get; }
    public string TypeDisplay { get; }
    public string? PosterPath { get; }
    public string Initials { get; }
    public IReadOnlyList<string> Categories { get; }
    public IReadOnlyList<string> Genres { get; }
    public string CategoriesText => Categories.Count == 0 ? "—" : string.Join(", ", Categories);
    public string GenresText => Genres.Count == 0 ? "—" : string.Join(", ", Genres);
    public string FileSummary { get; }
    public string SizeSummary { get; }
    public int FilesOnDisk { get; }
    public int FileCount { get; }
    public string Availability { get; }

    /// <summary>Available / Partial / Missing — used by UI triggers for the status dot color.</summary>
    public string AvailabilityState { get; }

    /// <summary>True while this card is the selected one in the grid.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
