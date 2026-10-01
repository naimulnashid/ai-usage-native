# Contributing

Thanks for looking. This is a personal tool shared as is, so the bar is: keep
it working for its one real use, and keep it honest about its numbers.

- **Bugs and questions:** open an issue. Please leave out your own project
  names, paths and spend figures; describe the shape of the problem instead
  (proportions, not totals). A transcript excerpt is the easiest way to leak
  code you did not mean to share.
- **Small fixes:** a pull request is welcome.
- **Anything larger:** open an issue first, so we can agree it fits before you
  spend the time.
- **Security issues:** not in a public issue; see [SECURITY.md](SECURITY.md).

## Before sending a change

```powershell
dotnet build AIUsageNative.slnx
dotnet test --project tests\UsageCore.Tests\UsageCore.Tests.csproj
```

The tests need no real transcripts: they write their fixtures at run time.
If you touch a parser, read [CLAUDE.md](CLAUDE.md) first - it records the
traps in both agents' file formats, each found on real data.

## Conventions

- **Conventional commit messages** (`feat:`, `fix:`, `docs:`), one concern per
  commit.
- **Nothing derived from real transcripts is committed** - not in code,
  comments, docs, screenshots or commit messages. `*.jsonl`, `demo-data/`,
  `out/` and `screenshots/` are gitignored as a backstop.
- **Screenshots come from the demo data only**
  (`dotnet run --project src\UsageCli -- demo-data`, then
  `tools\Capture-Readme.ps1`).
- **Keep every `.ps1` pure ASCII.** Windows PowerShell 5.1 misreads a UTF-8
  dash in a BOM-less script, and silently changes its logic.
- **Nothing runs elevated, and nothing leaves the machine.** A change that
  needs either is a design change: open an issue first.
- **No new dependencies without a reason.** The app is meant to build years
  from now from what is on disk.
