# Builds the library and stages a release zip that installs by extracting
# over the game root. The version comes from Branding.cs - the one source.
#
# Packaging must not touch the live installation, and must not trust one
# either: both builds run with DeployToSpt=false, and the quickstart check
# runs against the freshly built DLL in an isolated staging structure - not
# against whatever happens to be deployed under the SPT root. An outside
# review caught both halves of this: a packaging run used to deploy as a side
# effect, and the check used to prove the wrong binary.
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    # The SPT installation whose BepInEx and Unity assemblies the builds
    # borrow (read-only). Defaults like the csproj: three folders up.
    [string] $SptRoot = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $SptRoot) {
    $SptRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '..\..'))
}
if (-not (Test-Path (Join-Path $SptRoot 'BepInEx\core\BepInEx.dll'))) {
    throw "No BepInEx under '$SptRoot'. Pass -SptRoot pointing at an SPT installation."
}

$brandingPath = Join-Path $repositoryRoot 'WebOverlay\Branding.cs'
$branding = Get-Content -LiteralPath $brandingPath -Raw
if ($branding -notmatch 'PluginVersion\s*=\s*"([^"]+)"') {
    throw "PluginVersion was not found in Branding.cs."
}
$version = $Matches[1]

dotnet build (Join-Path $repositoryRoot 'WebOverlay\WebOverlay.csproj') -c $Configuration -p:SptRoot=$SptRoot -p:DeployToSpt=false
if ($LASTEXITCODE -ne 0) {
    throw "The library build failed."
}

# The README promises a quickstart that compiles as pasted; a release that
# breaks that promise must not package. The check runs against an isolated
# SPT-shaped structure holding the DLL just built, with the game and BepInEx
# assemblies borrowed read-only from the real installation - so it proves the
# artifact being released, and packaging can never write into the live game.
$checkRoot = Join-Path ([IO.Path]::GetTempPath()) 'weboverlay-release-check'
if (Test-Path $checkRoot) { Remove-Item -Recurse -Force $checkRoot }
New-Item -ItemType Directory -Path (Join-Path $checkRoot 'BepInEx\core') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $checkRoot 'BepInEx\plugins\Anvil-WebOverlay') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $checkRoot 'EscapeFromTarkov_Data\Managed') -Force | Out-Null
Copy-Item (Join-Path $SptRoot 'BepInEx\core\BepInEx.dll') (Join-Path $checkRoot 'BepInEx\core')
foreach ($assembly in 'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.InputLegacyModule.dll') {
    Copy-Item (Join-Path $SptRoot "EscapeFromTarkov_Data\Managed\$assembly") (Join-Path $checkRoot 'EscapeFromTarkov_Data\Managed')
}
Copy-Item (Join-Path $repositoryRoot "WebOverlay\bin\$Configuration\Anvil-WebOverlay.dll") (Join-Path $checkRoot 'BepInEx\plugins\Anvil-WebOverlay')
Copy-Item (Join-Path $repositoryRoot "WebOverlay\bin\$Configuration\Anvil-WebOverlay.xml") (Join-Path $checkRoot 'BepInEx\plugins\Anvil-WebOverlay')

& (Join-Path $repositoryRoot 'tools\Check-Quickstart.ps1') -SptRoot $checkRoot
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

# The README ships inside an immutable zip, so its links must not follow a
# moving main: pin them to this release's tag. Only the staged copy - the
# repository file keeps pointing at main, which is what a browsing reader
# wants.
$readme = Get-Content -Raw (Join-Path $repositoryRoot 'README.md')
$readme = $readme.Replace('https://github.com/maschine34675/WebOverlay/blob/main/', "https://github.com/maschine34675/WebOverlay/blob/v$version/")
$readme = $readme.Replace('https://raw.githubusercontent.com/maschine34675/WebOverlay/main/', "https://raw.githubusercontent.com/maschine34675/WebOverlay/v$version/")
Set-Content -LiteralPath (Join-Path $pluginDirectory 'README.md') -Value $readme -Encoding UTF8

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
dotnet build (Join-Path $repositoryRoot 'WebOverlay.Demo\WebOverlay.Demo.csproj') -c $Configuration -p:SptRoot=$SptRoot -p:DeployToSpt=false
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
