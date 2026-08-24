namespace EAVdrop.Models;

public sealed class PlaybackHistoryItem
{
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTimeOffset PlayedDate { get; set; }

    public string DateDisplay => PlayedDate.LocalDateTime.ToString("g");
}

public sealed class UserPlaybackSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string PlaybackSummary { get; set; } = "";
}
