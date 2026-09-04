using System.Text;

namespace HockeyPractice.Infrastructure;

/// <summary>
/// Turns <see cref="MaintenanceState"/> into an actual guarantee. Every request that could change
/// something is refused while writes are paused, which is what makes the pause worth trusting
/// before a backup is taken or restored.
///
/// Reads are left alone. A pause should not take the site away from a player standing at the rink
/// looking up which videos to watch; it only has to stop the database moving under the copy.
/// </summary>
public class MaintenanceMiddleware
{
    /// <summary>
    /// The only writes still allowed. Signing in and out is here because the alternative is a
    /// locked room: an admin whose cookie lapses mid-pause could otherwise never get back in to
    /// lift it, and would have to wait out the deadline or redeploy. The backup endpoints are
    /// here because refusing the very work the pause exists for would be circular.
    ///
    /// Everything else under /admin stays blocked, creating and deleting teams included. A pause
    /// that the person who set it can write straight through is not a pause.
    /// </summary>
    private static readonly string[] Allowed =
    {
        "/admin/login",
        "/admin/logout",
        "/admin/maintenance",
        "/admin/backup"
    };

    private readonly RequestDelegate _next;
    private readonly MaintenanceState _state;

    public MaintenanceMiddleware(RequestDelegate next, MaintenanceState state)
    {
        _next = next;
        _state = state;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!IsWrite(ctx.Request.Method) || !_state.IsPaused || IsAllowed(ctx.Request.Path))
        {
            await _next(ctx);
            return;
        }

        // 503 with Retry-After is the honest status: this is temporary and the deadline is known.
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers.RetryAfter = ((int)MaintenanceState.Window.TotalSeconds).ToString();

        // Written here rather than handed to the shared error page, which would re-execute a GET
        // and lose the one thing worth saying: nothing was saved, and roughly when to try again.
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(Page(_state.MinutesLeft), Encoding.UTF8);
    }

    private static bool IsWrite(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    private static bool IsAllowed(PathString path) =>
        Allowed.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Deliberately self-contained. Rendering this through a view would mean a database read, and
    /// the whole point of the pause is that the database is being copied or replaced right now.
    /// </summary>
    private static string Page(int minutesLeft)
    {
        var wait = minutesLeft <= 1 ? "in a minute or so" : $"in about {minutesLeft} minutes";
        return $"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>Saving is paused</title></head>
            <body style="margin:0;background:#f4f6f9;color:#12161d;
                         font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;">
              <div style="max-width:32rem;margin:3rem auto;padding:1.25rem;background:#fff;
                          border:1px solid #d9dfe8;border-radius:14px;">
                <h1 style="font-size:1.2rem;margin:0 0 .6rem;">Saving is paused right now</h1>
                <p style="margin:0 0 .6rem;line-height:1.5;">
                  The site is being backed up, so it is read only for a few minutes.
                  <strong>Nothing you just entered was saved.</strong>
                </p>
                <p style="margin:0 0 1rem;line-height:1.5;">
                  Go back, try again {wait}, and it will save normally.
                  Reading plans still works in the meantime.
                </p>
                <a href="javascript:history.back()"
                   style="display:inline-block;padding:.6rem 1rem;border-radius:10px;
                          background:#0B4EA2;color:#fff;text-decoration:none;">Go back</a>
              </div>
            </body></html>
            """;
    }
}
