# Checks left over from the September 2026 audit

Fourteen batches shipped between 18 September and 12 October 2026 as `v24` through `v44`. Almost
everything was verified locally and in production as it went. These are the checks that could not
be run from a terminal session, because each needs a **team code or the site-admin code**, which
the sessions doing the work did not have.

None of them is known to be broken. Each was verified locally against the same code, often with
before-and-after measurements. They are listed because "verified locally" is not the same claim as
"verified in production", and saying so is cheaper than pretending otherwise.

**Delete this file once you have been through it.** It exists only because the checks outlived the
work plan that generated them.

## The two worth doing first

- **Edit and save a plan, a drill, and a roster entry, then reload each and confirm the change
  stuck.** (`v41`, batch H.) `AsNoTracking` was added to read-only query paths. Its failure mode is
  a silently broken write: no error, the edit simply does not persist. Verified locally on all
  three, plus a static check that no method using it also writes.

- **Replace a team logo and confirm the new one appears immediately.** (`v39`/`v40`, batches F
  and G.) Logos are now cached `immutable` for a year, keyed on a `?v=` token derived from the
  file name. If the token were ever wrong, a replaced logo would be invisible for a year and
  nothing server-side could correct it.

## The rest

- **Open a plan as a player; confirm it appears in "who's seen it". Then open the same plan as a
  coach in preview mode and confirm it does NOT.** (`v42`, batch I.) The preview half is the easy
  one to get backwards. Measured locally: 1 view recorded with the old guard, 0 with the fix.

- **On a team with more than 20 plans, page to the last page and confirm the oldest plan is still
  reachable.** (`v43`, batch K.) Also confirm the "next practice" card still appears first, and
  that the manage page and the player list agree on what exists.

- **On a team with more than 10 past practices, confirm "Show N older practices" appears on the
  player list and reaches all of them.** (`v43`.) The cap is a preview, never a limit; if anything
  is unreachable that is a real bug.

- **Upload a picture to a drill; confirm it is stored and smaller than what you sent. Then upload a
  non-image renamed to `.png` and confirm a friendly refusal, not an error.** (`v40`, batch G.)
  A `.tiff` should now also be refused: the decoder set was narrowed to JPEG, PNG, GIF and WebP.

- **In dark mode, open a delete confirmation and a drill that is over its time, and confirm the red
  reads clearly.** (`v44`, batch L.) Then **print a plan from a phone in dark mode** and confirm
  the colours reset on paper. That is the trap `--hp-success` had to be fixed for once already.

- **Press Tab from the top of a page.** (`v44`, batch M.) The skip link should be the first stop
  and should land in the content. Confirm the current tab is announced as current, not just
  coloured.

- **Tab through the plan editor, the roster form and the admin delete confirmation**, and confirm
  every field announces a name rather than "edit text". (`v44`, batch N.)

- **Paste a plan link into iMessage, WhatsApp or Slack and look at the card.** (Shipped before the
  audit's batch O, which found the tags already live.) This is the product's core loop and the
  actual deliverable of that work, rather than the tags themselves.
