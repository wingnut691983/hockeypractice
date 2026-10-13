# Upgrade from .NET 8 to .NET 10

> Status: **steps 1 to 4 done on branch `dotnet-10`, 12 October 2026; not yet deployed.** **Hard
> deadline: .NET 8 support ends 10 November 2026.** Do this before `drill-diagram-tool.md`. Delete
> this file when the upgrade ships.
>
> Results so far, all against the 12 October production database copy: EF Core 10.0.12 reports no
> pending model changes, so there is no migration. Cookies issued by .NET 8 open on .NET 10. .NET 8
> starts cleanly on a database that has `__EFMigrationsLock`. Plan pages are byte-identical between
> versions for the Detroit and Chicago teams, and the image has tzdata. SQLite goes from the March
> 2023 build to a June 2026 one, and a fresh volume is still WAL. The coach-side click-through
> passed (done by the user), and so did a database-only and a full archive download and restore.
> The emulated amd64 image needs `DOTNET_EnableWriteXorExecute=0` to start on a Mac; see README
> "Deploying". Production must not get it.

## Context
.NET 8 and EF Core 8 go out of support on **10 November 2026**. After that there are no security patches for the runtime, ASP.NET Core or EF Core. .NET 10 is the current long-term release, supported to November 2028. Nobody chose the old SQLite: it came in through EF Core 8 (`SQLitePCLRaw` 2.1.6, SQLite 3.41 as far as I know). Upgrading also brings a current SQLite. The .NET 10 SDK (10.0.300) and runtime (10.0.8) are already installed on this Mac.

**StatsLogic (`~/Documents/git/statslogic_ut`) has the same deadline.** It gets its own plan in its own session in that repo. This plan only touches `hockeypractice`.

## What changes
| File | Change |
|---|---|
| `HockeyPractice.csproj` | `net8.0` becomes `net10.0`. Both EF Core packages go to the latest 10.0.x. Update the "net8.0 target" wording in the AWSSDK note. |
| `global.json` | `8.0.421` becomes `10.0.300` (rollForward stays `latestFeature`). |
| `Dockerfile`, `Dockerfile.fast` | `sdk:8.0` and `aspnet:8.0` become `10.0`. |
| `Program.cs` | `KnownNetworks` is obsolete in .NET 10 and becomes `KnownIPNetworks`. Keep `Clear()`, and keep the comment explaining why it isn't an empty initializer. |
| `Migrations/` | Possibly one migration, only if EF 10 reports pending model changes (see risk 1). It must be **empty**. |
| `README.md`, `CLAUDE.md`, `~/.claude/CLAUDE.md` | "ASP.NET Core 8" becomes 10 wherever it describes this app. The global file waits until StatsLogic is also upgraded, because it describes both. |

**Unchanged on purpose:**
- `AWSSDK.S3` stays at its exact pin, because its request shape is what makes R2 uploads work.
- `SixLabors.ImageSharp` stays at 3.1.12. Upgrading it is a licence decision, not part of this work.
- `PdfPig` stays where it is.

All three run on .NET 10 unchanged, because they target older frameworks that .NET 10 still runs.

## Risks, ranked
1. **EF Core 9 and later refuse to migrate if the model and the migration snapshot disagree** (`PendingModelChangesWarning` is now an error). This app runs `Database.Migrate()` at startup inside a catch that logs and *keeps starting*. A mismatch would therefore not crash the pod. `/health` would answer 200 while every page that reads data failed: the quiet kind of broken this project's rules warn about.
   - **Mitigation:** run `dotnet ef migrations has-pending-model-changes` before building. If it reports anything, add the empty migration. Post-deploy checks must load a page that reads the database, not just the home page.
   - **That migration stays empty, even though it trips a trigger.** `known-issues.md` #16e says "the next migration for any reason" should add `IsDemo`. Do not fold it in here. A rollback to the .NET 8 image is only safe while this upgrade adds no real schema change, so `IsDemo` ships as its own deploy afterwards.
2. **EF Core 9 and later take a migration lock on SQLite**, which creates a `__EFMigrationsLock` table on first start. That is harmless, but it is a new table in production and in every backup from then on.
   - **Check:** a rollback to the .NET 8 image must still start against a database that has it. EF 8 ignores tables it doesn't know, so I expect it to; I'll prove it locally rather than assume.
3. **The container base image changes from Debian 12 to Ubuntu 24.04** (the default for .NET 10 images).
   - Ubuntu's image already has a `ubuntu` user at uid 1000, which this app runs as. That should be harmless, since keys and data all live under `DATA_DIR`.
   - **Check:** `WhenLabel` (time zone lookup) still finds its zone, and the dates on a plan render the same as now.
   - **This check, and risk 5's, must run in the built `linux/amd64` image, not under `dotnet run`.** On the Mac, `WhenLabel.NowIn` reads macOS's zone database and SQLite loads the osx native library, so neither touches Ubuntu 24.04. `NowIn` also falls back to UTC *silently* on `TimeZoneNotFoundException`. A missing tzdata would therefore just show wrong "today"/"tomorrow" labels and log no error. Look at a plan's date label on an `America/Chicago` team in the container.
4. **Player cookies must survive.** Data Protection keys are format-stable across versions, so cookies issued by the .NET 8 site should still open on .NET 10.
   - **Check:** locally, issue a cookie on the .NET 8 build, then start the .NET 10 build against the same `dpkeys` and confirm the cookie still works. A failure here would make every player on the team re-enter the code, so it's worth the five minutes.
5. **SQLite 3.41 to roughly 3.50.** The file format is stable both ways, and nothing here opts into new features. WAL, `VACUUM INTO`, `Swap` and the sidecar handling are all exercised in verification below.
6. **R2 backups.** The AWS SDK version doesn't change, but the runtime under it does. Run one real backup from the site-admin page after deploy, and confirm the archive appears in the bucket under `hockeypractice/`.
7. **Build warnings.** The build was taken to zero warnings in the last audit. New .NET 10 analyzer warnings get fixed, not suppressed, unless a fix would change behaviour.

**Overall: low to moderate.** It is a version-number change in four files, plus one obsolete API rename. The real risks are quiet ones (1 and 4), and each has a specific check. Rollback is one command, and it only reverts the image. Rollback stays safe as long as this upgrade adds no real schema change, so it adds none.

## Steps
1. Branch, bump the four files, `dotnet build` with zero warnings, run `has-pending-model-changes`.
2. Local run against a **copy of production data**: the latest R2 archive, or the database download from site admin (the user fetches it, since it needs the site-admin code). Restore it into `../.localdata` after backing up the current local data.
3. Run the checks for risks 1 to 5 locally, including the cookie test and a rollback rehearsal (start the .NET 8 build against the migrated copy). Run risks 3 and 5 in the image built from `Dockerfile.fast` (`podman run` with the copy mounted at `/persisted-data`), not under `dotnet run`.
4. Click through the site at 360px and desktop width, in both light and dark mode: team code entry, a plan with PDF and drills, the drill library, upload a picture, print view, manage pages, and site admin, including "Back up now" and a database-only restore on the local copy.
5. Deploy following the CLAUDE.md procedure. Confirm the slug, app id, URL and the tag from `list_deployment_versions` with the user before pushing. Check the image architecture is amd64.
6. After the deploy:
   - Fetch the URL and confirm the body comes from this build (I'll add a What's New line).
   - Load a data-backed page.
   - Read the app logs for the "Database ready" line (and the absence of "migration failed").
   - Run one manual backup and confirm it reaches R2.
7. Update the README and CLAUDE.md, and delete `docs/plans/dotnet-10-upgrade.md`.

## Time
About an hour of work. Most of it is the checks, not the code change.
