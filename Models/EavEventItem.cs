namespace EAVdrop.Models;

public enum EavEventKind
{
    Playback,
    Transcoding,
    User,
    Sync
}

public sealed class EavEventItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public EavEventKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";

    public string KindDisplay => Kind switch
    {
        EavEventKind.Playback => "PLAYBACK",
        EavEventKind.Transcoding => "TRANSCODING",
        EavEventKind.User => "USER",
        EavEventKind.Sync => "SYNC'EM",
        _ => "EVENT"
    };

    public string TimeDisplay => Timestamp.LocalDateTime.ToString("g");

    public string DetailLine =>
        string.IsNullOrWhiteSpace(Detail)
            ? KindDisplay
            : Detail;
}
