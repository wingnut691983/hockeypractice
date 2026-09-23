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

The three are stored differently, and the differences are deliberate rather than historical. The
**team code** is kept in plaintext beside its hash so the coach can re-share the join link all
season. The **site admin code** is never stored at all; it is read from the environment at startup.
The **manager code** is the only one held hash-only, which is why it is the only one hashed with a
slow KDF. See "What I'd flag".

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
- **Stretching a drill for one practice.** A drill carries its own run time in the library, and
  that number is what every plan using it counts. When a drill is complex enough that teaching it
  eats into the session, the plan's running order can add minutes to it: a 15 minute drill that
  takes 10 minutes to teach runs 25 in that practice, and stays 15 in the library and in every
  other plan. It also gives a length to a drill that has none, which is how a plan that was showing
  "1 drill with no time set" gets its countdown clock and its finish line back. What is stored is
  the addition, so re-timing the drill later keeps the allowance. Everyone reading the plan sees
  where the number came from: a lengthened drill reads **25 min (15 min + 10 min)** with the added
  part in green, in the running order and on the drill card, on screen and on the printed sheet. A
  drill with no library time at all just shows its total, since there is nothing to have added to.
- **Duplicating a plan.** Next week's practice is usually last week's with a few changes, so any
  plan can be copied: **Duplicate this plan** in the editor's Reuse panel, or **Duplicate** on the
  plan page. It opens a form with the title, rink, note and tags already filled in and the date
  deliberately empty, and the copy is created when that's submitted. The copy is a fresh draft with
  its own link and its own "who's read it" — publishing, changing or deleting it never touches the
  plan it came from, and nothing records that it was a copy.

  What comes across is the *materials*, not a second set of them. A drill plan references the same
  library drills, exactly as one built by hand does. A PDF plan shares the same file: plan PDFs are
  stored under the SHA-256 of their contents, so two plans holding the same document point at one
  file on the volume, and duplicating costs no storage at all. Video labels come across too,
  including the fact that a manager edited them, so re-reading names on the copy keeps their
  wording. Any minutes the plan added to a drill come across too, because they describe how long
  that drill ran in that practice rather than what the drill is. A copy of a plan uploaded *before*
  PDFs were content-addressed is the one case that spends bytes: its file is copied into the store
  once, and any further copy of it is free.
- **The plan page's actions are one partial rendered twice.** `Views/Plan/_PlanActions.cshtml`
  holds Print / Share / Duplicate / Edit (Duplicate and Edit only for a manager; Print and Share
  for everyone, since a parent printing the plan for the car is half that audience), and
  `Details.cshtml` renders it
  once under the title and once at the foot. Print and Edit used to sit at opposite ends of the
  page, so either one cost a scroll past a dozen drill cards to reach. Because it renders twice,
  the Share button is keyed on a class and the script binds every copy. An `id` there would be a
  duplicate, and `getElementById` would wire up only the first row.
- **Printing.** A **Print this plan** button on any plan opens a clean sheet at
  `/t/<team>/plans/<id>/print`, so you can see what will come out before spending the paper. Page
  one is the run sheet on its own — countdown clock, drill titles, times, and the over/under line
  — which is the page a coach actually holds on the bench, with the coach notes and the videos as
  URLs you can type rather than tap. Every drill then gets a card that is kept whole on one sheet,
  its diagrams underneath the description and capped in height so the two never land on different
  pages. A PDF plan's button reads **Print the PDF** and opens the file itself: the browser's own
  viewer paginates it better than we can.
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
SITE_ADMIN_CODE=devcode-local-only dotnet run --project HockeyPractice
```

The code has a minimum length (see Configuration below), which is why this example is not the
`devcode` it used to be: anything shorter is refused at startup and the sign-in page tells you so.

Then open http://localhost:8080/admin, sign in with that code, and create a team. The manager
code is shown once, on creation, and can't be read back later — only re-issued. The team
(player) code is stored in plain text specifically so it can be shared again later; see
[What I'd flag](#what-id-flag).

Local data (SQLite, uploads, keys) goes to `../.localdata` (one level above the project, outside
`HockeyPractice/`), which is gitignored.

## Configuration

| Variable | Required | Notes |
|---|---|---|
| `SITE_ADMIN_CODE` | yes | Gates `/admin`. Fails closed: unset means no one can sign in. **At least 12 characters**, enforced at startup, and **case-sensitive**. A shorter one is refused rather than accepted with a warning, and the sign-in page says so rather than claiming the variable is unset. |
| `DATA_DIR` | no | Defaults to `/persisted-data`. Set to `../.localdata` in development. |
| `PATH_PREFIX` | no | Injected by UpTurtle. Empty locally. |
| `RESEND_API_KEY` | no | Enables real email. Without it, mail is logged instead of sent and the signup box is hidden. The full message body is logged **only in Development**; everywhere else the log line is the subject alone. See "What I'd flag". |
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

**The off-site archive** is the whole volume in one zip: that same database, every plan PDF (in
both the content-addressed store and the older per-plan layout — the walk is recursive over
`teams/`, so neither needed adding by hand), every drill diagram, the team logos, and `dpkeys`. It runs nightly to Cloudflare R2, keeps the newest
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

- **A plan PDF is deleted only when no row still points at it, and "replaced with the same file"
  is the case that catches you out.** Since PDFs are named by their hash, duplicating a plan gives
  two rows one file, so deleting a plan can no longer just delete its PDF — `DeletePlan` and
  `ReplaceFile` both go through `CoachController.DropUnreferencedPdfAsync`, after `SaveChanges`, so
  the rows themselves answer the question and no "except this one" clause is needed. The trap is
  `ReplaceFile`: re-uploading a file that has not actually changed hashes to the key the plan
  already had, so the naive "delete the old blob if nothing references it" deletes the document the
  plan has just been pointed at. Hence the `key == keeping` guard. I reproduced it before adding the
  guard was a reflex: replace, then replace again with the identical file, then open the plan.
  Getting this wrong loses a practice plan; failing to delete only leaves bytes the storage meter
  already counts.

- **The old per-plan PDF path is still read, and must never be "cleaned up" into the store.**
  Backfilling every `teams/<team>/plans/<plan>/plan.pdf` into the hashed store looks like the
  obvious tidy-up and would break restores. A restore rolls the database back and only ever *adds*
  files, so restoring any archive taken before this change brings back rows with `PdfKey` null —
  which resolve to the legacy path. Move those files and every one of those plans is a broken page.
  Copying instead of moving would work and doubles the bytes for every existing PDF on a 1 GiB
  volume, which is why it hasn't been done. Reading both layouts costs one null check in
  `PlanStorageService.ResolvePath`. Verified end to end: an old-layout plan renders, re-extracts its
  video names, and duplicates correctly, and the duplicate's file lands in the store while the
  original's stays exactly where it was.

- **Duplicating a plan copies `PlanLink.WasEditedByCoach`, not just the label.** The flag is what
  tells a later Re-extract on the copy to keep the manager's wording. Copy the text and drop the
  flag and the copy looks right until someone presses "Re-read video names", at which point every
  name they fixed reverts — on the copy only, which is the sort of bug nobody reports accurately.

- **A new plan's date starts empty on purpose; it used to default to tomorrow at 6pm.** The default
  was convenient and wrong about as often as it was right, and a guessed date publishes exactly as
  readily as a real one — the team gets a plan for a practice that isn't happening. The field is
  `required`, but that is the browser's promise: `NewPlan`, `EditPlan` and `DuplicatePlan` all bind
  `DateTime?` and refuse null themselves. Binding it as plain `DateTime` is what made this
  dangerous before, because an empty value became year 1 and `EditPlan` assigned it straight over a
  good date, which reads on the manage list as the plan simply vanishing to the bottom.

- **There are no em dashes in the site's copy, and that is maintained on purpose.** I swept the
  lot on 15 September 2026: 39 replacements across 12 files, covering rendered text, the notices
  controllers hand back, the two subscription emails, and the placeholder glyph the drill summary
  shows for an unset run time. A colon, a comma or a full stop does the same work. The rule is
  about what a person reads, so code comments still use them freely, and `PdfTextMap` still
  *matches* them on purpose: its three regexes strip a leading or trailing dash off a label pulled
  out of a coach's PDF, and taking those characters out of the character classes would leave a
  video named "— D-to-D reversal". Checked by crawling 46 rendered pages as manager, player and
  site admin with `<script>` blocks and HTML comments stripped out first — the inline scripts do
  ship comments containing them, which is why a naive grep over the served HTML looks like a
  failure and isn't. Zero in visible copy.

- **Dark mode follows you onto paper, and it overrides the colour tokens in three separate
  places.** `prefers-color-scheme: dark` still matches while a page is printing, so a plan printed
  from a phone in dark mode came out with the drill cards on a near-black background. The print
  block resets the surface tokens (`--hp-bg`, `--hp-surface`, `--hp-surface-2`, `--hp-border`,
  `--hp-text`, `--hp-muted`) — and, separately and easy to miss, `--hp-primary-text` /
  `--hp-accent-text`, which the dark block swaps to the `-ink-dark` variants: the team colour
  *lightened* for a dark surface, which prints almost invisibly on white. Reset the first group
  and not the second and the running-order clock disappears. `--hp-success` is the third and was
  added later, when a plan's added-time note started using it as plain text: the dark block
  lightens it to `#3fbe7e` to be legible on a dark surface, and the print block puts it back to
  `#1a8a52` so it does not print pale on white. **Any token the dark block redefines has to be
  reset here too**, and that is now a rule rather than two special cases. What makes the reset win is the
  scoping — `html.hp-printpage` (0-1-1) outranks the dark block's `:root` (0-1-0) — not source
  order; keeping the block last in the file is belt and braces. Verified by forcing all five
  `prefers-color-scheme: dark` blocks to apply unconditionally and printing: all seven pages came
  out byte-identical to the light-mode render.

- **The printed diagram caps are measured, and the two-diagram one was wrong first time.** A drill
  card is held together with `break-inside: avoid`, but that is *ignored* on a box taller than a
  page — so if a diagram is not capped, the card splits and the picture ends up on a sheet of its
  own, which is the exact complaint the print view exists to fix. The first attempt allowed 84mm
  per picture on a two-diagram card; a 936-character description then filled the sheet and pushed
  both pictures onto a page with no text on it at all. 70mm holds. The one-diagram (130mm) and
  three-or-more (52mm, two across) caps were re-checked the same way and needed no change. Sized
  for **US Letter**, not A4: Letter is 18mm shorter, and it is the paper that will be in the tray.
  If you change a cap, re-print a real six-drill plan and check that no page comes out with no
  text on it — that is the signature of a split card.

  **The page margin is part of that measurement, which is why it is asymmetric.**
  `@page { margin: 12mm 16mm }`. The 12mm is vertical and load-bearing: the caps were measured
  against the 255mm of content height it leaves on Letter, so raising it silently invalidates
  all three and brings the split back. The 16mm is horizontal and had to go up from 12mm,
  because a drill card has no side margin and the print sheet drops the shell's padding, so the
  card's border sat right on the page box and some printers clipped it, giving a card with no
  border down one side. Measured by printing to PDF and finding the first inked pixel: 11.9mm
  from the paper edge before, 18.8mm after, with the 3mm of shell padding added as insurance
  against a print dialog set to "None" margins, which overrides `@page` entirely. Width carries
  no budget, because diagrams are capped on height and scale to width, so a narrower sheet makes
  them shorter and never taller. Do not tidy the two values into one.

  The other half of that rule: **never pin both dimensions of a printed diagram.** The caps keep
  `width: auto; height: auto` so `max-width` and `max-height` resolve together, and the
  three-or-more grid needs `justify-items: start`, because a grid item defaults to `stretch`,
  which hands the image a definite width. Pin both and the browser drops the aspect ratio — a
  portrait diagram comes out visibly squashed. This is the same lesson as the comment already on
  `.hp-drill-diagram`, now in a second place because the print rules are where it will be
  re-broken.

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
  One content trap came out of this: `.hp-drill-body` is `white-space: pre-wrap`, so a coach's own
  line breaks survive, and that applies to the illustration too. Written across several indented
  source lines, the drill text rendered as a ragged hanging indent with breaks mid-sentence.
  Those two lines are long on purpose. Re-wrapping them to tidy the file puts it straight back.

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
- **A plan can add minutes to a drill, and what is stored is the addition, not the answer.**
  `PlanDrill.ExtraRunTimeMinutes` is what makes a complex drill run 25 in one practice while
  staying 15 everywhere else. Storing the addition rather than the total is what makes re-timing
  the library drill safe: 15 + 10 becomes 12 + 10, not a stale 25. It hangs off `PlanDrill.Id`
  and not off the drill, because the same drill legitimately appears twice in one practice, and
  because reordering swaps `SortOrder` and leaves `Id` alone, so the time stays on the row a coach
  set it on. If the reorder is ever rewritten as delete-and-reinsert, this is what it silently
  throws away. The same column doubles as an absolute time for a drill that has none, since there
  is nothing to add to; `RunTime.Effective` is the one place that decides, and a drill that gains a
  plan-level time stops counting toward the "N drills with no time set" tail and joins the sum.
  **`DrillCard.EffectiveRunTimeMinutes` is the single definition every view goes through**, and
  reading a drill's own `RunTimeMinutes` in a view is exactly how this gets re-broken.
  `grep -rn "Drill\.RunTimeMinutes" HockeyPractice/Views` should stay empty.

  The breakdown beside it ("25 min (15 min + 10 min)") is `Views/Shared/_DrillTime.cshtml`, drawn
  in four places: the running order and the drill cards, on the plan page and on the print sheet.
  It is a partial rather than four copies because the bench sheet has to agree with the phone. The
  **caller** keeps the not-set branch, because those four places disagree about it: a dash in the
  running order, nothing at all on a drill card, "no time set" on the printed card. It shows the
  breakdown only when the drill has a library time to have been added to, so a drill timed only by
  the plan reads as a plain total instead of "(0 min + 25 min)".
- **`_DrillRow.cshtml` renders both a plan's drills and the library's, and the plan's minutes must
  never reach the library.** It is rendered from four places: the library, Copy to team, the plan
  editor's running order, and the picker directly below that running order. The reason one plan's
  teaching time cannot show up against the drill everywhere it appears is not a flag in the
  partial, it is that only the three plan-side card builders set `ExtraRunTimeMinutes` at all, so a
  library card carries none and falls back to the drill. Keep the partial context-free; a check
  like `if (isPlanRow)` in there would be the bug waiting to happen. Verified against the library
  page, the picker and the Copy to team page with an adjusted drill live in a plan.
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
- **`LoggingEmailSender` logs the message body only in Development, and it has to stay that way.**
  The body is the whole point of that sender locally: the confirm and unsubscribe links exist
  nowhere else, so there is no other way to walk the subscribe flow without a provider. It is also
  exactly why it cannot be logged anywhere else. That sender is selected whenever `RESEND_API_KEY`
  is unset, which includes **production with the key simply forgotten**, which is what production
  is today. The body carries the confirm token, the unsubscribe token and the subscriber's address
  in plain text, so anyone who could read the log could confirm subscriptions and unsubscribe
  families at will. The recipient is left out of the production line too, for the same reason
  `ResendEmailSender` never logs a provider's response body: an address is the thing worth not
  writing down. Verified both ways against a real subscribe on a Production-mode instance: the log
  line is the subject alone, and the address, both tokens and the confirm URL are all absent; the
  same run in Development still prints the clickable confirm link. The tempting change is to put
  the body back so something is easier to debug against a real deployment. Don't: send yourself a
  test subscription locally instead.

- **A plan's own drill rows need `Tags` included explicitly, and the bug hides if you test on a
  small team.** `CoachController.PlanDrillsAsync` included `Diagrams` but not `Tags`, while
  `_DrillRow` renders tag pills for both the plan's running order and the library picker below it.
  Lazy loading is off, so the pills were populated only by EF's relationship fix-up from the
  picker's query, which does include `Tags`. The result: a drill's tags showed in the running order
  only when that same drill happened to be on the page of the picker you were looking at, and
  vanished when you paged past it. Nothing about the code reads as broken, which is why it survived.
  Measured on a team padded to 22 drills so the picker pages: picker page 1 rendered **0** tag
  pills, picker page 2 rendered **29**, from the same plan on the same build. With the include, page
  1 renders 14, which is exactly the tag count on that plan's drills. If you are tempted to drop the
  second `Include` because "the tags are already loaded", they are not; they were borrowed.
  Test paging, not just a team whose whole library fits on one page.

- **Only the manager code is hashed with a slow KDF, and that is the whole point rather than an
  oversight.** `CoachCodeHash` is PBKDF2-HMAC-SHA256 with a per-row salt, stored self-describing
  as `pbkdf2$iterations$salt$hash` in the same column, which needed no migration because the
  column has no `MaxLength`. The other two codes are deliberately left alone: `ViewCode` sits in
  **plaintext** in the next column on purpose, so hashing it harder guards a secret that is
  already readable, and the site admin code has no stored hash at all. **If you ever "finish the
  job" by upgrading `ViewCodeHash`, you have done work that protects nothing.** The reason this
  mattered only for the manager code is that it is the one code an attacker holding a downloaded
  archive could not simply read, and under the old single-round unsalted SHA-256 they could have
  recovered it in well under an hour.

- **The old manager-hash branch in `Security.ManagerCodeMatches` is permanent. Do not remove it.**
  It reads like a migration shim and is not one. A restore rolls the database back while only ever
  adding files, so an archive taken before this change brings back rows in the old format, and
  that is precisely the moment a manager has to be able to sign in. Deleting the branch would turn
  a recovery into a site-wide lockout. `TryGrantAsync` upgrades such a row in place the first time
  the code is used, so a restored database heals itself.

- **Rehashing on sign-in is NOT how the existing hashes were upgraded, and could not have been.**
  `TeamController.TryGrantAsync` is the only read of `CoachCodeHash` in the app: after it matches,
  a claim goes into the access cookie and every later request authorizes from that, never
  returning to the database. That cookie lasts 180 days **and slides**, so a coach who opens the
  site most weeks may never type their code again. A rehash that only fires on verification
  therefore has no end condition. What actually replaced the stored hashes was reissuing every
  team's manager code from the admin page. If a similar change comes up again, check where the
  value is read before assuming sign-ins will carry a migration.

- **The admin sign-in limiter is partitioned per browser, and that is what stops an attacker
  locking the operator out.** It used to be one shared bucket: thirty posts a minute to
  `/admin/login` from anyone at all, and the person who needs to get in and lift a stuck
  maintenance pause meets a 429 every time. I reproduced it before changing anything, and then
  again afterwards to be sure the fix was the thing that mattered: under the old code an attacker
  sending 35 attempts left the real admin, typing the correct code, with **429**; under the new
  one the attacker exhausts only their own bucket after 10 and the admin signs in normally.
  The partition key is a cookie the sign-in page sets. **It is not a credential**, grants nothing
  and is never checked against anything, so do not be tempted to "secure" it. An attacker can
  fetch a fresh one and get a fresh bucket, which means this deliberately does not cap total
  attempts from the internet, and it cannot: without a trustworthy client identity any single
  shared budget is one an attacker can exhaust, and the gateway does not reliably pass
  `X-Forwarded-For`, which is the same reason team code entry partitions on the slug.
  What caps guessing now is the code's own length, enforced at startup rather than assumed.
  **The two halves only work as a pair, so do not relax either on its own.**

- **The site admin code keeps its case; team codes do not.** `Security.HashCode` upper-cases,
  which is right for a team code (uppercase alphabet by construction, and a fifteen-year-old
  reading one off a phone screen should not be punished for the shift key) and wrong for an
  operator's passphrase, where it silently discarded close to a bit per letter. `HashSecret` is
  the case-preserving one and only the admin code uses it. This needed no migration and never
  will: the admin hash is derived from the environment variable at startup and compared against a
  hash of what was just typed, so both sides move together on the next boot. **The manager codes
  are the opposite case** and are why the wider hashing work is still open: those hashes are
  stored, come back from archives, and cannot be changed without a dual-read window longer than
  the archive retention.

- **A filename read back out of the database is checked before it becomes a path, and the drill
  diagram helper is the one place that doesn't do this.** `DataPaths.PlanOverview` validates the
  name against the exact shape we write (`overview-` plus 32 lower-case hex plus `.webp`) and
  throws otherwise, the way `TeamPdf` has always checked its key. `DataPaths.DrillDiagram`
  concatenates its `FileName` column into a path unchecked; it predates the rule rather than
  disagreeing with it, so copy `PlanOverview` for the next file type, not its neighbour.
  This is not theoretical tidiness: a restore rolls rows back while only ever adding files, so a
  row can arrive from an archive describing a file this deployment never wrote. I checked it with
  four hand-edited rows, including `../../../../../../etc/passwd` and a name differing only in
  extension: all four return a clean 404 with nothing in the log. The check sits in
  `PlanStorageService.OverviewExists`, **before** the path is built, which is what makes the guard
  fail closed instead of throwing a 500 out of a page render.

## Things worth knowing before you change anything

- **The print sheet is a separate layout and deliberately records nothing.**
  `Views/Plan/Print.cshtml` runs under `_PrintLayout`, which has no team header, no tab bar, no
  footer and none of `_Layout`'s four script blocks — so a banner or script added to `_Layout`
  will not appear there. That is the intent, but check whether it should. It also fires **no view
  beacon**: opening a print preview is not reading the plan, and because `PlanView` is unique on
  `(PlanId, PlayerId)` a beacon here would not double-count, it would falsely *first*-count, and a
  player who printed without reading would show as seen on the coach's roster.

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
  day someone restores and finds it missing. The one exception is a directory *inside* `teams/`,
  which the recursive walk already covers — `teams/<id>/pdfs/` needed no change when plan PDFs
  moved there, and I checked a real archive rather than assuming it.
- **A plan can carry one overview picture, and it lives in the plan's own directory.**
  `teams/<team>/plans/<planId>/overview-<guid>.webp`, with the name on `PracticePlan.OverviewFileName`
  and the stored size on `OverviewBytes`. It shows on the plan between the running order and the
  drills, and on sheet 1 of the printout, for a practice run as simultaneous stations that no
  running order can describe. Drill plans only, guarded in both the upload and the serve action:
  a PDF plan is already a document, and letting one carry an overview would leave a row claiming
  one kind while holding the other's content.
  Per the rule above I checked rather than assumed: it is inside `teams/`, so the archive's
  recursive walk already captures it, `PlanStorageService.DeletePlan` already removes it with the
  plan's directory, and `DataPaths.UsedBytes()` already counts it. **No backup, delete or quota
  code changed.** Images only, because it renders inline; a PDF is refused with a message that
  says so rather than the drill path's "needs to be a PDF or an image".

- **Plan PDFs live under the hash of their contents, in two layouts.** A plan's file is
  `teams/<team>/pdfs/<sha256>.pdf`, named by what is in it, and `PracticePlan.PdfKey` holds that
  name. Two plans holding the same document — what duplicating a plan produces — share one file,
  and neither knows about the other. A null `PdfKey` means the older layout,
  `teams/<team>/plans/<plan>/plan.pdf`, keyed on the row id; both are read, only the store is
  written. `PlanStorageService.ResolvePath` is the single place that decides which, so nothing
  else should be building either path.
- **Every way out of the plan editor's drill picker carries an anchor, and they are not all the
  same one.** The page is long: on a phone the picker sits about 3,000px below the top, so any
  round trip that forgets its anchor dumps the coach at the title field. Adding, moving or
  removing a drill goes through `CoachController.BackToPlan` and lands on `#hp-plan-drills`, the
  plan as it now stands. Turning a page of the library, searching, and clearing a search all land
  on `#hp-drill-picker`, because those are browsing the library rather than changing the plan. The
  search is a GET form, so its anchor rides on the form's `action`: submitting replaces the query
  and leaves the fragment alone. Drop the `action` as redundant and the search silently starts
  landing at the top of the page again.

  **One path deliberately has no anchor, and it looks like an oversight.** A refused drill time
  (`CoachController.DrillTimeRefused`) redirects without `#hp-plan-drills` on purpose, because the
  notice renders at the *top* of the editor and an anchored redirect scrolls straight past the only
  reason the coach is back on the page. Make it consistent with its neighbours and every one of
  those messages becomes invisible. It is also why those messages name the drill in quotes: landing
  at the top is what costs the coach sight of the row they were editing.
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

### Which app this repo deploys to

| Slug | App id | URL |
|---|---|---|
| `hockeypractice` | `fa9fc989-ece2-4a32-97a9-11241eb35d4c` | `https://ebhockeyplan.com/` |

`upturtle.yaml` pins that slug, so an unqualified "deploy" means production, and that is deliberate
rather than incidental: production is the common case and should not need a decision.

There is a second app on this UpTurtle account, **`statslogic`**, which is a different project
living in `~/Documents/git/statslogic_ut`. It is also ASP.NET Core 8 on SQLite with a `publish/`
directory and a `Dockerfile.fast`, so a build run from the wrong directory produces a valid image
of the wrong site, and neither the registry nor the provisioner would object. The check that
catches it is the app id: the image reference `get_registry_credentials` hands back is
`package.upturtle.com/<app id>/app`, and that id must match the one above before anything is
pushed. Do it every time; it is one tool call. The account-wide rules live in `~/.claude/CLAUDE.md`.

The two apps have separate 1 GiB volumes, so their databases, uploads and `dpkeys` are unrelated.
They do share the R2 bucket `ebhpbackup`, kept apart only by `ARCHIVE_S3_PREFIX`
(`hockeypractice/` here, `statslogic/` there). Retention keeps three archives per prefix, so an
app pointed at the other's prefix would delete the other site's backups in three nights. Never
copy that variable between apps.

### If you stand up a second app from this repo

There was one, `hockeypractice-restore`, created on 2026-09-10 to rehearse a restore and deleted
once that was done. Confirmed gone on 2026-09-11: `get_application_status` returns null for the
slug, `list_slots` shows only the two apps above, and the only trace left was a set of local
podman images under its orphaned app id.

If another one is ever needed, the one thing to carry forward is this: **give it no `ARCHIVE_S3_*`
variables at all.** That is a capability it does not have rather than a rule someone has to
remember. Missing any one of the four values makes `S3BackupStore.IsConfigured` false, so
`NullBackupStore` is registered and its upload, download and delete all throw, and the scheduler
sees `Enabled == false` and never starts. It cannot reach the bucket, so its retention can never
prune a real archive. The restore app confirmed this in its startup log: `Off-site backups are not
configured, so nothing is scheduled.`

To rehearse a restore on such an app, download the newest archive from R2 by hand and upload the
zip on its admin page. `RestoreArchive` takes either a `key` from the bucket or an uploaded file,
and the upload branch touches the store not at all. Give it a `SITE_ADMIN_CODE` of its own, which
works because the code is read from the environment rather than the database and so survives
restoring a production archive over the top. Restoring production data onto a second public URL
puts real team codes, the roster and unencrypted `dpkeys` there, so treat that URL as
production-sensitive and delete the app when you are done, which takes its volume with it.

Source: `github.com/wingnut691983/hockeypractice`.
