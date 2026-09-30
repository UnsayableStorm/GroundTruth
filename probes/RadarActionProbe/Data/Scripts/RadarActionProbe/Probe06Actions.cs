using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace RadarActionProbe
{
    // 0.6 - Toolbar actions on one block.
    //
    // QUESTION
    //   Can CustomActionGetter put Next / Previous / Clear actions on the Rotating
    //   Antenna ALONE, without registering them against every antenna, and do they
    //   survive a reload? Selecting a contact from a cockpit toolbar depends on it.
    //
    // WHY THE CARE
    //   This mod's whole history with terminal lists is that touching them early or
    //   broadly costs every block of a type its vanilla controls (TerminalApi.cs, and the
    //   GroundTruthSession.BeforeStart comment). So: the handler is subscribed at load,
    //   which is what GroundTruthSession does with CustomControlGetter and is safe because
    //   subscribing reads nothing; the actions are added only to the list the game hands
    //   this handler for ONE block; AddAction is never called; and a vanilla antenna's own
    //   action list is compared against a baseline on every call so any damage shows up
    //   at once.
    //
    // WHAT IS LOGGED
    //   ARMED           at load
    //   ALIVE           on every handler call for an antenna - the mechanism line
    //   call            subtype, action count before and after, whether ours were added
    //   baseline        the vanilla antenna's full action id list, on its first call
    //   EXECUTED        inside each action's delegate - an action that "did nothing" with
    //                   no EXECUTED line was never called (ENGINE_TRAPS 12)
    //
    // PROCEDURE (needs you)
    //   1. In a cockpit on RP-O open the toolbar config. Find the dish: the three probe
    //      actions must be listed. Find a vanilla antenna: they must NOT be.
    //   2. Put "RadarProbe Next" on the toolbar. Press it twice.
    //   3. Save, exit to the menu, reload. Press it again BEFORE opening any terminal or
    //      toolbar screen.
    //   4. Repeat 1-3 as a client on the dedicated server.
    //
    // WHAT DECIDES IT
    //   0.6.scoped   PASS  on the dish, all three actions are present; on a vanilla
    //                      antenna, none are
    //   0.6.vanilla  PASS  a vanilla antenna's action list is identical to its baseline
    //                FAIL  any change - STOP, remove this probe, record it
    //   0.6.execute, 0.6.reload, 0.6.side: judged at the gate from the EXECUTED lines and
    //   the procedure above. "side" is the side prefix on the EXECUTED lines during the
    //   dedicated-server run.
    //
    // WHAT EACH RESULT CHANGES
    //   vanilla FAIL -> toolbar actions are dropped from the plan permanently and the
    //                   result becomes an ENGINE_TRAPS entry
    //   reload FAIL  -> dropped for now; D4 ships with the dropdown only
    //
    // UNVERIFIED: the namespace of IMyTerminalAction (written as Sandbox.ModAPI.Interfaces),
    // CreateAction<T>, and the Name / Icon / Action / Enabled members on it.
    public sealed class Probe06Actions
    {
        public const string Id = "0.6";

        private const string ActNext = "RadarProbe_Next";
        private const string ActPrev = "RadarProbe_Prev";
        private const string ActClear = "RadarProbe_Clear";
        private static readonly string[] OurIds = new string[] { ActNext, ActPrev, ActClear };

        private readonly List<IMyTerminalAction> _ours = new List<IMyTerminalAction>();
        private string _baseline;
        private bool _hooked;

        private int _calls;
        private int _dishCalls;
        private int _vanillaCalls;
        private int _executed;

        public void Hook()
        {
            MyAPIGateway.TerminalControls.CustomActionGetter += OnGetter;
            _hooked = true;
            Log.Line(Id, "ARMED subscribed to CustomActionGetter; no action is registered against any block type");
        }

        public void Unhook()
        {
            if (!_hooked) return;
            try { MyAPIGateway.TerminalControls.CustomActionGetter -= OnGetter; } catch { }
            _hooked = false;
        }

        public void Status()
        {
            Log.Line(Id, "STATUS handlerCalls=" + _calls + " dishCalls=" + _dishCalls + " vanillaCalls=" + _vanillaCalls
                + " executed=" + _executed + " baseline=" + (_baseline == null ? "none yet" : "[" + _baseline + "]"));
        }

        private void OnGetter(IMyTerminalBlock block, List<IMyTerminalAction> actions)
        {
            try
            {
                // Only antennas matter: the dish is one, and the control is a vanilla one.
                // Every other block type is ignored so the log holds nothing irrelevant.
                if (!(block is Sandbox.ModAPI.IMyRadioAntenna)) return;

                _calls++;
                Log.Alive(Id, _calls);

                string subtype = block.BlockDefinition.SubtypeName;
                bool dish = Fixture.IsDish(block);
                int before = actions.Count;

                if (dish)
                {
                    _dishCalls++;
                    EnsureActions();
                    for (int i = 0; i < _ours.Count; i++)
                        if (!Has(actions, _ours[i].Id)) actions.Add(_ours[i]);

                    int present = CountOurs(actions);
                    Log.Line(Id, "call subtype=" + subtype + " id=" + block.EntityId + " actionsBefore=" + before
                        + " actionsAfter=" + actions.Count + " oursPresent=" + present + "/" + OurIds.Length);

                    if (present == OurIds.Length) Log.Pass("0.6.scoped", "all three actions present on the dish");
                    else Log.Fail("0.6.scoped", "only " + present + " of " + OurIds.Length + " actions present on the dish");
                }
                else
                {
                    _vanillaCalls++;
                    string ids = Join(actions);
                    int leaked = CountOurs(actions);

                    Log.Line(Id, "call subtype=" + subtype + " id=" + block.EntityId + " actionsBefore=" + before
                        + " actionsAfter=" + actions.Count + " oursPresent=" + leaked);

                    if (leaked == 0) Log.Pass("0.6.scoped", "no probe action on vanilla antenna " + subtype);
                    else Log.Fail("0.6.scoped", leaked + " probe action(s) leaked onto vanilla antenna " + subtype);

                    if (_baseline == null)
                    {
                        _baseline = ids;
                        Log.Line(Id, "baseline vanilla=" + subtype + " ids=[" + ids + "]");
                    }
                    else if (ids == _baseline)
                    {
                        Log.Pass("0.6.vanilla", "vanilla antenna action list identical to baseline (" + actions.Count + " ids)");
                    }
                    else
                    {
                        Log.Fail("0.6.vanilla", "vanilla antenna action list CHANGED. STOP: remove this probe and record it. now=[" + ids
                            + "] baseline=[" + _baseline + "]");
                    }
                }
            }
            catch (Exception e) { Log.Threw(Id, e); }
        }

        private void EnsureActions()
        {
            if (_ours.Count > 0) return;
            _ours.Add(Make(ActNext, "RadarProbe Next", "Textures\\GUI\\Icons\\Actions\\Increase.dds"));
            _ours.Add(Make(ActPrev, "RadarProbe Previous", "Textures\\GUI\\Icons\\Actions\\Decrease.dds"));
            _ours.Add(Make(ActClear, "RadarProbe Clear", "Textures\\GUI\\Icons\\Actions\\Off.dds"));
        }

        // A separate method, not a lambda in a loop, so each action closes over its own id.
        private IMyTerminalAction Make(string id, string name, string icon)
        {
            var a = MyAPIGateway.TerminalControls.CreateAction<IMyTerminalBlock>(id);
            a.Name = new StringBuilder(name);
            a.Icon = icon;
            a.Enabled = b => true;
            a.Action = b =>
            {
                _executed++;
                Log.Line(Id, "EXECUTED " + id + " on " + b.EntityId);
            };
            return a;
        }

        private static bool Has(List<IMyTerminalAction> list, string id)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].Id == id) return true;
            return false;
        }

        private static int CountOurs(List<IMyTerminalAction> list)
        {
            int n = 0;
            for (int i = 0; i < OurIds.Length; i++) if (Has(list, OurIds[i])) n++;
            return n;
        }

        private static string Join(List<IMyTerminalAction> list)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++) sb.Append(i > 0 ? "," : "").Append(list[i].Id);
            return sb.ToString();
        }
    }
}
