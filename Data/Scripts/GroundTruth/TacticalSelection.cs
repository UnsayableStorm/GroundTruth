using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace GroundTruth
{
    // Which contact a ship is tracking (TACTICAL_PLAN D3, D4).
    //
    // ONE SELECTION PER SHIP, STORED IN THE DISHES. The tracked contact's grid EntityId
    // is written into the block-wide [GroundTruth] section of every dish in the ship's
    // mechanical group:
    //
    //   [GroundTruth]
    //   Track.Id = 104857600012345
    //
    // Custom Data saves with the world and syncs to the server - probe 0.8 measured a
    // client's write reaching the server in 1.9 s and surviving three restarts - so the
    // selection needs no network message of its own and every player aboard sees the
    // same target. Writing it to every dish means losing one dish does not lose it.
    //
    // AUTOMATIC IS THE NEAREST ENEMY (D4, refined by Jason 2026-10-05). An empty or
    // absent Track.Id means automatic: the nearest DETECTED contact whose relation is
    // Enemies. No enemy, no automatic track - it never falls back to a neutral, and a
    // dark return (relation UNKNOWN) is never picked automatically. A manual selection
    // holds through LOST and falls back to automatic once the contact has EXPIRED and
    // left the table. The stale id is left in Custom Data rather than cleared, because
    // clearing it would be a write from every client that noticed; if that same grid
    // is detected again, the ship is tracking it again, which is what was asked for.
    //
    // ACCESS IS CHECKED HERE, NOT LEFT TO THE ENGINE (probe 0.8.access). A Custom Data
    // write from a player without access is applied on that player's own machine and
    // silently dropped by the server, which would leave them looking at a selection
    // nobody else has. So a write is refused, visibly, unless the player has access.
    //
    // BOTH WAYS OF SELECTING RUN ON THE CLIENT. The terminal dropdown is a client UI,
    // and probe 0.6 found toolbar action delegates executing on the client that pressed
    // them. That is why this can ask HasLocalPlayerAccess: there is a local player.
    public static class TacticalSelection
    {
        private const string TrackKey = "Track.Id";
        private const long KeyAutomatic = 0L;

        private const string ActNext = "GT_TrackNext";
        private const string ActPrev = "GT_TrackPrevious";
        private const string ActAuto = "GT_TrackAutomatic";

        // Parsed Custom Data per dish. PanelSelection re-parses only when the string
        // changes, so reading the selection once a second per panel costs a compare.
        private static readonly Dictionary<long, PanelSelection> _parsed = new Dictionary<long, PanelSelection>();

        private static readonly List<IMyTerminalAction> _actions = new List<IMyTerminalAction>();
        private static IMyTerminalControlCombobox _combo;
        private static IMyTerminalBlock _context;   // see PanelControls, engine fact 3

        private static readonly List<Tactical.Contact> _list = new List<Tactical.Contact>();
        private static readonly List<IMyCubeGrid> _grids = new List<IMyCubeGrid>();
        private static readonly List<IMySlimBlock> _slims = new List<IMySlimBlock>();

        public static void Unload()
        {
            _parsed.Clear();
            _actions.Clear();
            _combo = null;
            _context = null;
        }

        // ---- reading ---------------------------------------------------------------

        /// <summary>The manually selected contact id stored on this dish, or 0 for automatic.</summary>
        public static long StoredId(IMyTerminalBlock dish)
        {
            if (dish == null) return KeyAutomatic;
            PanelSelection p;
            if (!_parsed.TryGetValue(dish.EntityId, out p))
            {
                p = new PanelSelection();
                _parsed[dish.EntityId] = p;
            }
            // -1: the block-wide section only. A dish has no surfaces to address.
            p.Refresh(dish.CustomData, -1);

            // Garbage reads as automatic. A hand-mangled line must not stop the radar.
            long id;
            var v = p.Setting(TrackKey);
            return v != null && long.TryParse(v, out id) ? id : KeyAutomatic;
        }

        /// <summary>
        /// The contact this dish's ship is tracking, or null. Manual when the stored id is
        /// still in the table; otherwise the nearest detected enemy, or nothing.
        /// </summary>
        public static Tactical.Contact Tracked(Tactical.Table t, IMyTerminalBlock dish, out bool manual)
        {
            manual = false;
            if (t == null || dish == null) return null;

            long stored = StoredId(dish);
            Tactical.Contact c;
            if (stored != KeyAutomatic && t.Contacts.TryGetValue(stored, out c))
            {
                manual = true;
                return c;
            }

            Tactical.Contact best = null;
            double bestD = double.MaxValue;
            var p = dish.GetPosition();
            foreach (var kv in t.Contacts)
            {
                var k = kv.Value;
                if (k.State != Tactical.StateDetected) continue;
                if (k.Relation != (int)VRage.Game.MyRelationsBetweenPlayerAndBlock.Enemies) continue;
                double d = Vector3D.DistanceSquared(p, k.Position);
                if (d < bestD) { bestD = d; best = k; }
            }
            return best;
        }

        // ---- writing ---------------------------------------------------------------

        /// <summary>
        /// Track this contact (0 = automatic) on every dish of the ship the player has
        /// access to. Refused, with a notification, when the player has no access to the
        /// dish they acted on.
        /// </summary>
        public static bool Select(IMyTerminalBlock dish, long contactId)
        {
            if (dish == null) return false;

            if (!HasAccess(dish))
            {
                Notify("No access to " + dish.CustomName + ": tracked contact not changed.", true);
                return false;
            }

            string value = contactId == KeyAutomatic ? "" : contactId.ToString();

            // Every dish on the ship, working or not: a dish that is off when the choice
            // is made should come back on tracking the same thing as the others.
            MechanicalGroup(dish.CubeGrid);
            int written = 0, denied = 0;
            for (int g = 0; g < _grids.Count; g++)
            {
                _slims.Clear();
                _grids[g].GetBlocks(_slims, null);
                for (int i = 0; i < _slims.Count; i++)
                {
                    var tb = _slims[i].FatBlock as IMyTerminalBlock;
                    if (tb == null) continue;
                    if (Instruments.RoleOf(tb.BlockDefinition.SubtypeName) != Instruments.RoleRadar) continue;
                    if (tb != dish && !HasAccess(tb)) { denied++; continue; }

                    string before = tb.CustomData ?? "";
                    string after = PanelSelection.WriteBlockKey(before, TrackKey, value);
                    if (after != before) tb.CustomData = after;
                    written++;
                }
            }
            _slims.Clear();
            _grids.Clear();

            // Some dishes on this ship are someone else's. They keep their own selection,
            // and the player is told rather than left to find the disagreement on a screen.
            if (denied > 0)
                Notify(string.Format("Tracked contact set on {0} dish{1}; {2} other{3} not yours.",
                    written, written == 1 ? "" : "es", denied, denied == 1 ? " is" : "s are"), false);
            return true;
        }

        /// <summary>Step the tracked contact through the list, nearest first. dir +1 / -1.</summary>
        public static void Step(IMyTerminalBlock dish, int dir)
        {
            if (dish == null) return;
            var t = Tactical.TableFor(dish, GroundTruthSession.Seconds);
            Tactical.Sorted(t, dish, _list);
            if (_list.Count == 0)
            {
                Notify("No contacts to track.", false);
                return;
            }

            bool manual;
            var current = Tracked(t, dish, out manual);
            int at = -1;
            for (int i = 0; i < _list.Count; i++)
                if (current != null && _list[i].Id == current.Id) { at = i; break; }

            int next;
            if (dir >= 0) next = at < 0 ? 0 : (at + 1) % _list.Count;
            else next = at <= 0 ? _list.Count - 1 : at - 1;

            var pick = _list[next];
            _list.Clear();
            if (Select(dish, pick.Id))
                Notify("Tracking " + (string.IsNullOrEmpty(pick.Name) ? "UNIDENTIFIED" : pick.Name), false);
        }

        private static bool HasAccess(IMyTerminalBlock block)
        {
            // A dedicated server has no local player and never selects anything itself.
            if (MyAPIGateway.Utilities.IsDedicated) return false;
            try { return block.HasLocalPlayerAccess(); }
            catch { return false; }
        }

        private static void Notify(string text, bool bad)
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;
            try { MyAPIGateway.Utilities.ShowNotification(text, 3000, bad ? "Red" : "White"); }
            catch { }
        }

        private static void MechanicalGroup(IMyCubeGrid grid)
        {
            _grids.Clear();
            try
            {
                var g = grid.GetGridGroup(GridLinkTypeEnum.Mechanical);
                if (g != null) g.GetGrids(_grids);
            }
            catch { _grids.Clear(); }
            if (_grids.Count == 0) _grids.Add(grid);
        }

        // ---- the dish's terminal dropdown (D4.2) -----------------------------------
        //
        // Injected through CustomControlGetter exactly as PanelControls injects the
        // instrument dropdown, so nothing is registered against the RadioAntenna type and
        // vanilla antennas never see it.

        public static void InjectControls(IMyTerminalBlock block, List<IMyTerminalControl> controls)
        {
            if (block == null || controls == null) return;
            if (Instruments.RoleOf(block.BlockDefinition.SubtypeName) != Instruments.RoleRadar) return;

            _context = block;
            if (_combo == null) _combo = CreateCombo();
            controls.Add(_combo);
        }

        private static IMyTerminalControlCombobox CreateCombo()
        {
            var box = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlCombobox, IMyTerminalBlock>("GT_TrackSelect");
            box.Title = MyStringId.GetOrCompute("Tracked contact");
            box.Tooltip = MyStringId.GetOrCompute(
                "Which contact this ship tracks, shown on every Ground Truth tactical screen aboard.\n" +
                "Automatic tracks the nearest enemy, and nothing when there is none.");
            box.ComboBoxContent = Content;
            box.Getter = b => Current(b);
            box.Setter = (b, key) => Select(b, key);
            box.Visible = b => true;
            box.Enabled = b => true;
            box.SupportsMultipleBlocks = false;
            return box;
        }

        private static void Content(List<MyTerminalControlComboBoxItem> items)
        {
            items.Add(new MyTerminalControlComboBoxItem
            {
                Key = KeyAutomatic,
                Value = MyStringId.GetOrCompute("Automatic (nearest enemy)")
            });

            var dish = _context;
            if (dish == null) return;

            var t = Tactical.TableFor(dish, GroundTruthSession.Seconds);
            Tactical.Sorted(t, dish, _list);
            for (int i = 0; i < _list.Count; i++)
            {
                var c = _list[i];
                // No range in the label: every distinct string becomes a permanent
                // MyStringId, and a range would mint a new one every time a ship moved.
                // Name, class and relation are stable.
                string label = (string.IsNullOrEmpty(c.Name) ? "UNIDENTIFIED" : c.Name)
                    + " - " + Tactical.ClassOf(c) + " - " + Tactical.RelationName(c.Relation);
                items.Add(new MyTerminalControlComboBoxItem { Key = c.Id, Value = MyStringId.GetOrCompute(label) });
            }
            _list.Clear();
        }

        private static long Current(IMyTerminalBlock dish)
        {
            long stored = StoredId(dish);
            if (stored == KeyAutomatic) return KeyAutomatic;
            var t = Tactical.TableFor(dish, GroundTruthSession.Seconds);
            // An expired selection reads as Automatic, because that is what is in effect.
            return t != null && t.Contacts.ContainsKey(stored) ? stored : KeyAutomatic;
        }

        // ---- toolbar actions (D4.3) ------------------------------------------------
        //
        // The game asks for the dish's actions about 120 times a second while one of them
        // sits on a toolbar (probe 0.6). So this handler builds the three actions once
        // and afterwards only checks they are in the list: no allocation, no scan, no
        // logging. Added to the list it was given, never with AddAction, so vanilla
        // antennas are untouched - 0.6.vanilla held for a whole run on that basis.

        public static void OnActionGetter(IMyTerminalBlock block, List<IMyTerminalAction> actions)
        {
            try
            {
                if (block == null || actions == null) return;
                if (Instruments.RoleOf(block.BlockDefinition.SubtypeName) != Instruments.RoleRadar) return;

                if (_actions.Count == 0)
                {
                    _actions.Add(MakeAction(ActNext, "Track next contact", "Textures\\GUI\\Icons\\Actions\\Increase.dds", 1));
                    _actions.Add(MakeAction(ActPrev, "Track previous contact", "Textures\\GUI\\Icons\\Actions\\Decrease.dds", -1));
                    _actions.Add(MakeAction(ActAuto, "Track automatic (nearest enemy)", "Textures\\GUI\\Icons\\Actions\\Off.dds", 0));
                }

                for (int a = 0; a < _actions.Count; a++)
                {
                    bool present = false;
                    for (int i = 0; i < actions.Count; i++)
                        if (actions[i].Id == _actions[a].Id) { present = true; break; }
                    if (!present) actions.Add(_actions[a]);
                }
            }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole("GroundTruth action getter: " + e.Message); }
        }

        // A method rather than a lambda in a loop, so each action closes over its own dir.
        private static IMyTerminalAction MakeAction(string id, string name, string icon, int dir)
        {
            var a = MyAPIGateway.TerminalControls.CreateAction<IMyTerminalBlock>(id);
            a.Name = new StringBuilder(name);
            a.Icon = icon;
            a.Enabled = b => true;
            a.Action = b =>
            {
                try
                {
                    if (dir == 0) { if (Select(b, KeyAutomatic)) Notify("Tracking automatic: nearest enemy", false); }
                    else Step(b, dir);
                }
                catch (Exception e) { MyLog.Default.WriteLineAndConsole("GroundTruth track action: " + e.Message); }
            };
            return a;
        }
    }

    // Subscribes the action getter at LoadData - the timing probe 0.6 proved, including
    // a toolbar slot working after a reload before any terminal was opened. It lives in
    // its own component so GroundTruthSession's LoadData/BeforeStart timing, which this
    // mod paid dearly to get right, is not touched. Subscribing asks to be told when the
    // game builds a list; it reads and writes nothing itself.
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class TacticalSession : MySessionComponentBase
    {
        private bool _hooked;

        public override void LoadData()
        {
            try
            {
                MyAPIGateway.TerminalControls.CustomActionGetter += TacticalSelection.OnActionGetter;
                _hooked = true;
            }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole("GroundTruth TacticalSession hook: " + e.Message); }
        }

        protected override void UnloadData()
        {
            if (_hooked)
            {
                try { MyAPIGateway.TerminalControls.CustomActionGetter -= TacticalSelection.OnActionGetter; } catch { }
                _hooked = false;
            }
            TacticalSelection.Unload();
        }
    }
}
