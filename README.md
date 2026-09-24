# AI Usage (native)

A native Windows app that shows how much **Claude Code** and **Codex** you use:
tokens, estimated cost and approximate runtime, overall, per project and per
day. It reads the agents' own transcript files on this PC. Nothing leaves the
machine: no network calls, no server, no account.

Built with WinUI 3 on .NET 10.

## Install

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build
(Visual Studio is not needed), then:

```powershell
powershell -ExecutionPolicy Bypass -File tools\Install.ps1
```

That publishes a self-contained build to `%LOCALAPPDATA%\Programs\AI Usage`
and adds **AI Usage** to the Start menu. The installed app carries its own .NET
and Windows App SDK runtime (~190 MB), so it needs neither installed. Run the
same command again to update; `-Uninstall` removes it and keeps your data.

Windows 10 1809 or later, x64. Built and tested on Windows 11. The build is
unsigned: with Smart App Control on, Windows may block it.

## Using it

- **Sidebar**: switch between Claude Code and Codex. **Top bar**: Overview and
  Projects, Refresh (or F5), and Settings.
- **The tray icon** shows today's spend for each agent. Closing the window
  keeps the app there; right-click the icon to exit.
- **Refresh when transcripts change** is on by default: while an agent is
  writing, its numbers update at most once a minute.
- **Start at login** is in Settings and in the tray menu. It starts straight to
  the tray.
- **Project logos**: in a project's ⋯ menu choose **Set logo…**, or drag an
  image onto its card. SVG, PNG, JPG, WebP, GIF, BMP and ICO all work. Logos
  live in `%LOCALAPPDATA%\AI Usage Native\project-logos\<agent>\`, one file per
  project named after it (`My App.svg`); dropping files there works too, and
  **Import project logos…** copies in a whole folder.
- **Hide from project list** (⋯ menu) hides a project from the list only. Its
  spend still counts in every total and chart.

## Settings files

Everything the app writes is in `%LOCALAPPDATA%\AI Usage Native\`
(**Settings → Open settings folder**):

| File | What |
|---|---|
| `config\settings.json` | `localUtcOffsetHours`, `weekStartsOn` (`monday`/`sunday`/`saturday`), `maxIdleGapMinutes` |
| `config\projects.json`, `config\codex-projects.json` | `merge` a renamed folder's history into its new name; `displayNames` |
| `config\pricing.json`, `config\codex-pricing.json` | a rate card that replaces the built-in one |
| `history\` | the daily archive that keeps days after the agents delete their transcripts |

Every key is optional. Refresh after editing.

## The two cost figures mean different things

| Agent | Headline | Meaning |
|---|---|---|
| Claude Code | Total estimated spend | Tokens priced at Anthropic's per-token API rates |
| Codex | API-equivalent spend | What these tokens would cost through the OpenAI API. On a ChatGPT subscription this is not what you paid. |

Do not add the two together.

## Caveats

- **Estimates, not bills.** Rates come from the built-in rate cards; the Cost
  by model panel says when each was last verified. A model missing from the
  card is flagged as unpriced, never counted as free.
- **Runtime is approximate**: the sum of gaps between transcript lines, with
  gaps over 30 minutes treated as time away.
- **Codex fast mode** roughly doubles API rates and is not recorded in the
  transcripts, so heavy fast-mode use would cost more than shown.

## Development

```powershell
dotnet build AIUsageNative.slnx
dotnet test --project tests\UsageCore.Tests\UsageCore.Tests.csproj
dotnet run --project src\UsageCli -- parse claude     # diagnostics and totals; or codex
dotnet run --project src\UsageCli -- demo-data         # synthetic transcripts for both agents
```

`demo-data` prints three environment variables that point the app at the
invented transcripts. **Set all three**, including `AIUSAGE_DATA_DIR`, or the
invented days are merged into your real archive. `tools\Capture-Views.ps1`
screenshots any page on that data.

[CLAUDE.md](CLAUDE.md) explains how the numbers are computed and why, and the
rules the UI follows.

## Trademarks

Claude and Claude Code are trademarks of Anthropic. Codex and ChatGPT are
trademarks of OpenAI. Their marks in `src/UsageApp/Assets/AgentMarks` are the
vendors' own files, unmodified. This project is not affiliated with or endorsed
by either. Geist is © Vercel, under the SIL Open Font License
(`src/UsageApp/Assets/Fonts/OFL.txt`).
