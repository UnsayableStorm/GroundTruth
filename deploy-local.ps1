# Copy Ground Truth to the SE local mods folder for testing.
# pb_test_script.cs is a Programmable Block script, not part of the mod.
$src  = $PSScriptRoot
$dest = "$env:APPDATA\SpaceEngineers\Mods\GroundTruth"

# Excluded from the published mod:
#   tools/   the icon build pipeline and its source art - it produces what ships
#   docs/    the GitHub documentation and screenshots
#   probes/  whitelist probe mods, published for other modders, not part of this one
#   .git/    obviously
#
# NEVER MIRROR modinfo.sbmi OR metadata.mod.
#
# Those two belong to the AppData copy and are written BY THE GAME when it
# publishes. modinfo.sbmi is where Steam's Workshop id lives:
#
#   <WorkshopIds><WorkshopId><Id>3781444888</Id>...
#
# The copy in this folder is a hand-written stub carrying WorkshopId 0. Mirroring
# it over the real one erases the id, and the next publish then has nothing to
# update - so the game silently creates a BRAND NEW Workshop item instead.
#
# That is exactly what happened on 2026-08-12: the seal fix went out as new item
# 3782506060 while the server sat on 3781444888 receiving nothing, and two rounds
# of "the fix isn't working" were actually "the fix was never there". It is also
# the most likely explanation for the duplicated Illuminated Column item.
robocopy $src $dest /MIR /XD ".git" "tools" "docs" "probes" `
    /XF "deploy-local.ps1" "pb_test_script.cs" "README.md" ".gitignore" "LICENSE" `
        "modinfo.sbmi" "metadata.mod" `
    /NFL /NDL /NJH /NJS

# robocopy returns 0-7 for success; 8+ is a real failure.
if ($LASTEXITCODE -ge 8) { Write-Host "ROBOCOPY FAILED, exit $LASTEXITCODE" -ForegroundColor Red }

# Say out loud which Workshop item the next publish will update, because getting
# this wrong creates a duplicate rather than failing.
$mi = "$dest\modinfo.sbmi"
if (Test-Path $mi) {
    $t = Get-Content $mi -Raw
    if ($t -match "<Id>(\d+)</Id>") {
        Write-Host "Publish target: Workshop item $($Matches[1])"
    } else {
        Write-Host "WARNING: modinfo.sbmi carries no Workshop id - publishing will CREATE A NEW ITEM" -ForegroundColor Yellow
    }
} else {
    Write-Host "WARNING: no modinfo.sbmi in the publish folder - publishing will CREATE A NEW ITEM" -ForegroundColor Yellow
}

Write-Host "Deployed to $dest"
