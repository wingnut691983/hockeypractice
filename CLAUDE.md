# HockeyPractice

Phone-first site for sharing youth hockey practice plans with players (14–16) and their parents.
Coach uploads a plan PDF; players open a link, see the videos to watch before practice, and read the plan inline.

## Stack

ASP.NET Core 8 MVC · EF Core + SQLite · PdfPig for link extraction · pdf.js for inline viewing.
No Bootstrap, no jQuery — bespoke mobile-first CSS. Keep page weight low; players load this on rink wifi.

## Non-obvious constraints

- **All durable state lives under `DATA_DIR`** (`/persisted-data` in production, `../.localdata`
  locally — one level above the project, outside `HockeyPractice/`). Anything written elsewhere
  is wiped on redeploy.
- **Data Protection keys are persisted to `DATA_DIR/dpkeys`.** Without this every redeploy invalidates
  every player's access cookie and the whole team has to re-enter the team code. Do not remove it.
- **Do not use `ISession`.** Access state is a persistent cookie auth ticket so it survives restarts.
- **`/health` must stay unprefixed and cheap.** It is registered before `UsePathBase` and must not touch
  the database — the kubelet probes it every 5s and a slow handler causes pod restarts.
- **Uploads are never served from `wwwroot`.** They stream through a controller action so the team-code
  gate applies.
- **Storage is capped at 1 GiB with no resize path.** The upload guard is a correctness requirement.
- **Backup/restore lives in `DatabaseBackupService`.** Copies are taken with `VACUUM INTO` (never a
  file copy); restoring swaps the file and then exits the process on purpose, because `Migrate()`
  only runs at startup and that is what lets an older backup be restored at all. `-wal`/`-shm` are
  named after the database path, so they must be checkpointed and cleared on every move.
- **Pausing writes (`MaintenanceState`) is in-memory, per-process, and expires after 30 minutes.**
  It can get stuck open, never stuck shut. A second replica would need it moved to `DATA_DIR`.
- **Site admin is a separate axis from team access, not the top of the same ladder.** It grants
  manager access (create teams, issue codes, or explicitly "take" a team) but never has it by
  default. See README's "What I'd flag" before changing `TeamAccessService`.
- **`PlanView` uniqueness is keyed on the resolved player (`PlanId, PlayerId`), not the device**,
  once a player is known — see README before touching the `Viewed` beacon or its indexes.
- **The team (player) code is stored in plain text on purpose**, so a manager can re-share the
  join link; the manager code and site-admin code stay hash-only.
- **PDF auto-sizing observes `#viewer` inside the iframe, not `#viewerContainer`** — the latter's
  own box doesn't grow as content overflows it, so a ResizeObserver on it never fires.
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
