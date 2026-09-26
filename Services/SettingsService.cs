using System.Text.Json;

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

public enum AppThemePreference
{
    System,
    Light,
    Dark
}

public sealed class SettingsService
{
    private const string LocalUrlKey = "local_url";
    private const string RemoteUrlKey = "remote_url";
    private const string ConnectionModeKey = "connection_mode";
    private const string PlaybackHistoryRangeKey = "playback_history_range";
    private const string DeviceIdKey = "device_id";
    private const string AccessTokenKey = "emby_access_token";
    private const string LegacyApiKeyKey = "emby_api_key";
    private const string AuthenticatedUserIdKey = "authenticated_user_id";
    private const string AuthenticatedUserNameKey = "authenticated_user_name";
    private const string SyncParticipantLeadMsKey = "sync_participant_lead_ms";
    private const string SyncLastHostIdentityKey = "sync_last_host_identity";
    private const string SyncLastParticipantIdentitiesKey = "sync_last_participant_identities";
    private const string FavoriteUserIdsKey = "favorite_user_ids";
    private const string AppThemePreferenceKey = "app_theme_preference";

    public AppThemePreference ThemePreference
    {
        get => GetSavedThemePreference();
        set => Preferences.Default.Set(AppThemePreferenceKey, value.ToString());
    }

    public static AppThemePreference GetSavedThemePreference()
    {
        var raw = Preferences.Default.Get(
            AppThemePreferenceKey,
            AppThemePreference.System.ToString());

        return Enum.TryParse<AppThemePreference>(raw, out var preference)
            ? preference
            : AppThemePreference.System;
    }

    public static AppTheme ResolveAppTheme(AppThemePreference preference) => preference switch
    {
        AppThemePreference.Light => AppTheme.Light,
        AppThemePreference.Dark => AppTheme.Dark,
        _ => AppTheme.Unspecified
    };

    public static void ApplyAppearance(Application app, AppThemePreference preference)
    {
        app.UserAppTheme = ResolveAppTheme(preference);

        var dark = preference == AppThemePreference.Dark ||
                   (preference == AppThemePreference.System &&
                    app.RequestedTheme == AppTheme.Dark);

        static Color C(string value) => Color.FromArgb(value);

        app.Resources["EavPageBackground"] =
            C(dark ? "#111827" : "#FFFFFF");
        app.Resources["EavText"] =
            C(dark ? "#F9FAFB" : "#111827");
        app.Resources["EavMuted"] =
            C(dark ? "#9CA3AF" : "#4B5563");
        app.Resources["EavCardBackground"] =
            C(dark ? "#1F2937" : "#F3F4F6");
        app.Resources["EavMediaCardBackground"] =
            C(dark ? "#172033" : "#F9FAFB");
        app.Resources["EavCardStroke"] =
            C(dark ? "#334155" : "#E5E7EB");
        app.Resources["EavControlBackground"] =
            C(dark ? "#111827" : "#FFFFFF");
        app.Resources["EavPlaceholder"] =
            C(dark ? "#9CA3AF" : "#6B7280");
        app.Resources["EavShellBackground"] =
            C(dark ? "#111827" : "#FFFFFF");
        app.Resources["EavTabBarBackground"] =
            C(dark ? "#0F172A" : "#F3F4F6");
        app.Resources["EavTabUnselected"] =
            C(dark ? "#94A3B8" : "#64748B");
    }

    public string LocalUrl
    {
        get => Preferences.Default.Get(LocalUrlKey, "");
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
        PlaybackHistoryRange.OneMonth => "the past month",
        PlaybackHistoryRange.TwoMonths => "the past 2 months",
        PlaybackHistoryRange.ThreeMonths => "the past 3 months",
        _ => "all available history"
    };

    public string NoPlaybackText => HistoryRange == PlaybackHistoryRange.Unlimited
        ? "No playback history found"
        : $"Nothing played in {HistoryRangeCaption}";

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

    public string AuthenticatedUserId => Preferences.Default.Get(AuthenticatedUserIdKey, "");
    public string AuthenticatedUserName => Preferences.Default.Get(AuthenticatedUserNameKey, "");

    public int SyncParticipantLeadMilliseconds
    {
        get => Math.Clamp(Preferences.Default.Get(SyncParticipantLeadMsKey, 500), 0, 2000);
        set => Preferences.Default.Set(SyncParticipantLeadMsKey, Math.Clamp(value, 0, 2000));
    }

    public string SyncLastHostIdentity
    {
        get => Preferences.Default.Get(SyncLastHostIdentityKey, "");
        set => Preferences.Default.Set(SyncLastHostIdentityKey, value ?? "");
    }

    public IReadOnlyList<string> SyncLastParticipantIdentities
    {
        get
        {
            var raw = Preferences.Default.Get(SyncLastParticipantIdentitiesKey, "");
            if (string.IsNullOrWhiteSpace(raw))
                return [];

            try
            {
                return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
            }
            catch
            {
                return [];
            }
        }
        set
        {
            var clean = (value ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Preferences.Default.Set(
                SyncLastParticipantIdentitiesKey,
                JsonSerializer.Serialize(clean));
        }
    }

    public IReadOnlyList<string> FavoriteUserIds
    {
        get
        {
            var raw = Preferences.Default.Get(FavoriteUserIdsKey, "");
            if (string.IsNullOrWhiteSpace(raw))
                return [];

            try
            {
                return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
            }
            catch
            {
                return [];
            }
        }
        set
        {
            var clean = (value ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Preferences.Default.Set(
                FavoriteUserIdsKey,
                JsonSerializer.Serialize(clean));
        }
    }

    public bool IsFavoriteUser(string userId) =>
        !string.IsNullOrWhiteSpace(userId) &&
        FavoriteUserIds.Contains(userId, StringComparer.OrdinalIgnoreCase);

    public bool ToggleFavoriteUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return false;

        var ids = FavoriteUserIds.ToList();
        var existing = ids.FindIndex(x =>
            string.Equals(x, userId, StringComparison.OrdinalIgnoreCase));

        if (existing >= 0)
        {
            ids.RemoveAt(existing);
            FavoriteUserIds = ids;
            return false;
        }

        ids.Add(userId);
        FavoriteUserIds = ids;
        return true;
    }

    public async Task<string> GetAccessTokenAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(AccessTokenKey) ?? "";
        }
        catch
        {
            return "";
        }
    }

    public async Task SaveAuthenticationAsync(string accessToken, string userId, string userName)
    {
        await SecureStorage.Default.SetAsync(AccessTokenKey, accessToken);
        Preferences.Default.Set(AuthenticatedUserIdKey, userId);
        Preferences.Default.Set(AuthenticatedUserNameKey, userName);
        SecureStorage.Default.Remove(LegacyApiKeyKey);
    }

    public Task ClearAuthenticationAsync()
    {
        SecureStorage.Default.Remove(AccessTokenKey);
        SecureStorage.Default.Remove(LegacyApiKeyKey);
        Preferences.Default.Remove(AuthenticatedUserIdKey);
        Preferences.Default.Remove(AuthenticatedUserNameKey);
        return Task.CompletedTask;
    }

    public async Task<bool> IsSignedInAsync() =>
        !string.IsNullOrWhiteSpace(await GetAccessTokenAsync());

    public async Task<bool> HasMinimumConfigurationAsync() =>
        GetCandidateUrls().Any() && await IsSignedInAsync();

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
