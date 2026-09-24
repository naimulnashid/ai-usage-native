# AI Usage (native)

A native Windows app that shows how much **Claude Code** and **Codex** you use:
tokens, estimated cost and approximate runtime, overall, per project and per
day. It reads the agents' own transcript files on this PC. Nothing leaves the
machine: no network calls, no server, no account.

Built with WinUI 3 on .NET 10.

## Requirements

- Windows 10 1809 or later (built and tested on Windows 11), x64.
- To build: the [.NET 10 SDK](https://dotnet.microsoft.com/download). Visual
  Studio is not needed.

The built app is self-contained: it carries its own .NET and Windows App SDK
runtime, so the PC running it needs neither installed.

## Build and run

```powershell
dotnet build src\UsageApp\UsageApp.csproj -c Release
.\src\UsageApp\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\AIUsage.exe
```

Tests:

```powershell
dotnet test --project tests\UsageCore.Tests\UsageCore.Tests.csproj
```

## The command-line tool

```powershell
dotnet run --project src\UsageCli -- parse claude     # or codex
dotnet run --project src\UsageCli -- demo-data         # synthetic transcripts
```

`parse` prints the parser's diagnostics, the per-model and per-project totals,
and folds the result into the app's archive (`--no-archive` to skip that).

`demo-data` writes invented transcripts for both agents and prints the three
environment variables that point the app at them. **Set all three**, including
`AIUSAGE_DATA_DIR`, or the invented days are merged into your real archive.

## Where things live

Everything the app writes is in `%LOCALAPPDATA%\AI Usage Native\`:
settings, project merges, rate-card overrides, the daily archive and project
logos. See [CLAUDE.md](CLAUDE.md) for the details, and for why the numbers are
computed the way they are.

## The two cost figures mean different things

| Agent | Headline | Meaning |
|---|---|---|
| Claude Code | Total estimated spend | Tokens priced at Anthropic's per-token API rates |
| Codex | API-equivalent spend | What these tokens would cost through the OpenAI API. On a ChatGPT subscription this is not what you paid. |

Do not add the two together.

## Caveats

- **Estimates, not bills.** Rates come from the built-in rate cards; each shows
  when it was last verified. A model missing from the card is flagged as
  unpriced, never counted as free.
- **Runtime is approximate**: the sum of gaps between transcript lines, with
  gaps over 30 minutes treated as time away.
- **Codex fast mode** roughly doubles API rates and is not recorded in the
  transcripts, so heavy fast-mode use would cost more than shown.

## Trademarks

Claude and Claude Code are trademarks of Anthropic. Codex and ChatGPT are
trademarks of OpenAI. This project is not affiliated with or endorsed by either.
