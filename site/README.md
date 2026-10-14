# Project site

A one-page site that shows what this app is and does, for people who find
the repository: what it reads, how the two parsers work, every page of the
app, every chart it draws, and every feature.

Plain HTML, CSS and one script, with no framework and no dependencies. The
charts are drawn in the browser from a fixed random seed, so they are the
same on every load and are nobody's usage; their colours, rates and rules
follow the app's own (`src/UsageCore/View/ModelColors.cs`, `Providers.cs`,
`Pricing/*.json`, `src/UsageApp/Charts/`). The app screenshots come from
`docs/screenshots/`, which `tools\Capture-Readme.ps1` regenerates in both
themes from one demo run; the page shows the set matching its own theme, and
the build copies them in rather than keeping a second set here. It copies
`.github/social-preview.png` (the repository's social card) in the same way,
as the link preview image; the preview tags use absolute URLs to the deployed
address, since link unfurlers need them.

**Phones get the desktop layout, zoomed to fit**: the viewport tag is
`width=1024` with no initial scale, so a phone lays the page out 1024px wide
and scales it down. Desktop browsers ignore the tag. The narrow-width CSS
still serves a narrow desktop window.

## Build and preview

```bash
node site/build.mjs
```

writes the site to `site/dist/` (git-ignored). Serve that folder with any
static server to look at it, for example:

```bash
python -m http.server 7953 --bind 127.0.0.1 --directory site/dist
```

## Deploying on Vercel

Import the repository and set **Root Directory** to `site`. Everything else
comes from `site/vercel.json`: no framework, no install step, the build
command and output folder, and the security headers. Vercel's build can read
files outside the root directory, which is how the screenshots get in.

**A push builds only when the site could have changed**: something under
`site/`, `docs/screenshots/` or the social card differs from the last
successful deployment (`ignoreCommand`; exit 0 skips, anything else builds). With no previous
deployment, or one whose commit is not in Vercel's clone, the command fails
and the build runs, so it errs towards building. Any other folder the site
starts reading from must be added to that path list.

`?agent=codex` opens the chart gallery on Codex instead of Claude Code.
