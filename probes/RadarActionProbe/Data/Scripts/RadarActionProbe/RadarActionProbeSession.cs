using System;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;

namespace RadarActionProbe
{
    // RadarActionProbe - Phase 0 probes that TOUCH the world: 0.6 (toolbar actions on the
    // Rotating Antenna) and 0.8 (Custom Data written by mod code). Design and pass/fail
    // criteria: docs/PHASE0_PROBES.md. The read-only probes are in RadarProbe; this mod is
    // separate so a failure here can be removed without losing them.
    //
    // WHAT IT CHANGES
    //   0.6  adds three toolbar actions to the list the game builds for ONE block type
    //        (the Rotating Antenna subtypes). It never calls AddAction, so nothing is
    //        registered against any block type.
    //   0.8  writes a [RadarProbe] section into the Custom Data of the dishes on the grid
    //        named RP-O, when - and only when - someone types /radarprobe write.
    //
    // RUN IT ON A COPY OF A WORLD. Nothing here is expected to damage anything, but 0.6
    // injects into terminal action lists, and this mod's history with terminal lists is
    // reason enough. Remove the mod from the world afterwards.
    //
    // COMMANDS (chat)
    //   /radarprobe write    stamp every dish on RP-O (0.8)
    //   /radarprobe status   print the 0.6 counters and the verdict tally
    //   Plain /radarprobe belongs to RadarProbe and is ignored here.
    //
    // THE PROCEDURES ARE IN THE FILE HEADERS OF Probe06Actions.cs AND Probe08CustomData.cs.
    // Both need you at the keyboard: 0.6 is a toolbar exercise and a reload, and 0.8 needs a
    // write from a client and a server restart.
    //
    // Extract the log the same way as RadarProbe:
    //   Select-String -Path "$env:APPDATA\SpaceEngineers\SpaceEngineers_*.log" `
    //     -Pattern 'RADARPROBE' | ForEach-Object { $_.Line } | Set-Content out.txt
    // and the same against the dedicated server's log folder.
    //
    // A WHITELIST REJECTION IS A RESULT, NOT A BUG (PHASE0_PROBES 1.1). One prohibited
    // member stops the whole mod. Record it as a FAIL for the question it served, delete
    // that call, reload.
    //
    // NOTHING HERE HAS BEEN COMPILED. It was written in a session with no game install.
    // Run: tools\check-compile.ps1 -Source ..\probes\RadarActionProbe\Data\Scripts\RadarActionProbe
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class RadarActionProbeSession : MySessionComponentBase
    {
        private const int PollStartTick = 600;      // 10 s, past the loading screen
        private const int PollIntervalTicks = 60;   // once a second

        private readonly Probe06Actions _p06 = new Probe06Actions();
        private readonly Probe08CustomData _p08 = new Probe08CustomData();

        private long _tick;
        private bool _armed;
        private bool _chatHooked;
        private bool _actionsHooked;
        private bool _writeRequested;
        private bool _statusRequested;

        public override void LoadData()
        {
            // Subscribing is the only thing done this early. It asks to be told when the
            // game builds a list; it does not read or write one. Same pattern as
            // GroundTruthSession.LoadData with CustomControlGetter.
            try
            {
                _p06.Hook();
                _actionsHooked = true;
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("RADARPROBE ? 0.6 hook THREW " + e.GetType().Name + ": " + e.Message);
            }

            try
            {
                MyAPIGateway.Utilities.MessageEntered += OnMessage;
                _chatHooked = true;
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("RADARPROBE ? chat hook THREW " + e.GetType().Name + ": " + e.Message);
            }
        }

        protected override void UnloadData()
        {
            if (_actionsHooked) _p06.Unhook();
            if (_chatHooked)
            {
                try { MyAPIGateway.Utilities.MessageEntered -= OnMessage; } catch { }
                _chatHooked = false;
            }
        }

        private void OnMessage(string text, ref bool sendToOthers)
        {
            if (text == null) return;

            string t = text.Trim();
            if (t.Equals("/radarprobe write", StringComparison.OrdinalIgnoreCase)) _writeRequested = true;
            else if (t.Equals("/radarprobe status", StringComparison.OrdinalIgnoreCase)) _statusRequested = true;
            else return;

            sendToOthers = false;
        }

        public override void UpdateAfterSimulation()
        {
            _tick++;

            if (!_armed)
            {
                _armed = true;
                Log.DecideSide();
                bool dedicated = false;
                try { dedicated = MyAPIGateway.Utilities.IsDedicated; } catch { }
                Log.Line("SESSION", "ARMED probes=0.6,0.8 pollStart=" + PollStartTick + " pollInterval=" + PollIntervalTicks
                    + " dedicated=" + Log.B(dedicated) + " actions=" + Log.B(_actionsHooked) + " chat=" + Log.B(_chatHooked));
            }

            // Commands run here, not inside the chat callback, so no work happens while
            // the game is in the middle of dispatching a message.
            if (_writeRequested)
            {
                _writeRequested = false;
                try { _p08.Write(); }
                catch (Exception e) { Log.Threw("0.8", e); }
            }

            if (_statusRequested)
            {
                _statusRequested = false;
                try { _p06.Status(); Log.WriteSummary(); }
                catch (Exception e) { Log.Threw("0.6", e); }
            }

            if (_tick >= PollStartTick && ((_tick - PollStartTick) % PollIntervalTicks) == 0)
            {
                try { _p08.Poll(_tick); }
                catch (Exception e) { Log.Threw("0.8", e); }
            }
        }
    }
}
