using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace RadarProbe
{
    // 0.7 - Ship controller telemetry.
    //
    // QUESTION
    //   Do the ship-controller members the Navigation app needs exist, and do they agree
    //   with the vanilla HUD? Navigation reads from a cockpit or remote control, and
    //   must not synthesise a heading or altitude when there is no controller.
    //
    // WHAT IS READ, every sample, from the first ship controller on RP-O
    //   TryGetPlanetElevation for both Sealevel and Surface (value and return flag),
    //   GetNaturalGravity and GetArtificialGravity (m/s^2 and g), GetShipSpeed,
    //   GetShipVelocities (linear and angular), DampenersOverride, and whether the seat
    //   is occupied (IsUnderControl).
    //
    // THE INDEPENDENT CHECK
    //   The same numbers are shown on screen with ShowNotification for 2 s. Take a
    //   screenshot with the vanilla HUD's altitude and gravity in the same frame: that
    //   screenshot is the evidence for 0.7.altitude and 0.7.gravity, which are judged by
    //   hand at the gate and are emitted here as INCONCLUSIVE.
    //
    // THE PROCEDURE (needs you)
    //   Sample on a planet surface, then flying, then in space. Toggle dampeners once.
    //   Leave the seat once while it samples.
    //
    // WHAT DECIDES IT
    //   0.7.dampeners   PASS  DampenersOverride flipped when toggled (a flip was seen);
    //                   INCONCLUSIVE if it was never toggled during the run
    //   0.7.unoccupied  PASS  values still live with nobody seated (any of natural
    //                         gravity, elevation, or speed non-zero)
    //                   FAIL  all zero while unoccupied, having been non-zero while
    //                         occupied; INCONCLUSIVE otherwise, including "never unseated"
    //   0.7.altitude / 0.7.gravity: the gate, from the screenshot.
    //
    // WHAT EACH RESULT CHANGES
    //   unoccupied FAIL -> the Navigation app says NO PILOT rather than show frozen values.
    //
    // Members are on Sandbox.ModAPI.Ingame.IMyShipController, which the ModAPI interface
    // inherits. Types from that namespace are fully qualified because importing it would
    // make IMyTerminalBlock and its relatives ambiguous.
    public sealed class Probe07Controller : Probe
    {
        public override string Id { get { return "0.7"; } }

        private const double OneG = 9.81;

        private IMyShipController _chosen;
        private bool _haveDamp;
        private bool _lastDamp;
        private bool _dampFlipSeen;
        private bool _unoccupiedSeen;
        private bool _lastOccupiedNonZero;

        public override void Sample(Ctx c)
        {
            if (c.Sample == 1)
            {
                Log.Inconclusive("0.7.altitude", "judged from the screenshot against the vanilla HUD, at the gate");
                Log.Inconclusive("0.7.gravity", "judged from the screenshot against the vanilla HUD, at the gate");
            }

            // Which controller is read, in order:
            //   1. The seat the local player is in, on ANY grid. The Moon pass flew a pasted copy
            //      of RP-O, and with two grids of that name the probe kept reading the original,
            //      parked 300 km away with nobody in it. The question is how ship controllers
            //      report, which any seat answers; it was never a question about RP-O itself.
            //   2. The last controller chosen, while it still exists. Leaving the seat is part
            //      of the test (0.7.unoccupied), so standing up must not switch to another block.
            //   3. On RP-O: the main cockpit, else any seat that can fly the ship, else anything.
            //      The very first build read the first controller found, which was RP-O's cryo
            //      chamber - a seat nobody flies from - so everything read zero.
            IMyShipController sc = null;
            string source = null;
            try
            {
                var p = MyAPIGateway.Session.Player;
                if (p != null && p.Controller != null)
                    sc = p.Controller.ControlledEntity as IMyShipController;
            }
            catch { sc = null; }
            if (sc != null) source = "player-seat";

            if (sc == null && _chosen != null && !_chosen.Closed) { sc = _chosen; source = "last-chosen"; }

            if (sc == null && c.Observer != null)
            {
                IMyShipController main = null, flies = null, any = null;
                var blocks = Ctx.FatBlocks(c.Observer.Grid);
                for (int i = 0; i < blocks.Count; i++)
                {
                    var s = blocks[i] as IMyShipController;
                    if (s == null) continue;
                    if (main == null && s.IsMainCockpit) main = s;
                    if (flies == null && s.CanControlShip) flies = s;
                    if (any == null) any = s;
                }
                sc = main ?? flies ?? any;
                if (sc != null) source = "RP-O";
            }

            if (sc == null)
            {
                Log.Inconclusive("0.7.dampeners", "no seat occupied and no ship controller on RP-O");
                return;
            }
            if (sc != _chosen)
            {
                _chosen = sc;
                var g = sc.CubeGrid;
                Log.Line(Id, "chose controller id=" + sc.EntityId + " name=\"" + sc.CustomName + "\" grid=\""
                    + (g != null ? g.DisplayName : "?") + "\" source=" + source + " occupied=" + Log.B(sc.IsUnderControl)
                    + " main=" + Log.B(sc.IsMainCockpit) + " canControl=" + Log.B(sc.CanControlShip));
            }

            double elevSea = 0, elevSurf = 0;
            bool okSea = sc.TryGetPlanetElevation(Sandbox.ModAPI.Ingame.MyPlanetElevation.Sealevel, out elevSea);
            bool okSurf = sc.TryGetPlanetElevation(Sandbox.ModAPI.Ingame.MyPlanetElevation.Surface, out elevSurf);

            Vector3D nat = sc.GetNaturalGravity();
            Vector3D art = sc.GetArtificialGravity();
            double natLen = nat.Length();
            double artLen = art.Length();

            double speed = sc.GetShipSpeed();
            var vel = sc.GetShipVelocities();
            double linear = vel.LinearVelocity.Length();
            double angular = vel.AngularVelocity.Length();

            bool damp = sc.DampenersOverride;
            bool occupied = sc.IsUnderControl;

            Log.Line(Id, "controller id=" + sc.EntityId + " name=\"" + sc.CustomName + "\" occupied=" + Log.B(occupied)
                + " elevSea=" + (okSea ? Log.F(elevSea, 1) : "false") + " elevSurface=" + (okSurf ? Log.F(elevSurf, 1) : "false")
                + " naturalG=" + Log.F(natLen, 3) + "m/s2(" + Log.F(natLen / OneG, 3) + "g)"
                + " artificialG=" + Log.F(artLen, 3) + "m/s2(" + Log.F(artLen / OneG, 3) + "g)"
                + " speed=" + Log.F(speed, 2) + " linear=" + Log.F(linear, 2) + " angular=" + Log.F(angular, 3)
                + " dampeners=" + Log.B(damp));

            Notify(okSea, elevSea, okSurf, elevSurf, natLen / OneG, speed, damp, occupied);
            JudgeDampeners(damp);
            JudgeUnoccupied(occupied, okSea || okSurf || natLen > 0.001 || speed > 0.01);
        }

        // On screen beside the vanilla HUD, so one screenshot holds both. Only on a
        // machine with a screen.
        private static void Notify(bool okSea, double sea, bool okSurf, double surf, double g, double speed, bool damp, bool occupied)
        {
            try
            {
                if (MyAPIGateway.Utilities.IsDedicated) return;
                MyAPIGateway.Utilities.ShowNotification(
                    "PROBE 0.7  sea=" + (okSea ? Log.F(sea, 0) : "-") + "  surf=" + (okSurf ? Log.F(surf, 0) : "-")
                    + "  g=" + Log.F(g, 2) + "  spd=" + Log.F(speed, 1)
                    + "  damp=" + (damp ? "on" : "off") + "  seat=" + (occupied ? "occupied" : "empty"),
                    2000, MyFontEnum.White);
            }
            catch { }
        }

        private void JudgeDampeners(bool damp)
        {
            if (_haveDamp && damp != _lastDamp)
            {
                _dampFlipSeen = true;
                Log.Pass("0.7.dampeners", "DampenersOverride flipped " + Log.B(_lastDamp) + " -> " + Log.B(damp));
            }
            _haveDamp = true;
            _lastDamp = damp;
        }

        private void JudgeUnoccupied(bool occupied, bool anyNonZero)
        {
            if (occupied)
            {
                _lastOccupiedNonZero = anyNonZero;
                return;
            }

            _unoccupiedSeen = true;
            if (anyNonZero) Log.Pass("0.7.unoccupied", "values are live with the seat empty");
            else if (_lastOccupiedNonZero) Log.Fail("0.7.unoccupied", "everything reads zero with the seat empty, but was non-zero while occupied");
            else Log.Inconclusive("0.7.unoccupied", "everything reads zero with the seat empty, and there is no non-zero occupied sample to compare with");
        }

        public override void Finish()
        {
            if (!_dampFlipSeen)
                Log.Inconclusive("0.7.dampeners", "dampeners were not toggled during the run");
            if (!_unoccupiedSeen)
                Log.Inconclusive("0.7.unoccupied", "the seat was never empty during the run");
        }
    }
}
