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
tests/UsageCore.Tests/   xUnit v3; fixtures written at run time.
tools/Capture-Window.ps1 Launch the app and screenshot its window.
```

## Where the app keeps its state

Everything it writes lives in `%LOCALAPPDATA%\AI Usage Native\`:

| Path | What |
|---|---|
| `config\settings.json` | day offset, week start, idle cutoff (all optional) |
| `config\projects.json`, `config\codex-projects.json` | merges and display names |
| `config\pricing.json`, `config\codex-pricing.json` | a rate card that REPLACES the built-in one |
| `history\claude-history.json`, `history\codex-history.json` | the archive |
| `project-logos\claude\`, `project-logos\codex\` | one image per project |

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
  and shown; `<synthetic>` is priced at zero on purpose.
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
  the two records reflect live transcripts only.

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

## Privacy

This repo is private, but the rule is the same as the original's: nothing
derived from real transcripts is committed. `*.jsonl`, `demo-data/`, `out/` and
`screenshots/` are gitignored as a backstop. Tests write fixtures at run time.
No real project names, paths or spend figures in code, comments, docs or
commit messages — proportions, not totals.

## Git

Conventional commits, authored by the repo owner alone — **never a
`Co-Authored-By` trailer**.
