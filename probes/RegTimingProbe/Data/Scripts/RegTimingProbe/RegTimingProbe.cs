using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;

namespace RegTimingProbe
{
    // RegTimingProbe - can terminal properties be registered against IMyUpgradeModule at
    // world load, without waiting for a player to open a terminal, and without costing any
    // block type its vanilla controls?
    //
    // WHY THIS IS A QUESTION AT ALL
    //
    // Ground Truth registers its GT_ properties only from CustomControlGetter, i.e. only
    // once somebody opens an upgrade module's terminal, and on a dedicated server only when
    // a PB writes GT_API_ENABLE. That was the fix for 2026-08-18, when early registration
    // was blamed for every upgrade module losing Name / On/Off / Show in Terminal. Jason,
    // 2026-10-05: depending on a terminal being opened is not acceptable.
    //
    // WHAT THE DECOMPILE SAYS (Space Engineers 01_210_014, read 2026-10-05)
    //
    //   - MyTerminalControlFactory.GetList(type) creates a type's control list on first
    //     request by copying whatever its BASE classes' lists hold at that moment, and the
    //     type then counts as "created" for good.
    //   - Each terminal block builds its own and its bases' lists in
    //     MyTerminalBlock.BeforeGameLogicInit -> CreateTerminalControls(). That runs during
    //     block init on every machine, dedicated servers included. No UI involved.
    //   - The ModAPI's CreateProperty, AddControl and GetControls all go straight to
    //     GetList. None of them calls EnsureControlsAreCreated; only CreateControl does.
    //
    // So touching a list is harmful only BEFORE any block of the relevant type has
    // initialised: at LoadData, say, it freezes MyTerminalBlock's list empty and every
    // block type in the game loses Name. In August every early-registration build still
    // had ControlRepair.Capture() calling GetControls<IMyTerminalBlock> at LoadData
    // (commit a1e32f2 found it), which is exactly that. The hypothesis under test: the
    // timing was never the problem; that LoadData read was. Register from an upgrade
    // module's own UpdateOnceBeforeFrame - after its BeforeGameLogicInit - and every list
    // involved has already been built by the game.
    //
    // WHAT THIS PROBE DOES
    //
    //   - LoadData: subscribes to CustomControlGetter (to COUNT terminal opens - it never
    //     edits the list) and to chat. No other terminal API call happens before the
    //     trigger. That is the point: nothing here may touch a list early.
    //   - TRIGGER: the first vanilla upgrade module (Productivity / Effectiveness / Energy)
    //     to reach UpdateOnceBeforeFrame. Reads the IMyUpgradeModule list, registers two
    //     properties (REGPROBE_Value = 42, REGPROBE_Flag = true), reads it again.
    //   - CHECKS at 10 s, 60 s and 300 s after the first simulation frame, and on
    //     /regprobe status: the IMyUpgradeModule list; every terminal block TYPE present
    //     in the world, by instance, for its vanilla properties; that our properties
    //     appear on upgrade modules only; and that an upgrade module returns 42 when read.
    //
    // WHAT IS LOGGED (tag REGPROBE)
    //   ARMED                 at LoadData
    //   R.trigger RECORD      which block triggered, when, terminal opens so far
    //   R.prelist             IMyUpgradeModule list at the trigger, before we add anything
    //   R.register            the same list after adding our two
    //   R.list.<when>         that list later - catches anything that empties it afterwards
    //   R.types.<when>        every terminal block type in the world, one instance each:
    //                         Name, ShowInTerminal, ShowInToolbarConfig, and OnOff on
    //                         functional blocks. THE check: this is the August symptom.
    //   R.scope.<when>        REGPROBE_Value must not appear on non-upgrade-module blocks
    //   R.read.<when>         an upgrade module returns 42 for REGPROBE_Value
    //   R.noterminal          whether the 10 s read happened before ANY terminal was opened
    //   R.open RECORD         the first five terminal opens: type, control count, missing
    //
    // PROCEDURE - see probes/RegTimingProbe/README.md.
    //
    // WHAT DECIDES IT
    //   PASS overall = R.prelist, R.register, R.list.*, R.types.*, R.scope.*, R.read.* all
    //   PASS, R.noterminal PASS, in every run (SP, DS server, DS client, and the
    //   Animation Engine runs), plus the eyes-on terminal check in the README.
    //   Any R.types FAIL: stop, remove the probe from the world, record it.
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class RegTimingProbeSession : MySessionComponentBase
    {
        public const string PropValue = "REGPROBE_Value";
        public const string PropFlag = "REGPROBE_Flag";

        // The vanilla controls every upgrade module must carry - the six the August
        // measurement tracked.
        private static readonly string[] UpgradeCore =
            { "OnOff", "ShowInTerminal", "ShowInToolbarConfig", "Name", "ShowOnHUD", "CustomData" };

        private const int TicksPerSecond = 60;
        private static readonly int[] CheckSeconds = { 10, 60, 300 };

        private static RegTimingProbeSession _instance;

        private long _tick;
        private long _triggerTick = -1;
        private bool _registered;
        private bool _getterHooked;
        private bool _chatHooked;
        private bool _statusRequested;
        private int _nextCheck;
        private int _terminalOpens;
        private int _openLogged;
        private bool _noTerminalJudged;

        public override void LoadData()
        {
            _instance = this;
            Log.DecideSide();

            // Subscribing reads and writes nothing; it asks to be told when the game builds a
            // list. Ground Truth has done this at LoadData safely since August.
            try
            {
                MyAPIGateway.TerminalControls.CustomControlGetter += OnControlGetter;
                _getterHooked = true;
            }
            catch (Exception e) { Log.Threw("hook getter", e); }

            try
            {
                MyAPIGateway.Utilities.MessageEntered += OnMessage;
                _chatHooked = true;
            }
            catch (Exception e) { Log.Threw("hook chat", e); }

            Log.Line("ARMED - no terminal API call until an upgrade module initialises");
        }

        protected override void UnloadData()
        {
            if (_getterHooked)
            {
                try { MyAPIGateway.TerminalControls.CustomControlGetter -= OnControlGetter; } catch { }
                _getterHooked = false;
            }
            if (_chatHooked)
            {
                try { MyAPIGateway.Utilities.MessageEntered -= OnMessage; } catch { }
                _chatHooked = false;
            }
            _instance = null;
        }

        public override void UpdateAfterSimulation()
        {
            _tick++;
            if (_tick == 1) Log.DecideSide();   // multiplayer state is settled by now

            if (_nextCheck < CheckSeconds.Length && _tick == CheckSeconds[_nextCheck] * TicksPerSecond)
            {
                RunChecks(CheckSeconds[_nextCheck] + "s");
                _nextCheck++;
            }

            if (_statusRequested)
            {
                _statusRequested = false;
                RunChecks("manual");
                Log.WriteSummary();
                try { MyAPIGateway.Utilities.ShowMessage("RegProbe", "Checks run; see the game log (REGPROBE)."); } catch { }
            }
        }

        private void OnMessage(string text, ref bool sendToOthers)
        {
            if (text == null) return;
            if (!text.Trim().Equals("/regprobe status", StringComparison.OrdinalIgnoreCase)) return;
            _statusRequested = true;
            sendToOthers = false;
        }

        // ---- the trigger ----------------------------------------------------------------

        public static void UpgradeModuleReady(IMyTerminalBlock block)
        {
            var self = _instance;
            if (self == null || self._registered || block == null) return;
            self._registered = true;
            self.Register(block);
        }

        private void Register(IMyTerminalBlock block)
        {
            _triggerTick = _tick;
            Log.DecideSide();
            Log.Record("R.trigger", "subtype=" + block.BlockDefinition.SubtypeName
                + " entity=" + block.EntityId
                + " grid=" + (block.CubeGrid != null ? block.CubeGrid.DisplayName : "?")
                + " tick=" + _tick
                + " terminalOpensSoFar=" + _terminalOpens);

            int before = -1;
            try
            {
                List<IMyTerminalControl> list;
                MyAPIGateway.TerminalControls.GetControls<IMyUpgradeModule>(out list);
                before = list == null ? -1 : list.Count;
                string missing = Missing(list, UpgradeCore);
                Log.Record("R.prelist", "count=" + before + " missing=[" + missing + "] ids=" + Ids(list));
                if (before > 0 && missing.Length == 0)
                    Log.Pass("R.prelist", "vanilla upgrade module controls present at the trigger, count=" + before);
                else
                    Log.Fail("R.prelist", "count=" + before + " missing=[" + missing + "]");
            }
            catch (Exception e) { Log.Threw("R.prelist", e); Log.Fail("R.prelist", "threw"); }

            try
            {
                var value = MyAPIGateway.TerminalControls.CreateProperty<float, IMyUpgradeModule>(PropValue);
                value.Getter = b => 42f;
                value.Setter = (b, v) => { };
                MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(value);

                var flag = MyAPIGateway.TerminalControls.CreateProperty<bool, IMyUpgradeModule>(PropFlag);
                flag.Getter = b => true;
                flag.Setter = (b, v) => { };
                MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(flag);

                Log.Record("R.register", "added " + PropValue + " and " + PropFlag);
            }
            catch (Exception e) { Log.Threw("R.register add", e); Log.Fail("R.register", "add threw"); return; }

            try
            {
                List<IMyTerminalControl> list;
                MyAPIGateway.TerminalControls.GetControls<IMyUpgradeModule>(out list);
                int after = list == null ? -1 : list.Count;
                string missing = Missing(list, UpgradeCore);
                bool ours = Has(list, PropValue) && Has(list, PropFlag);
                Log.Record("R.register", "before=" + before + " after=" + after + " missing=[" + missing + "] ours=" + Log.B(ours));
                if (missing.Length == 0 && ours && after == before + 2)
                    Log.Pass("R.register", "list grew by exactly our two, vanilla intact");
                else
                    Log.Fail("R.register", "before=" + before + " after=" + after + " missing=[" + missing + "] ours=" + Log.B(ours));
            }
            catch (Exception e) { Log.Threw("R.register read", e); Log.Fail("R.register", "read threw"); }
        }

        // ---- checks ---------------------------------------------------------------------

        private void RunChecks(string when)
        {
            Log.Line("CHECK " + when + " tick=" + _tick + " registered=" + Log.B(_registered)
                + " triggerTick=" + _triggerTick + " terminalOpens=" + _terminalOpens);

            if (!_registered)
            {
                Log.Inconclusive("R.trigger", "no vanilla upgrade module has initialised by " + when
                    + " - the fixture needs one (Productivity, Effectiveness or Energy module)");
                return;
            }

            CheckList(when);
            CheckTypes(when);
        }

        private void CheckList(string when)
        {
            string check = "R.list." + when;
            try
            {
                List<IMyTerminalControl> list;
                MyAPIGateway.TerminalControls.GetControls<IMyUpgradeModule>(out list);
                string missing = Missing(list, UpgradeCore);
                bool ours = Has(list, PropValue) && Has(list, PropFlag);
                int count = list == null ? -1 : list.Count;
                if (missing.Length == 0 && ours) Log.Pass(check, "count=" + count + " vanilla and ours present");
                else Log.Fail(check, "count=" + count + " missing=[" + missing + "] ours=" + Log.B(ours));
            }
            catch (Exception e) { Log.Threw(check, e); Log.Fail(check, "threw"); }
        }

        // One instance of every terminal block type in the world. Reading through an
        // INSTANCE goes to a list the game built when that block initialised, so this check
        // cannot itself create anybody's list early - unlike GetControls<T> on a type that
        // may not exist in the world.
        private void CheckTypes(string when)
        {
            var seen = new Dictionary<string, IMyTerminalBlock>();
            IMyTerminalBlock upgrade = null;

            try
            {
                var grids = new HashSet<IMyEntity>();
                MyAPIGateway.Entities.GetEntities(grids, e => e is IMyCubeGrid);
                var slims = new List<IMySlimBlock>();
                foreach (var e in grids)
                {
                    var g = e as IMyCubeGrid;
                    if (g == null || g.Physics == null) continue;   // skip projections
                    slims.Clear();
                    g.GetBlocks(slims, s => s.FatBlock is IMyTerminalBlock);
                    for (int i = 0; i < slims.Count; i++)
                    {
                        var tb = slims[i].FatBlock as IMyTerminalBlock;
                        if (tb == null) continue;
                        string type = tb.BlockDefinition.TypeId.ToString();
                        if (!seen.ContainsKey(type)) seen[type] = tb;
                        if (upgrade == null && tb is IMyUpgradeModule) upgrade = tb;
                    }
                }
            }
            catch (Exception e) { Log.Threw("R.types." + when + " walk", e); }

            var broken = new StringBuilder();
            var leaked = new StringBuilder();
            var names = new List<string>(seen.Keys);
            names.Sort();

            for (int i = 0; i < names.Count; i++)
            {
                var b = seen[names[i]];
                string miss = "";
                try
                {
                    if (b.GetProperty("Name") == null) miss += "Name ";
                    if (b.GetProperty("ShowInTerminal") == null) miss += "ShowInTerminal ";
                    if (b.GetProperty("ShowInToolbarConfig") == null) miss += "ShowInToolbarConfig ";
                    if (b is IMyFunctionalBlock && b.GetProperty("OnOff") == null) miss += "OnOff ";

                    bool hasOurs = b.GetProperty(PropValue) != null;
                    if (hasOurs && !(b is IMyUpgradeModule)) leaked.Append(names[i]).Append(' ');
                }
                catch (Exception e) { miss += "THREW:" + e.GetType().Name + " "; }

                if (miss.Length > 0) broken.Append(names[i]).Append("(").Append(miss.Trim()).Append(") ");
            }

            Log.Record("R.types." + when, "types=" + names.Count + " [" + string.Join(" ", names) + "]");

            if (names.Count == 0)
                Log.Inconclusive("R.types." + when, "no terminal blocks found in the world");
            else if (broken.Length == 0)
                Log.Pass("R.types." + when, names.Count + " block types, every one has its vanilla controls");
            else
                Log.Fail("R.types." + when, "missing: " + broken.ToString().Trim());

            if (leaked.Length == 0) Log.Pass("R.scope." + when, "REGPROBE_Value on upgrade modules only");
            else Log.Fail("R.scope." + when, "REGPROBE_Value also on: " + leaked.ToString().Trim());

            string read = "R.read." + when;
            if (upgrade == null)
            {
                Log.Inconclusive(read, "no upgrade module in the world to read");
            }
            else
            {
                try
                {
                    float v = upgrade.GetValueFloat(PropValue);
                    if (v == 42f) Log.Pass(read, upgrade.CustomName + " returned 42");
                    else Log.Fail(read, upgrade.CustomName + " returned " + v);
                }
                catch (Exception e) { Log.Fail(read, "GetValueFloat threw " + e.GetType().Name + " - property absent in this process"); }
            }

            // The whole point is "works before anyone opens a terminal". The first check is
            // only evidence of that if nobody had.
            if (!_noTerminalJudged)
            {
                _noTerminalJudged = true;
                if (_terminalOpens == 0) Log.Pass("R.noterminal", "first check at " + when + " ran with zero terminal opens in this process");
                else Log.Inconclusive("R.noterminal", _terminalOpens + " terminal opens before the first check - reload and wait 10 s without opening one");
            }
        }

        // ---- terminal opens: counted and described, never edited -----------------------

        private void OnControlGetter(IMyTerminalBlock block, List<IMyTerminalControl> controls)
        {
            _terminalOpens++;
            if (_openLogged >= 5 || block == null) return;
            _openLogged++;

            try
            {
                string type = block.BlockDefinition.TypeId + "/" + block.BlockDefinition.SubtypeName;
                bool upgrade = block is IMyUpgradeModule;
                string missing = upgrade ? Missing(controls, UpgradeCore) : "";
                Log.Record("R.open", "n=" + _terminalOpens + " block=" + type
                    + " controls=" + (controls == null ? -1 : controls.Count)
                    + (upgrade ? " missing=[" + missing + "] ours=" + Log.B(Has(controls, PropValue)) : "")
                    + " registeredYet=" + Log.B(_registered));
            }
            catch (Exception e) { Log.Threw("R.open", e); }
        }

        // ---- helpers --------------------------------------------------------------------

        private static bool Has(List<IMyTerminalControl> list, string id)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].Id == id) return true;
            return false;
        }

        private static string Missing(List<IMyTerminalControl> list, string[] ids)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < ids.Length; i++)
                if (!Has(list, ids[i])) sb.Append(ids[i]).Append(' ');
            return sb.ToString().Trim();
        }

        private static string Ids(List<IMyTerminalControl> list)
        {
            if (list == null) return "null";
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(list[i] == null ? "null" : list[i].Id);
            }
            return sb.ToString();
        }
    }

    // The trigger: the first vanilla upgrade module to reach its first frame. By then its
    // BeforeGameLogicInit has run CreateTerminalControls for MyTerminalBlock and
    // MyFunctionalBlock, so every list registration touches already exists.
    //
    // Vanilla subtypes, and Ground Truth's instruments - the same block class
    // (MyUpgradeModule), and the real case: the tactical testing world has a Weather
    // Station and no vanilla module. On GT subtypes this sits beside InstrumentPower, so
    // F11 shows one "Possible entity type script logic collision" notice per subtype
    // while the probe is loaded. Expected, harmless, gone when the probe is removed.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false,
        "LargeProductivityModule", "LargeEffectivenessModule", "LargeEnergyModule",
        "GT_RadiationMonitor", "GT_RadiationMonitor_S",
        "GT_RadiationMonitorAlt", "GT_RadiationMonitorAlt_S",
        "GT_WeatherStation", "GT_WeatherStation_S",
        "GT_WeatherStationAlt", "GT_WeatherStationAlt_S",
        "GT_BioScanner", "GT_BioScanner_S",
        "GT_BioScannerAlt", "GT_BioScannerAlt_S",
        "GT_HabitatMonitor", "GT_HabitatMonitor_S",
        "GT_HabitatMonitorAlt", "GT_HabitatMonitorAlt_S")]
    public class RegTrigger : MyGameLogicComponent
    {
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try { RegTimingProbeSession.UpgradeModuleReady(Entity as IMyTerminalBlock); }
            catch (Exception e) { Log.Threw("trigger", e); }
        }
    }
}
