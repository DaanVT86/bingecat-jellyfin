using Jellyfin.Plugin.BingeCat.Configuration;
using Jellyfin.Plugin.BingeCat.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.BingeCat.Controllers;

/// <summary>Admin-only endpoints used by the embedded plugin configuration page.</summary>
[ApiController]
[Route("Plugins/BingeCat")]
[Authorize(Policy = "RequiresElevation")]
public sealed class BingeCatController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly PairingService _pairingService;
    private readonly OutboxStore _outbox;

    public BingeCatController(IUserManager userManager, PairingService pairingService, OutboxStore outbox)
    {
        _userManager = userManager;
        _pairingService = pairingService;
        _outbox = outbox;
    }

    [HttpGet("Users")]
    public ActionResult Users()
    {
#if NET10_0_OR_GREATER
        var users = _userManager.GetUsers();
#else
        var users = _userManager.Users;
#endif
        return Ok(users
            .OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
            .Select(user => new { id = user.Id, name = user.Username }));
    }

    [HttpGet("Status")]
    public ActionResult Status()
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        object[] links;
        lock (configuration.ProfileLinksSyncRoot)
        {
            links = configuration.ProfileLinks.Select(link => (object)new
            {
                userId = link.JellyfinUserId,
                userName = link.JellyfinUserName,
                connectionId = link.ConnectionId,
                linkedAt = link.LinkedAtUtc,
                lastHeartbeatAt = link.LastHeartbeatAtUtc,
                lastSyncAt = link.LastSyncAtUtc,
                lastErrorCode = link.LastErrorCode
            }).ToArray();
        }

        return Ok(new
        {
            serviceEnabled = configuration.ServiceEnabled,
            links
        });
    }

    [HttpPost("ConnectStart")]
    public async Task<ActionResult> ConnectStart([FromBody] UserRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.UserId, out var userId))
        {
            return BadRequest(new { error = "invalid_user_id" });
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound(new { error = "user_not_found" });
        }

        try
        {
            return Ok(await _pairingService.StartAsync(user.Id, user.Username, cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException exception)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "bingecat_unavailable", message = exception.Message });
        }
    }

    [HttpPost("Poll")]
    public async Task<ActionResult> Poll([FromBody] UserRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.UserId, out var userId))
        {
            return BadRequest(new { error = "invalid_user_id" });
        }

        return Ok(await _pairingService.PollAsync(userId, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("Disconnect")]
    public async Task<ActionResult> Disconnect([FromBody] UserRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.UserId, out var userId))
        {
            return BadRequest(new { error = "invalid_user_id" });
        }

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound(new { error = "link_not_found" });
        }

        ProfileLink? link;
        lock (plugin.Configuration.ProfileLinksSyncRoot)
        {
            link = plugin.Configuration.FindLink(userId)?.Copy();
        }

        if (link is null)
        {
            return NotFound(new { error = "link_not_found" });
        }

        try
        {
            await _pairingService.RevokeAsync(link, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Local disconnect must remain available while BingeCat is down.
            // The server-side token naturally becomes unreachable once erased.
        }

        var gate = _outbox.UserGate(userId);
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var removed = false;
            lock (plugin.Configuration.ProfileLinksSyncRoot)
            {
                var current = plugin.Configuration.FindLink(userId);
                if (current is not null
                    && string.Equals(current.DeviceToken, link.DeviceToken, StringComparison.Ordinal))
                {
                    current.DeviceToken = string.Empty;
                    plugin.Configuration.ProfileLinks.Remove(current);
                    plugin.SaveConfiguration();
                    removed = true;
                }
            }

            if (removed)
            {
                await _outbox.ClearUserAsync(userId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
        _pairingService.Cancel(userId);
        return NoContent();
    }

    [HttpPost("Settings")]
    public ActionResult Settings([FromBody] SettingsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        lock (plugin.Configuration.ProfileLinksSyncRoot)
        {
            plugin.Configuration.ServiceEnabled = request.ServiceEnabled;
            plugin.SaveConfiguration();
        }
        return NoContent();
    }
}

public sealed class UserRequest
{
    public string UserId { get; set; } = string.Empty;
}

public sealed class SettingsRequest
{
    public bool ServiceEnabled { get; set; }
}
