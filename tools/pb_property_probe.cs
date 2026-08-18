// GROUND TRUTH - PB property probe
//
// Answers one question: can a Programmable Block see the GT_ terminal properties?
//
// It matters because Programmable Blocks execute SERVER-SIDE in multiplayer, while
// terminal property registration happens in CustomControlGetter - a UI hook that a
// dedicated server never fires. On a DS the properties therefore do not exist until
// something asks for them, which a script does by writing GT_API_ENABLE into the Custom
// Data of any Ground Truth instrument. This probe performs that handshake, so it doubles
// as the worked example for script authors.
//
// Paste into any Programmable Block on a grid that has at least one Ground Truth
// instrument, then Run. Read the output in the PB's detail panel.
//
//   Run 1 on a fresh server: reports the API is absent and requests it.
//   Run 2:                   PASS, with live values.
//
// GetProperty is used throughout rather than GetValueFloat, because GetValueFloat THROWS
// when the property is absent - which is the exact case being tested.

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
    var anyBySubtype = (IMyTerminalBlock)null;

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
        // on this grid" from "instruments present but API not registered". It is also
        // how the handshake finds a block to write to - you cannot detect the API with
        // the API when the question is whether the API exists.
        var sub = b.BlockDefinition.SubtypeName;
        if (sub.StartsWith("GT_") && !sub.StartsWith("GT_RotatingRadarDish"))
        {
            instruments++;
            if (anyBySubtype == null) anyBySubtype = b;
        }
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
        // This is the expected first-run result on a dedicated server, not a failure.
        // Ask for the API and stop; the next run reads it.
        anyBySubtype.CustomData = "GT_API_ENABLE";

        Echo("API not registered in this process - REQUESTED IT.");
        Echo("Wrote GT_API_ENABLE to " + anyBySubtype.CustomName + ".");
        Echo("");
        Echo("Run this script again. If the second run still shows 0, the request");
        Echo("did not reach the mod - check the server log for a 'GT TERMINAL' line.");
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

    // Confirms the mod acknowledged the handshake rather than the properties having been
    // registered by someone opening a terminal in the same process (single player).
    if (anyBySubtype != null && anyBySubtype.CustomData.Contains("GT_API_READY"))
        Echo("Handshake acknowledged (GT_API_READY).");
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
