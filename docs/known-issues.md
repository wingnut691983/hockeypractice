# Known issues, deferred

Findings from a full read of the codebase on 2026-09-09, covering first run, access control,
uploads, the drill library, plans, email, and the backup and restore system.

Two findings were fixed at the time and are not listed here: an access code travelling in a
redirect's query string, and a crash in Re-extract / Replace file on plans that link one video
twice. Everything below was judged safe to leave for now, with the reason recorded so the call
can be re-made rather than re-derived.

Each entry says what it is, why it does not bite yet, and **what changes that**. The trigger is
the useful part: none of these need a calendar reminder, they need someone to notice the
condition has arrived.

---

## Blocked on configuration that is not set

Production currently has no `RESEND_API_KEY`, so `IEmailSender` resolves to `LoggingEmailSender`,
`NotificationService.IsLive` is false, and the signup box does not render at all. Both of these
are inert until that changes.

### 1. Publishing a plan blocks on sending every subscriber email

`CoachController.Publish` awaits `NotificationService.NotifyPublishedAsync`, which loops
subscribers one at a time, each with a 15 second `HttpClient` timeout in `ResendEmailSender`.
Thirty subscribers against a degraded provider is 7.5 minutes of a held request. The coach sees a
gateway error and cannot tell whether the plan published. It did: `Status` and `PublishedUtc` are
saved before the mail loop runs, which makes the ambiguity worse rather than better.

**Trigger: the day `RESEND_API_KEY` is set.** Fix before switching mail on, not after. Either
hand the loop to a background service, or give the whole loop one shared budget the way
`VideoTitleService.PopulateTitlesAsync` already does with `OverallBudget`.

### 2. Unsubscribe is a destructive GET, and there is no `List-Unsubscribe` header

`GET /s/unsub/{token}` deletes the `Subscriber` row outright. Corporate link scanners and mail
client prefetchers follow links in message bodies, so a parent can be unsubscribed without ever
clicking, and nobody finds out: they simply stop getting mail and assume the site is broken.

Separately, `ResendEmailSender` sends no `List-Unsubscribe` / `List-Unsubscribe-Post` headers,
which Gmail and Yahoo now expect from bulk senders. Without them, deliverability degrades in a
way that is invisible from inside the app.

**Trigger: same day as #1.** A confirmation page (or a POST form) fixes the first; two headers fix
the second. Note the two interact: `List-Unsubscribe-Post` is what lets a one-click unsubscribe
stay a POST, so doing both together is less work than doing either alone.

---

## Blocked on scale the volume has not reached

The archive is roughly 10 MB against a 1 GiB quota, so anything that only misbehaves on a full
volume has a long runway.

### 3. A restore double-counts the staged database against the quota

`BackupRunner.RestoreAsync` calls `VolumeBackupService.ValidateAsync` first, which extracts the
archive's database onto the volume. Only then does it measure:

```csharp
var headroom = Math.Max(0, _storageQuota - _paths.UsedBytes());
if (check.AdditionalBytes > headroom)
```

`UsedBytes()` now includes the staged file, and `AdditionalBytes` is
`additional + new FileInfo(staged).Length`. The same database is charged twice, so a restore that
would fit gets refused with a message overstating what it needs by one database.

It fails in the safe direction and there is a workaround (free space, or use the database-only
path, which measures before staging and gets this right). But it is in the recovery path, which
is the worst place to be debugging arithmetic.

**Trigger: the volume passing roughly half full.** Fix by measuring headroom before `ValidateAsync`
stages, or by subtracting the staged file's size from the `UsedBytes()` reading.

### 4. `UsedBytes()` walks the entire volume, and it is called in loops

`DataPaths.UsedBytes()` is a recursive `EnumerateFiles` over the whole persistent root.
`PlanStorageService.IsFull()` calls it, and `IsFull()` is called once per drill inside both bulk
copy loops (`DrillController.CopySelected`, `DrillController.CopyAll`), once per uploaded diagram
inside `SaveDiagramAsync`, on every upload validate, and on every render of the manage and admin
pages. An end-of-season rollover of a fifty-drill library is fifty full walks of a volume holding
every PDF on the site.

**Trigger: a team library large enough that a rollover feels slow, or the volume holding enough
files that a page load stalls.** Fix by caching the total for a few seconds, or tracking it
incrementally on write.

---

## Rare trigger, low consequence

### 5. A failed restore strands `.incoming` files inside the archive allowlist

`VolumeBackupService.ApplyFiles` writes each entry to `target + ".incoming"` and renames it into
place. The `catch` deletes it, but a process kill, an OOM, or a pod eviction part way through
leaves them behind. Nothing removes them afterwards: `DatabaseBackupService.SweepStagingFiles`
only scans the volume root for `snapshot-*.db` and `restore-*.db`, while
`VolumeBackupService.CreateAsync` enumerates *every* file under `TeamsRoot`.

So the orphans get captured into every subsequent nightly archive, and restored from it, forever.
That undercuts the guarantee written down in CLAUDE.md that the allowlist cannot leak a transient
name, and it permanently consumes quota on a volume with no resize path.

The consequence is wasted bytes and junk in archives, not corruption: a stray file with no
database row pointing at it is invisible to the app.

**Trigger: any restore that does not complete.** Fix by adding `*.incoming` to the startup sweep
(recursively, unlike the current root-only scan) and skipping the suffix in `CreateAsync`.

### 6. Site admin actions use `Forbid()`, which lands on a 404

All 13 guarded actions in `SiteAdminController` do `if (!_access.IsSiteAdmin(User)) return Forbid();`.
The cookie scheme defines no `AccessDeniedPath`, so the framework default `/Account/AccessDenied`
applies, that route does not exist, and `UseStatusCodePagesWithReExecute` renders it as
"That page isn't here".

`TeamScopedController.ResolveAsync` already documents exactly this trap and avoids it by
redirecting instead. The admin surface never got the same treatment.

Narrow in practice: the ticket lasts 180 days, and the natural recovery is a GET of `/admin`,
which renders `AdminLogin` correctly. Only a POST with a lapsed ticket hits the dead end.

**Trigger: an admin reporting a 404 during admin work.** Fix by redirecting to `Index` the way the
team controller redirects to `EnterCode`.

### 7. A coach previewing as a player records a plan view

`PlanController.Viewed` guards with `if (ctx!.IsManager) return NoContent();`. `TeamContext.IsManager`
is the *display* level, which the preview toggle deliberately clamps to Player. So the guard added
specifically to stop a coach's own glance inflating the anonymous view count stops working the
moment they use preview, and the row it writes is permanent.

`TeamAccessService` states that preview is presentational and must never change what anyone can
do. This is the one place it changes stored data.

**Trigger: none needed, it is simply wrong.** One-word fix: use `ctx.IsRealManager`. Left here only
because the damage is a slightly inflated count on the coach's own dashboard. Worth folding into
the next change that touches this file.

### 8. The join link shares a 30/minute per-team rate budget

The `code-entry` policy partitions on the team slug (correct, given the gateway does not reliably
pass `X-Forwarded-For`) but now covers `GET /t/{slug}` unconditionally, plus `POST t/{slug}/code`
and `POST t/{slug}/subscribe`, all inside one 30-per-minute fixed window with no queue.

Reaching 30 in a single minute needs a genuinely synchronized burst, such as a coach telling a
room to tap the link at once. Ordinary joining trickles over hours, and link-preview fetches are
generated once on the sender's device rather than once per recipient, so a group chat costs a
handful of requests rather than one per family.

**Trigger: a 429 reported during onboarding, or a plan to have a room join together.** Fix by giving
the `?c=` GET its own looser partition, or exempting the bare GET that carries no code.

---

## Small items

Individually not worth a commit; worth sweeping the next time each file is open.

- `SiteAdminController.DeleteReplaced` is `async` with no `await` (build warning CS1998), and
  calls `DatabaseBackupService.DeleteSidecars` unguarded. That method does a bare `File.Delete`,
  so an `IOException` becomes a 500. Every other caller wraps it.
- `SiteAdminController.RestoreArchive` does not catch exceptions from `IBackupStore.ListAsync` or
  `BackupRunner.FetchAsync`. A transient R2 error gives the admin a raw 500 instead of the careful
  message every other failure path in that controller produces.
- No `X-Content-Type-Options: nosniff`, `Referrer-Policy`, or frame-ancestors headers anywhere.
  Low risk here: uploads are manager-gated, and modern browsers already default the referrer to
  origin-only cross-site. Three lines of middleware whenever someone is in `Program.cs`.
- `MaxLength` attributes on the models are documentation only. SQLite does not enforce them, the
  controllers take raw `string` parameters so there is no `ModelState` to check, and nothing
  truncates. A manager can store a megabyte plan title. A `Truncate` helper alongside the existing
  `SafeFileName` would close it.
- `_Pager.cshtml:13` nullability warning CS8620: the dictionary should be
  `Dictionary<string, string?>`.

---

## Checked and found clean

Recorded so the next pass does not spend time here again:

- Path traversal on every uploaded filename. Logo and diagram names are server-generated GUIDs,
  and archive entries are resolved against a two-root allowlist in `VolumeBackupService.ResolveEntry`.
- Team isolation on every drill, diagram, plan, and copy path, including
  `DrillController.ResolveCopyTargetAsync`, which reads manager claims rather than the submitted form.
- The `returnUrl` open-redirect guard in `TeamScopedController.TryReturnUrlRedirect`.
- `DatabaseBackupService.Swap` ordering, its same-directory guard, and the checkpoint-throws contract.
- Backup run order: build, upload, confirm via `SizeAsync`, then prune.
- `BackgroundServiceExceptionBehavior` handling in `ScheduledBackupService`, including the
  `Task.Yield()` before anything that can throw.
- The R2 client settings (`RequestChecksumCalculation`, `UseChunkEncoding`, the exact version pin).
- XSS across the views and the tag-suggestion JavaScript, which builds nodes with `textContent`.
- pdf.js is 6.2.108, well clear of CVE-2024-4367.
- `mcr.microsoft.com/dotnet/aspnet:8.0` does ship tzdata, verified by running the image, so
  `WhenLabel` resolves `America/Chicago` correctly in production rather than silently falling back
  to UTC.
