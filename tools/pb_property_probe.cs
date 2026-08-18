// GROUND TRUTH - PB property probe
//
// Answers one question: can a Programmable Block see the GT_ terminal properties?
//
// It matters because Programmable Blocks execute SERVER-SIDE in multiplayer, while
// terminal property registration was moved (2026-08-18) into CustomControlGetter - a
// UI hook that a dedicated server never fires. If that leaves the server without our
// properties, PB scripts and other mods lose the entire API on every DS, while single
// player keeps working perfectly and hides it.
//
// Paste into any Programmable Block on a grid that has at least one Ground Truth
// instrument, then Run. Read the output in the PB's detail panel.
//
// GetProperty is used rather than GetValueFloat because GetValueFloat THROWS when the
// property is absent - which is the exact case being tested. GetProperty returns null
// instead, so absence can be reported rather than crashing the script.

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.None;
}

public void Main(string argument, UpdateType updateSource)
{
    var blocks = new List<IMyTerminalBlock>();
    GridTerminalSystem.GetBlocks(blocks);

    int instruments = 0;
    int withGtProps = 0;
    int upgradeModules = 0;
    int upgradeModulesWithName = 0;

    var firstInstrument = (IMyTerminalBlock)null;

    for (int i = 0; i < blocks.Count; i++)
    {
        var b = blocks[i];

        // Every upgrade module shares one control list - vanilla modules, shield
        // generators, warp drives and our instruments alike. Counting how many still
        // have "Name" tells us whether the vanilla list is intact in THIS process.
        if (b is IMyUpgradeModule)
        {
            upgradeModules++;
            if (b.GetProperty("Name") != null) upgradeModulesWithName++;
        }

        // GT_SysBlockRole exists on every Ground Truth instrument and nowhere else.
        var role = b.GetProperty("GT_SysBlockRole");
        if (role != null)
        {
            withGtProps++;
            if (firstInstrument == null) firstInstrument = b;
        }

        // Subtype check is independent of registration: it identifies our blocks even
        // when the properties are missing, which is what distinguishes "no instruments
        // on this grid" from "instruments present but API not registered".
        var sub = b.BlockDefinition.SubtypeName;
        if (sub.StartsWith("GT_") && !sub.StartsWith("GT_RotatingRadarDish")) instruments++;
    }

    Echo("=== Ground Truth PB probe ===");
    Echo("GT instruments on grid (by subtype): " + instruments);
    Echo("Blocks exposing GT_ properties:      " + withGtProps);
    Echo("");
    Echo("Upgrade modules on grid:             " + upgradeModules);
    Echo("  ...of those, still have 'Name':    " + upgradeModulesWithName);
    Echo("");

    if (instruments == 0)
    {
        Echo("VERDICT: no instruments on this grid - put the PB on a grid with one.");
        return;
    }

    if (withGtProps == 0)
    {
        Echo("VERDICT: FAIL. Instruments are present but expose NO GT_ properties.");
        Echo("The API is not registered in the process running this script.");
        Echo("On a server that means registration never happened server-side.");
        return;
    }

    // Read a few real values, so this proves the properties WORK and not merely that
    // they exist. -1 is a legitimate reading meaning 'this block cannot answer'.
    Echo("VERDICT: PASS. Reading values from " + firstInstrument.CustomName + ":");
    EchoProp(firstInstrument, "GT_SysApiVersion");
    EchoProp(firstInstrument, "GT_SysBlockRole");
    EchoProp(firstInstrument, "GT_RadRate");
    EchoProp(firstInstrument, "GT_EnvOxygen");
    EchoBool(firstInstrument, "GT_HabSealKnown");
    EchoBool(firstInstrument, "GT_HabAirtight");
}

void EchoProp(IMyTerminalBlock b, string id)
{
    if (b.GetProperty(id) == null) { Echo("  " + id + " : ABSENT"); return; }
    Echo("  " + id + " : " + b.GetValueFloat(id).ToString("0.####"));
}

void EchoBool(IMyTerminalBlock b, string id)
{
    if (b.GetProperty(id) == null) { Echo("  " + id + " : ABSENT"); return; }
    Echo("  " + id + " : " + b.GetValueBool(id));
}
