<#
.SYNOPSIS
  Proves the README quickstart compiles exactly as a reader would paste it.

.DESCRIPTION
  The README promises a quickstart that "compiles and runs as pasted". A
  promise like that rots silently: the API moves, the example does not, and
  the first person to notice is a stranger with six compiler errors - which
  is exactly what an outside assessment found in the example this one
  replaced.

  So the example is not trusted, it is extracted. This script reads the two
  marked blocks out of README.md - the csproj reference ItemGroup and the
  plugin source - writes them into a scratch project verbatim, and builds it
  against the assemblies of a real SPT installation, including the built
  library DLL the reference block itself points at. If a reader cannot paste
  the quickstart, this exits 1 and the release process stops.

  Run it after building the library (the reference block points at the
  deployed DLL under the SPT root, which a normal build refreshes).

.EXAMPLE
  ./tools/Check-Quickstart.ps1              # SptRoot inferred like the csproj
  ./tools/Check-Quickstart.ps1 -SptRoot D:\SPT41
#>
param(
    [string]$SptRoot = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $SptRoot) {
    # Same convention as the library csproj: three folders above the project.
    $SptRoot = [IO.Path]::GetFullPath((Join-Path $repo '..\..'))
}
if (-not (Test-Path (Join-Path $SptRoot 'BepInEx\core\BepInEx.dll'))) {
    throw "No BepInEx under '$SptRoot'. Pass -SptRoot pointing at an SPT installation."
}

$readme = Get-Content -Raw (Join-Path $repo 'README.md')

function Extract([string]$name) {
    $pattern = "(?s)<!-- $name`:begin -->.*?``````\w*\r?\n(.*?)``````"
    $m = [regex]::Match($readme, $pattern)
    if (-not $m.Success) { throw "README marker '$name' not found - the quickstart moved without its check." }
    return $m.Groups[1].Value
}

$itemGroup = Extract 'quickstart-csproj'
$plugin = Extract 'quickstart-plugin'

$work = Join-Path ([IO.Path]::GetTempPath()) 'weboverlay-quickstart-check'
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Path $work | Out-Null

Set-Content -Path (Join-Path $work 'Plugin.cs') -Value $plugin -Encoding UTF8

$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
    <SptRoot>$SptRoot</SptRoot>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="All" />
    <Compile Include="Plugin.cs" />
  </ItemGroup>
$itemGroup
</Project>
"@
Set-Content -Path (Join-Path $work 'Quickstart.csproj') -Value $csproj -Encoding UTF8

$output = & dotnet build (Join-Path $work 'Quickstart.csproj') -c Release --nologo -v quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Quickstart check FAILED - the README example does not compile as pasted:' -ForegroundColor Red
    $output | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host 'Quickstart check passed: the README example compiles exactly as a reader would paste it.'
exit 0
