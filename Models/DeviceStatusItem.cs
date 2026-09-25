using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EAVdrop.Models;

public sealed class DeviceStatusItem : INotifyPropertyChanged
{
    private string _progressText = "";
    private double _progress;
    private DateTimeOffset? _lastActivityDate;

    public string SessionId { get; set; } = "";
    public string UserName { get; set; } = "Unknown user";
    public string DeviceName { get; set; } = "Unknown device";
    public string Client { get; set; } = "";
    public string MediaTitle { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string PlaybackMethod { get; set; } = "";
    public string StreamDetails { get; set; } = "";
    public string QualityDisplay { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public bool IsPlaying { get; set; }
    public bool IsPaused { get; set; }
    public bool IsTranscoding { get; set; }
    public bool SupportsRemoteControl { get; set; }

    public string ProgressText
    {
        get => _progressText;
        set
        {
            if (string.Equals(_progressText, value, StringComparison.Ordinal))
                return;

            _progressText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SecondaryDetail));
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) < 0.0001)
                return;

            _progress = value;
            OnPropertyChanged();
        }
    }

    public DateTimeOffset? LastActivityDate
    {
        get => _lastActivityDate;
        set
        {
            if (_lastActivityDate == value)
                return;

            _lastActivityDate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastActivityDisplay));
            OnPropertyChanged(nameof(ConnectionDetail));
        }
    }

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

    public bool HasSameCardState(DeviceStatusItem other) =>
        string.Equals(SessionId, other.SessionId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(UserName, other.UserName, StringComparison.Ordinal) &&
        string.Equals(DeviceName, other.DeviceName, StringComparison.Ordinal) &&
        string.Equals(Client, other.Client, StringComparison.Ordinal) &&
        string.Equals(MediaTitle, other.MediaTitle, StringComparison.Ordinal) &&
        string.Equals(MediaType, other.MediaType, StringComparison.Ordinal) &&
        string.Equals(PlaybackMethod, other.PlaybackMethod, StringComparison.Ordinal) &&
        string.Equals(StreamDetails, other.StreamDetails, StringComparison.Ordinal) &&
        string.Equals(QualityDisplay, other.QualityDisplay, StringComparison.Ordinal) &&
        string.Equals(Endpoint, other.Endpoint, StringComparison.Ordinal) &&
        IsPlaying == other.IsPlaying &&
        IsPaused == other.IsPaused &&
        IsTranscoding == other.IsTranscoding &&
        SupportsRemoteControl == other.SupportsRemoteControl;

    public void UpdateLiveValues(DeviceStatusItem other)
    {
        ProgressText = other.ProgressText;
        Progress = other.Progress;
        LastActivityDate = other.LastActivityDate;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
