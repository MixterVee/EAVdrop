namespace EAVdrop.Services;

public enum ConnectionMode
{
    Auto,
    Local,
    Remote
}

public enum PlaybackHistoryRange
{
    OneMonth,
    TwoMonths,
    ThreeMonths,
    Unlimited
}

public sealed class SettingsService
{
    private const string LocalUrlKey = "local_url";
    private const string RemoteUrlKey = "remote_url";
    private const string ConnectionModeKey = "connection_mode";
    private const string PlaybackHistoryRangeKey = "playback_history_range";
    private const string DeviceIdKey = "device_id";
    private const string ApiKeyKey = "emby_api_key";

    public string LocalUrl
    {
        get => Preferences.Default.Get(LocalUrlKey, "http://192.168.1.188:19096");
        set => Preferences.Default.Set(LocalUrlKey, value.Trim());
    }

    public string RemoteUrl
    {
        get => Preferences.Default.Get(RemoteUrlKey, "");
        set => Preferences.Default.Set(RemoteUrlKey, value.Trim());
    }

    public ConnectionMode Mode
    {
        get
        {
            var raw = Preferences.Default.Get(ConnectionModeKey, ConnectionMode.Auto.ToString());
            return Enum.TryParse<ConnectionMode>(raw, out var mode) ? mode : ConnectionMode.Auto;
        }
        set => Preferences.Default.Set(ConnectionModeKey, value.ToString());
    }

    public PlaybackHistoryRange HistoryRange
    {
        get
        {
            var raw = Preferences.Default.Get(PlaybackHistoryRangeKey, PlaybackHistoryRange.OneMonth.ToString());
            return Enum.TryParse<PlaybackHistoryRange>(raw, out var range) ? range : PlaybackHistoryRange.OneMonth;
        }
        set => Preferences.Default.Set(PlaybackHistoryRangeKey, value.ToString());
    }

    public string HistoryRangeCaption => HistoryRange switch
    {
        PlaybackHistoryRange.OneMonth => "last month",
        PlaybackHistoryRange.TwoMonths => "last 2 months",
        PlaybackHistoryRange.ThreeMonths => "last 3 months",
        _ => "all available history"
    };

    public string NoPlaybackText => HistoryRange == PlaybackHistoryRange.Unlimited
        ? "No playback history found"
        : $"Nothing played in the {HistoryRangeCaption}";

    public DateTimeOffset? GetPlaybackHistoryCutoff(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        return HistoryRange switch
        {
            PlaybackHistoryRange.OneMonth => current.AddMonths(-1),
            PlaybackHistoryRange.TwoMonths => current.AddMonths(-2),
            PlaybackHistoryRange.ThreeMonths => current.AddMonths(-3),
            _ => null
        };
    }

    public static string GetHistoryRangeSettingLabel(PlaybackHistoryRange range) => range switch
    {
        PlaybackHistoryRange.OneMonth => "1 month",
        PlaybackHistoryRange.TwoMonths => "2 months",
        PlaybackHistoryRange.ThreeMonths => "3 months",
        _ => "Unlimited"
    };

    public string DeviceId
    {
        get
        {
            var id = Preferences.Default.Get(DeviceIdKey, "");
            if (!string.IsNullOrWhiteSpace(id)) return id;
            id = Guid.NewGuid().ToString("N");
            Preferences.Default.Set(DeviceIdKey, id);
            return id;
        }
    }

    public async Task<string> GetApiKeyAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(ApiKeyKey) ?? "";
        }
        catch
        {
            return "";
        }
    }

    public async Task SaveApiKeyAsync(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SecureStorage.Default.Remove(ApiKeyKey);
            return;
        }

        await SecureStorage.Default.SetAsync(ApiKeyKey, value.Trim());
    }

    public async Task<bool> HasMinimumConfigurationAsync() =>
        !string.IsNullOrWhiteSpace(LocalUrl) && !string.IsNullOrWhiteSpace(await GetApiKeyAsync());

    public IEnumerable<string> GetCandidateUrls()
    {
        static string Normalize(string value) => value.Trim().TrimEnd('/');
        var local = Normalize(LocalUrl);
        var remote = Normalize(RemoteUrl);

        return Mode switch
        {
            ConnectionMode.Local => string.IsNullOrWhiteSpace(local) ? [] : [local],
            ConnectionMode.Remote => string.IsNullOrWhiteSpace(remote) ? [] : [remote],
            _ => new[] { local, remote }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)
        };
    }
}
