# Practice Plans

A phone-first site for sharing youth hockey practice plans with players and their parents.

The manager uploads a practice plan PDF. Players open a link, see the drill videos to watch
before practice as tappable cards, and read the plan inline — no app, no password, no account.

## Roles

Three separate things, on purpose — see [What I'd flag](#what-id-flag) for why they used to be
tangled and aren't anymore.

- **Player** — the default. Reads plans, watches videos, optionally picks their name from the
  roster so the manager can see who's read a plan. One team code, shared with every family.
- **Manager** — an elevated Player: everything a player can do, plus uploading and editing
  plans, the roster, and team branding. A separate, longer code — not the same as the team code.
- **Site admin** — a site-wide role, not a team role. Creates and deletes teams, issues (or
  reissues) both codes for any team. Has **no** access to any team's plans or roster by default;
  reaching one requires either that team's manager code or a deliberate one-click "take manager
  access" action in the admin panel, which is visibly flagged on every page while it's active.

## How it works

- **Landing page.** Each team is a card with two ways in: a primary "See practice plans" button,
  and a quiet "Coach or team manager? Sign in to manage" link underneath. A device that's already
  signed in sees its own status ("You manage this team" / "You're signed in") and skips straight
  past the code. A footer link to `/admin` is on every page, for whoever runs the site.
- **Getting in.** The manager shares a join link (`/t/<team>?c=CODE`) into the team's group chat;
  tapping it grants access and remembers the device for 180 days — no typing. Players who haven't
  already are asked to tap their name from the roster once (or "I'm a parent" to skip), which is
  what lets the manager see who's read each plan; a strip under the header always shows who the
  site currently thinks you are, with a one-tap way to change it.
- **Uploading.** PDF only. The upload is checked by its `%PDF-` header, not its file extension.
  Word and Google Docs both export PDF in one click. A plan's PDF can be replaced later without
  losing its URL, view history, or any video labels the manager edited — no more delete-and-
  reupload to fix a typo.
- **Video links.** At upload, links are pulled out of the PDF — both real hyperlinks and URLs
  typed as plain text — and shown as tappable cards above the document, grouped under the
  section of the plan they belong to. Labels come from the document first (a hyperlink's anchor
  text, or the line the URL sits on); a bare URL the document doesn't describe falls back to the
  video's own published title (via oEmbed — no API key), and only then to "Video N". See
  [docs/authoring-plans.md](docs/authoring-plans.md) for how to format a plan so extraction works
  well. The manager can relabel or hide any link, and re-run extraction on an existing plan
  without losing hand-made edits.
- **Watching them.** Videos play *in place*, over the plan, with Previous/Next to move through
  the list — including links tapped *inside* the rendered PDF, which are caught and routed to
  the same player rather than navigating away. The PDF itself auto-sizes to its full rendered
  height rather than sitting in its own scrollable box, so the whole plan is one continuous
  scroll with nothing to get stuck in partway down a long document. Pinch-to-zoom on the PDF is
  deliberately disabled (it fought the page's own scroll); a small floating +/- control stays
  reachable no matter how far into the document you've scrolled.
- **Draft → Publish.** Nothing reaches players and no email goes out until the manager publishes.
  Republishing after an unpublish does not re-send the email.
- **Notifications.** Parents opt in with their own address (double opt-in, one-click
  unsubscribe) from the plans page — hidden entirely from managers, and hidden site-wide until a
  mail provider is actually configured, so it's never offered and then silently never sent.
- **How it works.** A public page at `/how-it-works`, linked from the landing page, showing both
  sides of the site to someone who has no code yet. The demo team's code previews the player side,
  but there is no read-only manager mode, so previewing the coach side would mean handing out a
  manager code and with it the ability to wreck the demo team. The coach screens there are drawn
  instead: static markup built from the real CSS classes, so they weigh nothing, reflow on a phone
  and follow dark mode, and so no access is needed to see them. Nothing inside one is a real
  control. See "What I'd flag" before changing how they are built.

  The page ends with a link into the demo team, as a player sees it. Its slug is a constant in
  `HomeController` (`DemoTeamSlug`), because a slug is the visible half of every link to a team and
  is not a secret; the code that opens it is, and is not stored there. It is read from the team's
  own row when the page renders, so rotating the demo team's code needs no code change. Rename or
  delete that team and the panel quietly disappears, which is why nothing else on the page depends
  on it.
- **Team colours.** A manager sets two brand colours; every place they're used as a button
  background or as text is computed for WCAG-legible contrast rather than assumed white-on-team-
  colour, so a light team colour (gold, white, powder blue) doesn't produce unreadable buttons.

## Running locally

```sh
SITE_ADMIN_CODE=devcode dotnet run --project HockeyPractice
```

Then open http://localhost:8080/admin, sign in with that code, and create a team. The manager
code is shown once, on creation, and can't be read back later — only re-issued. The team
(player) code is stored in plain text specifically so it can be shared again later; see
[What I'd flag](#what-id-flag).

Local data (SQLite, uploads, keys) goes to `../.localdata` (one level above the project, outside
`HockeyPractice/`), which is gitignored.

## Configuration

| Variable | Required | Notes |
|---|---|---|
| `SITE_ADMIN_CODE` | yes | Gates `/admin`. Fails closed — unset means no one can sign in. |
| `DATA_DIR` | no | Defaults to `/persisted-data`. Set to `../.localdata` in development. |
| `PATH_PREFIX` | no | Injected by UpTurtle. Empty locally. |
| `RESEND_API_KEY` | no | Enables real email. Without it, mail is logged and the signup box is hidden. |
| `EMAIL_FROM` | no | e.g. `Bantam A <plans@yourdomain.com>`. Needs a verified domain. |
| `ARCHIVE_S3_ENDPOINT` | no | Cloudflare R2 S3 endpoint, `https://<accountid>.r2.cloudflarestorage.com`. **Without the bucket on the end**; the SDK appends it, and pasting the bucket's own "S3 API" value gives `.../<bucket>/<bucket>/<key>` and a missing-bucket error. |
| `ARCHIVE_S3_BUCKET` | no | Bucket name. |
| `ARCHIVE_S3_PREFIX` | no | `hockeypractice/` in production, `local-test/` for testing. A safety fence, not tidiness: everything the app lists, offers and prunes is confined to this prefix. |
| `ARCHIVE_S3_ACCESS_KEY_ID` | no | R2 token key pair, not the token *value*, which is for Cloudflare's own API and is unused here. |
| `ARCHIVE_S3_SECRET_ACCESS_KEY` | no | Shown once by Cloudflare on creation. |

All five must be present or off-site backups are simply off: the scheduler never starts and the
admin page says so. Everything else keeps working. The tunables live in `appsettings.json` under
`Archive` (`HourUtc` 8, which is 03:00 Central in summer; `Keep` 3; `MaxBytes` 512 MB) because they
are code decisions rather than deployment config. Override with `Archive__Keep` style names.

## Backups

There are two, and they cover different things.

**The database download** (site admin, "Download database") is one SQLite file: teams, plans,
drills, roster, view history and the codes. It does not include a single uploaded PDF or drill
picture, because those are files on the volume rather than rows.

**The off-site archive** is the whole volume in one zip: that same database, every plan PDF and
drill diagram, the team logos, and `dpkeys`. It runs nightly to Cloudflare R2, keeps the newest
three, and can also be triggered by hand or downloaded straight to your machine. This is the one to
reach for if the app is ever lost, which is not hypothetical: the 1 GiB volume went with the app
when the trial ended on 7 September 2026, and only a hand-downloaded database survived.

Treat an archive like the codes themselves, and then some. It carries every team's plaintext player
code, subscriber emails, the whole roster, and the Data Protection key ring, which is unencrypted
on disk and is what signs the access cookie. Someone holding an archive can forge access to the
site, not merely read a copy of it. `hockeypractice-*.zip` is gitignored; keep the bucket private.

### Restoring

Both routes need **Pause saving** first, and the word `REPLACE` typed back. The site restarts
itself afterwards, which takes about twenty seconds.

*The whole site, from an archive.* Site admin, Off-site backups, "Restore the whole site from a
backup". Pick one from storage (preferred: it is fetched server side, so the archive never travels
up through your connection and cannot meet whatever request-size ceiling the gateway imposes, which
has never been measured), or upload a zip.

*The database only, from a `.db` file.* Site admin, Backup, "Restore from a backup file". Use this
when you have a database download rather than an archive, or to put back the copy a previous
restore set aside.

What a restore does, exactly:

- **The database is replaced outright.** Anything created since that backup is gone from the site;
  anything deleted since comes back.
- **Files are put back over the top, and nothing is ever deleted.** A PDF the backup contains
  overwrites whatever is there. A file uploaded *after* the backup stays on disk with no row
  pointing at it, so it is invisible to the site but still counts against the 1 GiB. That
  asymmetry is deliberate: deleting would let a restore destroy a file a newer backup still needs.
- **`dpkeys` is merged, not replaced**, so people stay signed in.
- **The database it replaced is kept** as `hockeypractice.db.replaced`, and site admin can download
  or delete it. That is the way back from restoring the wrong thing, but only one is kept and the
  next restore overwrites it. Check the site looks right *before* deleting it.

Worth doing occasionally, and it is the only thing that proves a backup is real: download the
newest archive, point a scratch local instance at an empty `DATA_DIR`, restore it there, and open a
plan. That answers "is last night's backup actually restorable", which is the question that had no
answer in September.

## What I'd flag

- **The What's new page is public, and changelog entries kept leaking security detail.** It is
  served with no sign-in on purpose (`HomeController.WhatsNew`, so a player or parent can read it
  without being handed anything first), which means every word on it is readable by anyone probing
  the site. Written as if it were an internal changelog, several entries had spelled out how the
  code box's guess limiting is measured and configured, how the off-site archive is retained, and
  which admin-only paths exist for putting an archive back. Read together they were a tuning guide
  for guessing a team code, which no entry on that page needs to be. Rewritten on 2026-09-10; the
  view now carries a comment saying the same thing where the next batch gets written.

  The rule: say what changed and what it means for the person reading. No thresholds, no counts,
  no keying, no admin-only paths, and never which piece of infrastructure was at fault. "Guessing a
  team code is rate-limited" is the entry; the number is not. Also removed was an entry announcing
  that access codes used to appear in web addresses, because the fix had already shipped and the
  announcement only told people where to go looking. A fixed exposure does not need announcing to
  readers who cannot act on it; if one ever does, rotate the affected codes and tell managers
  directly instead of publishing it. Nothing was known to be exploited, and this came from reading
  the page rather than from an incident.

  Note this repo is public, so the pre-rewrite wording is still in git history and this file is
  published too. That is why this entry describes the *categories* rather than restating the
  values: the detail that makes the lesson stick here is the rule, not the numbers.

- **Site admin used to implicitly be a manager of every team** — one line
  (`if (IsSiteAdmin) return TeamAccessLevel.SiteAdmin`) put it at the top of the same access
  ladder as team roles. Fixed: site admin is a completely separate axis now. It can *grant*
  manager access (create teams, issue codes, or explicitly "take" access to one team for
  troubleshooting) but never *has* it by default. If you're touching `TeamAccessService` or
  `TeamScopedController`, keep those two things from merging back together.
- **The team (player) code is stored in plain text**, deliberately, unlike the manager code and
  the site-admin code (both hash-only). It's a shared, low-privilege secret handed to every
  family on the team — its whole purpose is to be given out repeatedly, and hashing it made the
  invite link unusable the moment the creation notice scrolled away. Don't hash it "for
  consistency" without re-solving how a manager re-shares the join link.
- **An access code must never travel in a redirect's query string.** Creating a team, issuing a
  new manager code and rotating the team code all used to end with
  `RedirectToAction(nameof(Index), new { notice })`, where the notice contained the code. That
  writes it into the browser's history permanently and into the gateway's access log, which is
  the last place anyone would think to look for it, and the manager code is the one credential
  this site stores hash-only and tells you cannot be read back. Those three paths now hand the
  notice to the page through `TempData` instead (`SecretNotice` in `SiteAdminController`,
  `SecretNoticeKey` in `CoachController`), which rides an encrypted cookie protected by the same
  key ring that signs the access ticket. It is shown once, consumed on reload, and leaves nothing
  behind. Ordinary notices still ride the query string, where they are harmless and survive a
  refresh; the rule is only about codes. Note this is `TempData` on its cookie provider, not
  `ISession`, which this app deliberately does not use.

  **The same rule now covers a name.** Removing a player from the roster ended with
  `notice = $"Removed {player.Name}."`, putting a 14-to-16-year-old's real name in the query
  string of the redirect. That action is a hard delete of the `Player` row and every `PlanView`
  for them, which is what makes it worse than it first looks: the name outlived the row it had
  just been deleted from, in browser history and the gateway log, which is the one place nobody
  would think to go and scrub. Same two-line fix through `SecretNoticeKey`. The line to hold is
  that a notice is safe in a URL when it is about a *thing* and not when it is about a *person*.
- **A code in `?c=` was only stripped from the URL on the paths that worked.**
  `TeamController.Index`'s own doc comment promises the join link's code is "stripped from the URL
  immediately" and should not sit in browser history. The success paths do redirect. Two failure
  paths did not, because `UseStatusCodePagesWithReExecute` re-executes `/Home/Error` *without
  changing the browser URL*, which is exactly what it is for and exactly what makes it a trap
  here. An unknown slug (mistyped link, deleted team) hit `NotFound()` before the code was ever
  read, and a 429 from the `code-entry` limiter was rejected in middleware before the action ran
  at all. Either way the player was left looking at the team code in the address bar, on the one
  screen most likely to get photographed and sent to whoever could not get in.

  Both now redirect to the same path with the code dropped, and both terminate, because the retry
  carries no code and so takes the plain 404 or 429. The 429 half is an `OnRejected` handler on the
  rate limiter, and that is the part I checked rather than assumed: the middleware sets the
  rejection status *before* calling `OnRejected` and does not set it again afterwards, so a
  `Response.Redirect` written in there is what actually goes out. I proved that against a throwaway
  app first, then measured both paths on this one: 302 to the clean path, then a clean 429. If you
  ever move `OnRejected`, re-check that ordering; if it flips, the redirect silently becomes a 429
  with a stale `Location` header and the code goes back in the address bar.
- **`Referrer-Policy: strict-origin`, and it must sit below `UseExceptionHandler`.** I had this
  filed as a nicety, reasoning that browsers already default to origin-only cross-site. That is
  true cross-site and misses the point: the default `strict-origin-when-cross-origin` sends the
  **full URL** on same-origin navigation, which is every internal link on the site, so it was an
  amplifier for the two leaks above rather than something unrelated to them. The placement is the
  half that would fail silently. `UseExceptionHandler` calls `Response.Clear()` before
  re-executing, so a header set above it is dropped on precisely the error pages that were the
  ones carrying codes. Verified by curling a 404 and confirming the header is still on it.
  `X-Content-Type-Options: nosniff` went in alongside from the same known-issues bullet;
  `frame-ancestors` deliberately did not, because the plan viewer runs pdf.js in an iframe and
  that deserves its own look rather than a drive-by CSP.
- **`PlanView` uniqueness is keyed on the resolved player, not the device**, once a player is
  known: `(PlanId, PlayerId)` when a player is picked, `(PlanId, ViewerKey)` only while
  anonymous. Keying purely on device (the original design) meant a shared family device could
  only ever have one player's view recorded per plan, permanently. If you touch the `Viewed`
  beacon, keep that split — see `AppDbContext.OnModelCreating` for the two partial unique
  indexes.
- **The PDF auto-sizes by observing `#viewer` inside the iframe, not `#viewerContainer`.** The
  latter is the absolutely-positioned, `overflow:auto` scroll box — its own box size does not
  change as content overflows it, so a `ResizeObserver` on it never fires as pages render in.
  `#viewer` is the plain block-flow div pdf.js actually stacks pages into; that's what grows
  with content and what has to be observed. Got this wrong once already — see `Details.cshtml`.
- **The tag box commits on blur *and* on Enter, and the suggestion panel drives both of them.**
  Typing "touch" and then picking "Touch Pass" used to leave the wrong chips behind, two different
  ways. With the mouse: pressing a suggestion row blurs the input (the panel isn't focusable, and
  the suggestion script deliberately does *not* `preventDefault` on `pointerdown`, because that is
  what made the list unscrollable on iOS), so the chip script's blur handler committed the partial
  word. Measured in headless Chrome against the old code: the chips came out `["touch"]` and the
  tag actually clicked never landed at all. With the keyboard: ArrowDown then Enter produced
  `["touch", "Touch Pass"]`, because the chip script's `keydown` is registered first and therefore
  runs first, and the suggestion script's `preventDefault` cannot undo a commit that has already
  happened. The two scripts are separate IIFEs on purpose (see the comment above the suggestion
  one), so they coordinate through the DOM instead: the panel sets `data-suggest-press` on the
  input for exactly the length of a press and blur skips while it's there, and Enter defers to the
  suggestion script whenever `aria-activedescendant` says a row is highlighted. Both fixes verified
  before and after, along with the plain paths: a new tag typed and then clicked away from, and
  Enter with nothing highlighted, still make one chip and don't submit the form. Any third commit
  path you add needs the same two guards.
- **The illustrations on `/how-it-works` must stay inert, and that is three separate things.**
  They are drawn from the real classes rather than screenshotted, which keeps the page imageless
  and self-updating with the palette, but it means a fake button is one attribute away from
  looking exactly like a real one. First, the markup contains no `a`, `button`, `input`, `form` or
  `details` at all, and none of the `data-` attributes the layout's scripts bind to, so no site
  script can attach to one; `<span class="hp-btn">` renders identically because every one of those
  rules is class-only. Second, each frame is `role="img"` with a descriptive `aria-label`, which
  collapses the subtree to a single node so a screen reader announces a picture instead of reading
  out a form nobody can submit. That makes the label the only thing conveyed, so it has to be a
  real sentence. Do not add `inert` as well: it removes the node from the accessibility tree and
  takes the label with it. Third, `pointer-events: none` and a `cursor` override, because
  `.hp-btn` sets `cursor: pointer` and that is the one cue that would still say "tap me".
  Measured at 390 and 360 px: `scrollWidth` equals the viewport, so the page never scrolls
  sideways. The third video card deliberately runs past the frame edge and is clipped by
  `overflow: hidden`, which is how the real strip's sideways scroll is suggested without being
  scrollable. Checked in both colour schemes.
- **The suggestion panel opens on `click` as well as `focus`, and it needs both.** Reported from
  the plan form on 2026-09-11: after adding one tag, clicking the box again showed no suggestions
  until you clicked away and clicked back. Every path that commits a tag leaves the input focused,
  deliberately, so the next tag can be typed straight away. Enter never moves focus, `pick()`
  calls `focus()` back, and the Add button's handler ends with `entry.focus()`. The panel closes
  behind each of them. That leaves the input focused with the list shut, and clicking a box that
  is already focused fires no `focus` event, which was the only thing that reopened it. Adding a
  second tag is the normal case, so this was most of the time. The fix is one `click` listener,
  guarded with `if (open) return` so the first click of all, which `focus` has already handled,
  does not render a second time. Do not delete it as redundant with `focus`: it only looks
  redundant on the first tag. Reproduced in jsdom against the shipped script, before and after:
  the box focused and the panel shut, then a click, panel open `false` before and `true` after,
  with the click-away-and-back workaround still working in both.
- **The bottom tab bar steps out of the way while a field has focus, on purpose.**
  `position: fixed` pins it to the *layout* viewport, and an on-screen keyboard does not shrink
  that: reported from a phone editing a plan, the bar stopped sitting at the bottom the moment a
  text field was tapped and drifted across the middle of the screen on every scroll. It is also a
  navigation bar, so a mis-tap on it abandons a half-written plan. It now slides off on `focusin`
  of anything that raises a keyboard or a picker (`datetime-local`, which the plan editor uses,
  counts) and comes back on `focusout`. Two things to keep if you touch it. The CSS rule sits in a
  `max-width: 699.98px` block, because from 700px up the bar is in normal flow beside the header
  and hiding it there would be a bug. And the `visualViewport` resize check is the only way back
  when iOS lets you swipe the keyboard down without blurring the field, which `focusout` never
  sees; it reads the height only, never a position, so it can move the bar out of sight or back
  but never misplace it. Verified in an emulated phone viewport: focus hides it, blur restores it,
  focusing a submit button does not, the desktop width is untouched, and the viewport returning to
  its resting height brings it back while the field still holds focus. Not verified on a real
  iPhone, which is where the underlying behaviour comes from.
- **A wheel over a focused `<input type="number">` edits it.** Type 12 into a drill's run time,
  then two-finger scroll down the page to reach Save, and you save 13 without ever seeing it
  change. Reproduced: 12 became 13 on a single wheel event. The layout now drops focus from any
  focused number field on a wheel, rather than preventing the wheel: the page keeps scrolling
  normally, the typed value is untouched, and the arrows and keyboard still work. It's its own
  small IIFE for the same reason the suggestion script is. Nothing about a spinner is worth
  risking the delete confirmations over.
- **A run time is required on a new drill and optional on an edit, and the asymmetry is on
  purpose.** Without a time a drill contributes nothing to the plan total, which is the number a
  coach uses to decide whether practice fits the ice time, so a new one has to carry one
  (`ValidateFields(..., requireRunTime: true)` from `Create`, plus `required` on the input, which
  the view puts there only when `Model.IsNew`). Drills that pre-date the rule, and copies of them,
  have no time; demanding one before a coach can fix a typo in a title would be a tax on them for
  that. So `Update` stays optional, and `RunTime.StartTimes` and `PlanTotal` must keep handling
  nulls honestly rather than treating a blank as zero.
- **One plan can legitimately hold two links to the same video, so never key a dictionary on
  `PlanLink.Url` without grouping first.** Extraction keeps a separate card whenever the document
  names the same clip differently, because a warm-up video demonstrating three drills is three
  cards a player goes looking for by name. Both "Re-extract links" and "Replace file" preserve a
  coach's own wording by building a lookup from the edited links, and they built it with a plain
  `ToDictionary(l => l.Url, ...)`. Relabel two cards that point at one video and that throws
  `ArgumentException` on the duplicate key: HTTP 500, both buttons dead for that plan from then
  on, and nothing on screen saying why. Reproduced and fixed; both paths now go through
  `CoachController.CoachEdits`, which groups by URL first. The trade it makes is that duplicates
  can no longer be told apart, so the first edit's label is applied to every fresh card sharing
  that URL. That is a small loss of precision in a rare case, against a permanently broken button.
- **The backup download is the whole site's secrets in one file.** It carries every team's
  plaintext player code and every subscriber's email. It's gated behind the site-admin code
  and nothing else, which is the same gate as deleting a team, so that gate is now load-bearing
  in a second way. It also does *not* include uploaded PDFs or drill pictures, which are files
  on the volume rather than rows: restore a backup from before a plan was deleted and the plan
  comes back pointing at a PDF that isn't there.
- **Restoring replaces the database and then deliberately exits the process.** A fresh start is
  what reopens the file, applies any migrations the backup predates, and leaves nothing holding
  the old one. Don't "improve" this into a hot swap: `Migrate()` only runs at startup, so a hot
  swap would silently refuse every backup older than the current schema, which is exactly when
  you need a restore most.
- **The database runs in WAL mode, and a database at rest lies about it.** Nothing here sets
  `journal_mode`; EF Core's provider does. A fresh volume shows `-wal`/`-shm` and header
  `write_version` 2 before the app serves a request. But an idle file reads `write_version` 1,
  because SQLite checkpoints and removes the WAL on clean close and every `VACUUM INTO` output is
  rollback-mode by construction. I got this wrong once by testing a hand-made
  `Microsoft.Data.Sqlite` database instead of one the app creates, and nearly "fixed"
  `DeleteSidecars` to chase `-journal` files that never exist here. Check a *running* volume.
- **`Checkpoint()` throws rather than logging and carrying on, and that is the point.** In WAL mode
  the newest transactions live in the sidecar, and `Swap` moves the database aside and clears the
  sidecars, so a checkpoint that quietly failed hands back an undo copy missing the most recent
  writes. It runs before anything moves, so a throw really does mean nothing changed. **This has no
  test**: two attempts to reproduce a failing checkpoint were both invalid, because `chmod` doesn't
  stop a `File.Move` (that needs directory permission) and corrupting the file behind the app
  doesn't reach a pooled connection. Worth a real one if you touch this.
- **`Swap` refuses an incoming database from another filesystem.** `File.Move` is a rename within
  one filesystem and a copy across two. A copy can fail half-written, and the recovery path checks
  `!File.Exists(live)`, so it would skip and leave a truncated database live with the good one in
  `.replaced`. The archive restore stages the zip in ephemeral temp but extracts the *database*
  onto the volume for exactly this reason.
- **Restores extract each file to a sibling name and rename it into place.** Extracting straight
  over the target truncates it the moment it's opened, so a volume filling up mid-restore leaves a
  gutted PDF where a complete one was (measured: 36 bytes became 5, with nothing reporting it). A
  rename within a directory is atomic, and it also means a player midway through downloading a plan
  keeps reading the file they opened rather than a torn one.
- **The archive captures by allowlist, never by excluding transient names.** An exclusion list has
  to be kept in step with every `-wal`, `.replaced` and staging file, and one of those extracted
  next to a restored database corrupts it. The cost is that a genuinely new durable directory would
  be missed silently, so anything added under `DATA_DIR` must also be added to
  `VolumeBackupService.CreateAsync`.
- **Cloudflare R2 needs two AWS behaviours switched off, and fixing one alone still fails.**
  `RequestChecksumCalculation = WHEN_REQUIRED` on the client, and `UseChunkEncoding = false` on the
  upload. The second was found the hard way, by the first real production upload failing with
  `STREAMING-AWS4-HMAC-SHA256-PAYLOAD not implemented` *after* the archive had built. `AWSSDK.S3`
  is pinned to an exact version, not a `3.7.*` range, because the request shape changes inside that
  range. Don't relax it.
- **A backup failure must never take the site down.** Since .NET 6 an unhandled exception in a
  `BackgroundService` stops the host, so a transient storage outage would take the whole site
  offline because a *backup* failed. Every path in `ScheduledBackupService` is wrapped, including
  the startup catch-up, which lists the bucket and can fail as readily as an upload. This was
  confirmed accidentally in production: the failed upload above left the site serving normally.
- **Plan directories are keyed on the row id, and a restore rolls the ids back.** The directories of
  plans created since are not removed, so new plans walk back up through ids whose folder already
  holds an old `plan.pdf`. A PDF plan overwrites it; a drill plan writes nothing, and
  `PlanController.File` used to check only that a file existed. It would have streamed a previous
  plan's PDF to anyone with the team code. It now guards on `PlanKind.Pdf`.
- **Pausing writes is in-memory and per-process** (`MaintenanceState`), with a 30-minute
  deadline. Both are deliberate: a pause that survived a restart could outlive the person who
  set it, and the failure that actually hurts is a site stuck read-only with nobody left who
  knows why. A second replica would need this moved onto the volume before it meant anything.
- **SQLite sidecars belong to the filename, not the file.** `-wal` and `-shm` are named after
  the database path, so one left beside a path that later holds a *different* database gets
  replayed into it. `DatabaseBackupService.Swap` checkpoints first, then clears the sidecars of
  both the live file and the kept copy. Every file move in there has to keep that invariant.
- **Storage is capped at 1 GiB with no resize path.** Roughly 2,500 PDF-only plans. The upload
  guard and usage meter on the manage page are correctness features, not polish. Restores add to
  it: files the backup doesn't contain are left in place as invisible orphans, and every restore
  keeps one whole database as `.replaced` until it's deleted by hand. Abandoned `snapshot-*` and
  `restore-*` staging files are swept at startup, which is the one moment nothing can be holding
  them; a periodic sweep would race a download that's mid-stream.
- The real PdfPig NuGet package id is **`PdfPig`** (Apache 2.0). `UglyToad.PdfPig` on nuget.org
  is an unrelated placeholder package with a template description — don't install it.

## Things worth knowing before you change anything

- **All durable state lives under `DATA_DIR`.** Anything written elsewhere is wiped on redeploy.
- **Data Protection keys are persisted to `DATA_DIR/dpkeys`.** Remove that and every redeploy
  signs everyone out. (The "no XML encryptor configured" warning at startup is expected — the
  keys sit on a volume private to this app.)
- **`/health` is registered before `UsePathBase` and must stay cheap.** The orchestrator probes
  it every 5 seconds without the path prefix; a slow handler gets the pod restarted.
- **Uploads never go in `wwwroot`.** They stream through a controller so the team-code gate
  applies. Serving them statically would expose every plan to anyone who guesses a path.
- **Anything new and durable under `DATA_DIR` has to be added to the archive by hand.**
  `VolumeBackupService.CreateAsync` takes an explicit allowlist (the database snapshot, `dpkeys`,
  `teams/**`). A new directory is not picked up automatically, and the failure is silent until the
  day someone restores and finds it missing.
- **Every way out of the plan editor's drill picker carries an anchor, and they are not all the
  same one.** The page is long: on a phone the picker sits about 3,000px below the top, so any
  round trip that forgets its anchor dumps the coach at the title field. Adding, moving or
  removing a drill goes through `CoachController.BackToPlan` and lands on `#hp-plan-drills`, the
  plan as it now stands. Turning a page of the library, searching, and clearing a search all land
  on `#hp-drill-picker`, because those are browsing the library rather than changing the plan. The
  search is a GET form, so its anchor rides on the form's `action`: submitting replaces the query
  and leaves the fragment alone. Drop the `action` as redundant and the search silently starts
  landing at the top of the page again.
- **A failed startup migration is shown on the admin page, not just logged.** The app deliberately
  serves on rather than crash-looping, which leaves a site that looks healthy and fails on every
  write. Restoring an archive old enough to need a migration is exactly when that happens.
- **Known problems that were left alone on purpose are written down**, in
  [`docs/known-issues.md`](docs/known-issues.md), each with the condition that should bring it
  back rather than a date. Two of them are waiting on `RESEND_API_KEY` being set and want fixing
  *before* mail is switched on: publishing a plan currently blocks on sending every subscriber
  email one at a time, and unsubscribing is a destructive GET that a mail scanner can trip. That
  file also records what has already been audited and found clean, so the next pass over the code
  does not spend its time re-deriving it.

## Deploying

Deployment targets UpTurtle; see `AGENTS.md` for the platform contract and the exact tool order.
The app slug is pinned in `upturtle.yaml`. Two Dockerfiles exist:

- **`Dockerfile`** — builds everything inside the container (SDK image, `dotnet restore` and
  `publish` in-container). Correct for CI, where the build host's architecture doesn't matter.
- **`Dockerfile.fast`** — publishes natively on the host first, then assembles the image on the
  `linux/amd64` runtime base. .NET framework-dependent output is IL and carries no architecture
  of its own, so this is safe and skips QEMU entirely. Use this on an Apple Silicon Mac —
  `dotnet restore` under emulation in the plain `Dockerfile` takes 20+ minutes; this takes
  seconds. This is the one actually used for every real deploy so far.

  Two things measured on 2026-09-10, after a session followed `AGENTS.md`'s generic
  `podman build .` and got the plain `Dockerfile` by accident. First, "20+ minutes" is optimistic:
  that restore can simply never finish. An isolated run produced no output at all in 150 seconds,
  and an orphan left over from an earlier attempt had burned 9h48m of CPU still sitting in
  restore. Second, interrupting `podman build` does not stop the work. The client detaches but the
  emulated compile keeps running inside the Podman VM, so two dead builds were pegging a core each
  and starving the live one in a 2 GiB machine. If a build ever looks stuck, check with
  `podman machine ssh "ps aux | grep qemu-x86_64-static"` and kill what you find. Then use
  `Dockerfile.fast`, which is what should have happened in the first place: publish 3.7s, image
  1.9s.

```sh
# Dockerfile.fast path (recommended on an M-series Mac):
dotnet publish HockeyPractice/HockeyPractice.csproj -c Release -o ./publish
podman build --platform linux/amd64 -f Dockerfile.fast -t <image>:<tag> .
podman push <image>:<tag>

# Then, once the deploy is confirmed live, in the same sitting:
podman rmi <image>:<superseded tags>   # keep the live tag and the one before it
podman image prune -f                  # drops the intermediate layers each build leaves
```

Do that last step every time. Each image is about 250 MB and the intermediate layers are not
reused by this path, so they only accumulate: 508 images and 5.8 GB had piled up before anyone
looked, on 2026-09-10.

Local images are keyed on the UpTurtle app **id**, not the slug, so deleting an app strands its
images under an id nothing references again. This app was deleted and recreated when the trial
ended on 2026-09-08, and `upturtle.yaml` never changed because the slug did not. The 68 images
from the old id sat on disk until 2026-09-11, about 2 GB. So when sweeping, list the app ids
present and check each one still exists (`list_slots` names every app the org has); anything
unmatched is a dead app and all of its tags can go. The full sweep took the machine from 434
images and 5.8 GB to 21 and 960 MB.

### There are two apps on UpTurtle, for now

| Slug | What it is | URL |
|---|---|---|
| `hockeypractice` | Production, and the default deploy target. | `https://ebhockeyplan.com/` |
| `hockeypractice-restore` | Temporary scratch copy for rehearsing a restore. Delete when done. | `https://mbhockey.upturtle.app/hockeypractice-restore/` |

`upturtle.yaml` pins **`hockeypractice`**, so an unqualified "deploy" means production, and that is
deliberate rather than incidental: production is the common case and should not need a decision.
The restore app is deployed only when its slug is named explicitly. Each app has its own 1 GiB
volume, so their databases, uploads and `dpkeys` are unrelated.

The restore app was stood up on 2026-09-10 and is expected to be short-lived. Deleting it takes its
volume and its $5/month with it. When it goes, delete this subsection too.

**The restore app deliberately has no `ARCHIVE_S3_*` variables.** That is the safety fence, and it
is a capability the app does not have rather than a rule someone has to remember: without all five
values `S3BackupStore.IsConfigured` is false, `NullBackupStore` is registered instead, and its
upload, download and delete all throw. The scheduler sees `Enabled == false` and never starts, so
it cannot write into the bucket and retention can never prune a real archive. Confirmed in its
startup log: `Off-site backups are not configured, so nothing is scheduled.`

To rehearse a restore, download the newest archive from R2 by hand and upload the zip on the
restore app's admin page. `RestoreArchive` takes either a `key` from the bucket or an uploaded
file, and the upload branch touches the store not at all. If you ever do give that app
credentials, `ARCHIVE_S3_PREFIX` must not be `hockeypractice/`: retention keeps three, so three
nights of its backups would prune every real archive.

Its `SITE_ADMIN_CODE` is deliberately different from production's. That works because the code is
read from the environment, not the database, so it survives restoring a production archive over
the top. Restoring production data there does put real team codes, the roster and unencrypted
`dpkeys` on a second public URL, so treat that URL as production-sensitive and delete the app when
you are done, which takes its volume with it.

Source: `github.com/wingnut691983/hockeypractice`.
