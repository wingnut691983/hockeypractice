# Practice Plans

A phone-first site for sharing youth hockey practice plans with players and their parents.

The coach uploads a practice plan PDF. Players open a link, see the drill videos to watch
before practice as tappable cards, and read the plan inline — no app, no password, no account.

## How it works

- **Getting in.** Each team has a 6-character code. The coach shares a join link
  (`/t/<team>?c=CODE`) into the team's group chat; tapping it grants access and remembers the
  device for 180 days. Players optionally tap their name from the roster once, which is what
  lets the coach see who has read each plan.
- **Uploading.** PDF only. The upload is checked by its `%PDF-` header, not its file extension.
  Word and Google Docs both export PDF in one click.
- **Video links.** At upload, links are pulled out of the PDF — both real hyperlinks and URLs
  typed as plain text — and shown as tappable cards above the document. YouTube and Vimeo links
  are rebuilt from their video id, so a URL that picked up a stray character during text
  extraction still works. The coach can relabel or hide any of them.
- **Naming them.** Labels come out of the document: a hyperlink's anchor text, else the line
  the URL sits on, else the nearest section heading above it, else "Video N". Each card also
  shows which block of practice it belongs to. See [docs/authoring-plans.md](docs/authoring-plans.md)
  for how to format a plan so this works well — it matters more than any tuning in the code.
- **Watching them.** Videos play *in place*, over the plan, with Previous/Next to move through
  the list. Links clicked *inside* the rendered PDF are caught and routed to the same player,
  so tapping one never navigates the plan away. Opening a new tab per drill leaves players stranded in a stack of tabs and they stop
  coming back to the plan. The phone's Back gesture closes the player rather than leaving the
  page, and closing stops playback. Links we can't embed still open in a new tab.
- **Draft → Publish.** Nothing reaches players and no email goes out until the coach publishes.
  Republishing after an unpublish does not re-send the email.
- **Notifications.** Parents opt in with their own address (double opt-in, one-click
  unsubscribe). Without `RESEND_API_KEY` the app logs mail instead of sending it, so the whole
  flow works before you have a sending domain.

## Running locally

```sh
SITE_ADMIN_CODE=devcode dotnet run --project HockeyPractice
```

Then open http://localhost:8080/admin, sign in with that code, and create a team. The team code
and coach code are shown once, on creation — only their hashes are stored.

Local data (SQLite, uploads, keys) goes to `.localdata/`, which is gitignored.

## Configuration

| Variable | Required | Notes |
|---|---|---|
| `SITE_ADMIN_CODE` | yes | Gates `/admin`. Fails closed — unset means no one can sign in. |
| `DATA_DIR` | no | Defaults to `/persisted-data`. Set to `../.localdata` in development. |
| `PATH_PREFIX` | no | Injected by UpTurtle. Empty locally. |
| `RESEND_API_KEY` | no | Enables real email. Without it, mail is logged. |
| `EMAIL_FROM` | no | e.g. `Bantam A <plans@yourdomain.com>`. Needs a verified domain. |

## Things worth knowing before you change anything

- **All durable state lives under `DATA_DIR`.** Anything written elsewhere is wiped on redeploy.
- **Data Protection keys are persisted to `DATA_DIR/dpkeys`.** Remove that and every redeploy
  signs out the entire team. (The "no XML encryptor configured" warning at startup is expected —
  the keys sit on a volume private to this app.)
- **`/health` is registered before `UsePathBase` and must stay cheap.** The orchestrator probes
  it every 5 seconds without the path prefix; a slow handler gets the pod restarted.
- **Uploads never go in `wwwroot`.** They stream through a controller so the team-code gate
  applies. Serving them statically would expose every plan to anyone who guesses a path.
- **Storage is capped at 1 GiB with no resize path.** Roughly 2,500 plans. The upload guard and
  the usage meter on the manage page are correctness features, not polish.
- The real PdfPig NuGet package id is **`PdfPig`** (Apache 2.0). `UglyToad.PdfPig` on nuget.org
  is an unrelated placeholder package with a template description — don't install it.

## Deploying

Deployment targets UpTurtle; see `AGENTS.md` for the platform contract and the exact tool order.
The app slug is pinned in `upturtle.yaml`.

```sh
# after getting credentials + logging in (see AGENTS.md)
podman build --platform linux/amd64 -t <image>:<tag> .
podman push <image>:<tag>
```
