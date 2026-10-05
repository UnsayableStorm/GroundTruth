# Compiles TacticalMath.cs with MathTest.cs against the game's own VRage.Math.dll and runs
# the hand-computed cases. Exit code 0 = all pass. No game, no world load, about a second.
#
# This is what made it safe to hand the geometry to the cheapest model tier
# (TACTICAL_PLAN 9.4): the output is either right or this says so.
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$bin = "F:\SteamLibrary\steamapps\common\SpaceEngineers\Bin64"
$csc = Get-ChildItem "C:\Program Files (x86)\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe" | Select-Object -First 1
$out = Join-Path $env:TEMP "gt_tactical_math_test"
if (-not (Test-Path $out)) { New-Item -ItemType Directory $out | Out-Null }
foreach ($d in @("VRage.Math.dll", "VRage.Library.dll", "netstandard.dll")) { Copy-Item (Join-Path $bin $d) $out -Force }
$exe = Join-Path $out "MathTest.exe"
& $csc.FullName /nologo /langversion:6 /target:exe ("/out:" + $exe) `
    ("/r:" + (Join-Path $out "VRage.Math.dll")) ("/r:" + (Join-Path $out "VRage.Library.dll")) ("/r:" + (Join-Path $out "netstandard.dll")) `
    (Join-Path $here "..\..\Data\Scripts\GroundTruth\TacticalMath.cs") (Join-Path $here "MathTest.cs")
if ($LASTEXITCODE -ne 0) { exit 1 }
& $exe
exit $LASTEXITCODE
