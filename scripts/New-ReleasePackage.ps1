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

$artifactDirectory = Join-Path $repositoryRoot 'artifacts'
$stageRoot = Join-Path $artifactDirectory "Anvil-WebOverlay-v$version"
$archivePath = Join-Path $artifactDirectory "Anvil-WebOverlay-v$version.zip"
if (Test-Path $stageRoot) { Remove-Item -Recurse -Force $stageRoot }
if (Test-Path $archivePath) { Remove-Item -Force $archivePath }

$pluginDirectory = Join-Path $stageRoot 'BepInEx\plugins\Anvil-WebOverlay'
New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $repositoryRoot "WebOverlay\bin\$Configuration\Anvil-WebOverlay.dll") -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'ThirdParty\WebView2\WebView2Loader.dll') -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'ThirdParty\WebView2\WebView2-LICENSE.txt') -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'ThirdParty\WebView2\WebView2-NOTICE.txt') -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $stageRoot

# The zip must contain exactly these files - never game or BepInEx assemblies.
$expected = @(
    'BepInEx\plugins\Anvil-WebOverlay\Anvil-WebOverlay.dll',
    'BepInEx\plugins\Anvil-WebOverlay\WebView2Loader.dll',
    'BepInEx\plugins\Anvil-WebOverlay\WebView2-LICENSE.txt',
    'BepInEx\plugins\Anvil-WebOverlay\WebView2-NOTICE.txt',
    'BepInEx\plugins\Anvil-WebOverlay\LICENSE',
    'README.md'
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
