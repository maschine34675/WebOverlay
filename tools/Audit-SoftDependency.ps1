<#
.SYNOPSIS
  Enforces the Anvil-WebOverlay soft-dependency contract on a consumer's
  compiled plugin.

.DESCRIPTION
  This library asks every consumer to follow a rule that is easy to get wrong
  and fails hard when it is - a TypeLoadException on a player's machine rather
  than a missing feature. So the check for it ships here, next to the rule.

  The rule (docs/SOFT-DEPENDENCY.md): no field, base type, interface, generic
  argument, return type or parameter anywhere in the assembly may name a type
  from this library. Only method BODIES may, and only inside the gate class,
  because a body is resolved when the method is first called while a signature
  is resolved when something reflects over the type's methods - and other mods
  do exactly that across every loaded assembly.

  It also checks that those gate methods are NoInlining. A body inlined into a
  caller outside the gate takes its library references with it, which is the
  same failure by another route.

  Uses BepInEx's own Mono.Cecil, which is always beside the game.

.EXAMPLE
  ./Audit-SoftDependency.ps1 -AssemblyPath bin/Release/maschine-YourMod.dll `
      -GateType YourMod.UI.WebOverlayGate

.NOTES
  Wire it into the consumer's csproj as a post-build step, so a mistake is a
  build failure rather than a bug report.
#>
param(
    [Parameter(Mandatory = $true)][string]$AssemblyPath,
    [Parameter(Mandatory = $true)][string]$GateType,
    [string]$CecilPath = '',
    [string]$LibraryName = 'Anvil-WebOverlay'
)

$ErrorActionPreference = 'Stop'
if (-not $CecilPath) {
    # $PSScriptRoot is empty under some hosts; resolve from the script file.
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    foreach ($candidate in @(
            (Join-Path $here '..\..\..\BepInEx\core\Mono.Cecil.dll'),
            (Join-Path $here '..\..\..\..\BepInEx\core\Mono.Cecil.dll'))) {
        if (Test-Path $candidate) { $CecilPath = $candidate; break }
    }
}
if (-not $CecilPath -or -not (Test-Path $CecilPath)) {
    throw "Mono.Cecil was not found. Pass -CecilPath pointing at BepInEx/core/Mono.Cecil.dll."
}
Add-Type -Path (Resolve-Path $CecilPath)

$module = [Mono.Cecil.ModuleDefinition]::ReadModule((Resolve-Path $AssemblyPath).Path)
$violations = New-Object System.Collections.Generic.List[string]

function Refs([Mono.Cecil.TypeReference]$t) {
    if ($null -eq $t) { return $false }
    if ($t.Scope -and $t.Scope.Name -eq $LibraryName) { return $true }
    if ($t -is [Mono.Cecil.GenericInstanceType]) {
        foreach ($a in $t.GenericArguments) { if (Refs $a) { return $true } }
    }
    if ($t -is [Mono.Cecil.TypeSpecification]) { return Refs $t.ElementType }
    return $false
}

function BodyRefs([Mono.Cecil.MethodDefinition]$m) {
    if (-not $m.HasBody) { return $false }
    foreach ($ins in $m.Body.Instructions) {
        $op = $ins.Operand
        if ($op -is [Mono.Cecil.TypeReference]) { if (Refs $op) { return $true } }
        elseif ($op -is [Mono.Cecil.MemberReference]) { if (Refs $op.DeclaringType) { return $true } }
    }
    return $false
}

foreach ($type in $module.GetTypes()) {
    $inGate = $type.FullName.StartsWith($GateType)
    if (Refs $type.BaseType) { $violations.Add("$($type.FullName): base type") }
    foreach ($i in $type.Interfaces) { if (Refs $i.InterfaceType) { $violations.Add("$($type.FullName): interface $($i.InterfaceType.Name)") } }
    foreach ($f in $type.Fields) { if (Refs $f.FieldType) { $violations.Add("$($type.FullName).$($f.Name): field of type $($f.FieldType.Name)") } }
    foreach ($m in $type.Methods) {
        if (Refs $m.ReturnType) { $violations.Add("$($type.FullName).$($m.Name): return type") }
        foreach ($p in $m.Parameters) { if (Refs $p.ParameterType) { $violations.Add("$($type.FullName).$($m.Name): parameter $($p.Name)") } }

        $touches = BodyRefs $m
        if ($touches -and -not $inGate) {
            $violations.Add("$($type.FullName).$($m.Name): body references $LibraryName outside the gate")
        }
        # A gate body that may be inlined carries its references into a caller
        # that is not the gate, which is the same failure by another route.
        #
        # Both sides are cast to int on purpose. MethodImplAttributes is backed
        # by UInt16, and Windows PowerShell 5.1 throws InvalidCastException on
        # -band over such an enum - so without the casts this script does not
        # merely misjudge, it dies before judging anything, on exactly the host
        # the csproj snippet in docs/SOFT-DEPENDENCY.md invokes. PowerShell 7
        # accepts it either way, which is what let it through here.
        # Compiler-generated members (lambdas, iterators) are exempt: the
        # developer cannot mark them, and they are only reached from a gate
        # body that is itself marked.
        if ($touches -and $inGate -and -not $m.IsConstructor `
                -and -not ([int]$m.ImplAttributes -band [int][Mono.Cecil.MethodImplAttributes]::NoInlining) `
                -and -not $m.Name.StartsWith('<')) {
            $violations.Add("$($type.FullName).$($m.Name): touches $LibraryName but is not [MethodImpl(MethodImplOptions.NoInlining)]")
        }
        # Locals of a library type are fine - they resolve with the body.
    }
}

if ($violations.Count -gt 0) {
    Write-Host "Soft-dependency audit FAILED ($($violations.Count)):" -ForegroundColor Red
    $violations | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host "Soft-dependency audit passed: no type-level reference to $LibraryName outside method bodies of $GateType."
exit 0
