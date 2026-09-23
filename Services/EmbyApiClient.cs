using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAVdrop.Models;

namespace EAVdrop.Services;

public sealed class EmbyApiClient
{
    private const string AppVersion = "0.3.1";

    private readonly SettingsService _settings;
    private readonly HttpClient _http = new();
    private readonly SemaphoreSlim _historyGate = new(3, 3);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public string LastConnectedBaseUrl { get; private set; } = "";

    public EmbyApiClient(SettingsService settings)
    {
        _settings = settings;
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<AuthenticationResultDto> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        username = username.Trim();
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("Enter your Emby username.");

        Exception? lastError = null;

        foreach (var baseUrl in _settings.GetCandidateUrls())
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));

                var requestUri = BuildUri(baseUrl, "Users/AuthenticateByName");
                using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
                request.Headers.TryAddWithoutValidation("X-Emby-Authorization", BuildAuthorizationHeader());
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["Username"] = username,
                    ["Pw"] = password
                });

                using var response = await _http.SendAsync(request, timeout.Token);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    lastError = new UnauthorizedAccessException("Invalid Emby username or password.");
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<AuthenticationResultDto>(_json, timeout.Token);
                if (result is null || string.IsNullOrWhiteSpace(result.AccessToken) || result.User is null)
                    throw new InvalidOperationException("Emby returned an incomplete sign-in response.");

                if (result.User.Policy?.IsAdministrator != true)
                    throw new UnauthorizedAccessException("EAVdrop needs an Emby administrator account to view server-wide users and playback activity.");

                await _settings.SaveAuthenticationAsync(result.AccessToken, result.User.Id, result.User.Name);
                LastConnectedBaseUrl = baseUrl;
                return result;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                lastError = new TimeoutException("The Emby sign-in request timed out after 30 seconds.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                lastError = ex;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
            throw new InvalidOperationException($"Unable to sign in to Emby. {lastError.Message}", lastError);

        throw new InvalidOperationException("No Emby server URL is configured.");
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var token = await _settings.GetAccessTokenAsync();

        try
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                foreach (var baseUrl in _settings.GetCandidateUrls())
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));

                        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUrl, "Sessions/Logout"));
                        request.Headers.TryAddWithoutValidation("X-Emby-Token", token);
                        request.Headers.TryAddWithoutValidation(
                            "X-Emby-Authorization",
                            BuildAuthorizationHeader(_settings.AuthenticatedUserId));

                        using var response = await _http.SendAsync(request, timeout.Token);
                        if (response.IsSuccessStatusCode)
                            break;
                    }
                    catch
                    {
                        // Local sign-out still happens below even if the server is unavailable.
                    }
                }
            }
        }
        finally
        {
            await _settings.ClearAuthenticationAsync();
        }
    }

    public Task<SystemInfoDto> GetSystemInfoAsync(CancellationToken ct = default) =>
        GetAsync<SystemInfoDto>("System/Info", ct);

    public Task<List<SessionInfoDto>> GetSessionsAsync(CancellationToken ct = default) =>
        GetAsync<List<SessionInfoDto>>("Sessions", ct);

    public Task<ActivityLogResultDto> GetActivityAsync(int limit = 200, CancellationToken ct = default) =>
        GetAsync<ActivityLogResultDto>($"System/ActivityLog/Entries?Limit={limit}", ct);

    public Task<UserQueryResultDto> GetUsersAsync(CancellationToken ct = default) =>
        GetAsync<UserQueryResultDto>("Users/Query?Limit=200&SortOrder=Ascending", ct);

    public Task<UserItemQueryResultDto> GetRecentPlayedItemsAsync(string userId, int limit = 250, int startIndex = 0, CancellationToken ct = default)
    {
        var escapedUserId = Uri.EscapeDataString(userId);
        return GetAsync<UserItemQueryResultDto>(
            $"Users/{escapedUserId}/Items?Recursive=true&SortBy=DatePlayed&SortOrder=Descending&IncludeItemTypes=Movie%2CEpisode%2CVideo%2CAudio&Limit={limit}&StartIndex={startIndex}&Fields=UserDataLastPlayedDate&EnableUserData=true",
            ct);
    }

    public async Task<List<BaseItemDto>> GetPlaybackHistoryItemsAsync(string userId, DateTimeOffset? cutoff, CancellationToken ct = default)
    {
        await _historyGate.WaitAsync(ct);
        try
        {
            const int pageSize = 250;
            var startIndex = 0;
            var results = new List<BaseItemDto>();

            while (true)
            {
                var page = await GetRecentPlayedItemsAsync(userId, pageSize, startIndex, ct);
                if (page.Items.Count == 0) break;

                var dated = page.Items.Where(i => i.UserData?.LastPlayedDate is not null).ToList();
                foreach (var item in dated)
                {
                    var playedDate = item.UserData!.LastPlayedDate!.Value;
                    if (!cutoff.HasValue || playedDate >= cutoff.Value)
                        results.Add(item);
                }

                var reachedCutoff = cutoff.HasValue && dated.Any(i => i.UserData!.LastPlayedDate!.Value < cutoff.Value);
                var reachedUnplayedItems = dated.Count < page.Items.Count;
                startIndex += page.Items.Count;

                if (reachedCutoff || reachedUnplayedItems || page.Items.Count < pageSize ||
                    (page.TotalRecordCount > 0 && startIndex >= page.TotalRecordCount))
                    break;
            }

            return results.OrderByDescending(i => i.UserData!.LastPlayedDate).ToList();
        }
        finally
        {
            _historyGate.Release();
        }
    }

    private async Task<T> GetAsync<T>(string relativePath, CancellationToken ct)
    {
        var token = await _settings.GetAccessTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("You are not signed in to Emby. Open Settings and sign in first.");

        Exception? lastError = null;
        var authenticationRejected = false;

        foreach (var baseUrl in _settings.GetCandidateUrls())
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));

                var requestUri = BuildUri(baseUrl, relativePath);
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.TryAddWithoutValidation("X-Emby-Token", token);
                request.Headers.TryAddWithoutValidation(
                    "X-Emby-Authorization",
                    BuildAuthorizationHeader(_settings.AuthenticatedUserId));

                using var response = await _http.SendAsync(request, timeout.Token);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    authenticationRejected = true;
                    lastError = new UnauthorizedAccessException("The saved Emby sign-in was rejected.");
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<T>(_json, timeout.Token);
                if (result is null)
                    throw new InvalidOperationException("Emby returned an empty response.");

                LastConnectedBaseUrl = baseUrl;
                return result;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                lastError = new TimeoutException("The Emby request timed out after 30 seconds.", ex);
            }
            catch (Exception ex) when (ex is not UnauthorizedAccessException)
            {
                lastError = ex;
            }
        }

        if (authenticationRejected && lastError is UnauthorizedAccessException)
        {
            await _settings.ClearAuthenticationAsync();
            throw new UnauthorizedAccessException("Your Emby sign-in has expired or was revoked. Open Settings and sign in again.");
        }

        if (lastError is not null)
            throw new InvalidOperationException($"Unable to connect to Emby. {lastError.Message}", lastError);

        throw new InvalidOperationException("No Emby server URL is configured.");
    }

    private string BuildAuthorizationHeader(string? userId = null)
    {
        var userPart = string.IsNullOrWhiteSpace(userId) ? "" : $"UserId=\"{userId}\", ";
        return $"Emby {userPart}Client=\"EAVdrop\", Device=\"{DeviceInfo.Current.Platform}\", DeviceId=\"{_settings.DeviceId}\", Version=\"{AppVersion}\"";
    }

    private static Uri BuildUri(string baseUrl, string relativePath)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        if (!root.EndsWith("/emby", StringComparison.OrdinalIgnoreCase))
            root += "/emby";

        return new Uri($"{root}/{relativePath.TrimStart('/')}");
    }
}
