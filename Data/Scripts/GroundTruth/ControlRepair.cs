using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace GroundTruth
{
    // DEAD CODE, KEPT AS A RECORD. Nothing calls this. It was written to repair damage
    // we believed another mod was doing, and the belief was wrong: the empty control
    // list was OUR doing, caused by Ground Truth calling into MyAPIGateway.
    // TerminalControls on its own schedule. Registering only when the game asks fixed
    // it outright, and this file has had no call site since 2026-08-18. Read the
    // accusation below with that correction in mind - the pattern described is real
    // code, but nothing here establishes that it was responsible for what we saw.
    //
    // THE DAMAGE, AS IT WAS UNDERSTOOD AT THE TIME
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
        // OnOff is deliberately NOT in this list.
        //
        // Vanilla upgrade modules have no On/Off control at all - documented in
        // TerminalApi.cs weeks before this file existed, and confirmed again by
        // capture/harvest logs. Restoring it was scope creep past the actual report
        // (Name, ShowInTerminal, ShowInToolbarConfig), and it broke a THIRD mod.
        //
        // Measured 2026-08-18: harvesting "OnOff" by id across eight interfaces
        // returned a MyTerminalControlCheckbox<MyUpgradeModule> - a checkbox, not a
        // switch, presumably some other mod's control that happens to share the id.
        // Draygo's Block Extensions API (used by Build Info and others) finds any
        // control named "OnOff" and hard-casts it to IMyTerminalControlOnOffSwitch
        // with no type check, and crashed every client that opened the block:
        //
        //   InvalidCastException: MyTerminalControlCheckbox`1[MyUpgradeModule] ->
        //   IMyTerminalControlOnOffSwitch
        //     at Draygo.BlockExtensionsAPI.DefinitionExtensionsAPICore
        //         .TerminalControls_CustomControlGetter
        //
        // OUR bug, not theirs. Their cast assumes a control named "OnOff" on a block
        // type is the switch that has always been there - which was true of every world
        // that existed until we grafted a foreign control onto that type under the same
        // id. It never fired before because the situation could not arise. We do not get
        // to invent a control on a type that never had one, discover that a same-named
        // control means something different in another mod's world, and call the result
        // a repair. Same-id-different-type is not a hazard specific to OnOff - it is
        // inherent to searching by id across interfaces we do not own - so Find() below
        // now verifies TYPE, not just id, before returning anything harvested.
        // CustomData is deliberately not attempted, harvest or synthesis. Vanilla
        // Custom Data is a button that opens a full multi-line editor screen - not an
        // inline field - and no mod API exposes that screen. A synthesised textbox
        // would hold the same string but behave nothing like the control a player
        // remembers, which teaches them to distrust it. Faked shape, not faked absence,
        // is the harm; a missing field is honest, a wrong one is not.
        private static readonly string[] Wanted =
        {
            "Name", "ShowInTerminal", "ShowInToolbarConfig", "ShowOnHUD"
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

        // Long enough that a type's controls will have been built in the normal course
        // of things. Repairing earlier risks mistaking "not built yet" for "destroyed".
        private const int GraceSeconds = 10;
        private static int _seconds;

        /// <summary>
        /// Put back any wanted control missing from the upgrade module list, but only
        /// once damage is proven. True if anything was restored.
        /// </summary>
        public static bool Repair()
        {
            if (_repairAttempted) return false;
            if (++_seconds < GraceSeconds) return false;

            // AN EMPTY LIST IS NOT PROOF OF DAMAGE.
            //
            // On a healthy server the upgrade module list is also empty until the game
            // builds it, and a client joining reads exactly the same zero. Repairing
            // then would make US the creator of a list holding six controls and none of
            // the rest - the precise bug this file exists to undo, inflicted on servers
            // that never had the problem.
            //
            // The base list is the discriminator. If IMyTerminalBlock still holds Name
            // and ShowInTerminal, nothing has been wiped and an empty type list simply
            // means the game has not got to it yet - so wait, and let it.
            // WHAT COUNTS AS PROOF OF DAMAGE.
            //
            // Not "the base list looks empty" - this mod has never measured what that
            // list holds in a HEALTHY session, and acting on an unverified belief about
            // it is how you synthesise duplicate controls onto servers that were fine.
            //
            // The symptom itself is the test, and it needs both halves:
            //
            //   list is EMPTY            -> the game has not built it yet. Not damage.
            //                               Say nothing, try again next second.
            //   list is NON-EMPTY + Name -> healthy. Nothing to do, ever.
            //   list is NON-EMPTY - Name -> something built this list without the
            //                               inherited controls. That is the bug.
            List<IMyTerminalControl> probe;
            MyAPIGateway.TerminalControls.GetControls<IMyUpgradeModule>(out probe);

            if (probe == null || probe.Count == 0) return false;   // not built yet - wait

            bool hasName = false;
            for (int i = 0; i < probe.Count; i++)
                if (probe[i] != null && probe[i].Id == "Name") { hasName = true; break; }

            if (hasName)
            {
                _repairAttempted = true;
                MyLog.Default.WriteLineAndConsole(
                    "GT REPAIR: upgrade module controls are intact - nothing to repair.");
                return false;
            }

            _repairAttempted = true;

            try
            {
                // Restore the SHARED list first, if we managed to capture anything to
                // restore it with. Every block type built after this inherits from it,
                // so it helps blocks this mod has nothing to do with.
                if (_captured.Count > 0) RepairBaseList();

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

                    // Nothing to borrow: Animation Engine emptied the base list before
                    // any type inherited from it, so these objects exist nowhere in the
                    // session. Measured on Long Haul - eight interfaces searched, all
                    // absent, three sessions running. Build a replacement instead.
                    if (control == null) control = Synthesise(missing[i]);
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


        private static bool BaseListIsHealthy()
        {
            try
            {
                List<IMyTerminalControl> list;
                MyAPIGateway.TerminalControls.GetControls<IMyTerminalBlock>(out list);
                if (list == null) return false;

                bool name = false, show = false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] == null) continue;
                    if (list[i].Id == "Name") name = true;
                    else if (list[i].Id == "ShowInTerminal") show = true;
                }
                return name && show;
            }
            catch { return false; }
        }

        private static void RepairBaseList()
        {
            try
            {
                List<IMyTerminalControl> list;
                MyAPIGateway.TerminalControls.GetControls<IMyTerminalBlock>(out list);

                var present = new HashSet<string>();
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null) present.Add(list[i].Id);

                int restored = 0;
                foreach (var kv in _captured)
                {
                    if (present.Contains(kv.Key) || kv.Value == null) continue;
                    MyAPIGateway.TerminalControls.AddControl<IMyTerminalBlock>(kv.Value);
                    restored++;
                }

                MyLog.Default.WriteLineAndConsole(
                    "GT REPAIR: shared IMyTerminalBlock list was damaged - restored "
                    + restored + " control(s) to it.");
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("GT REPAIR base restore threw: " + e.Message);
            }
        }

        // Same id does not mean same control across interfaces we do not own - that is
        // how we crashed clients through Draygo's Block Extensions API. Every id we might harvest has one specific vanilla
        // shape, checked here before anything is accepted.
        private static bool MatchesExpectedShape(string id, IMyTerminalControl c)
        {
            switch (id)
            {
                case "Name":
                case "CustomData":
                    return c is IMyTerminalControlTextbox;
                case "ShowInTerminal":
                case "ShowInToolbarConfig":
                case "ShowOnHUD":
                    return c is IMyTerminalControlCheckbox;
                default:
                    return false;   // unknown id: refuse rather than guess
            }
        }

        // Captured first; otherwise harvest the same object from any block interface
        // whose list survived, ordered from most to least likely to be built early.
        // Anything not matching the expected shape for that id is rejected rather than
        // used - a wrongly-typed control is worse than a missing one.
        //
        // No local functions here - the game's mod compiler enforces C# 6, and a local
        // function is a C# 7 feature. My own compile checker did not catch this: it
        // sets /langversion:6 but Roslyn did not enforce it the way SE's compiler does,
        // so it reported clean on code the game rejected outright. Fixed alongside this.
        private static IMyTerminalControl Find(string id)
        {
            IMyTerminalControl c;
            if (_captured.TryGetValue(id, out c) && c != null && MatchesExpectedShape(id, c))
            {
                MyLog.Default.WriteLineAndConsole("GT REPAIR:   " + id + " from capture");
                return c;
            }

            c = HarvestTyped<IMyTerminalBlock>(id);
            if (c == null) c = HarvestTyped<IMyFunctionalBlock>(id);
            if (c == null) c = HarvestTyped<IMyLightingBlock>(id);
            if (c == null) c = HarvestTyped<IMyBatteryBlock>(id);
            if (c == null) c = HarvestTyped<IMyCargoContainer>(id);
            if (c == null) c = HarvestTyped<IMyTextPanel>(id);
            if (c == null) c = HarvestTyped<IMyDoor>(id);
            if (c == null) c = HarvestTyped<IMyReactor>(id);

            MyLog.Default.WriteLineAndConsole("GT REPAIR:   " + id
                + (c != null ? " harvested from another block type" : " nothing of the right shape found"));
            return c;
        }

        private static IMyTerminalControl HarvestTyped<T>(string wantedId)
        {
            var found = HarvestFrom<T>(wantedId);
            return found != null && MatchesExpectedShape(wantedId, found) ? found : null;
        }


        // ---- last resort: build replacements ----
        //
        // These are imitations. They carry the vanilla ids and titles and drive the same
        // block properties, so they behave correctly and anything looking a control up
        // by id still finds one - but they are ours, not Keen's, and they only ever get
        // added to a list already PROVEN to be missing them.
        //
        // CustomData is the compromise: vanilla opens a full editor screen, which no mod
        // API exposes. A textbox holds the same string and is a great deal better than
        // no access at all.
        private static IMyTerminalControl Synthesise(string id)
        {
            try
            {
                switch (id)
                {
                    case "Name":
                        return Textbox("Name", "Name",
                            b => b.CustomName,
                            (b, v) => b.CustomName = v);

                    case "ShowInTerminal":
                        return Checkbox("ShowInTerminal", "Show in terminal",
                            b => b.ShowInTerminal,
                            (b, v) => b.ShowInTerminal = v);

                    case "ShowInToolbarConfig":
                        return Checkbox("ShowInToolbarConfig", "Show in toolbar config",
                            b => b.ShowInToolbarConfig,
                            (b, v) => b.ShowInToolbarConfig = v);

                    case "ShowOnHUD":
                        return Checkbox("ShowOnHUD", "Show on HUD",
                            b => b.ShowOnHUD,
                            (b, v) => b.ShowOnHUD = v);
                }
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("GT REPAIR synth " + id + " threw: " + e.Message);
            }
            return null;
        }

        private static IMyTerminalControl Textbox(string id, string title,
            Func<IMyTerminalBlock, string> get, Action<IMyTerminalBlock, string> set)
        {
            var c = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlTextbox, IMyUpgradeModule>(id);
            c.Title = MyStringId.GetOrCompute(title);
            c.SupportsMultipleBlocks = false;
            c.Getter = b => new StringBuilder(get(b));
            c.Setter = (b, sb) => set(b, sb.ToString());
            MyLog.Default.WriteLineAndConsole("GT REPAIR:   " + id + " SYNTHESISED (replacement, not Keen's)");
            return c;
        }

        private static IMyTerminalControl Checkbox(string id, string title,
            Func<IMyTerminalBlock, bool> get, Action<IMyTerminalBlock, bool> set)
        {
            var c = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlCheckbox, IMyUpgradeModule>(id);
            c.Title = MyStringId.GetOrCompute(title);
            c.SupportsMultipleBlocks = true;
            c.Getter = b => get(b);
            c.Setter = (b, v) => set(b, v);
            MyLog.Default.WriteLineAndConsole("GT REPAIR:   " + id + " SYNTHESISED (replacement, not Keen's)");
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
