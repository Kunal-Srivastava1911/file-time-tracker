<#
.SYNOPSIS
    Builds, tests and packages the Employee File-Time Tracker (design.md).

.DESCRIPTION
    Stages:
      restore/build  the four projects
      test           the xUnit suite (design.md §8)
      publish        agent, watchdog, aggregator and the tray app as framework-dependent
                     single-file x64 exes
      msi            the per-machine MSI (design.md §6)
      bundle         the single setup .exe, chaining the .NET 8 Desktop Runtime

    Publishing single-file keeps the installed footprint to two executables, which in turn keeps
    the §6 Defender exclusion scoped to a folder holding nothing else. Framework-dependent rather
    than self-contained: the bundle chains the .NET 8 Desktop Runtime, which is a ~60 MB download
    once per machine instead of ~150 MB carried in every build. (Single-file compression is not
    available for framework-dependent publishes, so it is not requested here.)

.EXAMPLE
    ./build.ps1                     # build + test + publish
    ./build.ps1 -Package            # also produce the MSI and setup .exe
    ./build.ps1 -Configuration Debug -SkipTests
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipTests,

    # Build the MSI and the bundle. Requires the WiX v4 tool: dotnet tool install --global wix --version 4.0.5
    [switch]$Package,

    # Authenticode signing (§6 upgrade path: removes the need for the Defender exclusion).
    [string]$SigningCertThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publishRoot = Join-Path $artifacts 'publish'
$runtimeVersion = '8.0.11'
$runtimeUrl = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$runtimeVersion/windowsdesktop-runtime-$runtimeVersion-win-x64.exe"

# The SDK may be a user-scope install that is not on PATH.
$localDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if ((Test-Path (Join-Path $localDotnet 'dotnet.exe')) -and ($env:PATH -notlike "*$localDotnet*")) {
    $env:PATH = "$localDotnet;$env:PATH"
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)

    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan

    # Out-Host, not the pipeline: otherwise every line of dotnet's output becomes part of the
    # calling function's return value.
    & $Action | Out-Host

    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

function Publish-App {
    param([string]$Project, [string]$OutputName)

    $out = Join-Path $publishRoot $OutputName
    Invoke-Step "publish $OutputName" {
        dotnet publish (Join-Path $root $Project) `
            -c $Configuration `
            -r win-x64 `
            --self-contained false `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=none `
            -o $out `
            --nologo
    }

    return $out
}

function Invoke-Sign {
    param([string[]]$Paths)

    if (-not $SigningCertThumbprint) { return }

    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*\x64\*' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if (-not $signtool) {
        Write-Warning 'signtool.exe was not found; skipping Authenticode signing.'
        return
    }

    foreach ($path in $Paths) {
        Write-Host "  signing $path"
        & $signtool.FullName sign /sha1 $SigningCertThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $path
        if ($LASTEXITCODE -ne 0) { throw "Signing '$path' failed." }
    }
}

# ---- build -------------------------------------------------------------------------------

Invoke-Step 'restore' { dotnet restore (Join-Path $root 'FileTimeTracker.sln') --nologo }
Invoke-Step 'build'   { dotnet build (Join-Path $root 'FileTimeTracker.sln') -c $Configuration --no-restore --nologo -v minimal }

if (-not $SkipTests) {
    Invoke-Step 'test' {
        dotnet test (Join-Path $root 'tests\FileTimeTracker.Tests\FileTimeTracker.Tests.csproj') `
            -c $Configuration --no-build --nologo -v minimal
    }
}

# ---- publish -----------------------------------------------------------------------------

if (Test-Path $publishRoot) { Remove-Item $publishRoot -Recurse -Force }

$agentOut      = Publish-App 'src\FileTimeTracker.Agent\FileTimeTracker.Agent.csproj' 'agent'
$watchdogOut   = Publish-App 'src\FileTimeTracker.Watchdog\FileTimeTracker.Watchdog.csproj' 'watchdog'
$aggregatorOut = Publish-App 'src\FileTimeTracker.Aggregator\FileTimeTracker.Aggregator.csproj' 'aggregator'

# The desktop app belongs here too, even though the MSI does not install it. It was published by
# hand once and then went stale while the build kept reporting success, so a code change reached
# the repo but not the shortcut anyone actually clicks. Worse, the wipe above removes whatever is
# in the publish folder, so leaving this out does not merely skip the tray — it deletes it.
$trayOut       = Publish-App 'src\FileTimeTracker.Tray\FileTimeTracker.Tray.csproj' 'tray'

# The MSI installs the agent and the watchdog from one folder.
Copy-Item (Join-Path $watchdogOut 'FileTimeTracker.Watchdog.exe') $agentOut -Force

Invoke-Sign @(
    (Join-Path $agentOut 'FileTimeTracker.Agent.exe'),
    (Join-Path $agentOut 'FileTimeTracker.Watchdog.exe'),
    (Join-Path $aggregatorOut 'FileTimeTracker.Aggregator.exe'),
    (Join-Path $trayOut 'FileTimeTracker.Tray.exe')
)

Write-Host ''
Write-Host 'Published:' -ForegroundColor Green
Get-ChildItem $publishRoot -Recurse -Filter '*.exe' | ForEach-Object {
    '  {0,-42} {1,8:N0} KB' -f $_.Name, ($_.Length / 1KB)
}

if (-not $Package) {
    Write-Host ''
    Write-Host 'Done. Re-run with -Package to build the MSI and setup .exe.' -ForegroundColor Green
    return
}

# ---- package -----------------------------------------------------------------------------

if (-not (Get-Command 'wix' -ErrorAction SilentlyContinue)) {
    throw 'The WiX v4 tool was not found. Install it with: dotnet tool install --global wix --version 4.0.5'
}

$msiOut = Join-Path $artifacts 'msi'
New-Item -ItemType Directory -Force $msiOut | Out-Null

Invoke-Step 'msi' {
    dotnet build (Join-Path $root 'installer\FileTimeTracker.Installer\FileTimeTracker.Installer.wixproj') `
        -c $Configuration --nologo -v minimal `
        -p:OutputPath="$msiOut\" `
        -p:PublishRoot="$publishRoot\"
}

Invoke-Sign @((Join-Path $msiOut 'FileTimeTracker.msi'))

# Burn needs the real prerequisite locally so it can compute the payload hash; it is still
# downloaded at install time rather than embedded, which keeps the setup .exe small.
$prereq = Join-Path $artifacts 'prereq'
New-Item -ItemType Directory -Force $prereq | Out-Null
$runtimeExe = Join-Path $prereq "windowsdesktop-runtime-$runtimeVersion-win-x64.exe"

if (-not (Test-Path $runtimeExe)) {
    Write-Host ''
    Write-Host "==> downloading .NET Desktop Runtime $runtimeVersion" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $runtimeUrl -OutFile $runtimeExe -UseBasicParsing
}

$bundleOut = Join-Path $artifacts 'bundle'
New-Item -ItemType Directory -Force $bundleOut | Out-Null

Invoke-Step 'bundle' {
    dotnet build (Join-Path $root 'installer\FileTimeTracker.Bundle\FileTimeTracker.Bundle.wixproj') `
        -c $Configuration --nologo -v minimal `
        -p:OutputPath="$bundleOut\" `
        -p:ArtifactsRoot="$artifacts\" `
        -p:RuntimeUrl="$runtimeUrl"
}

# A bundle must be signed with `wix burn` detach/reattach, not signtool alone.
if ($SigningCertThumbprint) {
    Write-Warning 'Signing a Burn bundle requires detaching and reattaching its engine; see docs/DEPLOYMENT.md.'
}

Write-Host ''
Write-Host 'Packaged:' -ForegroundColor Green
Write-Host "  $(Join-Path $msiOut 'FileTimeTracker.msi')"
Write-Host "  $(Join-Path $bundleOut 'FileTimeTrackerSetup.exe')"
