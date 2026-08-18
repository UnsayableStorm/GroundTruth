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
#
# THIS SCRIPT HAS ALREADY LIED ONCE. 2026-08-18: it reported "Compiles clean" on a
# file using a C# 7 local function, which the game's compiler rejects outright and
# which crashed nothing only because the mistake was caught by reading the actual
# game log instead of trusting this tool. Root cause: referencing EVERY .dll in
# Bin64 includes ~50 native, non-.NET libraries (Havok, opus, steam_api64, ...).
# csc cannot load them as metadata and ABORTS THE WHOLE COMPILATION before
# reaching the source files at all - producing only reference-load errors, in a
# format ("error CSxxxx: ..." with no "file(line,col):" prefix) the error filter
# below did not recognise. Zero matched errors read as a clean build. Fixed by
# filtering to genuinely managed assemblies before compiling, and by treating the
# exit code as authoritative rather than trusting a text-pattern match alone.

$ErrorActionPreference = "Stop"

$bin = "F:\SteamLibrary\steamapps\common\SpaceEngineers\Bin64"
$src = Join-Path $PSScriptRoot "..\Data\Scripts\GroundTruth"

$csc = Get-ChildItem "C:\Program Files (x86)\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe" -ErrorAction SilentlyContinue |
       Select-Object -First 1
if (-not $csc) {
    Write-Host "No Roslyn csc found - install VS Build Tools" -ForegroundColor Red
    exit 1
}

# Reference every MANAGED assembly the game ships - not every DLL.
#
# A curated-by-name list was tried first and was wrong: IMyCubeBlock resolves
# through type forwards that need assemblies an obvious list omits. Referencing
# literally everything was tried next and was ALSO wrong, for the opposite reason -
# see the header. The correct middle ground is to ask each DLL whether it is a
# .NET assembly at all, which AssemblyName.GetAssemblyName answers without loading
# or executing any code: it throws BadImageFormatException for native binaries and
# succeeds for managed ones.
$allDlls = Get-ChildItem $bin -Filter *.dll
$managedDlls = @($allDlls | Where-Object {
    try { [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null; $true }
    catch { $false }
})
Write-Host ("Referencing " + $managedDlls.Count + " managed assemblies of " + $allDlls.Count + " total in Bin64.")
$refs = @($managedDlls | ForEach-Object { '/r:' + $_.FullName })

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
$result = @(& $csc.FullName $cscArgs 2>&1)
$exitCode = $LASTEXITCODE

# THE EXIT CODE IS AUTHORITATIVE. Text-matching csc's output is a convenience for
# showing WHICH lines are errors, not the thing that decides pass or fail - that is
# what went wrong before. Any nonzero exit means real diagnostics exist somewhere
# in $result even if the pattern below fails to recognise their shape.
$errors = @($result | Where-Object { $_ -match 'error CS' })
$warnings = @($result | Where-Object { $_ -match 'warning CS' -and $_ -notmatch 'CS0105' })

if ($exitCode -ne 0) {
    Write-Host ("COMPILE FAILED - exit " + $exitCode + ", " + $errors.Count + " matched error line(s)") -ForegroundColor Red
    if ($errors.Count -gt 0) {
        $errors | Select-Object -First 20 | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Red }
    } else {
        # The exact failure mode this header describes: something failed and none of
        # it matched the expected shape. Dump everything rather than hide it.
        Write-Host "  (no line matched the expected error pattern - showing everything csc printed)" -ForegroundColor Yellow
        $result | Select-Object -First 30 | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Yellow }
    }
    exit 1
}

Write-Host ("Compiles clean (" + $files.Count + " files)") -ForegroundColor Green
if ($warnings.Count -gt 0) {
    Write-Host ($warnings.Count.ToString() + " warning(s), first few:")
    $warnings | Select-Object -First 5 | ForEach-Object { Write-Host ("  " + $_) }
}

exit 0
