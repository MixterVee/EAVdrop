namespace EAVdrop.Models;

public sealed class PlaybackHistoryItem
{
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTimeOffset PlayedDate { get; set; }

    public string DateDisplay => PlayedDate.LocalDateTime.ToString("g");
    public string TypeDisplay => string.IsNullOrWhiteSpace(Type) ? "Media" : Type;
}

public sealed class UserPlaybackSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    public bool IsNowPlaying { get; set; }
    public string NowPlayingTitle { get; set; } = "";
    public string NowPlayingDetail { get; set; } = "";
    public double NowPlayingProgress { get; set; }

    public string ActivityCountText { get; set; } = "";
    public string RecentHeading { get; set; } = "Recently watched";
    public string RecentLine1 { get; set; } = "";
    public string RecentLine2 { get; set; } = "";
    public string RecentLine3 { get; set; } = "";

    public bool HasRecentLine1 => !string.IsNullOrWhiteSpace(RecentLine1);
    public bool HasRecentLine2 => !string.IsNullOrWhiteSpace(RecentLine2);
    public bool HasRecentLine3 => !string.IsNullOrWhiteSpace(RecentLine3);
}


public sealed class ActivityFeedItem
{
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTimeOffset SortDate { get; set; }

    public bool IsNowPlaying { get; set; }
    public string DeviceDisplay { get; set; } = "";
    public string PlaybackMethod { get; set; } = "";
    public string StreamDetails { get; set; } = "";
    public string QualityDisplay { get; set; } = "";
    public string ProgressText { get; set; } = "";
    public double Progress { get; set; }

    public string TypeDisplay => string.IsNullOrWhiteSpace(Type) ? "Media" : Type;
    public string Eyebrow => IsNowPlaying ? "NOW PLAYING" : TypeDisplay.ToUpperInvariant();
    public string DateDisplay => IsNowPlaying ? "Live" : SortDate.LocalDateTime.ToString("g");
    public bool ShowProgress => IsNowPlaying;

    public string DetailLine
    {
        get
        {
            if (!IsNowPlaying)
                return UserName;

            return string.Join(
                " • ",
                new[] { UserName, PlaybackMethod, DeviceDisplay }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
        }
    }

    public string SecondaryDetailLine =>
        string.Join(
            " • ",
            new[] { ProgressText, StreamDetails, QualityDisplay }
                .Where(x => !string.IsNullOrWhiteSpace(x)));

    public bool HasSecondaryDetail =>
        !string.IsNullOrWhiteSpace(SecondaryDetailLine);

    public bool MatchesSearch(string search) =>
        string.IsNullOrWhiteSpace(search) ||
        Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        UserName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        Type.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        DeviceDisplay.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        PlaybackMethod.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        StreamDetails.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        QualityDisplay.Contains(search, StringComparison.OrdinalIgnoreCase);
}
