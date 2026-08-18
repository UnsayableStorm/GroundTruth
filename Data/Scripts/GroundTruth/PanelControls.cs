using System;
using System.Collections.Generic;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace GroundTruth
{
    // The instrument dropdown on an LCD's terminal.
    //
    // A base has a Habitat Monitor per room, and the panel needs to be told which one.
    // Nearest is the default and is usually right, but "usually" is not a setting - a
    // player looking at the wrong room's seal needs a control, in the terminal, next to
    // the app they just picked.
    //
    // So: one combo box per role, listing every instrument of that role on the grid,
    // ordered nearest first, with Automatic at the top.
    //
    // ---- THREE ENGINE FACTS THIS IS BUILT ON ----
    //
    // 1. CONTROLS ARE INJECTED, NOT REGISTERED. AddControl<IMyTextPanel> would put
    //    these on every LCD in the game forever, and this mod has already paid for
    //    registering too broadly once - see the note in TerminalApi.cs about a light
    //    losing its On/Off action. CustomControlGetter hands us the list SE is building
    //    for one specific block, and anything we add lives only in that list. It also
    //    fires again every time the terminal rebuilds, which is what makes the dropdown
    //    appear the moment the app is chosen from the script list rather than after a
    //    reselect.
    //
    // 2. THE SELECTED SURFACE IS READABLE. A cockpit has several screens and the
    //    terminal shows one at a time; a terminal control belongs to the BLOCK and has
    //    no idea which. MyMultiTextPanelComponent.SelectedPanelIndex is what the
    //    terminal itself uses, so the dropdown can follow the screen selector and store
    //    per surface. Blocks without the component have exactly one surface, index 0.
    //
    // 3. ComboBoxContent GETS NO BLOCK. Its signature is
    //    Action<List<MyTerminalControlComboBoxItem>> and nothing else, so the block has
    //    to come from somewhere. SE calls the getter for the selected block immediately
    //    before opening the list, and Inject runs before that again, so the last block
    //    we were asked about is the right one. This is the standard workaround and it
    //    is what Arthur's LCD Mod does for the same reason.
    public static class PanelControls
    {
        // Script ids, matching the MyTextSurfaceScript attributes in TextPanels.cs,
        // OverviewPanel.cs and StripPanel.cs. A typo here is a dropdown that never
        // appears, so they are checked against the roles below at first use.
        private const string ScriptRadiation = "GT_Radiation";
        private const string ScriptHabitat = "GT_Habitat";
        private const string ScriptWeather = "GT_Weather";
        private const string ScriptBio = "GT_Bio";
        private const string ScriptOverview = "GT_Overview";
        private const string ScriptStrip = "GT_Strip";

        // Automatic is key 0. Every other key is an instrument's EntityId, which is
        // unique, stable while the world runs, and cannot collide with 0.
        private const long KeyAutomatic = 0L;

        private static readonly List<IMyTerminalControl> _controls = new List<IMyTerminalControl>();
        private static bool _created;

        // The block SE last asked us about. See engine fact 3.
        private static IMyTerminalBlock _context;

        public static void Create()
        {
            if (_created) return;
            _created = true;

            Add(Instruments.RoleRadiation, "Radiation Monitor");
            Add(Instruments.RoleHabitat, "Habitat Monitor");
            Add(Instruments.RoleWeather, "Weather Station");
            Add(Instruments.RoleBio, "Bio Systems Scanner");
        }

        private static void Add(float role, string noun)
        {
            var box = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlCombobox, IMyTerminalBlock>(
                    "GT_Select_" + PanelSelection.KeyFor(role));

            box.Title = MyStringId.GetOrCompute(noun);
            box.Tooltip = MyStringId.GetOrCompute(
                "Which " + noun + " on this grid this screen reads.\n" +
                "Automatic picks the nearest one, which on a base with one per room is " +
                "the one in the room this screen is in.");

            box.ComboBoxContent = items => Content(role, items);
            box.Getter = b => Selected(b, role);
            box.Setter = (b, key) => Select(b, role, key);

            // Never consulted: SE evaluates Visible on controls it already owns, and
            // these are injected only when they apply. Set anyway, because a control
            // that answers false to a question nobody asked is cheaper than finding out
            // some code path did ask.
            box.Visible = b => true;
            box.Enabled = b => true;
            box.SupportsMultipleBlocks = false;

            _controls.Add(box);
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Called from the session's CustomControlGetter for every block whose terminal
        /// is being built. Adds a dropdown per role the surface's app actually reads.
        /// </summary>
        public static void Inject(IMyTerminalBlock block, List<IMyTerminalControl> controls)
        {
            if (!_created || block == null || controls == null) return;

            var provider = block as IMyTextSurfaceProvider;
            if (provider == null) return;

            _context = block;

            var surface = CurrentSurface(block);
            if (surface == null) return;
            if (surface.ContentType != ContentType.SCRIPT) return;
            if (string.IsNullOrEmpty(surface.Script)) return;

            for (int i = 0; i < _controls.Count; i++)
            {
                float role = RoleOfControl(i);
                if (!AppReads(surface.Script, role)) continue;

                // Nothing of this role on the grid is not a choice to offer. The panel
                // itself already says NO INSTRUMENT and names the block to build; a
                // dropdown whose only entry is Automatic would say less than that.
                if (CountOnGrid(block, role) == 0) continue;

                controls.Add(_controls[i]);
            }
        }

        // Control order matches the Add calls in Create.
        private static float RoleOfControl(int index)
        {
            switch (index)
            {
                case 0: return Instruments.RoleRadiation;
                case 1: return Instruments.RoleHabitat;
                case 2: return Instruments.RoleWeather;
                default: return Instruments.RoleBio;
            }
        }

        // Which roles an app reads. Overview and Strip read all four and get four
        // dropdowns; the per-role apps get exactly one.
        private static bool AppReads(string script, float role)
        {
            if (script == ScriptOverview || script == ScriptStrip) return true;
            if (script == ScriptRadiation) return role == Instruments.RoleRadiation;
            if (script == ScriptHabitat) return role == Instruments.RoleHabitat;
            if (script == ScriptWeather) return role == Instruments.RoleWeather;
            if (script == ScriptBio) return role == Instruments.RoleBio;
            return false;
        }

        // ------------------------------------------------------------------

        private static void Content(float role, List<MyTerminalControlComboBoxItem> items)
        {
            items.Add(new MyTerminalControlComboBoxItem
            {
                Key = KeyAutomatic,
                Value = MyStringId.GetOrCompute("Automatic (nearest)")
            });

            var block = _context;
            if (block == null) return;

            var found = Candidates(block, role);
            for (int i = 0; i < found.Count; i++)
            {
                var name = found[i].DisplayNameText;
                if (string.IsNullOrEmpty(name)) name = "unnamed instrument";

                items.Add(new MyTerminalControlComboBoxItem
                {
                    Key = found[i].EntityId,
                    Value = MyStringId.GetOrCompute(name)
                });
            }
        }

        private static long Selected(IMyTerminalBlock block, float role)
        {
            if (block == null) return KeyAutomatic;

            var selection = new PanelSelection();
            selection.Refresh(block.CustomData, SurfaceIndex(block));

            var want = selection.For(role);
            long wantId = selection.IdFor(role);
            if (want == null && wantId == 0) return KeyAutomatic;

            var found = Candidates(block, role);

            // Id first, exactly as the panel resolves it, so the terminal and the screen
            // can never point at different instruments - including right after a rename,
            // when the stored name is stale and only the id still resolves.
            for (int i = 0; i < found.Count; i++)
                if (found[i].EntityId == wantId) return wantId;

            IMyTerminalBlock best = null;
            int bestRank = 0;

            for (int i = 0; i < found.Count; i++)
            {
                int rank = PanelSelection.Rank(found[i].DisplayNameText, want);
                if (rank <= bestRank) continue;      // Candidates is nearest-first, so
                best = found[i];                     // ties keep the nearer block
                bestRank = rank;
            }

            // A name that matches nothing reads as Automatic in the dropdown while the
            // panel says NO MATCH. That is the honest pair: the setting is not being
            // applied, and the screen is the thing saying why.
            return best == null ? KeyAutomatic : best.EntityId;
        }

        private static void Select(IMyTerminalBlock block, float role, long key)
        {
            if (block == null) return;

            string value = null;
            if (key != KeyAutomatic)
            {
                var target = MyAPIGateway.Entities.GetEntityById(key) as IMyTerminalBlock;
                if (target == null) return;
                value = target.DisplayNameText;
            }

            // BOTH the id and the name are stored. Each survives something the other
            // does not - the id a rename, the name a blueprint paste - and the binding
            // has to survive both. See the note in PanelSelection.cs.
            int surface = SurfaceIndex(block);

            var current = new PanelSelection();
            current.Refresh(block.CustomData, surface);
            if (value == null && current.For(role) == null && current.IdFor(role) == 0)
                return;                                               // already automatic

            block.CustomData = PanelSelection.Write(block.CustomData, surface, role,
                                                    value, key == KeyAutomatic ? 0L : key);
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// The surface the terminal is currently showing for this block. See engine
        /// fact 2 - blocks with one screen have no component and are always index 0.
        /// </summary>
        public static int SurfaceIndex(IMyTerminalBlock block)
        {
            try
            {
                var multi = block.Components.Get<MyMultiTextPanelComponent>();
                if (multi == null) return 0;

                int i = multi.SelectedPanelIndex;
                return i < 0 ? 0 : i;
            }
            catch { return 0; }
        }

        private static IMyTextSurface CurrentSurface(IMyTerminalBlock block)
        {
            try
            {
                var provider = block as IMyTextSurfaceProvider;
                if (provider == null || provider.SurfaceCount <= 0) return null;

                int i = SurfaceIndex(block);
                if (i >= provider.SurfaceCount) i = 0;
                return provider.GetSurface(i) as IMyTextSurface;
            }
            catch { return null; }
        }

        // Instruments of a role on the panel's own grid, NEAREST FIRST - the same grid
        // rule and the same ordering the panel resolves with, so the list a player picks
        // from is the list the panel is choosing between.
        private static List<IMyTerminalBlock> Candidates(IMyTerminalBlock block, float role)
        {
            var found = new List<IMyTerminalBlock>();
            try
            {
                var grid = block.CubeGrid as IMyCubeGrid;
                if (grid == null) return found;

                var slims = new List<IMySlimBlock>();
                grid.GetBlocks(slims, sb => sb.FatBlock is IMyTerminalBlock);

                var origin = block.GetPosition();
                var dist = new List<double>();

                foreach (var sb in slims)
                {
                    var tb = sb.FatBlock as IMyTerminalBlock;
                    if (tb == null) continue;
                    if (Instruments.RoleOf(tb.BlockDefinition.SubtypeName) != role) continue;
                    if (GroundTruthSession.StateFor(tb) == null) continue;

                    double d = Vector3D.DistanceSquared(origin, tb.GetPosition());

                    // Insertion sort. The count is single digits in every real base, and
                    // this avoids handing a comparator to List.Sort - which allocates and
                    // which mod code has no reason to reach for at this size.
                    int at = dist.Count;
                    while (at > 0 && dist[at - 1] > d) at--;
                    dist.Insert(at, d);
                    found.Insert(at, tb);
                }
            }
            catch { }
            return found;
        }

        private static int CountOnGrid(IMyTerminalBlock block, float role)
        {
            return Candidates(block, role).Count;
        }
    }
}
