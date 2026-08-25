# HockeyPractice

Phone-first site for sharing youth hockey practice plans with players (14–16) and their parents.
Coach uploads a plan PDF; players open a link, see the videos to watch before practice, and read the plan inline.

## Stack

ASP.NET Core 8 MVC · EF Core + SQLite · PdfPig for link extraction · pdf.js for inline viewing.
No Bootstrap, no jQuery — bespoke mobile-first CSS. Keep page weight low; players load this on rink wifi.

## Non-obvious constraints

- **All durable state lives under `DATA_DIR`** (`/persisted-data` in production, `./data` locally).
  Anything written elsewhere is wiped on redeploy.
- **Data Protection keys are persisted to `DATA_DIR/dpkeys`.** Without this every redeploy invalidates
  every player's access cookie and the whole team has to re-enter the team code. Do not remove it.
- **Do not use `ISession`.** Access state is a persistent cookie auth ticket so it survives restarts.
- **`/health` must stay unprefixed and cheap.** It is registered before `UsePathBase` and must not touch
  the database — the kubelet probes it every 5s and a slow handler causes pod restarts.
- **Uploads are never served from `wwwroot`.** They stream through a controller action so the team-code
  gate applies.
- **Storage is capped at 1 GiB with no resize path.** The upload guard is a correctness requirement.
- The real PdfPig NuGet package id is **`PdfPig`** (Apache 2.0). `UglyToad.PdfPig` on nuget.org is an
  unrelated placeholder package — do not install it.

## Commands

```sh
dotnet run --project HockeyPractice     # http://localhost:8080
dotnet build
```

<!-- upturtle:begin -->
@AGENTS.md
<!-- upturtle:end -->
