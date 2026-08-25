using System.Security.Claims;
using HockeyPractice.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace HockeyPractice.Services;

/// <summary>
/// Access is a persistent cookie ticket rather than a session, so it survives pod restarts and
/// redeploys — a player enters the team code once and stays in. Claims accumulate per team, so
/// one device can hold access to more than one team without signing anything out.
/// </summary>
public class TeamAccessService
{
    public const string TeamClaimPrefix   = "hp:team:";
    public const string PlayerClaimPrefix = "hp:player:";
    public const string SiteAdminClaim    = "hp:site";
    public const string ViewerKeyClaim    = "hp:vk";

    /// <summary>Cookie name for the coach's "see it as a player" preview.</summary>
    public const string PreviewCookie = "hp_preview";

    public TeamAccessLevel LevelFor(ClaimsPrincipal user, int teamId) =>
        RealLevelFor(user, teamId);

    /// <summary>
    /// Access as the viewer actually has it, ignoring any preview toggle. Authorisation must
    /// always use this; preview is a display concern and must never be able to grant anything.
    /// </summary>
    public TeamAccessLevel RealLevelFor(ClaimsPrincipal user, int teamId)
    {
        // Site admin is NOT consulted here. Administering the site does not make someone a
        // manager of a team — if a site admin needs to run a team, they hold that team's
        // manager code like anyone else. Their recovery path is regenerating the codes.
        return user.FindFirst(TeamClaimPrefix + teamId)?.Value switch
        {
            // Stored claim values are unchanged so existing cookies keep working.
            "coach"  => TeamAccessLevel.Manager,
            "viewer" => TeamAccessLevel.Player,
            _        => TeamAccessLevel.None
        };
    }

    /// <summary>
    /// The level to render with. Identical to the real level unless a coach has switched to
    /// player view, in which case it is clamped down to Viewer — never up.
    /// </summary>
    public TeamAccessLevel DisplayLevelFor(ClaimsPrincipal user, int teamId, HttpRequest request)
    {
        var real = RealLevelFor(user, teamId);
        if (real < TeamAccessLevel.Manager) return real;

        return IsPreviewingAsPlayer(request, teamId) ? TeamAccessLevel.Player : real;
    }

    public bool IsPreviewingAsPlayer(HttpRequest request, int teamId) =>
        request.Cookies.TryGetValue(PreviewCookie, out var value) &&
        value == teamId.ToString();

    public void SetPreview(HttpResponse response, HttpRequest request, int teamId, bool on)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = request.IsHttps,
            Path = request.PathBase.HasValue ? request.PathBase.Value! : "/"
        };

        if (on)
        {
            options.Expires = DateTimeOffset.UtcNow.AddHours(8);
            response.Cookies.Append(PreviewCookie, teamId.ToString(), options);
        }
        else
        {
            response.Cookies.Delete(PreviewCookie, options);
        }
    }

    public bool IsSiteAdmin(ClaimsPrincipal user) =>
        user.FindFirst(SiteAdminClaim)?.Value == "admin";

    public int? PlayerFor(ClaimsPrincipal user, int teamId) =>
        int.TryParse(user.FindFirst(PlayerClaimPrefix + teamId)?.Value, out var id) ? id : null;

    /// <summary>
    /// Opaque per-device key used to deduplicate views when no roster player has been picked.
    /// Minted on first grant; never derived from IP or anything personal.
    /// </summary>
    public string ViewerKeyFor(ClaimsPrincipal user) =>
        user.FindFirst(ViewerKeyClaim)?.Value ?? string.Empty;

    public Task GrantTeamAsync(HttpContext http, int teamId, TeamAccessLevel level) =>
        ReissueAsync(http, claims =>
        {
            claims.RemoveAll(c => c.Type == TeamClaimPrefix + teamId);
            if (level == TeamAccessLevel.Manager)
                claims.Add(new Claim(TeamClaimPrefix + teamId, "coach"));
            else if (level == TeamAccessLevel.Player)
                claims.Add(new Claim(TeamClaimPrefix + teamId, "viewer"));
        });

    public Task GrantSiteAdminAsync(HttpContext http) =>
        ReissueAsync(http, claims =>
        {
            claims.RemoveAll(c => c.Type == SiteAdminClaim);
            claims.Add(new Claim(SiteAdminClaim, "admin"));
        });

    public Task RevokeSiteAdminAsync(HttpContext http) =>
        ReissueAsync(http, claims => claims.RemoveAll(c => c.Type == SiteAdminClaim));

    public Task SetPlayerAsync(HttpContext http, int teamId, int? playerId) =>
        ReissueAsync(http, claims =>
        {
            claims.RemoveAll(c => c.Type == PlayerClaimPrefix + teamId);
            if (playerId is int id)
                claims.Add(new Claim(PlayerClaimPrefix + teamId, id.ToString()));
        });

    /// <summary>
    /// Rewrites the ticket, preserving every claim the device already holds. Granting access to a
    /// second team must not silently revoke the first.
    /// </summary>
    private static async Task ReissueAsync(HttpContext http, Action<List<Claim>> mutate)
    {
        var claims = http.User.Identity?.IsAuthenticated == true
            ? http.User.Claims.ToList()
            : new List<Claim>();

        if (claims.All(c => c.Type != ViewerKeyClaim))
            claims.Add(new Claim(ViewerKeyClaim, Util.Security.NewToken()));

        mutate(claims);

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true });

        // So the rest of THIS request sees the new claims — the cookie only takes effect
        // on the next request otherwise, which breaks redirect-then-authorize flows.
        http.User = principal;
    }
}
