using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.BingeCat.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BingeCat.Api;

/// <summary>Typed outbound-only client for the BingeCat Jellyfin API.</summary>
public sealed class BingeCatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BingeCatClient> _logger;

    public BingeCatClient(IHttpClientFactory httpClientFactory, ILogger<BingeCatClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<DeviceCodeResponse> RequestDeviceCodeAsync(DeviceCodeRequest request, CancellationToken cancellationToken)
    {
        using var response = await Client().PostAsJsonAsync(Url("device/code"), request, JsonOptions, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<DeviceCodeResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceTokenResponse> PollDeviceTokenAsync(string deviceCode, CancellationToken cancellationToken)
    {
        using var response = await Client().PostAsJsonAsync(Url("device/token"), new { device_code = deviceCode }, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.PreconditionRequired)
        {
            return new DeviceTokenResponse { Error = "authorization_pending" };
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return new DeviceTokenResponse { Error = error };
        }

        return await ReadAsync<DeviceTokenResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(ProfileLink link, CancellationToken cancellationToken)
    {
        using var response = await Authorized(link).PostAsJsonAsync(
            Url("heartbeat"),
            new { plugin_version = PluginVersionInfo.Value },
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<HeartbeatResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeAsync(ProfileLink link, CancellationToken cancellationToken)
    {
        using var response = await Authorized(link).PostAsJsonAsync(
            Url("device/revoke"), new { }, JsonOptions, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<EventBatchResponse> SendEventsAsync(ProfileLink link, IReadOnlyList<HistoryEvent> events, CancellationToken cancellationToken)
    {
        using var response = await Authorized(link).PostAsJsonAsync(Url("events/batch"), new { events }, JsonOptions, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<EventBatchResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> StartSnapshotAsync(ProfileLink link, long generation, CancellationToken cancellationToken)
    {
        using var response = await Authorized(link).PostAsJsonAsync(Url("snapshots"), new { generation }, JsonOptions, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await ReadAsync<SnapshotStartResponse>(response, cancellationToken).ConfigureAwait(false)).RunId;
    }

    public async Task ReportSnapshotFailureAsync(
        ProfileLink link,
        long generation,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        using var response = await Authorized(link).PostAsJsonAsync(
            Url("snapshots/fail"),
            new { generation, error_code = errorCode, message },
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> SendSnapshotPageAsync(ProfileLink link, string runId, int pageNo, IReadOnlyList<RemoteHistoryItem> items, CancellationToken cancellationToken)
    {
        using var response = await Authorized(link).PostAsJsonAsync(
            Url($"snapshots/{Uri.EscapeDataString(runId)}/pages"),
            new { page_no = pageNo, items },
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await ReadAsync<SnapshotPageResponse>(response, cancellationToken).ConfigureAwait(false)).Checksum;
    }

    public async Task CompleteSnapshotAsync(ProfileLink link, string runId, int pageCount, int itemCount, string checksum, CancellationToken cancellationToken)
    {
        using var response = await Authorized(link).PostAsJsonAsync(
            Url($"snapshots/{Uri.EscapeDataString(runId)}/complete"),
            new { page_count = pageCount, item_count = itemCount, checksum },
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private HttpClient Client()
    {
        var client = _httpClientFactory.CreateClient(HttpClientNames.BingeCat);
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"BingeCat-Jellyfin/{PluginVersionInfo.Value}");
        return client;
    }

    private HttpClient Authorized(ProfileLink link)
    {
        var client = Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", link.DeviceToken);
        return client;
    }

    private static string Url(string path)
    {
        var baseUrl = Plugin.Instance?.Configuration.ApiBaseUrl?.Trim().TrimEnd('/') ?? "https://bingecat.com";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && !(parsed.IsLoopback && parsed.Scheme == Uri.UriSchemeHttp)))
        {
            throw new InvalidOperationException("BingeCat API URL must use HTTPS.");
        }

        return $"{baseUrl}/api/jellyfin/v1/{path}";
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("BingeCat returned an empty response.");
    }

    private async Task<string> TryReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var value = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(JsonOptions, cancellationToken).ConfigureAwait(false);
            return value?.GetValueOrDefault("error") ?? $"http_{(int)response.StatusCode}";
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not parse BingeCat error response");
            return $"http_{(int)response.StatusCode}";
        }
    }
}
