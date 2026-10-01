# Security policy

## Reporting a vulnerability

Please report security issues **privately**, through GitHub's
**Security → Report a vulnerability** on this repository, not in a public
issue. Include what you found, how to reproduce it, and what an attacker could
do with it. You should hear back within a week.

Only the latest release is supported; there are no maintained release
branches.

## What is in scope

This is a local app with no network service and nothing that runs elevated.
It reads the transcript files Claude Code and Codex write under your user
profile, so the interesting boundaries are local ones:

- **The parsers.** They read files another program wrote. Anything that makes
  a crafted transcript crash the app, hang it, exhaust memory, or make it read
  or write outside the transcript folders and its own data folder is in scope.
- **What the app keeps.** Its archive and settings live as the signed-in user.
  Anything that lets another local account read them, or that makes the app
  store transcript CONTENT (prompts, code, file text) rather than the counts
  it derives, is in scope.
- **The installer and uninstaller** (`tools\Install.ps1`, `Uninstall.ps1`).
  Neither asks for, or should ever need, administrator rights.

Out of scope: anything requiring an already-elevated attacker, physical
access to an unlocked machine, or transcripts the user deliberately placed
where other accounts can write.
