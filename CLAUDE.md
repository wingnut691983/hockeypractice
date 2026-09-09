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
- **The gateway does not reliably pass `X-Forwarded-For`.** Measured, not assumed: the address the
  app resolves alternates between the real client and whichever gateway pod relayed the request.
  So never partition anything on the client address. The code-entry rate limiter keys on the team
  slug from the route for exactly this reason, and a per-address key silently stopped limiting
  altogether (36 rapid wrong codes all allowed). `UseForwardedHeaders` still clears its
  known-proxy lists, but only for `Request.Scheme` and logging.
- **Drill copying is keyed on provenance (`CopiedFromDrillId`), not title.** A copy the receiving
  team renamed is still the same drill, so title matching would duplicate it on the next copy.
  A partial unique index on `(TeamId, CopiedFromDrillId)` enforces it, and every copy path goes
  through `DrillController.CopyOneAsync`. Copying only ever inserts, never updates: once a copy
  lands it belongs to the other team. The feature is called *copying*, never *sharing*: the drills
  are independent duplicates, and "share" wrongly implies a live link.
- **There are two backup systems and they are not interchangeable.** `DatabaseBackupService` is the
  database only, downloaded by hand. `VolumeBackupService` + `S3BackupStore` + `ScheduledBackupService`
  is the whole volume (database, `dpkeys`, `teams/**`) zipped nightly to Cloudflare R2, three kept.
  `BackupRunner` owns the single definition of a run and a `SemaphoreSlim` every path shares.
  Restore of an archive is `BackupRunner.RestoreAsync`; the database-only restore is still
  `SiteAdminController.RestoreBackup`.
- **The database runs in WAL mode.** EF Core's provider puts the connection there; nothing in this
  repo sets `journal_mode`. Verified on a fresh volume: `-wal`/`-shm` exist and the header
  `write_version` is 2 before the app serves a request. **A database at rest looks like rollback
  mode** (`write_version=1`) because SQLite checkpoints and removes the WAL on clean close, and
  every `VACUUM INTO` output is rollback-mode by construction, so inspecting an idle file tells you
  nothing. A previous session got this wrong and nearly "fixed" `DeleteSidecars` in the wrong
  direction. `-wal`/`-shm` are the right sidecars; do not add `-journal` handling.
- **`DatabaseBackupService.Checkpoint()` throws on purpose and must keep throwing.** It runs before
  any file moves in `Swap`, so a failure genuinely means nothing changed. Swallowing it hands back
  an undo copy missing whatever was still in the WAL. This is not covered by a test: two attempts
  to reproduce a failing checkpoint were both invalid (`chmod` does not stop a `File.Move`, and
  corrupting the file behind the app does not reach a pooled connection).
- **`Swap` refuses an incoming file that is not in the same directory as the live database.**
  `File.Move` is a rename within one filesystem and a copy across two; a half-written copy makes
  the recovery path skip (it checks `!File.Exists(live)`) and leaves a truncated database live.
  The archive restore stages the zip in ephemeral temp but extracts the **database** onto the volume
  for exactly this reason.
- **A restore extracts files with write-then-rename, never straight over the target.**
  `entry.ExtractToFile(target)` truncates the target the instant it opens it, so a failure part way
  through guts a good PDF (measured: 36 bytes became 5). Renaming within a directory is atomic and
  also lets a player mid-download keep reading the file they opened.
- **A restore never deletes a file.** Files are added or overwritten only. Moving `teams/` aside
  would drop anything uploaded since the archive was taken, and since `Swap` deletes the previous
  `.replaced` first, a second restore would destroy the first one's undo. Orphaned bytes are the
  accepted cost.
- **The archive captures by allowlist** (database snapshot, `dpkeys`, `teams/**`), never by
  excluding transient names. An exclusion list has to be kept in step with every new `-wal`,
  `.replaced` or staging name, and one of those extracted next to a restored database corrupts it.
  Anything new and durable under `DATA_DIR` must be added to `VolumeBackupService.CreateAsync`.
- **R2 needs two AWS-isms switched off, and fixing one alone still fails.**
  `RequestChecksumCalculation = WHEN_REQUIRED` on the client config, and `UseChunkEncoding = false`
  on the `PutObjectRequest`. The second was found only by a real upload failing with
  `STREAMING-AWS4-HMAC-SHA256-PAYLOAD not implemented`. `AWSSDK.S3` is pinned to an **exact**
  version because the request shape changes across 3.7.x; do not relax it to a range.
- **Never let a backup failure reach the host.** `BackgroundServiceExceptionBehavior` defaults to
  `StopHost` since .NET 6, so an unhandled exception in `ScheduledBackupService` takes the site
  down because a backup failed. Every path is wrapped, including the startup catch-up.
- **"Last run" is read from the bucket, not from `BackupStatus`.** The singleton is lost on every
  restart and a restore restarts on purpose, so memory would report "never run" while three good
  archives sat in storage.
- **A restore is recorded by `DATA_DIR/last-restore`, not by `.replaced`'s mtime.** `File.Move`
  preserves the modified time, so `ReplacedAtUtc` is when the *old database was last written*,
  which on a quiet site is days before the restore. The scheduler uses the marker to skip its
  startup catch-up.
- **Plan directories are keyed on the row id, and a restore rolls ids back without removing the
  directories of plans created since.** New plans then walk back up through ids whose folder still
  holds an old `plan.pdf`. `PlanController.File` guards on `PlanKind.Pdf` for this reason: a drill
  plan writes no file, so without it the endpoint would stream a previous plan's PDF.
- **An archive can forge access, not just leak data.** It carries `dpkeys`, which
  `PersistKeysToFileSystem` stores unencrypted, alongside plaintext team codes and the roster.
  `hockeypractice-*.zip` is gitignored; keep the bucket private.
- **Pausing writes (`MaintenanceState`) is in-memory, per-process, and expires after 30 minutes.**
  It can get stuck open, never stuck shut. A second replica would need it moved to `DATA_DIR`.
- **Site admin is a separate axis from team access, not the top of the same ladder.** It grants
  manager access (create teams, issue codes, or explicitly "take" a team) but never has it by
  default. See README's "What I'd flag" before changing `TeamAccessService`.
- **`PlanView` uniqueness is keyed on the resolved player (`PlanId, PlayerId`), not the device**,
  once a player is known — see README before touching the `Viewed` beacon or its indexes.
- **The team (player) code is stored in plain text on purpose**, so a manager can re-share the
  join link; the manager code and site-admin code stay hash-only.
- **The tag editor's two scripts must not both commit the same keystroke.** The chip script adds
  on blur and on Enter; the suggestion panel blurs the input on `pointerdown` (it can't
  `preventDefault` there without killing iOS scrolling) and commits on release. They coordinate
  through the DOM (`data-suggest-press` on the input during a press, `aria-activedescendant` for
  a highlighted row), or picking a suggestion also adds the half-typed word beside it.
- **The bottom tab bar hides while a field has focus.** It is `position: fixed`, which on a phone
  means fixed to the layout viewport, and the keyboard does not shrink that, so it drifts across
  the middle of the screen instead of staying at the bottom. The hide is narrow screens only, and
  a `visualViewport` height check is what brings it back when the keyboard is swiped away without
  the field being blurred.
- **A run time is required when creating a drill, optional when editing one.** Drills that
  pre-date the rule have none, and blocking an unrelated edit over it would punish them for that.
- **PDF auto-sizing observes `#viewer` inside the iframe, not `#viewerContainer`** — the latter's
  own box doesn't grow as content overflows it, so a ResizeObserver on it never fires.
- The real PdfPig NuGet package id is **`PdfPig`** (Apache 2.0). `UglyToad.PdfPig` on nuget.org is an
  unrelated placeholder package — do not install it.

## Known issues

`docs/known-issues.md` holds findings that were deliberately deferred, each with the condition
that should bring it back. Two of them are waiting on `RESEND_API_KEY` being set and should be
fixed *before* mail is switched on, not after. It also records what has already been audited and
found clean, so a later pass doesn't re-derive it.

## Keep the README current, without being asked

`README.md` is this project's memory of *why*, not just what. It is the reason a later session
doesn't re-break something that was already got wrong once. It goes stale silently, and nobody
notices until the lesson has to be learned twice.

**After any change to this repo, re-read the relevant parts of `README.md` in the same session
and update them. Do not wait to be asked.** This is part of finishing the change, not a follow-up
task, and it applies to your own edits as much as to a feature someone requested.

What maps to where:

| A change to... | Update |
|---|---|
| An env var, or what happens when one is missing | the Configuration table |
| Backup, restore, or storage behaviour | "Backups" and the restore list under it |
| A new durable path under `DATA_DIR` | "Things worth knowing", **and** `VolumeBackupService.CreateAsync` |
| Roles, codes, or anything in `TeamAccessService` | "Roles", and usually "What I'd flag" |
| Deploy steps, Dockerfiles, or the image | "Deploying" |
| A bug you fixed that someone could innocently reintroduce | "What I'd flag" |
| A problem you found and deliberately left | `docs/known-issues.md`, with its trigger condition |

"What I'd flag" is the highest-value section and the easiest to under-fill. An entry earns its
place when the fix is not self-evident from the code. Write what actually broke, what it cost,
and what not to undo. Prefer measured detail over description: "36 bytes became 5" and
"`STREAMING-AWS4-HMAC-SHA256-PAYLOAD not implemented`" are why the existing entries are useful.

Keep the same voice as the surrounding entries: first person, direct, willing to say what was
got wrong. If something was reproduced or verified, say so, and say if it wasn't.

A change that genuinely needs no README edit is fine and common (a rename, a formatting pass, a
test). The *check* is what isn't optional. Say in one line what you checked and why nothing
needed changing, so it's visible that it happened rather than silently skipped.

## Commands

```sh
dotnet run --project HockeyPractice     # http://localhost:8080
dotnet build
```

<!-- upturtle:begin -->
@AGENTS.md
<!-- upturtle:end -->
