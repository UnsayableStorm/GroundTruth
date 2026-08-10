// Ground Truth - API test script for a Programmable Block.
//
// Paste the whole thing into a PB, Check Code, Remember & Exit, then Run.
// Output appears in the PB's own detail info panel.
//
// Identifies instruments by the VALUE of GT_SysBlockRole, not by whether the
// property exists. Terminal properties in SE are registered per block INTERFACE,
// so every functional block carries the names - they just answer with sentinels.
// Role returns 0 or -1 for anything that is not a real instrument.
//
// This is the pattern any consumer should copy.

public void Main(string arg)
{
    var blocks = new List<IMyTerminalBlock>();
    GridTerminalSystem.GetBlocksOfType<IMyTerminalBlock>(blocks);

    var props = new List<ITerminalProperty>();
    int scanned = 0, carriers = 0, instruments = 0;

    foreach (var b in blocks)
    {
        scanned++;

        float role;
        try { role = b.GetValueFloat("GT_SysBlockRole"); }
        catch { continue; }          // property not present on this block type at all

        carriers++;                  // block type carries the names
        if (role <= 0f) continue;    // ...but is not one of ours

        instruments++;
        Echo("=== " + b.CustomName);
        Echo("  role " + role
             + "  caps " + b.GetValueFloat("GT_SysCapabilities")
             + "  api " + b.GetValueFloat("GT_SysApiVersion"));

        props.Clear();
        b.GetProperties(props);
        foreach (var p in props)
        {
            if (!p.Id.StartsWith("GT_")) continue;
            if (p.Id.StartsWith("GT_Sys")) continue;

            if (p.TypeName == "Boolean")
                Echo("  " + p.Id + " = " + b.GetValueBool(p.Id));
            else
                Echo("  " + p.Id + " = " + b.GetValueFloat(p.Id).ToString("0.####"));
        }
        Echo("");
    }

    Echo("--------------------------------");
    Echo("blocks scanned    : " + scanned);
    Echo("carrying GT names : " + carriers);
    Echo("actual instruments: " + instruments);

    if (carriers > instruments)
        Echo("(" + (carriers - instruments) + " foreign blocks carry the property names but return sentinels)");
}
