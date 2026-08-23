namespace EAVdrop.Models;

public sealed class SessionInfoDto
{
    public string? Id { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? Client { get; set; }
    public string? DeviceName { get; set; }
    public DateTimeOffset? LastActivityDate { get; set; }
    public BaseItemDto? NowPlayingItem { get; set; }
    public PlayerStateInfoDto? PlayState { get; set; }
    public TranscodingInfoDto? TranscodingInfo { get; set; }

    public string UserDisplay => string.IsNullOrWhiteSpace(UserName) ? "Unknown user" : UserName;
    public string DeviceDisplay => string.Join(" • ", new[] { DeviceName, Client }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string MediaDisplay => NowPlayingItem?.DisplayName ?? "Idle";
    public bool IsPlaying => NowPlayingItem is not null;
    public double Progress => NowPlayingItem?.RunTimeTicks is > 0
        ? Math.Clamp((double)(PlayState?.PositionTicks ?? 0) / NowPlayingItem.RunTimeTicks.Value, 0, 1)
        : 0;
    public string ProgressText => NowPlayingItem?.RunTimeTicks > 0
        ? $"{FormatTicks(PlayState?.PositionTicks)} / {FormatTicks(NowPlayingItem.RunTimeTicks)}"
        : FormatTicks(PlayState?.PositionTicks);
    public string PlaybackMethod => PlayState?.IsPaused == true
        ? "Paused"
        : TranscodingInfo is not null
            ? "Transcoding"
            : PlayState?.PlayMethod ?? "Playing";

    private static string FormatTicks(long? ticks)
    {
        if (ticks is null || ticks < 0) return "";
        var ts = TimeSpan.FromTicks(ticks.Value);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }
}

public sealed class UserItemQueryResultDto
{
    public List<BaseItemDto> Items { get; set; } = [];
    public int TotalRecordCount { get; set; }
}

public sealed class BaseItemDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? SeriesName { get; set; }
    public int? ParentIndexNumber { get; set; }
    public int? IndexNumber { get; set; }
    public long? RunTimeTicks { get; set; }
    public string? Type { get; set; }
    public UserItemDataDto? UserData { get; set; }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SeriesName))
            {
                var episode = ParentIndexNumber.HasValue && IndexNumber.HasValue
                    ? $"S{ParentIndexNumber:00}E{IndexNumber:00}"
                    : "Episode";
                return $"{SeriesName} — {episode} — {Name}";
            }

            return Name ?? "Unknown media";
        }
    }
}

public sealed class UserItemDataDto
{
    public double? PlayedPercentage { get; set; }
    public long PlaybackPositionTicks { get; set; }
    public int PlayCount { get; set; }
    public DateTimeOffset? LastPlayedDate { get; set; }
    public bool Played { get; set; }
}

public sealed class PlayerStateInfoDto
{
    public long? PositionTicks { get; set; }
    public bool? IsPaused { get; set; }
    public bool? IsMuted { get; set; }
    public string? PlayMethod { get; set; }
}

public sealed class TranscodingInfoDto
{
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? Container { get; set; }
    public int? Bitrate { get; set; }
}
