using System;
using System.Text;
using Sandbox.ModAPI;
using VRage.Utils;

namespace GroundTruth
{
    // What every Ground Truth Event Controller event shares: where readings come from,
    // who is allowed to fire, and the info pane.
    //
    // READINGS COME FROM THE SESSION, NOT FROM GT_ PROPERTIES
    //
    // The events used to read their inputs with GetValueFloat("GT_..."), the same way a
    // Programmable Block does. That made them hostage to the property registration
    // rules in TerminalApi: the properties exist only after the game has built some
    // upgrade module's terminal in this session, and on a dedicated server they never
    // exist unless a script asks. Until then GetValueFloat throws, the helpers turned
    // that into "no reading", and an event with no reading never fires. So:
    //
    //   - on a dedicated server, every event was dead, always;
    //   - in single player, every event was dead after each load until somebody opened
    //     an instrument's terminal, and the block list offered no instruments at all.
    //
    // Reported from the Workshop 2026-10-05: an oxygen event wired to a toggle did
    // nothing and showed nothing. The properties are a public API for scripts; the
    // events are part of the mod and read the same BlockState the properties do,
    // directly. TerminalApi's getters are the reference for what each reading means,
    // and the event code mirrors them expression for expression.
    //
    // ONLY THE SERVER FIRES
    //
    // MyEventControllerBlock.TriggerAction returns at once unless Sync.IsServer
    // (decompiled 2026-10-05), so client calls were always harmless no-ops. Gating here
    // says so out loud instead of relying on it. Clients still evaluate every second -
    // that is what feeds the info pane - they just do not fire.
    //
    // THE INFO PANE
    //
    // Vanilla events write threshold, per-block values and the last output into the
    // controller's detailed info through MyEventControllerGenericEvent, every member of
    // which the whitelist prohibits. The mod route to the same pane is CustomInfo:
    // AppendingCustomInfo plus RefreshCustomInfo, which every terminal block renders
    // under its detailed info. Each client builds its own text from its own readings,
    // so nothing has to be sent over the network.
    public static class EventSupport
    {
        public static bool IsServer
        {
            get { return MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer; }
        }

        /// <summary>A dedicated server renders no terminal, so it writes no pane.</summary>
        public static bool HasDisplay
        {
            get { return MyAPIGateway.Utilities != null && !MyAPIGateway.Utilities.IsDedicated; }
        }

        /// <summary>Instrument role, or 0 for anything that is not one of ours.</summary>
        public static float RoleOf(IMyTerminalBlock block)
        {
            if (block == null || !Instruments.Is(block)) return 0f;
            return Instruments.RoleOf(block.BlockDefinition.SubtypeName);
        }

        /// <summary>
        /// The block's readings, or null if it is not an instrument. Creates and computes
        /// state on first access, exactly as a property read would, so an instrument
        /// nobody has opened still answers.
        /// </summary>
        public static GroundTruthSession.BlockState State(IMyTerminalBlock block)
        {
            return GroundTruthSession.StateFor(block);
        }

        /// <summary>Slot 0 is the "yes" toolbar, slot 1 the "no".</summary>
        public static void Fire(IMyEventControllerBlock ev, bool yes)
        {
            if (!IsServer) return;

            // TriggerAction returns void and can decline silently (not working, not
            // functional, blocks still resolving, or no toolbar item it is allowed to
            // run). ActionTriggered is raised once per slot it actually activates, inside
            // the call - so counting it says whether the controller did anything at all.
            // 2026-10-05: the event logged FIRE and the light stayed off; this is the line
            // that tells the event's half from the controller's.
            int slot = yes ? 0 : 1;
            var ran = new System.Collections.Generic.List<int>();
            Action<int> count = i => ran.Add(i);
            try
            {
                ev.ActionTriggered += count;
                ev.TriggerAction(slot);
            }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole("GT event trigger: " + e); }
            finally
            {
                try { ev.ActionTriggered -= count; } catch { }
            }

            Trace(ev, "TRIGGER slot=" + slot + " working=" + ev.IsWorking + " functional=" + ev.IsFunctional
                + " ran=[" + string.Join(",", ran) + "]", null);
        }

        // Same rounding the vanilla events use: value x 100, one decimal.
        public static string Percent(float fraction)
        {
            return (fraction * 100f).ToString("0.0") + " %";
        }

        public static string Output(bool? state, string yes, string no)
        {
            if (!state.HasValue) return "Output: waiting for a reading";
            return state.Value ? "Output: 1 - " + yes : "Output: 2 - " + no;
        }

        // ---- trace --------------------------------------------------------------------
        //
        // "The light never came on" (2026-10-05) arrived with no log evidence either way:
        // these events wrote nothing, so a dead tick, a reading that never changed and a
        // fire the controller refused all looked identical (ENGINE_TRAPS 12). So every
        // block-list change, every latch and every fire is logged, plus a heartbeat once a
        // minute per selected event carrying exactly what the pane shows. Volume: a line
        // a minute per configured controller, and one per real transition.
        //
        //   GT EVENT [SP|HOST|SERVER|CLIENT] <controller id> <what> | <pane text>

        public const double HeartbeatSeconds = 60.0;

        public static void Trace(IMyTerminalBlock controller, string what, StringBuilder pane)
        {
            string side = !IsServer ? "CLIENT"
                : (MyAPIGateway.Multiplayer != null && MyAPIGateway.Multiplayer.MultiplayerActive
                    ? (HasDisplay ? "HOST" : "SERVER") : "SP");
            MyLog.Default.WriteLineAndConsole("GT EVENT [" + side + "] "
                + (controller == null ? "?" : controller.EntityId.ToString()) + " " + what
                + (pane == null ? "" : " | " + pane.ToString().Replace('\n', '|')));
        }

        /// <summary>Logs a latch, a fire, or the heartbeat when one is due.</summary>
        public static void TraceTick(IMyTerminalBlock controller, string transition, StringBuilder pane, ref double lastBeat)
        {
            double now = GroundTruthSession.Seconds;
            if (transition != null) { Trace(controller, transition, pane); lastBeat = now; return; }
            if (now - lastBeat >= HeartbeatSeconds) { Trace(controller, "BEAT", pane); lastBeat = now; }
        }
    }

    /// <summary>
    /// One event component's share of its controller's info pane.
    ///
    /// Every Ground Truth event component exists on every Event Controller - one per
    /// event type - and only the selected one has anything to say. Each therefore
    /// writes only while it is the selected event, and clears what it wrote when it
    /// stops being selected, or the pane would keep a stale reading from an event the
    /// player has since switched away from.
    /// </summary>
    public sealed class EventPane
    {
        private readonly StringBuilder _next = new StringBuilder();
        private string _shown = "";
        private IMyTerminalBlock _hooked;

        /// <summary>Start building this second's text.</summary>
        public StringBuilder Begin()
        {
            _next.Clear();
            return _next;
        }

        /// <summary>Show what Begin built, refreshing the pane only if it changed.</summary>
        public void Commit(IMyTerminalBlock controller)
        {
            if (controller == null || !EventSupport.HasDisplay) return;
            Hook(controller);

            var text = _next.ToString();
            if (text == _shown) return;
            _shown = text;
            controller.RefreshCustomInfo();
        }

        /// <summary>Withdraw from the pane, once, when no longer the selected event.</summary>
        public void Clear(IMyTerminalBlock controller)
        {
            if (_shown.Length == 0) return;
            _shown = "";
            if (controller != null) controller.RefreshCustomInfo();
        }

        public void Unhook()
        {
            if (_hooked == null) return;
            _hooked.AppendingCustomInfo -= Append;
            _hooked = null;
        }

        private void Hook(IMyTerminalBlock controller)
        {
            if (_hooked == controller) return;
            Unhook();
            _hooked = controller;
            _hooked.AppendingCustomInfo += Append;
        }

        private void Append(IMyTerminalBlock block, StringBuilder info)
        {
            if (_shown.Length > 0) info.Append(_shown);
        }
    }
}
