<#
.SYNOPSIS
    Builds, tests and publishes the Butterfly framework to the local package feed.

.EXAMPLE
    ./build.ps1                 # Pack: builds every project and publishes it to Packages/Release/Butterfly
    ./build.ps1 Test            # Runs the tests
    ./build.ps1 Clean           # Empties the feed (current version) and the NuGet cache copies, keeps the folder
    ./build.ps1 InstallTool     # (Re)installs the 'butterfly' global tool from the local feed
    ./build.ps1 All             # Pack + Test + InstallTool + Samples
#>
param(
    [ValidateSet('Pack', 'Build', 'Test', 'Clean', 'InstallTool', 'Samples', 'All')]
    [string] $Target = 'Pack',

    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$Solution = Join-Path $PSScriptRoot 'Butterfly.slnx'
$Feed     = Join-Path $PSScriptRoot 'Packages/Release/Butterfly'
$Version  = ([xml](Get-Content (Join-Path $PSScriptRoot 'src/Butterfly.Sdk/Sdk/Butterfly.Version.props'))).Project.PropertyGroup.ButterflyVersion
$NuGetCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }

# The feed is a registered NuGet source: without its folder every restore on the machine fails (NU1301).
New-Item -ItemType Directory -Force $Feed | Out-Null

function Invoke-DotNet {
    Write-Host "> dotnet $args" -ForegroundColor Cyan
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed with exit code $LASTEXITCODE." }
}

function Test-Published([string] $Id) { Test-Path (Join-Path $Feed "$Id.$Version.nupkg") }

function Invoke-Build   { Invoke-DotNet build $Solution -c $Configuration }
# Release builds pack on build (GeneratePackageOnBuild). 'dotnet pack' is not used on purpose: with
# GeneratePackageOnBuild enabled, NuGet's Pack target no longer builds first and fails on a clean tree.
function Invoke-Pack    { Invoke-DotNet build $Solution -c Release; Write-Host "Butterfly $Version published to $Feed" -ForegroundColor Green }
function Invoke-Test    { Invoke-DotNet test $Solution -c $Configuration }

function Invoke-Clean {
    # Removes the packages of the current version from the feed and from the NuGet cache, so the next
    # restore of any consumer really takes the freshly built ones. The feed folder itself is kept.
    # The package ids come from the projects (not from the feed), so it also works on a half-emptied feed.
    Get-ChildItem (Join-Path $PSScriptRoot 'src'), (Join-Path $PSScriptRoot 'tools') -Recurse -Filter *.csproj | ForEach-Object {
        $package = Join-Path $Feed "$($_.BaseName).$Version.nupkg"
        $cached  = Join-Path $NuGetCache "$($_.BaseName.ToLowerInvariant())/$Version"
        foreach ($path in $package, $cached) { if (Test-Path $path) { Remove-Item $path -Recurse -Force } }
    }
    Get-ChildItem $PSScriptRoot -Recurse -Directory -Include bin, obj | Remove-Item -Recurse -Force
    Write-Host "Butterfly $Version removed from $Feed and from the NuGet cache" -ForegroundColor Green
}

function Invoke-InstallTool {
    if (-not (Test-Published 'Butterfly.Tool')) { Invoke-Pack }

    # Reinstalling (rather than updating) picks up a re-packed tool that keeps the same version.
    if (& dotnet tool list --global | Select-String -Pattern '^butterfly\.tool\s' -Quiet) {
        Invoke-DotNet tool uninstall --global Butterfly.Tool
    }
    Invoke-DotNet tool install --global Butterfly.Tool --version $Version --add-source $Feed
}

function Invoke-Samples {
    # Samples consume the feed like any external project: publish first if it is empty.
    if (-not (Test-Published 'Butterfly.Sdk')) { Invoke-Pack }
    Invoke-DotNet build (Join-Path $PSScriptRoot 'samples/Samples.slnx') -c $Configuration
}

switch ($Target) {
    'Build'       { Invoke-Build }
    'Pack'        { Invoke-Pack }
    'Test'        { Invoke-Test }
    'Clean'       { Invoke-Clean }
    'InstallTool' { Invoke-InstallTool }
    'Samples'     { Invoke-Samples }
    'All'         { Invoke-Pack; Invoke-Test; Invoke-InstallTool; Invoke-Samples }
}
