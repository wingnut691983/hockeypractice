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
    options.AddPolicy("code-entry", http =>
    {
        // Populated because UseRateLimiter runs after UseRouting. Requests with no slug are the
        // site-admin login, which shares one bucket: there is only ever one admin code, and at
        // 14 characters it is not the thing anyone is guessing.
        var slug = http.Request.RouteValues["slug"] as string;
        var key = string.IsNullOrWhiteSpace(slug)
            ? "site-admin"
            : "team:" + slug.ToLowerInvariant();

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
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

app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

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
