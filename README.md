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

## What I'd flag

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
- **Storage is capped at 1 GiB with no resize path.** Roughly 2,500 PDF-only plans. The upload
  guard and usage meter on the manage page are correctness features, not polish.
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

```sh
# Dockerfile.fast path (recommended on an M-series Mac):
dotnet publish HockeyPractice/HockeyPractice.csproj -c Release -o ./publish
podman build --platform linux/amd64 -f Dockerfile.fast -t <image>:<tag> .
podman push <image>:<tag>
```

Source: `github.com/wingnut691983/hockeypractice`.
