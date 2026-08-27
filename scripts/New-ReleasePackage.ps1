# Builds the library and stages a release zip that installs by extracting
# over the game root. The version comes from Branding.cs - the one source.
[CmdletBinding()]
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

$brandingPath = Join-Path $repositoryRoot 'WebOverlay\Branding.cs'
$branding = Get-Content -LiteralPath $brandingPath -Raw
if ($branding -notmatch 'PluginVersion\s*=\s*"([^"]+)"') {
    throw "PluginVersion was not found in Branding.cs."
}
$version = $Matches[1]

dotnet build (Join-Path $repositoryRoot 'WebOverlay\WebOverlay.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "The library build failed."
}


# The README promises a quickstart that compiles as pasted; a release that
# breaks that promise must not package. The check needs the freshly built
# library, so it runs after the build.
& (Join-Path $repositoryRoot 'tools\Check-Quickstart.ps1')
if ($LASTEXITCODE -ne 0) {
    throw "The README quickstart no longer compiles - fix the example (or the API) before packaging."
}
$artifactDirectory = Join-Path $repositoryRoot 'artifacts'
$stageRoot = Join-Path $artifactDirectory "Anvil-WebOverlay-v$version"
$archivePath = Join-Path $artifactDirectory "Anvil-WebOverlay-v$version.zip"
if (Test-Path $stageRoot) { Remove-Item -Recurse -Force $stageRoot }
if (Test-Path $archivePath) { Remove-Item -Force $archivePath }

$pluginDirectory = Join-Path $stageRoot 'BepInEx\plugins\Anvil-WebOverlay'
New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $repositoryRoot "WebOverlay\bin\$Configuration\Anvil-WebOverlay.dll") -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot "WebOverlay\bin\$Configuration\Anvil-WebOverlay.xml") -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'ThirdParty\WebView2\WebView2Loader.dll') -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'ThirdParty\WebView2\WebView2-LICENSE.txt') -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'ThirdParty\WebView2\WebView2-NOTICE.txt') -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $pluginDirectory

# The zip must contain exactly these files - never game or BepInEx assemblies.
$expected = @(
    'BepInEx\plugins\Anvil-WebOverlay\Anvil-WebOverlay.dll',
    'BepInEx\plugins\Anvil-WebOverlay\Anvil-WebOverlay.xml',
    'BepInEx\plugins\Anvil-WebOverlay\WebView2Loader.dll',
    'BepInEx\plugins\Anvil-WebOverlay\WebView2-LICENSE.txt',
    'BepInEx\plugins\Anvil-WebOverlay\WebView2-NOTICE.txt',
    'BepInEx\plugins\Anvil-WebOverlay\LICENSE',
    'BepInEx\plugins\Anvil-WebOverlay\README.md'
)
$actual = Get-ChildItem -Recurse -File $stageRoot | ForEach-Object {
    $_.FullName.Substring($stageRoot.Length + 1)
}
$unexpected = @($actual | Where-Object { $expected -notcontains $_ })
$missing = @($expected | Where-Object { $actual -notcontains $_ })
if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
    throw "Package manifest mismatch. Unexpected: $($unexpected -join ', '). Missing: $($missing -join ', ')."
}

Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
Write-Host "Created $archivePath"

# The demo ships as its own zip: it is a try-it-out plugin plus reference
# source, not something every player should install.
dotnet build (Join-Path $repositoryRoot 'WebOverlay.Demo\WebOverlay.Demo.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "The demo build failed."
}

$demoStage = Join-Path $artifactDirectory "Anvil-WebOverlayDemo-v$version"
$demoArchive = Join-Path $artifactDirectory "Anvil-WebOverlayDemo-v$version.zip"
if (Test-Path $demoStage) { Remove-Item -Recurse -Force $demoStage }
if (Test-Path $demoArchive) { Remove-Item -Force $demoArchive }
$demoPluginDirectory = Join-Path $demoStage 'BepInEx\plugins\Anvil-WebOverlayDemo'
New-Item -ItemType Directory -Path $demoPluginDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot "WebOverlay.Demo\bin\$Configuration\Anvil-WebOverlayDemo.dll") -Destination $demoPluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'WebOverlay.Demo\DemoPlugin.cs') -Destination $demoPluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'WebOverlay.Demo\web\cube.html') -Destination $demoPluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'WebOverlay.Demo\web\three-LICENSE.txt') -Destination $demoPluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $demoPluginDirectory
Set-Content -LiteralPath (Join-Path $demoPluginDirectory 'README.txt') -Value @"
Anvil-WebOverlay demo plugin.

Requires the Anvil-WebOverlay library (install its zip first).
In game: F10 toggles an interactive panel, F11 a transparent click-through
HUD, F8 an interactive glass panel, F7 a Three.js WebGL cube following the
player camera.
DemoPlugin.cs is the reference source for using the library from a mod;
cube.html is the Three.js page (the DLL embeds it together with three.min.js,
which is Three.js r149 under the MIT license in three-LICENSE.txt).
"@
# The demo zip gets the same manifest guarantee as the library zip.
$demoExpected = @(
    'BepInEx\plugins\Anvil-WebOverlayDemo\Anvil-WebOverlayDemo.dll',
    'BepInEx\plugins\Anvil-WebOverlayDemo\DemoPlugin.cs',
    'BepInEx\plugins\Anvil-WebOverlayDemo\cube.html',
    'BepInEx\plugins\Anvil-WebOverlayDemo\three-LICENSE.txt',
    'BepInEx\plugins\Anvil-WebOverlayDemo\LICENSE',
    'BepInEx\plugins\Anvil-WebOverlayDemo\README.txt'
)
$demoActual = Get-ChildItem -Recurse -File $demoStage | ForEach-Object {
    $_.FullName.Substring($demoStage.Length + 1)
}
$demoUnexpected = @($demoActual | Where-Object { $demoExpected -notcontains $_ })
$demoMissing = @($demoExpected | Where-Object { $demoActual -notcontains $_ })
if ($demoUnexpected.Count -gt 0 -or $demoMissing.Count -gt 0) {
    throw "Demo package manifest mismatch. Unexpected: $($demoUnexpected -join ', '). Missing: $($demoMissing -join ', ')."
}

Compress-Archive -Path (Join-Path $demoStage '*') -DestinationPath $demoArchive -CompressionLevel Optimal
Write-Host "Created $demoArchive"
