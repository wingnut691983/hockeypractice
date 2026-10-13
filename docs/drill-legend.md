# Drill diagram legend and rink

The reference the drawing editor (`HockeyPractice/wwwroot/js/drill-sketch.js`) was built against.
Kept because the legend image itself is not in the repo.

## The legend

The coach supplied a standard drills legend on 12 October 2026. The image is kept locally at
`docs/plans/reference/drill-legend.png` and is **gitignored on purpose**: the repo is public, and
the legend appears to come from a published hockey book, so it is not ours to republish. This is
the committed record of what it shows. If the image is missing, work from this.

**Paths** (all drawn left to right in the legend):

| Symbol | What it looks like | Default end |
|---|---|---|
| Forward skating | A plain solid line | Arrow |
| Backward skating | A row of small scallops: shallow arcs joined end to end, like a chain of cups, along the path | Arrow |
| Skating with the puck | A smooth sine wave along the path | Arrow |
| Pass / path of puck | A dashed line | Arrow |
| Shot | Two parallel lines close together, ending in one open, hollow arrowhead (like ⇒) | Hollow double arrow |
| Sidestep | Short vertical ticks, evenly spaced, **with no line through them** | None |
| Cross-overs | A row of small X marks, **with no line joining them** | None |

**Stamps:**

| Symbol | What it looks like |
|---|---|
| Pivot | A small standalone glyph: a short stem with a tight loop curling back on itself, like a cursive "ℓ" |
| Turn | A short hooked arrow: a line that curls back over itself, then ends in an arrowhead (like ↪) |
| Stop | Two short vertical bars, `||` |
| Player | A small `X` |
| Opponent | A small hollow circle, `O` |
| Forward, Center, Winger, Defenseman, Goaltender | The plain capital letters `F`, `C`, `W`, `D`, `G` |
| Coach | A circled C, `©` |
| Puck | A small solid black dot |
| Stick | A hockey stick drawn like a check mark: a long shaft and a short blade at an angle |
| Cone | A small solid black triangle |
| Net | A top-down net: a shallow U-shaped arc for the back of the net, with short posts at the open ends |

This settles two earlier questions. **Pivot and turn are standalone glyphs** in the legend, not
shapes on a path, so they stay stamps that rotate. **Stop is both:** the legend draws it alone, so
it is a stamp, and the `||` end cap stays too, because "skate here, then stop" is the common case.

## The rink

Drawn in feet to NHL dimensions, checked on 13 October 2026 against a labelled diagram the coach
supplied: 200 x 85, 28 ft corners, goal lines 11 ft from the end boards, blue lines 64 ft beyond
them (75 ft from the boards) and 50 ft apart, 15 ft circles, end-zone dots 20 ft out from the goal
line and 44 ft apart, neutral-zone dots 5 ft inside the blue lines, 6 ft nets. The crease is the
NHL shape: a 6 ft arc cut by straight sides 4 ft either side of centre, so 8 ft wide.

**The coach chose a solid red line and no referee crease.** Both were tried on 13 October and
taken out the same day, so do not add them back as a "fix". The coach's labelled diagram is not
drawn to its own scale (its goal line measures about 18 ft out), so compare against its numbers,
not its pixels.
