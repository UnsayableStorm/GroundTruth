# Copy Ground Truth to the SE local mods folder for testing.
# pb_test_script.cs is a Programmable Block script, not part of the mod.
$src  = $PSScriptRoot
$dest = "$env:APPDATA\SpaceEngineers\Mods\GroundTruth"
# Excluded from the published mod:
#   tools/   the icon build pipeline and its source art - it produces what ships
#   docs/    the GitHub documentation
#   probes/  whitelist probe mods, published for other modders, not part of this one
#   .git/    obviously
robocopy $src $dest /MIR /XD ".git" "tools" "docs" "probes" /XF "deploy-local.ps1" "pb_test_script.cs" "README.md" ".gitignore" "LICENSE" /NFL /NDL /NJH /NJS
Write-Host "Deployed to $dest"
