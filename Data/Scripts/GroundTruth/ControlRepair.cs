using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace GroundTruth
{
    // Putting back what somebody else took.
    //
    // THE DAMAGE
    //
    // Animation Engine (Workshop 2880317963), TerminalControlHelper.SetPosition, does
    // this to the IMyTerminalBlock control list - the shared list holding Name, OnOff,
    // ShowInTerminal, ShowInToolbarConfig and CustomData for every block in the game:
    //
    //     GetControls<T>(out controls);
    //     foreach (var x in controls) RemoveControl<T>(x);            // empties it
    //     for (i = 0; i < controls.Count; i++) AddControl<T>(controls[i]);
    //
    // The re-add loop reads the collection the removal loop just emptied. Any block type
    // whose control list is built after that inherits nothing, which is why upgrade
    // modules on Long Haul open with no Name field, no On/Off and no Custom Data.
    //
    // It has not been updated since 2024 and a great many mods depend on it, so it is a
    // fact of the environment rather than a bug that will be fixed. Ground Truth has to
    // work anyway - and if it can hand the player their terminal back on the way past,
    // so much the better.
    //
    // WHY THIS CAN WORK AT ALL
    //
    // RemoveControl does not destroy a control, it unlists it. The objects are Keen's
    // own - the real Name textbox with its real getter and setter - so holding a
    // reference and re-adding it restores the genuine control, not an imitation.
    //
    // TWO WAYS TO GET A REFERENCE
    //
    //   1. CAPTURE. Read the base list at LoadData, which on most load orders happens
    //      before the damage, and keep what we find.
    //   2. HARVEST. If we loaded too late and the base list is already empty, look at
    //      other block interfaces. A type whose list was built before the wipe still
    //      holds the same control objects.
    //
    // Whether SE accepts a control taken from one type's list into another's is the one
    // thing here that is NOT established. The logging says which path ran and whether
    // the count actually moved, so a single load settles it - and this file of all
    // files does not get to assume its writes took effect.
    public static class ControlRepair
    {
        // The controls whose absence players actually notice. Deliberately not the whole
        // base set: these are the ones named in the report, and restoring fewer things
        // is safer than restoring more.
        private static readonly string[] Wanted =
        {
            "Name", "OnOff", "ShowInTerminal", "ShowInToolbarConfig", "ShowOnHUD", "CustomData"
        };

        private static readonly Dictionary<string, IMyTerminalControl> _captured =
            new Dictionary<string, IMyTerminalControl>();

        private static bool _repairAttempted;

        /// <summary>
        /// Called as early as the session can manage. Whatever is in the base list now is
        /// what we are able to give back later.
        /// </summary>
        public static void Capture()
        {
            try
            {
                List<IMyTerminalControl> list;
                MyAPIGateway.TerminalControls.GetControls<IMyTerminalBlock>(out list);
                if (list == null) return;

                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                    if (!_captured.ContainsKey(c.Id)) _captured[c.Id] = c;
                }

                MyLog.Default.WriteLineAndConsole(
                    "GT REPAIR: captured " + _captured.Count + " base controls at load.");
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("GT REPAIR capture threw: " + e.Message);
            }
        }

        /// <summary>
        /// Put back any wanted control missing from the upgrade module list. True if
        /// anything was restored.
        /// </summary>
        public static bool Repair()
        {
            if (_repairAttempted) return false;
            _repairAttempted = true;

            try
            {
                List<IMyTerminalControl> current;
                MyAPIGateway.TerminalControls.GetControls<IMyUpgradeModule>(out current);

                var present = new HashSet<string>();
                if (current != null)
                    for (int i = 0; i < current.Count; i++)
                        if (current[i] != null) present.Add(current[i].Id);

                var missing = new List<string>();
                for (int i = 0; i < Wanted.Length; i++)
                    if (!present.Contains(Wanted[i])) missing.Add(Wanted[i]);

                if (missing.Count == 0) return false;

                MyLog.Default.WriteLineAndConsole("GT REPAIR: upgrade modules are missing "
                    + string.Join(", ", missing.ToArray()) + " - attempting to restore.");

                int restored = 0;
                for (int i = 0; i < missing.Count; i++)
                {
                    var control = Find(missing[i]);
                    if (control == null) continue;

                    MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(control);
                    restored++;
                }

                // Read the list back rather than trusting the calls. The entire reason
                // this file exists is a mod that assumed its writes had taken effect.
                List<IMyTerminalControl> after;
                MyAPIGateway.TerminalControls.GetControls<IMyUpgradeModule>(out after);

                var now = new HashSet<string>();
                if (after != null)
                    for (int i = 0; i < after.Count; i++)
                        if (after[i] != null) now.Add(after[i].Id);

                var stillMissing = new List<string>();
                for (int i = 0; i < Wanted.Length; i++)
                    if (!now.Contains(Wanted[i])) stillMissing.Add(Wanted[i]);

                var sb = new StringBuilder();
                sb.Append("GT REPAIR: re-added ").Append(restored).Append(" of ")
                  .Append(missing.Count).Append("; list is now ")
                  .Append(after == null ? 0 : after.Count).Append(" controls. Still absent: ")
                  .Append(stillMissing.Count == 0 ? "none" : string.Join(", ", stillMissing.ToArray()));
                MyLog.Default.WriteLineAndConsole(sb.ToString());

                return restored > 0;
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("GT REPAIR threw: " + e.Message);
                return false;
            }
        }

        // Captured first; otherwise harvest the same object from any block interface
        // whose list survived, ordered from most to least likely to be built early.
        private static IMyTerminalControl Find(string id)
        {
            IMyTerminalControl c;
            if (_captured.TryGetValue(id, out c) && c != null)
            {
                MyLog.Default.WriteLineAndConsole("GT REPAIR:   " + id + " from capture");
                return c;
            }

            c = HarvestFrom<IMyTerminalBlock>(id);
            if (c == null) c = HarvestFrom<IMyFunctionalBlock>(id);
            if (c == null) c = HarvestFrom<IMyLightingBlock>(id);
            if (c == null) c = HarvestFrom<IMyBatteryBlock>(id);
            if (c == null) c = HarvestFrom<IMyCargoContainer>(id);
            if (c == null) c = HarvestFrom<IMyTextPanel>(id);
            if (c == null) c = HarvestFrom<IMyDoor>(id);
            if (c == null) c = HarvestFrom<IMyReactor>(id);

            MyLog.Default.WriteLineAndConsole("GT REPAIR:   " + id
                + (c != null ? " harvested from another block type" : " NOT FOUND anywhere"));
            return c;
        }

        private static IMyTerminalControl HarvestFrom<T>(string id)
        {
            try
            {
                List<IMyTerminalControl> list;
                MyAPIGateway.TerminalControls.GetControls<T>(out list);
                if (list == null) return null;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].Id == id) return list[i];
            }
            catch { }
            return null;
        }
    }
}
