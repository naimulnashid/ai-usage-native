<#
.SYNOPSIS
  Builds the release download: a self-contained x64 build, zipped.

.DESCRIPTION
  The same publish as Install.ps1, into artifacts\ (gitignored), then
  AIUsage-<version>-win-x64.zip beside it. The version is Directory.Build.props'.
  Unzip anywhere and run AIUsage.exe: it carries its own .NET and Windows App
  SDK runtime.

  Refuses a build without AIUsage.pri, which would crash at startup - see
  EnableMsixTooling in CLAUDE.md.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

$out = Join-Path $root 'artifacts'
$staging = Join-Path $out 'AI Usage'
$zip = Join-Path $out "AIUsage-$version-win-x64.zip"
Remove-Item -Recurse -Force $staging, $zip -ErrorAction SilentlyContinue

& dotnet publish (Join-Path $root 'src\UsageApp\UsageApp.csproj') -c Release -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
if (-not (Test-Path (Join-Path $staging 'AIUsage.pri'))) {
  throw 'The published build has no AIUsage.pri, so it would crash at startup. Check EnableMsixTooling in UsageApp.csproj.'
}

# The folder inside the zip is "AI Usage", so unzipping gives one tidy folder.
Compress-Archive -Path $staging -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output ("{0}  {1:N0} MB  sha256 {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $hash)
