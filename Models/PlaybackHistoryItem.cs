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
