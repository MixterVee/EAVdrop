namespace EAVdrop.Models;

public sealed class DeviceStatusItem
{
    public string SessionId { get; set; } = "";
    public string UserName { get; set; } = "Unknown user";
    public string DeviceName { get; set; } = "Unknown device";
    public string Client { get; set; } = "";
    public string MediaTitle { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string PlaybackMethod { get; set; } = "";
    public string StreamDetails { get; set; } = "";
    public string QualityDisplay { get; set; } = "";
    public string ProgressText { get; set; } = "";
    public double Progress { get; set; }
    public string Endpoint { get; set; } = "";
    public DateTimeOffset? LastActivityDate { get; set; }
    public bool IsPlaying { get; set; }
    public bool IsPaused { get; set; }
    public bool IsTranscoding { get; set; }
    public bool SupportsRemoteControl { get; set; }

    public string DeviceDisplay =>
        string.Join(
            " • ",
            new[] { DeviceName, Client }
                .Where(x => !string.IsNullOrWhiteSpace(x)));

    public string StateLabel =>
        IsPlaying
            ? IsPaused
                ? "PAUSED"
                : IsTranscoding
                    ? "PLAYING • TRANSCODING"
                    : "PLAYING"
            : "IDLE";

    public string MainTitle =>
        IsPlaying && !string.IsNullOrWhiteSpace(MediaTitle)
            ? MediaTitle
            : DeviceDisplay;

    public string PrimaryDetail =>
        IsPlaying
            ? string.Join(
                " • ",
                new[] { UserName, DeviceDisplay }
                    .Where(x => !string.IsNullOrWhiteSpace(x)))
            : string.Join(
                " • ",
                new[] { UserName, "No media playing" }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));

    public string SecondaryDetail =>
        IsPlaying
            ? string.Join(
                " • ",
                new[] { PlaybackMethod, ProgressText, StreamDetails, QualityDisplay }
                    .Where(x => !string.IsNullOrWhiteSpace(x)))
            : RemoteControlText;

    public string ConnectionDetail =>
        string.Join(
            " • ",
            new[]
            {
                string.IsNullOrWhiteSpace(Endpoint) ? null : $"Endpoint {Endpoint}",
                LastActivityDisplay
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

    public string RemoteControlText =>
        SupportsRemoteControl
            ? "Remote control supported"
            : "Remote control not reported";

    public string LastActivityDisplay =>
        LastActivityDate.HasValue
            ? $"Last active {LastActivityDate.Value.LocalDateTime:g}"
            : "";

    public bool ShowProgress => IsPlaying;

    public bool Matches(string search) =>
        string.IsNullOrWhiteSpace(search) ||
        UserName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        DeviceName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        Client.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        MediaTitle.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        MediaType.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        PlaybackMethod.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        StreamDetails.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        QualityDisplay.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        Endpoint.Contains(search, StringComparison.OrdinalIgnoreCase);
}
