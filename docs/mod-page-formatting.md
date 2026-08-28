# Updating the mod pages

## The markup, first

Each page takes a DIFFERENT markup language, and they are not interchangeable. Paste the wrong
one and it renders as visible tag soup rather than failing quietly.

| file | site | markup | accepts |
|---|---|---|---|
| `nexus-description.txt` | Nexus Mods | **BBCode** | `[b]…[/b]` square brackets. **No Markdown. No HTML.** |
| `7daystodiemods-description.md` | 7daystodiemods.com | **Markdown + inline HTML** | `**bold**`, and raw `<span>` where Markdown has no equivalent |
| `7daystodiemods-description.html` | 7daystodiemods.com | **HTML fragment** | the same text as the `.md`, pre-rendered. No `<html>`/`<body>` wrapper |

So: **Nexus is BBCode, 7daystodiemods is Markdown (or HTML).** Everything below is the detail of
each.

These are hand-maintained copies of the same description, not generated from each other, so a
change to one is a change nobody has made to the others — see *Keeping them in sync* at the
bottom. The `.md` and `.html` hold the same text; keep whichever the site editor accepts on the
day and re-render the other rather than editing them independently.

## Nexus — BBCode

Nexus does not accept Markdown or HTML. Everything is BBCode.

This is the complete set of tags **verified in use** on the current page — counted out of
`nexus-description.txt`, not recalled. Matching them keeps the page visually consistent. Other
BBCode tags may well work on Nexus; nothing here has tested them, so treat anything not on this
list as unverified and preview it before publishing.

- `[b]…[/b]` bold, `[i]…[/i]` italic
- `[size=5]…[/size]` for section headings, `[size=4]…[/size]` for a sub-heading
- `[color=#ffffff]…[/color]` for a heading, `[color=#ff0000]…[/color]` for a warning
- `[line]` for a horizontal rule between sections
- `[url=https://…]text[/url]`
- `[list]` … `[/list]` with each item wrapped as `[*]item[/*]`
- `[code]…[/code]` for a config snippet

Two things that catch people out:

- **List items are closed.** Nexus writes `[*]text[/*]`, not the bare `[*]text` that most BBCode
  dialects use. An unclosed item renders as one run-on line.
- **Colour is per-run, not per-heading.** A heading is `[color=…][size=5][b]Title[/b][/size][/color]`
  — all four tags, in that nesting. Dropping the colour makes that heading a different shade from
  every other one on the page.

## 7daystodiemods — Markdown with inline HTML

Standard Markdown, with two site-specific habits:

- **Colour has no Markdown, so it is inline HTML**: `<span style="color: rgb(239, 68, 68);">…</span>`.
  The page uses a small fixed set — red `rgb(239, 68, 68)` for warnings, orange
  `rgb(249, 115, 22)` for links, amber `rgb(245, 158, 11)` and yellow `rgb(234, 179, 8)` for
  emphasis. Reuse those rather than picking new ones.
- **Section titles are bold paragraphs, not `#` headings** (`**Requirements**`), with `---` rules
  between sections. That is a deliberate look; `#` headings render much larger there.

The `.html` form is the same document with `<p>`, `<strong>`, `<em>`, `<ul>/<li>` and `<a href>`.
It has no `<html>`/`<body>` wrapper — it is a fragment pasted into the site's editor.

## What has to change on a release

- The **version list** under Requirements — game builds the release was tested against.
- The **keybind list**, if any default moved.
- The **"not thoroughly tested"** warning, if the tested set changed.
- Anything in the CHANGELOG worth a player's attention. The page is not a changelog; it describes
  the mod as it now behaves.

`ModInfo.xml` is NOT one of these — its version must stay parseable by `System.Version`
(see the comment in that file).

## Keeping them in sync

They have drifted before: a word-level comparison on 2026-08-28 found 74 words present only in the
Nexus copy and 144 only in the 7daystodiemods copy. Neither was wrong; they had each been edited
alone.

After changing any of them, diff the prose (ignoring markup) against the others:

```bash
python - <<'PY'
import re, html, collections
def words(p):
    t = open(p, encoding='utf-8', errors='replace').read()
    t = re.sub(r'\[/?[^\]]+\]', '', t)     # bbcode
    t = re.sub(r'<[^>]+>', '', t)          # html
    t = re.sub(r'[*_#`\-]', '', t)         # markdown marks
    return collections.Counter(re.findall(r"[A-Za-z0-9']+", html.unescape(t).lower()))
a = words('docs/nexus-description.txt')
b = words('docs/7daystodiemods-description.md')
print('only on Nexus :', ' '.join(sorted(set((a-b).elements()))[:40]))
print('only on 7dtdm :', ' '.join(sorted(set((b-a).elements()))[:40]))
PY
```

URLs will always differ — Markdown carries them as link text, BBCode inside `[url=…]` — so ignore
bare domain words in the output.
