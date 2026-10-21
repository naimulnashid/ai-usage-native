# Project site

The one-page site for people who find the repository: what the app does, all
13 of its charts drawn live, its pages, how it works, and how to install it.
Built on the **Onyx** design system.

| | |
|---|---|
| Built with | [Astro](https://astro.build) 7, static output; Geist from Fontsource, self-hosted |
| Design | Onyx tokens in `src/styles/onyx.css`, copied unchanged from the Onyx repo: refresh it there, never edit it here |
| Accent | **Claude Code's terracotta and Codex's green, by the owner's choice (2026-10-21)**, against Onyx rule 2 (no accent on a data colour). `data-agent` on `<html>` or a section picks the family (`src/styles/global.css`) |
| Icons | Lucide 1.61.0 shapes inlined in `src/components/Icon.astro`; no icon library ships |
| Charts | `src/scripts/charts.js`: drawn from a fixed seed, so the same on every load and nobody's usage. Colours, rates and rules follow the app's (`src/UsageCore/View/ModelColors.cs`, `Providers.cs`, `Pricing/*.json`, `src/UsageApp/Charts/`) |
| Screenshots | Imported from `docs/screenshots/` (`tools\Capture-Readme.ps1`); Astro resizes them, crops the first screen of each, and serves the dark or light one by theme |

`?agent=codex` opens the chart gallery on Codex.

## Build and preview

Needs Node.js 22.12 or newer (Astro's `engines`). From `site\`:

```bash
npm ci
npm run build
npm run preview
```

`npm run build` writes `dist/` (git-ignored) and then runs
`scripts/check-csp.mjs`, which fails if the page holds an inline script the
Content-Security-Policy does not allow (below). `npm run preview` serves
`dist/` at http://localhost:4321.

## Deploying on Vercel

Root Directory `site`; everything else is in `vercel.json`: the Astro build,
the security headers and when to build at all.

- **CSP.** Scripts load only from the site itself, plus the one inline theme
  snippet in `src/layouts/Base.astro` (it must run before first paint), allowed
  by its hash. Edit that snippet and the build prints the new hash to put in
  `vercel.json`. Astro is told never to inline anything else
  (`assetsInlineLimit: 0`). Styles allow `'unsafe-inline'`: the charts colour
  their shapes through style attributes.
- **A push builds only when the site could have changed**: something under
  `site/`, `docs/screenshots/`, `src/UsageApp/Assets/AgentMarks/` or the social
  card differs from the last successful deployment (`ignoreCommand`; exit 0
  skips). With no previous deployment it builds. Any other folder the site
  starts reading from must be added to that list.
- Link previews use the repository's social card (`.github/social-preview.png`),
  with absolute URLs from `VERCEL_PROJECT_PRODUCTION_URL`.
