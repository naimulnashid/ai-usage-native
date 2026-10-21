<#
.SYNOPSIS
  Builds the release downloads: a per-user setup.exe and a portable zip, both
  of the same self-contained x64 build.

.DESCRIPTION
  The same publish as Install.ps1, into artifacts\ (gitignored), then beside it:

    AIUsage-<version>-win-x64-setup.exe  installs for this user, no admin
                                         rights (installer\AIUsage.iss)
    AIUsage-<version>-win-x64.zip        unzip anywhere and run AIUsage.exe

  The version is Directory.Build.props'. Both carry their own .NET and Windows
  App SDK runtime.

  Needs Inno Setup 6 for the setup.exe:
    winget install --id JRSoftware.InnoSetup -e --scope user

  Refuses a build without AIUsage.pri, which would crash at startup - see
  EnableMsixTooling in src\UsageApp\UsageApp.csproj.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

# Found before the publish, so a missing tool fails in seconds, not minutes.
$iscc = @(
  (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
  (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
  (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { throw 'Inno Setup 6 is not installed. Install it with: winget install --id JRSoftware.InnoSetup -e --scope user' }

$out = Join-Path $root 'artifacts'
$staging = Join-Path $out 'AI Usage'
$zip = Join-Path $out "AIUsage-$version-win-x64.zip"
$setup = Join-Path $out "AIUsage-$version-win-x64-setup.exe"
Remove-Item -Recurse -Force $staging, $zip, $setup -ErrorAction SilentlyContinue

& dotnet publish (Join-Path $root 'src\UsageApp\UsageApp.csproj') -c Release -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
if (-not (Test-Path (Join-Path $staging 'AIUsage.pri'))) {
  throw 'The published build has no AIUsage.pri, so it would crash at startup. Check EnableMsixTooling in UsageApp.csproj.'
}

& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$staging" "/O$out" (Join-Path $root 'installer\AIUsage.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $setup)) { throw "Inno Setup did not write $setup" }

# The folder inside the zip is "AI Usage", so unzipping gives one tidy folder.
Compress-Archive -Path $staging -DestinationPath $zip -CompressionLevel Optimal

foreach ($file in $setup, $zip) {
  $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
  Write-Output ("{0}  {1:N0} MB  sha256 {2}" -f $file, ((Get-Item $file).Length / 1MB), $hash)
}
