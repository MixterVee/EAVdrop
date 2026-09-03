namespace EAVdrop.Models;

public sealed class SessionInfoDto
{
    public string? Id { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? Client { get; set; }
    public string? DeviceName { get; set; }
    public string? RemoteEndPoint { get; set; }
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
        ? $"{FormatTicks(PlayState?.PositionTicks)} / {FormatTicks(NowPlayingItem.RunTimeTicks)} • {Progress:P0}"
        : FormatTicks(PlayState?.PositionTicks);
    public string PlaybackMethod => PlayState?.IsPaused == true
        ? "Paused"
        : TranscodingInfo is not null
            ? "Transcoding"
            : PlayState?.PlayMethod ?? "Playing";

    public IReadOnlyList<string> QualityBadges
    {
        get
        {
            var badges = new List<string>();
            var video = NowPlayingItem?.MediaStreams?.FirstOrDefault(x => string.Equals(x.Type, "Video", StringComparison.OrdinalIgnoreCase));
            var audio = NowPlayingItem?.MediaStreams?.FirstOrDefault(x => string.Equals(x.Type, "Audio", StringComparison.OrdinalIgnoreCase));

            AddUnique(badges, GetResolutionBadge(TranscodingInfo?.Width ?? video?.Width, TranscodingInfo?.Height ?? video?.Height));
            AddUnique(badges, GetRangeBadge(video));
            AddUnique(badges, NormalizeVideoCodec(TranscodingInfo?.VideoCodec ?? video?.Codec));
            AddUnique(badges, GetAudioBadge(TranscodingInfo?.AudioCodec, audio));

            return badges;
        }
    }

    public string StreamDetails
    {
        get
        {
            if (TranscodingInfo is null)
                return PlayState?.PlayMethod is { Length: > 0 } method ? method : "Direct playback";

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(TranscodingInfo.Container)) parts.Add(TranscodingInfo.Container.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(TranscodingInfo.VideoCodec)) parts.Add(TranscodingInfo.VideoCodec.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(TranscodingInfo.AudioCodec)) parts.Add(TranscodingInfo.AudioCodec.ToUpperInvariant());
            if (TranscodingInfo.Bitrate is > 0) parts.Add(FormatBitrate(TranscodingInfo.Bitrate.Value));
            return parts.Count > 0 ? string.Join(" • ", parts) : "Transcoding";
        }
    }

    public string EndpointDisplay => string.IsNullOrWhiteSpace(RemoteEndPoint) ? "" : $"Connection • {RemoteEndPoint}";

    private static void AddUnique(List<string> badges, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !badges.Contains(value, StringComparer.OrdinalIgnoreCase))
            badges.Add(value);
    }

    private static string? GetResolutionBadge(int? width, int? height)
    {
        if (width is >= 3800 || height is >= 2100) return "4K";
        if (width is >= 2500 || height is >= 1400) return "1440p";
        if (width is >= 1900 || height is >= 1000) return "1080p";
        if (width is >= 1200 || height is >= 700) return "720p";
        return height is > 0 ? $"{height}p" : null;
    }

    private static string? GetRangeBadge(MediaStreamDto? video)
    {
        var range = string.Join(" ", new[] { video?.VideoRangeType, video?.VideoRange, video?.Profile, video?.DisplayTitle }
            .Where(x => !string.IsNullOrWhiteSpace(x))).ToUpperInvariant();

        if (range.Contains("DOLBY VISION") || range.Contains("DOVI")) return "DOLBY VISION";
        if (range.Contains("HDR10+") || range.Contains("HDR10PLUS")) return "HDR10+";
        if (range.Contains("HDR10")) return "HDR10";
        if (range.Contains("HLG")) return "HLG";
        if (range.Contains("HDR")) return "HDR";
        return null;
    }

    private static string? NormalizeVideoCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return null;
        return codec.Trim().ToLowerInvariant() switch
        {
            "hevc" or "h265" or "h.265" => "HEVC",
            "h264" or "h.264" or "avc" => "H.264",
            "av1" => "AV1",
            "vp9" => "VP9",
            "mpeg2video" or "mpeg2" => "MPEG-2",
            _ => codec.ToUpperInvariant()
        };
    }

    private static string? GetAudioBadge(string? transcodingCodec, MediaStreamDto? audio)
    {
        var description = string.Join(" ", new[]
        {
            transcodingCodec,
            audio?.Codec,
            audio?.Profile,
            audio?.DisplayTitle,
            audio?.Title,
            audio?.ChannelLayout
        }.Where(x => !string.IsNullOrWhiteSpace(x))).ToUpperInvariant();

        if (description.Contains("ATMOS")) return "ATMOS";
        if (description.Contains("DTS:X") || description.Contains("DTS-X")) return "DTS:X";
        if (description.Contains("TRUEHD")) return "TRUEHD";
        if (description.Contains("DTS-HD MA") || description.Contains("DTSHD_MA") || description.Contains("DTSHDMA")) return "DTS-HD MA";
        if (description.Contains("EAC3") || description.Contains("E-AC3")) return "E-AC3";
        if (description.Contains("AC3") || description.Contains("AC-3")) return "AC3";
        if (description.Contains("AAC")) return "AAC";
        if (description.Contains("FLAC")) return "FLAC";
        if (description.Contains("OPUS")) return "OPUS";
        if (description.Contains("DTS")) return "DTS";

        return !string.IsNullOrWhiteSpace(transcodingCodec)
            ? transcodingCodec.ToUpperInvariant()
            : !string.IsNullOrWhiteSpace(audio?.Codec)
                ? audio.Codec.ToUpperInvariant()
                : null;
    }

    private static string FormatBitrate(int bitrate) => bitrate >= 1_000_000
        ? $"{bitrate / 1_000_000d:0.#} Mbps"
        : $"{bitrate / 1000d:0} kbps";

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
    public List<MediaStreamDto> MediaStreams { get; set; } = [];

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

public sealed class MediaStreamDto
{
    public string? Type { get; set; }
    public string? Codec { get; set; }
    public string? Profile { get; set; }
    public string? DisplayTitle { get; set; }
    public string? Title { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? VideoRange { get; set; }
    public string? VideoRangeType { get; set; }
    public int? Channels { get; set; }
    public string? ChannelLayout { get; set; }
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
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? AudioChannels { get; set; }
}
