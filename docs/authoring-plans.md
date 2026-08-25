# Writing practice plans so the site names videos well

The site reads video names straight out of your PDF. It never sees your prompt or your
source document — only the finished PDF's text and layout. So a few formatting habits are
what determine whether a player sees **"Tight Turns and Crossovers"** or **"Video 2"**.

## The one rule that matters most

**Put the video's name and its URL on the same line.**

```
Tight turns and crossovers - https://youtu.be/aBcDeFgHiJk
```

The site strips the URL off the line and uses what's left as the name. A URL sitting alone
on its own line has nothing to be named after, so it falls back to the section heading, and
failing that, to "Video 1".

This is the difference between:

| In the PDF | Card name |
|---|---|
| `Tight turns and crossovers - https://youtu.be/aBc...` | Tight turns and crossovers |
| `Watch: D-to-D reversal https://youtu.be/aBc...` | D-to-D reversal |
| `https://youtu.be/aBc...` (alone on a line) | falls back to the section heading |
| `https://youtu.be/aBc...` (no headings either) | Video 1 |

Lead-in words — *watch, see, video, link, reference* — are stripped automatically, so
"Watch: X" and "Video — X" both come out as "X". Write whichever reads naturally.

## Use section headings

Each video also shows which block of practice it belongs to. Headings are detected when a
line is numbered, noticeably larger than the body text, short and in capitals, or ends in a
colon. Numbered blocks are the most reliable:

```
2. Breakouts
   D-to-D reversal - https://youtu.be/aBcDeFgHiJk
   20 minutes, both sides
```

That produces a card reading **BREAKOUTS › D-to-D reversal**.

## Things that reduce quality

- **Bare URLs on their own line** — the single most common cause of "Video 1".
- **Link shorteners** (`bit.ly`, `tinyurl`) — the site can't tell what provider they point
  at, so it can't build a thumbnail or play them inline. Use the real YouTube or Vimeo URL.
- **Links in the header or footer** — a link far from any heading gets no section, and
  boilerplate links are hidden by default anyway.
- **Tables and multi-column layouts** — text position is how sections are inferred, and
  columns confuse it. A single column reads best on a phone regardless.

## A prompt you can paste

> When you write the practice plan, format it for a phone-first site that extracts video
> links out of the PDF:
>
> - Number each block of practice as a heading on its own line: `1. Warmup`, `2. Breakouts`,
>   `3. Small-area games`.
> - Put every video on a single line in the form `Drill name - URL`, with a short,
>   specific drill name. Never put a URL alone on its own line.
> - Use full YouTube or Vimeo URLs. No link shorteners.
> - Single column, no tables. Keep it to one or two pages.
> - Put any team boilerplate or club links in the footer, away from the drill content.

## You can always override it

Whatever the site infers, the coach panel shows every detected link with its section and an
editable name. If one reads badly, fix it there — it takes a few seconds and only has to be
done once per plan.
