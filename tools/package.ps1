#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the release packages of Explorer Everything Search.

.DESCRIPTION
    Produces two archives in <repo>\artifacts:

      ExplorerEverythingSearch-<version>-win-x64-portable.zip
          Self contained, single file. No .NET runtime installation required.

      ExplorerEverythingSearch-<version>-win-x64-framework-dependent.zip
          Small package; requires the .NET 8 Desktop Runtime (x64).

    Both archives contain the application, README.md, README.zh-CN.md and LICENSE. The
    configuration file is created next to the executable on first start.

.PARAMETER Configuration
    Build configuration; Release by default.

.PARAMETER Version
    Version to use in the archive names. Defaults to the Version property of Directory.Build.props.

.PARAMETER OutputDirectory
    Where the archives are written. Defaults to <repo>\artifacts.

.PARAMETER SkipTests
    Skips the unit test run (use for a quick local package).

.EXAMPLE
    pwsh tools/package.ps1
    pwsh tools/package.ps1 -Version 1.0.0 -SkipTests
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Version,
    [string] $OutputDirectory,
    [switch] $SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\ExplorerEverythingSearch.App\ExplorerEverythingSearch.App.csproj'
$testsProject = Join-Path $repoRoot 'tests\ExplorerEverythingSearch.Tests\ExplorerEverythingSearch.Tests.csproj'
$artifacts = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $repoRoot 'artifacts' }
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("ees-package-" + [guid]::NewGuid().ToString('N'))

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    Write-Host "> dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK (dotnet) was not found on PATH. Install the .NET 8 SDK and try again.'
}

if (-not (Test-Path -LiteralPath $appProject)) {
    throw "Application project not found: $appProject"
}

if (-not $Version) {
    $props = Join-Path $repoRoot 'Directory.Build.props'
    if (Test-Path -LiteralPath $props) {
        $match = Select-String -LiteralPath $props -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
        if ($match) { $Version = $match.Matches[0].Groups[1].Value.Trim() }
    }
    if (-not $Version) { $Version = '0.0.0' }
}

Write-Host "Explorer Everything Search $Version -> $artifacts" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
New-Item -ItemType Directory -Force -Path $staging | Out-Null

try {
    if (-not $SkipTests) {
        if (Test-Path -LiteralPath $testsProject) {
            Invoke-DotNet -Arguments @('test', $testsProject, '-c', $Configuration, '--nologo')
        }
        else {
            Write-Warning "test project not found ($testsProject); skipping the unit test run"
        }
    }

    $portable = Join-Path $staging 'portable'
    $frameworkDependent = Join-Path $staging 'framework-dependent'

    # Self contained single file: everything the tool needs is inside the EXE.
    Invoke-DotNet -Arguments @(
        'publish', $appProject, '-c', $Configuration, '-r', 'win-x64', '--nologo',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=none',
        "-p:Version=$Version",
        '-o', $portable
    )

    # Framework dependent: much smaller, needs the .NET 8 Desktop Runtime.
    Invoke-DotNet -Arguments @(
        'publish', $appProject, '-c', $Configuration, '-r', 'win-x64', '--nologo',
        '--self-contained', 'false',
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:DebugType=none',
        "-p:Version=$Version",
        '-o', $frameworkDependent
    )

    foreach ($package in @($portable, $frameworkDependent)) {
        foreach ($document in @('README.md', 'README.zh-CN.md', 'LICENSE')) {
            $source = Join-Path $repoRoot $document
            if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $package -Force }
        }
    }

    $portableZip = Join-Path $artifacts "ExplorerEverythingSearch-$Version-win-x64-portable.zip"
    $frameworkZip = Join-Path $artifacts "ExplorerEverythingSearch-$Version-win-x64-framework-dependent.zip"
    Remove-Item -LiteralPath $portableZip, $frameworkZip -Force -ErrorAction SilentlyContinue

    Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $portableZip -CompressionLevel Optimal
    Compress-Archive -Path (Join-Path $frameworkDependent '*') -DestinationPath $frameworkZip -CompressionLevel Optimal

    Write-Host ''
    Get-ChildItem -LiteralPath $artifacts -Filter '*.zip' |
        Sort-Object Name |
        ForEach-Object { Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) -ForegroundColor Green }
    Write-Host ''
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
