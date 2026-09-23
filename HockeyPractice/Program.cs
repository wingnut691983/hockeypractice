using HockeyPractice.Controllers;
using HockeyPractice.Persistence;
using HockeyPractice.Infrastructure;
using HockeyPractice.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using System.IO.Compression;

var builder = WebApplication.CreateBuilder(args);

// UpTurtle wires its Service to targetPort 8080 and injects no PORT variable. Bind explicitly
// so the container serves traffic; ASPNETCORE_URLS still wins if something sets it.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    builder.WebHost.UseUrls("http://0.0.0.0:8080");

// PATH_PREFIX=/<appSlug> is injected at deploy time and forwarded through unchanged, so the app
// sees /<slug>/... in the path. Empty locally.
var pathPrefix = (Environment.GetEnvironmentVariable("PATH_PREFIX") ?? string.Empty).TrimEnd('/');

var paths = new DataPaths(builder.Configuration);
builder.Services.AddSingleton(paths);

// The key ring encrypts the access cookie. Persisting it to the volume is what stops every
// redeploy from signing the whole team out.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(paths.KeyRing))
    .SetApplicationName("HockeyPractice");

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(paths.ConnectionString));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "hp_access";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Scoped to the app's prefix so it isn't shared with other apps in the same workspace.
        options.Cookie.Path = string.IsNullOrEmpty(pathPrefix) ? "/" : pathPrefix;
        // Always in production; SameAsRequest keeps plain-http localhost working in dev.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        // Long-lived on purpose: a 15-year-old should enter the team code once a season.
        options.ExpireTimeSpan = TimeSpan.FromDays(180);
        options.SlidingExpiration = true;
    });

builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection("Site"));
builder.Services.AddScoped<TeamAccessService>();
builder.Services.AddScoped<PlanStorageService>();
builder.Services.AddSingleton<DatabaseBackupService>();

// Singleton because the pause has to mean the same thing to every request at once. It lives in
// memory, so a restart clears it, which is the failure direction to want: a site that can get
// stuck open, never one stuck read-only with nobody left who knows why.
builder.Services.AddSingleton<MaintenanceState>();
builder.Services.AddSingleton<StartupHealth>();
// Off-site archive of the whole volume. Same shape as IEmailSender below: a real store when the
// credentials are there, an inert one when they are not, so the app builds and runs locally with
// no cloud config at all and the admin page simply says archiving is off.
builder.Services.Configure<VolumeBackupOptions>(builder.Configuration.GetSection("Archive"));
builder.Services.AddSingleton<VolumeBackupService>();
builder.Services.AddSingleton<BackupStatus>();
builder.Services.AddSingleton<BackupRunner>();

if (S3BackupStore.IsConfigured(builder.Configuration))
    builder.Services.AddSingleton<IBackupStore, S3BackupStore>();
else
    builder.Services.AddSingleton<IBackupStore, NullBackupStore>();

builder.Services.AddHostedService<ScheduledBackupService>();

builder.Services.AddSingleton<LinkExtractionService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<VideoTitleService>();
builder.Services.AddHttpClient();

// A real provider only when a key is present; otherwise mail is logged, so the whole
// subscribe → confirm → notify flow still works before a sending domain exists.
if (!string.IsNullOrWhiteSpace(builder.Configuration["RESEND_API_KEY"]))
    builder.Services.AddScoped<IEmailSender, ResendEmailSender>();
else
    builder.Services.AddScoped<IEmailSender, LoggingEmailSender>();
// Nothing was compressed before this, on a site whose stated constraint is that players load it
// on rink wifi. Measured on /whats-new: 47 KB raw, about 12 KB gzipped. Roughly 24 KB of every
// page is the layout's four inline script blocks, identical on every page and never cached
// separately precisely because they are inline.
builder.Services.AddResponseCompression(options =>
{
    // The app sits behind a gateway that terminates TLS, but UseForwardedHeaders runs before this
    // and rewrites Request.Scheme to https from X-Forwarded-Proto. Without this flag the
    // middleware would then decline every request and compress nothing, while looking configured.
    //
    // The reason the flag exists is BREACH, which needs a secret and attacker-controlled input in
    // one compressed response. ASP.NET Core's antiforgery tokens are randomised per response,
    // which is the specific defence for the obvious target here.
    options.EnableForHttps = true;

    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();

    // The defaults miss the two that matter most here: the pdf.js bundle is .mjs and its
    // worker alone is 2.3 MB, and the viewer ships wasm.
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "text/javascript",
        "application/javascript",
        "image/svg+xml",
        "application/wasm"
    });
});

// Different levels for the two, which is not an oversight. Measured on this machine, best of
// three warmed requests, because nothing caches the compressed bytes and every request pays:
//
//                      raw        gzip Fastest   brotli Optimal
//   /whats-new         47.1 KB    15.8 KB 34ms   13.6 KB 33ms
//   pdf.worker.mjs     2383 KB     634 KB 52ms    517 KB 50ms
//
// Brotli at Optimal is both smaller and no slower than gzip here: .NET maps it to a mid quality
// rather than the pathological 11 that makes Brotli notorious. Browsers prefer it, so that is
// what almost everyone gets. Gzip is the fallback for whatever does not, and it stays at Fastest
// because gzip Optimal bought a further 2 KB for several times the CPU.
//
// Do not "make these consistent". Setting both Fastest gives browsers the WORSE of the two,
// because brotli Fastest (16.9 KB) loses to gzip Fastest (15.8 KB) and is still preferred.
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

builder.Services.AddControllersWithViews();

// Brute-forcing a 6-character team code is the only real attack surface here.
//
// Partitioned on the TEAM being guessed at, not on the caller's address. Measured against this
// environment: the gateway does not reliably pass X-Forwarded-For, so the address the app sees
// alternates between the real client and whichever gateway pod relayed the request. A per-address
// partition therefore shuffles between buckets and stops limiting anything at all: 36 rapid
// wrong codes from one machine all got through. The slug comes from the route, so it is the same
// on every request no matter which pod relayed it, and it caps guessing at the team whether the
// attempts come from one machine or a thousand.
//
// The cost is that someone can spend a team's budget and hold up new sign-ins for that team for
// a minute. That is a fair trade: access cookies last 180 days, so this only ever delays people
// joining, never anyone already in, and it never touches another team.
//
// 30 a minute, because a squad all entering the code the evening a season starts is a real burst
// and being locked out by your own teammates is the likelier failure. It still leaves the 31^6
// code space needing a median 28 years of sustained guessing.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // A 429 renders through UseStatusCodePagesWithReExecute, which deliberately does not change
    // the browser URL, so a join link rejected by the limiter left ?c= sitting in the address
    // bar, which is the one thing TeamController.Index's contract says will not happen. Bounce to
    // the same path without the code and let the retry be rejected there instead. It terminates:
    // the retry carries no code, so it takes the plain 429 below. Verified against the framework
    // rather than assumed. The middleware sets the rejection status before calling this and does
    // not set it again afterwards, so the redirect written here is what goes out.
    options.OnRejected = (context, _) =>
    {
        var request = context.HttpContext.Request;
        if (request.Query.ContainsKey("c"))
            context.HttpContext.Response.Redirect(request.PathBase + request.Path);
        return ValueTask.CompletedTask;
    };

    options.AddPolicy("code-entry", http =>
    {
        // Populated because UseRateLimiter runs after UseRouting.
        var slug = http.Request.RouteValues["slug"] as string;

        if (!string.IsNullOrWhiteSpace(slug))
        {
            return RateLimitPartition.GetFixedWindowLimiter(
                "team:" + slug.ToLowerInvariant(), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });
        }

        // No slug means the site-admin sign-in, and it used to share ONE bucket for everyone.
        // That capped guessing, and it also handed any anonymous caller a way to keep the real
        // admin out: thirty posts a minute to /admin/login, forever, and the person who needs to
        // get in and lift a stuck maintenance pause meets a 429 every time. Locking the operator
        // out of their own recovery tool is the worse of the two failures.
        //
        // Partitioned per browser now, on a cookie the sign-in page sets. That cookie is NOT a
        // credential and proves nothing about who is holding it; it exists only so one caller's
        // attempts cannot spend another's budget, which is what gives the admin a lane nobody
        // else can fill.
        //
        // An attacker can fetch a fresh cookie and get a fresh bucket, so this deliberately does
        // NOT cap total attempts from the internet. It cannot: without a trustworthy client
        // identity, any single shared budget is one an attacker can exhaust, and the gateway does
        // not reliably pass X-Forwarded-For (see the ForwardedHeaders note further down), so the
        // address is not that identity. What caps guessing instead is the code's own length,
        // enforced at startup by SiteAdminController.MinAdminCodeLength rather than assumed the
        // way this comment used to assume it. The two changes only work as a pair.
        var client = http.Request.Cookies[SiteAdminController.ClientCookie];

        return RateLimitPartition.GetFixedWindowLimiter(
            string.IsNullOrWhiteSpace(client) ? "site-admin:no-cookie" : "site-admin:" + client,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});

var app = builder.Build();

// Apply migrations on startup. Wrapped so a storage fault surfaces in the logs as a real error
// rather than as an opaque exit code — the pod should come up and report, not crash-loop silently.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
        logger.LogInformation("Database ready at {Path}", paths.Database);
    }
    catch (Exception ex)
    {
        logger.LogError("Database migration failed at startup; the app will start but data " +
                        "operations will fail until this is resolved. {Type}: {Error}",
                        ex.GetType().FullName, ex.Message);

        // Also recorded where a person will see it. Serving on rather than crash-looping is the
        // right call, but it produces a site that looks healthy and fails on every data
        // operation, and a log line is not where anyone looks first. Restoring an archive old
        // enough to need a migration is exactly when this fires.
        app.Services.GetRequiredService<StartupHealth>()
            .RecordMigrationFailure($"{ex.GetType().Name}: {ex.Message}");
    }

    // Clears staging files a download or restore abandoned. Startup is the only moment nothing
    // can be holding them.
    try
    {
        scope.ServiceProvider.GetRequiredService<DatabaseBackupService>()
            .SweepStagingFiles(TimeSpan.FromHours(1));
    }
    catch (Exception ex)
    {
        logger.LogWarning("Could not sweep staging files at startup. {Type}: {Error}",
            ex.GetType().Name, ex.Message);
    }
}

// The kubelet probes http://<pod-ip>:8080/health directly, WITHOUT the path prefix, every 5s.
// Registered before UsePathBase so it stays unprefixed, and deliberately does not touch the
// database — a slow health handler gets the pod restarted.
app.MapWhen(ctx => ctx.Request.Path == "/health", branch =>
    branch.Run(async ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.WriteAsync("ok");
    }));

// Compression goes here, and the position is load-bearing in both directions.
//
// BELOW the /health branch above, deliberately: that branch is terminal, so the probe never
// reaches this and never pays to compress "ok". The kubelet hits it every 5 seconds and a slow
// health handler gets the pod restarted.
//
// ABOVE everything that writes a body, because compression has to wrap the response before it is
// produced: the static files below, the error pages, and every view.
app.UseResponseCompression();

if (!string.IsNullOrEmpty(pathPrefix))
    app.UsePathBase(pathPrefix);

// TLS terminates at the gateway; without this the app builds http:// redirects behind https.
//
// Clearing the known-proxy lists is what actually switches this on. They default to loopback
// only, so behind a gateway that is not 127.0.0.1 the whole middleware silently did nothing:
// every request looked like it came from the gateway's own address. That broke the rate
// scheme resolution and every address this app logs.
//
// Note this is NOT what the rate limiter keys on. This gateway passes the header inconsistently,
// so the address it yields is not stable enough to partition by; see the limiter above.
//
// Safe to clear here because nothing reaches this container except through the gateway, and
// the gateway appends the real client address to X-Forwarded-For. ForwardLimit stays at its
// default of 1, so the rightmost entry, the one the gateway added, is the one trusted; a
// client-supplied value sits to the left of it and is never read.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor
};
// Clear() and not an empty collection initializer: `KnownProxies = { }` inside the object
// initializer is Add-syntax over zero elements, so it adds nothing and leaves the loopback
// defaults exactly where they were. It compiles, it reads like it clears the list, and it
// changes nothing.
forwarded.KnownNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();
else
    app.UseExceptionHandler("/Home/Error");

// A deleted plan's old link, a mistyped team name — without this those render as a blank
// white 404, which reads as "the site is broken" to a teenager or parent.
app.UseStatusCodePagesWithReExecute("/Home/Error", "?status={0}");

// Must stay BELOW UseExceptionHandler: it calls Response.Clear() before re-executing, so a header
// set above it is dropped on exactly the error pages that most need it.
//
// strict-origin, not the browser default. The default strict-origin-when-cross-origin is only
// origin-only across sites. Same-origin navigation still sends the FULL URL, which is every
// internal link on a page whose address carries a plan id, a team slug, or anything else we would
// rather not hand onward. This site has no need to send a path anywhere, so it sends none.
app.Use(async (context, next) =>
{
    context.Response.Headers["Referrer-Policy"] = "strict-origin";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";

    // frame-ancestors ONLY, and deliberately not a Content-Security-Policy beyond it.
    //
    // This controls who may put THIS site in a frame, which is the clickjacking direction: every
    // page including /admin was framable by anyone, while the layout wires single-click
    // destructive actions through data-confirm (delete team, delete player, delete plan, the
    // REPLACE restore).
    //
    // A full CSP stays deferred and the reason recorded in known-issues is still right, but it is
    // about the opposite direction: frame-SRC governs what this site may embed, and the plan page
    // embeds the pdf.js viewer, so that half needs testing against the viewer. It also needs
    // nonces first, because every page carries inline script. 'self' keeps the viewer working:
    // it is framed by its own origin.
    context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'self'";

    // The pre-CSP equivalent, for anything that does not honour frame-ancestors. Browsers that
    // understand both prefer the CSP, so the two cannot disagree in practice.
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";

    await next();
});

// The vendored pdf.js viewer ships font and locale assets whose extensions ASP.NET's default
// provider doesn't know, and unknown types are not served at all. Without these mappings the
// viewer 404s on its standard fonts (.pfb) — which is how a PDF that relies on the base-14
// fonts, as Word exports commonly do, ends up rendering with no text at all.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".pfb"]  = "application/x-font-type1";
contentTypes.Mappings[".ftl"]  = "text/plain";
contentTypes.Mappings[".wasm"] = "application/wasm";
contentTypes.Mappings[".icc"]  = "application/vnd.iccprofile";
contentTypes.Mappings[".bcmap"] = "application/octet-stream";

// Every static asset was revalidated on every navigation: the middleware sets ETag and
// Last-Modified but no max-age, so the browser asked again each time and was told "unchanged".
// That is a full round trip per asset per page, on rink wifi, for the stylesheet, the logos, the
// icons and all ~4.5 MB of the pdf.js viewer.
//
// Two tiers, split on whether the URL carries a version. asp-append-version writes ?v=<hash>, so
// a versioned URL names exactly one immutable file and can be cached for a year: a change to the
// file changes the hash, which changes the URL. Anything without one may be replaced in place, so
// it gets an hour: long enough to drop the repeat round trips inside a session, short enough that
// swapping a logo is not invisible for a day. The two logos are the assets that most want
// versioning; see audit finding 1.6.
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = ctx =>
    {
        var versioned = ctx.Context.Request.Query.ContainsKey("v");
        ctx.Context.Response.Headers["Cache-Control"] = versioned
            ? "public, max-age=31536000, immutable"
            : "public, max-age=3600";
    }
});

// Refuses anything that could change data while a backup is being taken or restored. After
// UsePathBase, so its allowlist can be written against plain paths; after static files, so the
// stylesheet and the pdf.js assets never reach it. Reads pass through untouched.
app.UseMiddleware<MaintenanceMiddleware>();

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

app.Run();
