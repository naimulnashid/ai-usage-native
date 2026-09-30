# CLAUDE.md — architecture and traps

Read this before changing a parser or a view. It records what cannot be inferred
from the code: why the numbers are computed the way they are, and which parts of
the UI are rules rather than taste. Written for Claude Code sessions and human
contributors alike.

## What this is

A **native Windows app** (WinUI 3, C# on .NET 10) that reads two coding agents'
own JSONL transcripts and reports token use, estimated cost and approximate
runtime — globally, per project and per day. Local only: no network calls, no
listener, no database. Numbers are recomputed from disk on every refresh.

| Agent | Transcripts | Accent | Cost figure |
|---|---|---|---|
| Claude Code | `%USERPROFILE%\.claude\projects\` (`CLAUDE_CONFIG_DIR`) | terracotta `#D97757` | estimate at per-token rates |
| Codex | `%USERPROFILE%\.codex\sessions\` (`CODEX_HOME`) | teal-green `#10A37F` | **API-equivalent**, not necessarily a charge |

**Do not blur that last column.** Codex is commonly used on a flat ChatGPT
subscription, so its figure only answers "what would these tokens cost through
the OpenAI API". Its headline reads **"API-equivalent spend"** where Claude
Code's reads "Total estimated spend" — that label, from
`ProviderMeta.CostLabel`, is the entire in-app treatment. Never hard-code
either wording, never present the two as comparable spend, never add them.

This project is a standalone port of a Next.js dashboard. It shares no code or
files with it; the knowledge below was carried over and re-verified here.

## Layout

```
src/UsageCore/           Parsers, pricing, archive, view math. net10.0, no UI.
  Model/Types.cs           The one report shape both parsers emit.
  Parsing/Guards.cs        What a malformed line can and cannot do. Day keys.
  Parsing/LineReading.cs   Byte lines and a forward-only JSON scan. Read before
                           touching how a file is read - see "Memory" below.
  Parsing/UsageMath.cs     Cells, buckets, daily rollup - shared by both parsers.
  Parsing/ClaudeParser.cs  Claude Code: 4 traps.
  Parsing/CodexParser.cs   Codex: 5 traps.
  Parsing/ReportShaping.cs Records, activity, empty-project hiding.
  History/HistoryArchive.cs  The daily archive that outlives deleted transcripts.
  Config/AppConfig.cs      Rate cards, settings, merge rules. All fail soft.
  Pricing/*.json           Built-in rate cards (embedded). Overridable.
  View/                    Formatting, model colours, day ranges, heat map, logos.
  Providers.cs             Everything per-agent that is not a number.
  AppPaths.cs              Where the app reads and writes.
src/UsageCli/            `aiusage parse <agent>` and `aiusage demo-data`.
src/UsageApp/            The WinUI 3 app.
  Program.cs               Entry point: single instance (AppInstance redirect).
  MainWindow.xaml.cs       Shell: title bar, rail, top bar, page host, navigation.
  Theme/Palette.cs         Colour tokens for both themes; every brush swapped in place.
  Theme/Ui.cs              Element builders: text, cards, panels, tips, buttons, motion.
  Charts/                  Hand-drawn charts on XAML shapes (see "Charts").
  Controls/                FitGrid, DataTable, CountUp, score icons, logos, range picker.
  Views/                   The four pages, built in code from the builders.
  State/                   AppState, tray, watcher, logos, hidden projects, start at login.
  Imaging/ImageLoader.cs   SVG through Svg.Skia; raster files as they are.
  Assets/                  Geist (OFL), the vendors' marks, the app icon.
tests/UsageCore.Tests/   xUnit v3; fixtures written at run time.
tools/Capture-Window.ps1 Launch the app and screenshot its window (optionally hovering).
tools/Capture-Views.ps1  Screenshot any page/section on the demo data.
tools/Install.ps1        Publish and install for this user: Start menu shortcut, Installed apps entry.
tools/Uninstall.ps1      The uninstall, shared by Install.ps1 -Uninstall and the Installed apps entry.
tools/make-icon.cs       Regenerate Assets/app.ico from Assets/app-icon.svg.
```

## Where the app keeps its state

Everything it writes lives in `%LOCALAPPDATA%\AI Usage Native\`:

| Path | What |
|---|---|
| `config\settings.json` | day offset, week start, idle cutoff (all optional) |
| `config\projects.json`, `config\codex-projects.json` | merges and display names |
| `config\pricing.json`, `config\codex-pricing.json` | a rate card that REPLACES the built-in one |
| `project-logos\claude\`, `project-logos\codex\` | one image per project, named after it |
| `hidden-projects.json`, `codex-hidden-projects.json` | projects hidden from the Projects list (ids only) |
| `model-settings.json`, `codex-model-settings.json` | prices and colours set from the Model prices table |
| `project-colors.json`, `codex-project-colors.json` | colours chosen for projects (ids and `#RRGGBB` only) |
| `app-settings.json` | the app's own preferences: rail, auto-refresh, close to tray, zoom, theme |
| `history\claude-history.json`, `history\codex-history.json` | the archive |

**`AIUSAGE_DATA_DIR` moves all of it, and anything reading demo transcripts
must set it.** Without it synthetic days are folded into the real archive, and
the archive keeps whichever copy of a day has more messages — a fabricated day
can permanently replace a real one. The archive is the one thing here that does
not rebuild itself from disk. `aiusage demo-data` prints the variable with the
rest of the setup for exactly this reason.

## Claude Code's data model, and its four traps

One JSONL file per session under `projects\<encoded-cwd>\<session-id>.jsonl`;
usage on `type: "assistant"` lines under `message.usage`. **The schema is
undocumented and changes between versions**: skip unparseable lines, skip
unreadable files, record a warning, keep going. Never assume a field exists.

**Trap 1 — the same message is written many times (~49% of lines).**
Streaming partials (one line per update, same `message.id` and `requestId`) and
session replay (resuming copies the earlier conversation into the new file,
sometimes in another project's directory). Counting every line overstates cost
by ~89%. De-duplicate **globally** on `(message.id, requestId)`, across all
files, walking files oldest-first so a replay is credited to the original.

This is what costs the memory, and one pass cannot avoid it: nothing can be
attributed until every file's keys are known.

**Trap 2 — `output_tokens` placeholders are recoverable.** Early partials carry
1–4; the final line carries the truth. Keep the **maximum** per key. The
residue (~0.2% of messages, almost all Sonnet thinking blocks logged as `2`)
moves the total by under a thousandth of a percent. **The UI shows no warning
about it, deliberately**: the only accurate fix would send transcript content
off the machine, a local estimate would swap a known-tiny error for an
unknown-sized one, and a permanent banner trains readers to ignore banners.
Detection stays in the parser (`SuspiciousMessageCount`, printed by the CLI) so
a logging change would be noticed.

**Trap 3 — subagent transcripts are nested** at
`<session-id>\subagents\agent-*.jsonl`. They are real, separately billed usage
with zero overlap. `Discover` recurses; keep it that way. (Claude Code's own
cleanup appears not to recurse there either, so these accumulate.)

**Trap 4 — cache writes are split by TTL and priced differently.** Prefer
`cache_creation.ephemeral_5m/1h_input_tokens`; the flat legacy
`cache_creation_input_tokens` is assumed 5m (the default TTL).

**Project names come from `cwd`, chosen by re-encoding.** The encoded
directory name is lossy (`[^A-Za-z0-9]` → `-`), so it cannot be decoded — but
it can be applied forwards. The first `cwd` in a file is NOT the answer: a
resumed session replays the old project's path into the new project's file.
`PickProjectCwd` keeps only paths that re-encode to the directory's own name;
ties (`My App` vs `My_App`) go to the spelling on the most lines, and
`displayNames` pins a preference.

**A directory can hold files yet contribute nothing** (pure replay). Such
projects are dropped and listed in `EmptyProjectsHidden`. The test is strict:
any token, message or second of runtime keeps a project.

## Codex's data model, and its five traps

One JSONL rollout per thread under `sessions\YYYY\MM\DD\`. The filename carries
LOCAL time; every timestamp inside is UTC. Usage is on `event_msg` lines with
`payload.type == "token_count"`.

**Trap 1 — usage is CUMULATIVE, and a reading gets repeated.** Treat
`total_token_usage` as authoritative and take per-turn usage as its **delta**;
a repeat then contributes zero. Summing `last_token_usage` runs ~0.4% high.
`last_token_usage` is still read as an independent check: a file whose deltas
disagree with it counts in `ReconcileFailures`. **That counter is how you find
out the schema moved** — watch it rather than the totals.

**Trap 2 — `input_tokens` already INCLUDES cached tokens** (~98% of the
prompt). `Input` carries only the uncached remainder. Codex's Input column
therefore looks tiny next to Cached — correct.

**Trap 3 — `reasoning_output_tokens` is INSIDE `output_tokens`.** Carried as
`Reasoning` for display, excluded from totals and cost. Codex's token table
does not sum across for this reason.

**Trap 4 — auto-review threads are separate files and real spend**
(`thread_source: "subagent"`, model `codex-auto-review`; roughly 9% of cost on
the reference data). Counted, as their own band; the rate card aliases the
model to `gpt-5.3-codex` for pricing only.

**Trap 5 — the model is a state.** `token_count` carries none; it comes from
the most recent `turn_context`, written only when it changes. Same for `cwd`,
so projects are attributed per event.

A running total that goes backwards (a context reset) is a new baseline, never
negative usage (`CounterResets`). Codex has no cache-write rate
(`HasCacheWrites = false` hides the column) and no placeholder bug.

## Shared rules

- **Reasoning null vs zero.** A Claude Code cell's `Reasoning` is null — "not
  reported". A `0` would claim it was measured. Do not tidy it into a default.
- **Records count chats, not transcripts.** "Peak tokens" and "Longest chat"
  fold each subagent into its parent: tokens and cost summed, runtime the
  parent's own (a subagent runs inside the parent's clock). An orphan stands
  alone.
- **Runtime is an approximation, and the UI says so.** Sum of gaps between
  consecutive lines, attributed to the current model, excluding gaps over
  `maxIdleGapMinutes` (30). First-to-last overstates ~5.8x; `SpanSeconds` keeps
  it for comparison only.
- **Day buckets** use `localUtcOffsetHours` (default: the machine's current
  offset, read once per parse). The undated bucket `(unknown date)` is usage
  but not a day: it sorts last and never enters a window.
- **Merges never change the grand total**, only the grouping.
- **Unpriced is never free.** A model missing from the rate card is flagged
  and shown, and can be given a price in the app (below).
- **`<synthetic>` is not counted** (`ClaudeParser.SyntheticModel`). Claude
  Code writes it for a turn that made no API call: no tokens, no cost. It is
  not a message, not a model, and not the current model for runtime - the gaps
  either side stay with the model that was working. Cost and tokens did not
  move at all when this changed; about 0.2% of messages and 0.05% of runtime
  (a gap before any real model in its session) went. Archives written before
  it drop the cell on load and take its messages out of the combined count, so
  the merge still compares like for like.
- **Custom rates** (`Config/ModelSettings.cs`): a rate set in the app's Model
  prices table is laid over whichever card is in use, for that model only,
  and wins until reset. Not written into `config\pricing.json`, because a card
  there REPLACES the built-in one wholesale - pricing one new model that way
  would freeze every other rate. `UsageService.Load` prices the parse and the
  archive with it and fills `UsageReport.ModelRates` for the table.
- **Sonnet 5 is $2/$10 permanently.** Cached vendor docs may still say $3/$15.

## The archive (`HistoryArchive`)

Both agents delete their own transcripts (Claude Code after
`cleanupPeriodDays`, 30 by default). A deleted transcript silently removes a
day, and a shrinking total reads as reduced spend. Every parse folds its days
into a per-agent archive and the report is rebuilt from it.

- **Numbers only.** Tokens, cost, runtime, project ids, names and paths.
- **One-directional merge.** A day is replaced only when the fresh parse has
  at least as many messages: today can grow, a thinned day cannot overwrite.
- **Fails soft.** Unreadable file → empty archive plus a warning; unreadable
  days are dropped one by one (`SanitizeDays`); the write is atomic.
- **Not archived:** hour histogram and sessions. Peak hour, session counts and
  the two records reflect live transcripts only - for the agent and for each
  project's own Activity cards, whose day-derived stats the archive refreshes.
- **Re-priced on the way out.** `Apply` takes the parse's pricing and prices
  every stored day again (the archive keeps token counts). Otherwise a rate set
  in the app would fix live days and leave archived ones at $0. A model with no
  rate now keeps what was stored; the file itself is not rewritten.

## Verifying a parser change

1. `dotnet test --project tests\UsageCore.Tests\UsageCore.Tests.csproj`. Each
   trap has a test named after it.
2. `aiusage demo-data`, set the three variables it prints, then
   `aiusage parse claude` / `aiusage parse codex`. The demo must show
   duplicates skipped and output recovered, and
   **`files reconciled: N ok / 0 mismatched`** for Codex.
3. `aiusage parse <agent> --no-archive` on real data, compared with a baseline
   you keep locally. If a number moves, understand why before shipping.

When this port was written, both parsers reproduced the original dashboard's
output **exactly** — cost to six decimals, tokens, messages, runtime, active
days, de-duplication counts, both records, peak hour — on the demo tree and on
real data, for both agents.

## Memory: why transcripts are scanned as bytes

The first port read each line with `ReadLine` and parsed it with
`JsonDocument`. The numbers were right; the memory was not. Transcript lines
can be megabytes long (a tool result is written into its line whole), and
`JsonDocument` rents its buffers from the shared array pool, which keeps the
largest ones for the life of the process. On real data a full parse peaked at
689 MB and **still held ~430 MB after it had finished and been collected** -
unacceptable for an app that lives in the tray.

`LineReading.cs` replaced it: lines are split as bytes out of one buffer per
file, and `Utf8JsonReader` pulls out only the fields a parser uses, skipping
everything else - message bodies included - in place. Peak fell to ~160 MB,
nothing is retained afterwards, and the parse is ~1.7x faster. Results were
re-verified identical to the original dashboard on real data.

Rules that follow from it:

- **Do not reintroduce `JsonDocument` or `JsonNode` on the per-line path.**
  (The archive still uses `JsonNode`: one small file, read once.)
- **A field read must consume its value.** Every `Json.*` helper leaves the
  reader past the value whatever its type; a hand-written read that forgets to
  `Skip()` a nested value desynchronises the rest of the line.
- **Invalid JSON anywhere in a line throws**, including inside a skipped value
  and trailing content after the root (`Json.End`). That is what makes such a
  line "unparseable", exactly as `JsonDocument.Parse` would have.
- Repeated strings (models, working directories) go through the per-parse
  `StringPool`, since a record is held per line until the parse ends.

After each refresh the app forces a compacting collection, and while it sits
in the tray it drops the built page entirely (`MainWindow.EnterTray`): idle in
the tray it holds ~175 MB, which is the WinUI runtime's own floor.

## The app

### One design system, built in code

The pages are assembled in C# from the builders in `Theme/Ui.cs`, not from
XAML templates - the equivalent of the original's shared CSS classes, so a
card, a panel head or an info tip exists in exactly one definition. Sizes are
the original's CSS pixels as effective pixels, one for one.

- **Colours come from `Palette`, never literals.** Every brush is a shared
  instance whose `Color` is swapped in place - by `Palette.ApplyProvider` for
  the agent and `Palette.ApplyTheme` for the theme - which is how switching
  either re-themes the shell without rebuilding it. The page is rebuilt after a
  theme change anyway, for the few colours read as values.
- **Geist is bundled** (`Assets/Fonts`, OFL) and loaded as
  `ms-appx:///Assets/Fonts/Geist-Variable.ttf#Geist`. An absolute file path
  silently falls back to Segoe UI.
- **The headline glow** is a composition drop shadow cut from the text's own
  alpha mask (`Ui.Glow`) - XAML has no text-shadow.
- **Motion** (the rise-in, count-ups, chart growth) is skipped when Windows
  has animations turned off (`Motion.Enabled`).

### Two themes

Dark (the default) and Light, or the Windows setting, from **Settings ->
Theme**; saved as `Theme` in `app-settings.json`, and "system" keeps
following Windows (`UISettings.ColorValuesChanged`). It matches the web
dashboard's light theme token for token.

- **Light is its own design**: white cards on a grey page, a border and a
  shadow on each, accents darkened to carry text at AA on white
  (`ProviderMeta.AccentLight`), hover going darker where the dark theme's goes
  brighter, and a heat map that DARKENS with spend - so the heat map's subtitle
  says "Darker" there and "Brighter" in the dark theme.
- **Model shades are data**, stored as their dark palette hex, so they cannot
  follow the theme on their own. `ModelColors.LightPalettes` holds a twin per
  palette index and `Palette.ModelColor` swaps via `ModelColors.Themed`.
- **WinUI's own controls** follow `Root.RequestedTheme`; the caption buttons
  are recoloured by hand (`ApplyControlTheme`). `App.xaml` still says Dark,
  which only sets the default an element-level theme overrides.
- **OpenAI's mark is white**, invisible on the light rail, so there it is drawn
  black through a Skia colour filter (`ImageLoader.RenderSvg(black:)`) - the
  vendor's own black variant of the same single-colour mark. The file is
  untouched.
- **The headline glow is dark-theme only**; on white it is a smudge.

### Shadows need a receiver (light theme)

`Ui.Elevate` gives every card a `ThemeShadow` at rest (Z 8) and raises it on
hover (24 for a panel, 40 for a card that is a link). **A ThemeShadow in this
window draws nothing without a receiver** - measured, the first version's cards
came out flat - and a receiver may not be an ancestor of the caster. The shell
therefore puts a plain grid behind the page's scroller (`Ui.ShadowReceiver`)
for every card's shadow to fall on. Z is set outright, never through a
`TranslationTransition`, which WinUI refuses beside the cards' RenderTransform.

### Project colours

The charts split by project draw each in its own colour
(`UsageCore.View.ProjectColors`, `State/ProjectColorStore`): the one chosen
with **Change colour…** (WinUI's `ColorPicker`, hex field on), else the logo's
dominant colour, else a fallback handed out in the report's order. Logo and
fallback colours are fitted to luminance 0.12-0.29, which clears 3:1 on both
themes' panels; a chosen colour is used as chosen.

- **The logo's background is its outer BAND, a few pixels deep**, not its
  outermost ring: an app-icon tile has a 1px border round a fill of another
  colour, and reading only the ring called the border the background and let
  the black fill win (two real logos came out grey in the web version).
  Coloured pixels win over grey ones, and a plain glyph on a coloured tile
  returns the tile.
- Logos are read at 64px through Skia (`ImageLoader.Pixels`, straight RGBA),
  cached per file and last-write time.
- The donut's slices and legend rows open their project; "Others" does not.

### Daily spend by project

After the heat map on the overview (`OverviewPage.ProjectPanel`): stacked
areas on the monotone curve (`ProjectAreaChart`), largest project at the
bottom, the top eight over the days shown plus Other - which also takes every
hidden project, as the donut does. Range 30 / 90 / all
(`RangePicker.ProjectRanges`). The areas are painted back to front, each from
its cumulative top down to the baseline, which leaves no seams between bands;
fills are mixed toward the panel rather than translucent, since stacked
translucent layers would darken where they overlap. The legend is centred chips
(`CenteredWrapPanel`) of colour, logo and name only, by the owner's choice.

### Charts are hand-drawn

`Charts/` draws on XAML shapes rather than using a chart library, because the
details that carry meaning are the ones a library fights: a band's shade
encoding its price, a legend in its share's own denominator, a hovered donut
slice fading the rest and the legend in step.

- **Axis ticks use Recharts' algorithm** (`ChartKit.NiceTicks`), so they land
  on the same round values the original drew ($0/$3/$6/$9/$12, 0/650K/1.30M).
- **The curve is d3's monotone-X**, which never overshoots the data.
- **Edge labels are nudged inward, not dropped** (`ClampCentre`): the last
  day is the one worth naming.
- **Tooltips are popups**, so the scroller cannot clip them.

### WinUI traps met while building it

- **A keyboard accelerator shows its key as a tooltip.** The F5 refresh
  accelerator lives on the window's root, so "F5" appeared over any spot with
  no tooltip of its own - the title bar included. The root sets
  `KeyboardAcceleratorPlacementMode.Hidden`; add accelerators the same way.

- **`Border` is sealed.** Components that are "a border with behaviour" are
  factories (`ProjectLogoView.Create`), not subclasses.
- **An element cannot have both a `RenderTransform` and a
  `TranslationTransition`** - it throws "Access denied" the moment the second
  is set. The rise-in and the card hover lift therefore share one
  `TranslateTransform`, animated by storyboards.
- **`CornerRadius(999)` is not "fully round".** CSS clamps an oversized
  radius; WinUI does not, and draws pointed ends. Use half the height.
- **An unpackaged app still needs `<EnableMsixTooling>true</EnableMsixTooling>`**
  to publish. Without it `dotnet publish` leaves out the app's own
  `resources.pri` and compiled XAML, and the published build dies at startup
  with `0xC000027B` inside `Microsoft.UI.Xaml.dll` - while the Debug build, run
  from `bin\`, works perfectly. `tools/Install.ps1` refuses a publish without
  `AIUsage.pri` for this reason. Test the *published* copy after build changes.
- **A lambda parameter named `_` next to a named one is not a discard**:
  `(_, e) => { _ = Task(); }` assigns to the parameter.
- **`dotnet test` on .NET 10 needs `global.json`'s
  `"test": { "runner": "Microsoft.Testing.Platform" }`**, and must run from the
  repo folder to see it.
- **A `ScrollViewer` centres a `MaxWidth` column by its DESIRED width**, not
  the viewport's. A page narrower than its cap (the Projects page) drifted
  sideways away from the top bar as the window grew. The column now sits in a
  frame `Grid` whose width is pinned to the viewport in `SizeChanged`.
- **`TextBlock` selection is off by default**, so nothing could be copied.
  `Ui.Text` turns it on; chrome (tabs, buttons, rail, chart axes) passes
  `selectable: false` or goes through `Ui.NoSelect`, since a selectable label
  inside a button swallows the press.
- **A selectable `TextBlock` marks `Tapped` handled.** A project card's tap
  handler is registered with `AddHandler(..., handledEventsToo: true)`, or a
  click on the project's name does nothing.
- **The Windows App SDK meta-package pulls in the AI/ML components** (~60 MB of
  onnxruntime and DirectML). The app references WinUI, Foundation,
  InteractiveExperiences and DWrite directly. For the same reason the tray
  uses `H.NotifyIcon` (core), not `H.NotifyIcon.WinUI`, which depends on the
  1.x meta-package.

### Project pages mirror the overview

A project page is the overview at project scope - Daily combined spend, Cost
by model, the Activity cards, Daily activity, the two stacked charts, Token
detail by model - with the same names, then what only a project has: its
day-by-model table. By the owner's choice it leaves out the cards' third row
(`ScoreCards.Build(showWhen: false)`), Model prices, the sessions table and
the combined daily table, and its stacked charts open on All days
(`StackedPanel(initial:)`) where the overview's open on 30. The per-model card grid
("Breakdown by model") is gone from both; Cost by model shows the same numbers.
`OverviewPage.CostPanel` / `TokenPanel` / `ActivityPanel` are shared, and the
heat map's Expand opens `ActivityPage` scoped to the project. Expand is always shown, on
both pages, by the owner's choice (2026-09-30); it used to appear only once
some day was older than the six-month strip.

### Model prices and colours

The last panel on the overview. Each row's **Edit** / **Set price** button opens a
flyout: the rates (five for Claude Code, three for Codex), "Cache rates from
input" (1.25x / 2x / 0.1x - a button, not a default, since cards do not always
follow it), reset, and the colour. A price saves and refreshes the agent; a
colour saves, updates `Palette`'s overrides and redraws, no parse. Colours are
limited to `ModelColors.Palettes`, the agent's own ramp: every shade clears 3:1
and every default is one of them. Flyouts are popups, so `PrintWindow` does not
capture them - check with UI Automation (every field and swatch is named).

### Page zoom

Ctrl+Plus / Ctrl+Minus / Ctrl+0 and Ctrl+wheel, 50% to 200% in Chrome's steps
(`Theme/Zoom.cs`), saved in `app-settings.json`. Everything below the title bar
zooms; the title bar does not, as a browser's chrome does not.

- **It reflows, like a browser's zoom** (`Controls/ZoomBox.cs`): the page is laid
  out as if the window were `1/zoom` as wide, then scaled up. WinUI has no
  LayoutTransform, so the panel is one - measure and arrange in unscaled units,
  scale as a RenderTransform. A plain scale, or the ScrollViewer's own pinch
  zoom, would magnify the page into a sideways scroll instead.
- **Anything sized from the WINDOW must divide by `Zoom.Level`.** The headline's
  "vw" sizing is the one case. And the headline total is now fitted to its own
  column's width (the three columns are fixed star shares), because the older
  "grid minus a reserve" form over-promised on a narrow window - which zoom makes
  common - and ellipsised the total ("$86....").
- **Popups are outside the tree and do not zoom.** Chart tooltips scale their
  own card (`ChartTooltip.Show`); flyouts, menus and drop-downs stay at 100%.
- **Ctrl+wheel reads the Ctrl key's state as well as `e.KeyModifiers`**, and is
  registered with `handledEventsToo` on the root. The keys are root
  accelerators, whose tooltip is hidden like F5's.
- **Nothing may re-lay out the page on every frame of an animation.** The
  rail's width animation used to, and it was only smooth by luck: at 100% on a
  wide screen the page sits at its max width, so a rail change just shifts it.
  Zoomed (or in any narrower window) the column's width follows the rail, so
  every frame rebuilt the charts - measured, 220ms of motion came out as 4-5
  frames of 60-140ms each at 110%, against ~50 frames of 4ms at 100%. The page
  frame now keeps its width while the rail moves (`_railMoving`) and is laid
  out once when it stops: ~50 frames of 4ms at 110% and 150%, then one ~50ms
  frame for that single re-layout. Measure frame times with
  `CompositionTarget.Rendering` before and after any change like it.
- **Testing it with synthetic input:** send Ctrl-down, wait, wheel, wait,
  Ctrl-up. Sent back to back, the wheel arrived with no modifier and the test
  failed for a reason no person would meet.

### Native-only features

- **Tray** (`TrayHost`): today's spend per agent in the tooltip, Codex's
  marked "API-equiv." Closing the window hides it to the tray unless that is
  turned off in Settings.
- **Refresh when transcripts change** (`TranscriptWatcher`): throttled per
  agent - the first change is picked up after a few seconds, and while changes
  keep coming an agent is re-parsed at most once a minute. A live session
  writes every few seconds, so "wait for quiet" would never fire.
- **Start at login**: the per-user Run key, launching with `--tray`. Off until
  turned on. Not tested end to end on the author's machine by automation (it
  writes the user's startup configuration); check it by hand after changes.
- **Single instance** (`Program.cs`): a second launch hands off to the running
  copy and exits. **One instance per data folder**, not per machine: a copy with
  its own `AIUSAGE_DATA_DIR` (the demo, a screenshot run) registers a key
  derived from that path, so it starts beside the copy in the tray instead of
  silently handing its launch - and its demo environment - to the real one.
- **Project logos**: watched folders, so a new file appears within a second;
  Set logo / Remove logo / drag-and-drop onto a card / Import. Every SVG goes
  through **Svg.Skia** - WinUI's SvgImageSource supports a subset (no text,
  filters or `<style>`) and draws nothing for the rest.

### Checking the UI

`tools/Capture-Views.ps1` launches the app on `./demo-data` (with its own
`AIUSAGE_DATA_DIR`) and screenshots any page at any scroll offset, through the
development-only `AIUSAGE_DEBUG_VIEW=agent,page[,projectId],scroll`:

```powershell
& tools\Capture-Views.ps1 -Views @('top=claude,overview,0', 'cards=codex,projects,700', 'hover=claude,overview,0@367:902')
```

- `@x:y` hovers at a point (physical pixels from the window's top-left) before
  capturing. The window is made topmost for that capture: Windows will not
  give focus to a background launch, so the pointer would otherwise land on
  whatever is in front. Hovering uses `SendInput`; `SetCursorPos` alone
  produces no pointer events.
- The capture uses `PrintWindow`, which does not include menus and flyouts
  (they are separate popup windows). Check those through UI Automation - every
  control has an automation name.
- Screenshots go to `screenshots/`, which is gitignored.
- The capture is cropped to the client area. The window rectangle also holds
  Windows 11's invisible resize borders, which `PrintWindow` draws as a dark
  frame; the app draws its own title bar, so the client area is all of it.

**Full pages** (`-FullPage 1440`) come from `AIUSAGE_DEBUG_FULLPAGE=<width>`:
the app grows its window to the page's whole height, so one capture holds the
page top to bottom *with the rail beside it* - a stitch of scrolled captures
would cut the rail off after the first screen. Two things make it work:

- **Windows caps a window at about the screen's size** through
  `WM_GETMINMAXINFO`, silently. The debug hook subclasses the window proc and
  raises `ptMaxTrackSize` before resizing, or the "full page" comes out one
  screen tall.
- **The resize lands through window messages**, so the page is re-measured on
  a timer after each resize, until it stops growing (at most six passes).
  `Capture-Window.ps1 -AppSized` then waits until the window has held the same
  size for two seconds.

`PrintWindow` with `PW_RENDERFULLCONTENT` captures the parts of the window
below the screen's edge too. Measured: a 6,900 px overview at 150% came out
whole.

### Where this deliberately differs from the original

- The heat map's subtitle says **"Brighter means a more expensive day."** in
  the dark theme and "Darker" in the light one, each true of its own ramp. The
  original said "Darker" in both until it gained a light theme as well; it now
  does the same as this.
- No password, cookie, CSP or LAN mode: there is no listener to protect.
- Loading placeholders are approximate; the original's pixel-measured
  skeletons existed to stop a browser layout jumping.

## Installing

```powershell
powershell -ExecutionPolicy Bypass -File tools\Install.ps1             # publish + install for this user
powershell -ExecutionPolicy Bypass -File tools\Install.ps1 -Uninstall  # remove (keeps your data)
```

The build is self-contained (~190 MB: .NET and the Windows App SDK travel
with it) and unsigned. It runs with Smart App Control off; with it on, an
unsigned build may be blocked.

**It is listed under Installed apps**, per user, through
`HKCU\...\CurrentVersion\Uninstall\AIUsage` (no admin rights). That list only
shows what has such an entry, which is why the app used to be missing from it:
the script copies files and makes a shortcut, and Windows knows nothing else.
Three things about the entry:

- **Its uninstall command runs `Uninstall.ps1` copied into the program
  folder**, not the repo's, so it keeps working after the repo moves or is
  deleted. That copy is about to delete its own folder, so it first copies
  itself to `%TEMP%` and hands over; the temp copy is removed afterwards.
- **`Install.ps1 -Uninstall` runs the same script** (from `tools\`), so there
  is one uninstall, not two that drift.
- **Never name a variable `$uninstall` in `Install.ps1`.** PowerShell names are
  case-insensitive, so it IS the `-Uninstall` switch, and assigning a string to
  it fails the install halfway - after the old copy was already removed.

Verified 2026-09-30: `winget list` shows the entry; running its exact
`UninstallString` closed the running app and removed the folder, shortcut and
entry, with every data file kept and no temp copy left; `Install.ps1
-Uninstall` did the same.

## Publishing

The repo is public; `main` is what people see. Four things are generated rather
than drawn, so they can be regenerated rather than go stale:

| Asset | Made by | Notes |
|---|---|---|
| `docs/screenshots/*.webp` | `tools\Capture-Readme.ps1` | Full pages at 1440 DIPs on fresh demo data, lossy WebP (`tools/to-webp.cs`). Re-run when a page changes shape. |
| `.github/social-preview.png` | `dotnet run tools/make-social-preview.cs` | 1280x640. **GitHub has no API for it**: upload it in Settings -> General -> Social preview. No vendor logos on it, by design (see the script's header). |
| The release zip | `tools\Package-Release.ps1` | Self-contained publish, zipped. Test the zip itself before uploading: unzip it and run `AIUsage.exe`. |
| `app.ico` | `dotnet run tools/make-icon.cs` | From `app-icon.svg`. |

The file-based scripts (`*.cs`) take SkiaSharp through `Svg.Skia`, which the
app already depends on, so the tooling needs nothing but the .NET SDK. Geist is
a variable font: a weight is chosen with
`SKTypeface.Clone([new SKFontVariationPositionCoordinate { Axis = SKFourByteTag.Parse("wght"), Value = 600 }])`.

**Releases**: bump `<Version>` in `Directory.Build.props`, commit, run
`Package-Release.ps1`, then `gh release create v<version>` with the zip. The
build is unsigned; the release notes say what SmartScreen will show.

### Code signing: not done yet, deliberately deferred

Looked into on 2026-09-24 and parked. Check prices and eligibility again
before acting; both change. What was established:

- **Signing does not by itself remove the SmartScreen warning.** SmartScreen
  goes by reputation. Since 2024 even EV certificates no longer skip it
  straight away; a signed app still warns until enough people have downloaded
  and run it. Signing gives that reputation somewhere to build up. Unsigned,
  it never does.
- **Smart App Control is where signing helps at once.** It blocks unsigned apps
  it does not recognise outright and allows a valid signature from a trusted
  certificate authority. Today the zip does not run at all with it on.
- **Sign our own binaries, not just the exe**: `AIUsage.exe`, `AIUsage.dll`
  and `UsageCore.dll`. The .NET and Windows App SDK files in the publish are
  already Microsoft-signed.
- **A self-signed certificate is useless** for other people's PCs.

| Option | Cost | Publisher shown | Notes |
|---|---|---|---|
| **SignPath Foundation** | Free for open source | "SignPath Foundation" | The favourite. Suits a public MIT project, but applications are reviewed and releases must be built in GitHub Actions, not locally. |
| **Microsoft Store** | Free individual developer account | Microsoft signs it | No SmartScreen warning at all, plus auto-update. Needs MSIX packaging, and start at login moved from the Run key to a packaged `StartupTask`. |
| **Paid certificate** (Certum's open-source certificate is the cheapest; Sectigo and DigiCert cost more) | Tens to hundreds of dollars a year | The owner's name | Key must live on a hardware token or a cloud HSM. The warning stays until reputation builds. |
| **Azure Trusted Signing** | About $10 a month | The owner's name | Individual sign-up was limited to a few countries (US and Canada) when checked; confirm first. |

Whichever is chosen, the groundwork is the same: a GitHub Actions release
workflow that runs `Package-Release.ps1`'s publish, signs the binaries, then
zips and uploads. Once signed, drop the SmartScreen paragraph from the README's
Download section and from the release notes.

## Privacy

This repo is public, and the rule is the same as the original's: nothing
derived from real transcripts is committed. `*.jsonl`, `demo-data/`, `out/` and
`screenshots/` are gitignored as a backstop. Tests write fixtures at run time.
No real project names, paths or spend figures in code, comments, docs or
commit messages — proportions, not totals.

## Git

Conventional commits, authored by the repo owner alone — **never a
`Co-Authored-By` trailer**.
