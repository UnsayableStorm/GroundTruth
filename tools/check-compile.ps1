# Compile the mod's scripts locally, without loading Space Engineers.
#
# WHY THIS EXISTS
#
# A script error is only reported by the game, at load, in the log - so every typo
# costs a world load, and on a dedicated server it costs a publish, a restart and a
# rejoin. On 2026-08-12 one property-vs-method mistake (room.OxygenLevel is a
# METHOD taking grid size) cost exactly that round trip, and the only symptom in
# game was that every app and every reading silently vanished.
#
# This runs the same compiler against the same game assemblies in about a second.
#
# IT IS NOT THE WHITELIST. The game additionally forbids types mods may not touch,
# and that check only happens in game. Clean here means "it compiles", not "it
# loads" - see docs/ENGINE_TRAPS.md for things that compile and still fail.

$ErrorActionPreference = "Stop"

$bin = "F:\SteamLibrary\steamapps\common\SpaceEngineers\Bin64"
$src = Join-Path $PSScriptRoot "..\Data\Scripts\GroundTruth"

$csc = Get-ChildItem "C:\Program Files (x86)\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe" -ErrorAction SilentlyContinue |
       Select-Object -First 1
if (-not $csc) {
    Write-Host "No Roslyn csc found - install VS Build Tools" -ForegroundColor Red
    exit 1
}

# Reference EVERY assembly the game ships.
#
# A curated list looked tidy and was wrong: IMyCubeBlock resolves through type
# forwards that need assemblies the obvious list omits. The game compiles mods
# against the whole set, so match it rather than guess. Unusable references are
# ignored by csc, so breadth costs nothing but a second.
$refs = @(Get-ChildItem $bin -Filter *.dll | ForEach-Object { '/r:' + $_.FullName })

# The game injects namespaces that mod sources never import.
#
# Three files here use IMyCubeBlock with only "using Sandbox.ModAPI;" and compile
# fine in game, because SE prepends a set of default usings to every mod script.
# IMyCubeBlock is actually VRage.Game.ModAPI.IMyCubeBlock and exists nowhere else.
# Without mirroring that, this checker reports errors the game does not have -
# which would be worse than no checker, because it would train you to ignore it.
#
# Compiled from copies so the real sources stay untouched.
$inject = @(
    'using VRage.Game.ModAPI;'
    'using VRage.ModAPI;'
    'using VRage.Game;'
    'using VRage.Game.Components;'
) -join "`r`n"

$tmp = Join-Path $env:TEMP "gt_compile_check_src"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory $tmp | Out-Null

$files = @(Get-ChildItem $src -Filter *.cs | ForEach-Object {
    $dst = Join-Path $tmp $_.Name
    ($inject + "`r`n" + (Get-Content $_.FullName -Raw)) | Out-File $dst -Encoding utf8
    $dst
})

$out = Join-Path $env:TEMP "gt_compile_check.dll"

$cscArgs = @('/nologo', '/target:library', '/langversion:6', ('/out:' + $out)) + $refs + $files
$result = & $csc.FullName $cscArgs 2>&1

$errors = @($result | Where-Object { $_ -match ': error ' })
# CS0105 is our own injected usings colliding with real ones - not the mod's problem.
$warnings = @($result | Where-Object { $_ -match ': warning ' -and $_ -notmatch 'CS0105' })

if ($errors.Count -gt 0) {
    Write-Host ("COMPILE FAILED - " + $errors.Count + " error(s)") -ForegroundColor Red
    $errors | Select-Object -First 20 | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Red }
    exit 1
}

Write-Host ("Compiles clean (" + $files.Count + " files)") -ForegroundColor Green
if ($warnings.Count -gt 0) {
    Write-Host ($warnings.Count.ToString() + " warning(s), first few:")
    $warnings | Select-Object -First 5 | ForEach-Object { Write-Host ("  " + $_) }
}

exit 0
