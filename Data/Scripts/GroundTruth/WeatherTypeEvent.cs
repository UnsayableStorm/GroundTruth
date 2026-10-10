using System;
using System.Globalization;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Network;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.ObjectBuilders;
using VRage.Sync;
using VRage.Utils;
using VRageMath;

namespace GroundTruth
{
    // "Weather type is X" - boolean, not a threshold.
    //
    // This is the trigger players actually want: when a sandstorm starts, close
    // everything. Intensity turned out to be a weak signal, because every effect ramps
    // to 100% in about 17 seconds - light fog and a sandstorm read identically.
    //
    // THE TYPE LIST COMES FROM THE PLANET, read from its generator definition, so the
    // choices offered are what can actually occur where the block stands - including
    // modded weather on modded planets. A hardcoded list of the 13 vanilla effects would
    // silently omit them.
    //
    // This event reads the weather NAME directly from the session rather than through a
    // Ground Truth property, because names are not floats and the terminal property
    // contract is numeric. The API is still used for what it is good at: finding the
    // Weather Stations to watch, and confirming a block is genuinely an instrument.

    [ProtoBuf.ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventWeatherType : MyObjectBuilder_ComponentBase
    {
    }

    [MyComponentType(typeof(GTEventWeatherType))]
    [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    [MyComponentBuilder(typeof(MyObjectBuilder_GTEventWeatherType), true)]
    public class GTEventWeatherType : MyEventProxyEntityComponent, IMyEventComponentWithGui
    {
        public static readonly List<GTEventWeatherType> Live = new List<GTEventWeatherType>();

        private static bool _controlsCreated;

        private readonly List<IMyTerminalBlock> _observed = new List<IMyTerminalBlock>();
        private readonly EventPane _pane = new EventPane();
        private double _lastBeat = -1e9;
        private bool? _state;

        // THE SELECTED WEATHER, SAVED AND SYNCED.
        //
        // This was a plain string field, set by the dropdown on whichever machine drew
        // it. It was never saved, so every reload reset the event to "any weather" until
        // someone reopened the terminal and picked again, and it never reached a
        // dedicated server, which therefore always watched "any weather". Both break the
        // rule that nothing may depend on a terminal being opened (2026-10-05).
        //
        // Now a MySync, which replicates both ways, saved through the vanilla
        // MyObjectBuilder_ModCustomComponent - the pattern SeMoreEvents (Workshop
        // 2935236892) ships for its own weather event, chosen because it is proven and
        // because a vanilla builder type still loads if this mod is ever removed.
        //
        // The value is a stable key of the weather NAME (KeyOf), 0 meaning "any weather".
        // Names are what the planet's generator lists, and the list differs per planet, so
        // a list index would point at a different weather on another planet. The key is
        // computed here rather than taken from any engine hash, so it is identical on
        // every machine by construction.
        private MySync<long, SyncDirection.BothWays> _selected;

        public override string ComponentTypeDebugString { get { return "GT_WeatherType"; } }

        public MyStringId EventDisplayName
        {
            get { return MyStringId.GetOrCompute("Weather type [Ground Truth]"); }
        }

        // MUST match Data/EntityComponents.sbc. Never change after publishing - saved
        // Event Controllers store the id of the event they were set to.
        public const long SelectionId = 1002L;
        public long UniqueSelectionId { get { return SelectionId; } }

        public bool IsSelected { get; set; }

        // No threshold, no condition. "Is it a sandstorm" has no magnitude and no
        // greater-or-less; leaving these true would draw a slider that means nothing.
        public bool IsThresholdUsed { get { return false; } }
        public bool IsConditionSelectionUsed { get { return false; } }
        public bool IsBlocksListUsed { get { return true; } }

        public string YesNoToolbarYesDescription { get { return "Weather type began"; } }
        public string YesNoToolbarNoDescription { get { return "Weather type ended"; } }

        public override void OnAddedToScene()
        {
            base.OnAddedToScene();
            if (!Live.Contains(this)) Live.Add(this);
        }

        public override void OnBeforeRemovedFromContainer()
        {
            Live.Remove(this);
            _pane.Unhook();
            base.OnBeforeRemovedFromContainer();
        }

        public bool IsBlockValidForList(IMyTerminalBlock block)
        {
            return EventSupport.RoleOf(block) == Instruments.RoleWeather;
        }

        public void AddBlocks(List<IMyTerminalBlock> blocks)
        {
            if (blocks == null) return;
            foreach (var b in blocks)
                if (b != null && !_observed.Contains(b)) _observed.Add(b);
            _state = null;
            EventSupport.Trace(Entity as IMyTerminalBlock, "BLOCKS +" + blocks.Count + " observing=" + _observed.Count, null);
        }

        public void RemoveBlocks(IEnumerable<IMyTerminalBlock> blocks)
        {
            if (blocks == null) return;
            foreach (var b in blocks) _observed.Remove(b);
            _state = null;
            EventSupport.Trace(Entity as IMyTerminalBlock, "BLOCKS - observing=" + _observed.Count, null);
        }

        // Keep the latch so a change that flips the answer fires on the next tick, as
        // vanilla does - see GTThresholdEvent.
        public void NotifyValuesChanged() { }

        public void RefreshDetailedInfo() { }

        // ---- the saved selection ---------------------------------------------------------

        /// <summary>Stable key of a weather name; 0 is reserved for "any weather".</summary>
        public static long KeyOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            // FNV-1a over the lower-cased name: deterministic everywhere, no engine hash.
            ulong h = 14695981039346656037UL;
            string s = name.ToLowerInvariant();
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 1099511628211UL;
            }
            long key = (long)(h & 0x7FFFFFFFFFFFFFFFUL);
            return key == 0 ? 1 : key;
        }

        /// <summary>The selected weather's name on this controller's planet, or null for any.</summary>
        private string SelectedName()
        {
            long key = _selected.Value;
            if (key == 0) return null;
            var ev = Entity as IMyEventControllerBlock;
            if (ev != null)
            {
                var names = PlanetWeather.NamesAt(ev.GetPosition());
                for (int i = 0; i < names.Count; i++)
                    if (KeyOf(names[i]) == key) return names[i];
            }
            return "weather not found on this planet";
        }

        public override MyObjectBuilder_ComponentBase Serialize(bool copy = false)
        {
            return new MyObjectBuilder_ModCustomComponent
            {
                ComponentType = "GTEventWeatherType",
                CustomModData = _selected.Value.ToString(CultureInfo.InvariantCulture),
                RemoveExistingComponentOnNewInsert = true,
                SubtypeName = "EventControllerBlockComponent"
            };
        }

        public override void Deserialize(MyObjectBuilder_ComponentBase builder)
        {
            base.Deserialize(builder);
            var custom = builder as MyObjectBuilder_ModCustomComponent;
            long key;
            if (custom != null && long.TryParse(custom.CustomModData, NumberStyles.Integer, CultureInfo.InvariantCulture, out key))
                _selected.Value = key;
        }

        public override bool IsSerialized()
        {
            return true;
        }

        // The game supplies threshold, condition and block list from the flags above. A
        // type selector is not among them, so it is built here - the same thing stock
        // events with unusual units do.
        public void CreateTerminalInterfaceControls<T>() where T : IMyTerminalBlock
        {
            if (_controlsCreated) return;
            _controlsCreated = true;

            var combo = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlCombobox, IMyEventControllerBlock>("GT_WeatherTypeSelect");

            combo.Title = MyStringId.GetOrCompute("Weather type");
            combo.Tooltip = MyStringId.GetOrCompute(
                "Effects this planet can produce, read from its own definition.");

            combo.Visible = b => Selected(b) != null;
            combo.Enabled = b => Selected(b) != null;

            combo.ComboBoxContent = list =>
            {
                list.Add(new MyTerminalControlComboBoxItem
                {
                    Key = 0,
                    Value = MyStringId.GetOrCompute("Any weather")
                });

                var names = _comboNames;
                for (int i = 0; i < names.Count; i++)
                    list.Add(new MyTerminalControlComboBoxItem
                    {
                        Key = i + 1,
                        Value = MyStringId.GetOrCompute(names[i])
                    });
            };

            combo.Getter = b =>
            {
                var ev = Selected(b);
                if (ev == null || ev._selected.Value == 0) return 0;
                for (int i = 0; i < _comboNames.Count; i++)
                    if (KeyOf(_comboNames[i]) == ev._selected.Value) return i + 1;
                return 0;
            };

            // Writing the MySync replicates the choice to the server and every client and
            // gets it saved. No latch reset: like a threshold change, a new selection that
            // flips the answer fires on the next tick.
            combo.Setter = (b, key) =>
            {
                var ev = Selected(b);
                if (ev == null) return;
                ev._selected.Value = (key <= 0 || key > _comboNames.Count)
                    ? 0 : KeyOf(_comboNames[(int)key - 1]);
            };

            MyAPIGateway.TerminalControls.AddControl<IMyEventControllerBlock>(combo);
        }

        // Names shown in the dropdown. Refreshed from the planet the controller is on
        // whenever the terminal is opened, which is the only time they are looked at.
        private static readonly List<string> _comboNames = new List<string>();

        private static GTEventWeatherType Selected(IMyTerminalBlock block)
        {
            var ev = block as IMyEventControllerBlock;
            if (ev == null) return null;

            var comp = ev.SelectedEvent as GTEventWeatherType;
            if (comp != null) comp.RefreshComboNames(ev);
            return comp;
        }

        private void RefreshComboNames(IMyEventControllerBlock ev)
        {
            var names = PlanetWeather.NamesAt(ev.GetPosition());
            if (names.Count == 0) return;

            // Only rewrite when it actually changed - the getter runs on every GUI frame.
            if (_comboNames.Count == names.Count)
            {
                bool same = true;
                for (int i = 0; i < names.Count; i++)
                    if (_comboNames[i] != names[i]) { same = false; break; }
                if (same) return;
            }

            _comboNames.Clear();
            _comboNames.AddRange(names);
        }

        public void Tick()
        {
            var ev = Entity as IMyEventControllerBlock;
            if (ev == null) return;

            if ((object)ev.SelectedEvent != this) { _pane.Clear(ev); return; }

            var wx = MyAPIGateway.Session.WeatherEffects;
            if (wx == null) return;

            var info = _pane.Begin();
            string transition = null;
            info.Append("Event: ").Append(EventDisplayName.String).Append('\n');
            info.Append("Watching for: ")
                .Append(SelectedName() ?? "any weather").Append('\n');

            if (!ev.IsWorking)
            {
                info.Append("Event Controller is off or unpowered.\n");
                EventSupport.TraceTick(ev, transition, info, ref _lastBeat);
                _pane.Commit(ev);
                return;
            }

            if (_observed.Count == 0) info.Append("No instruments selected.\n");

            bool any = false, sawInstrument = false;

            for (int i = _observed.Count - 1; i >= 0; i--)
            {
                var b = _observed[i];
                if (b == null || b.Closed) { _observed.RemoveAt(i); continue; }

                // Must genuinely be an instrument. This was GetValueFloat("GT_SysBlockRole"),
                // which THROWS while the GT_ properties are unregistered - and the throw
                // escaped to the session's catch, so the whole event died silently every
                // second. See EventSupport.
                if (EventSupport.RoleOf(b) <= 0f) continue;

                info.Append(b.CustomName).Append(": ");

                // The instrument must be a working one. A powered-down Weather Station
                // reporting nothing is not the same as clear skies, and must not clear
                // an alarm.
                // Enabled, functional AND powered: IsWorking alone ignores power on an
                // UpgradeModule, so an unpowered instrument used to keep answering.
                string offline = Instruments.OfflineReason(b);
                if (offline != null) { info.Append("offline - ").Append(offline.ToLowerInvariant()).Append('\n'); continue; }

                sawInstrument = true;

                string name = null;
                try { name = wx.GetWeather(b.GetPosition()); } catch { }

                bool active = !string.IsNullOrEmpty(name) && name != "Clear";
                info.Append(active ? name : "clear").Append('\n');
                if (!active) continue;

                if (_selected.Value == 0 ||
                    KeyOf(name) == _selected.Value)
                {
                    any = true;
                }
            }

            if (sawInstrument && (!_state.HasValue || _state.Value != any))
            {
                // Fire only on a change of state, and never on the first evaluation -
                // see WeatherIntensityEvent for why both matter.
                bool first = !_state.HasValue;
                _state = any;
                transition = first ? "LATCH " + (any ? "yes" : "no") : "FIRE " + (any ? "slot 1 (yes)" : "slot 2 (no)");
                if (!first) EventSupport.Fire(ev, any);
            }

            info.Append(EventSupport.Output(_state, YesNoToolbarYesDescription, YesNoToolbarNoDescription));
            EventSupport.TraceTick(ev, transition, info, ref _lastBeat);
            _pane.Commit(ev);
        }
    }

    // Which effects a planet can produce, read from its generator definition and cached
    // per generator - definitions do not change during a session, and every Earthlike
    // shares one, so the walk happens once per planet TYPE however many planets exist.
    //
    // This mirrors Ground Truth's WeatherCatalog deliberately rather than depending on
    // it: the two mods stay independent, and the read is cheap and cached in both.
    public static class PlanetWeather
    {
        private static readonly Dictionary<string, List<string>> _cache =
            new Dictionary<string, List<string>>();

        private static readonly List<string> _empty = new List<string>();

        public static List<string> NamesAt(Vector3D position)
        {
            var planet = MyGamePruningStructure.GetClosestPlanet(position);
            if (planet == null || planet.Generator == null) return _empty;

            var key = planet.Generator.Id.SubtypeName;
            List<string> names;
            if (_cache.TryGetValue(key, out names)) return names;

            names = new List<string>();
            try
            {
                var gens = planet.Generator.WeatherGenerators;
                if (gens != null)
                {
                    foreach (var gen in gens)
                    {
                        if (gen.Weathers == null) continue;
                        foreach (var w in gen.Weathers)
                        {
                            if (string.IsNullOrEmpty(w.Name)) continue;
                            if (!names.Contains(w.Name)) names.Add(w.Name);
                        }
                    }
                }
            }
            catch { }

            _cache[key] = names;
            return names;
        }
    }
}
