using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.ObjectBuilders;
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
        private bool? _state;

        // Empty means "any weather".
        public string SelectedType = "";

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
            base.OnBeforeRemovedFromContainer();
        }

        public bool IsBlockValidForList(IMyTerminalBlock block)
        {
            return Role(block) == 3f;   // Weather Station
        }

        public void AddBlocks(List<IMyTerminalBlock> blocks)
        {
            if (blocks == null) return;
            foreach (var b in blocks)
                if (b != null && !_observed.Contains(b)) _observed.Add(b);
            _state = null;
        }

        public void RemoveBlocks(IEnumerable<IMyTerminalBlock> blocks)
        {
            if (blocks == null) return;
            foreach (var b in blocks) _observed.Remove(b);
            _state = null;
        }

        public void NotifyValuesChanged() { _state = null; }

        public void RefreshDetailedInfo() { }

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
                if (ev == null || string.IsNullOrEmpty(ev.SelectedType)) return 0;
                int idx = _comboNames.IndexOf(ev.SelectedType);
                return idx < 0 ? 0 : idx + 1;
            };

            combo.Setter = (b, key) =>
            {
                var ev = Selected(b);
                if (ev == null) return;
                ev.SelectedType = (key <= 0 || key > _comboNames.Count)
                    ? "" : _comboNames[(int)key - 1];
                ev._state = null;
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
            if (ev == null || !ev.IsWorking || _observed.Count == 0) return;

            var wx = MyAPIGateway.Session.WeatherEffects;
            if (wx == null) return;

            bool any = false, sawInstrument = false;

            for (int i = _observed.Count - 1; i >= 0; i--)
            {
                var b = _observed[i];
                if (b == null || b.Closed) { _observed.RemoveAt(i); continue; }

                // The instrument must be a working one. A powered-down Weather Station
                // reporting nothing is not the same as clear skies, and must not clear
                // an alarm.
                if (b.GetValueFloat("GT_SysBlockRole") < 0f) continue;
                if (!b.IsWorking) continue;

                sawInstrument = true;

                string name = null;
                try { name = wx.GetWeather(b.GetPosition()); } catch { }

                bool active = !string.IsNullOrEmpty(name) && name != "Clear";
                if (!active) continue;

                if (string.IsNullOrEmpty(SelectedType) ||
                    string.Equals(name, SelectedType, StringComparison.OrdinalIgnoreCase))
                {
                    any = true;
                }
            }

            if (!sawInstrument) return;

            // Fire only on a change of state, and never on the first evaluation - see
            // WeatherIntensityEvent for why both matter.
            if (_state.HasValue && _state.Value == any) return;
            bool first = !_state.HasValue;
            _state = any;
            if (first) return;

            try { ev.TriggerAction(any ? 0 : 1); }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole("GTEventWeatherType: " + e); }
        }

        private static float Role(IMyTerminalBlock block)
        {
            if (block == null) return 0f;
            try { return block.GetValueFloat("GT_SysBlockRole"); }
            catch { return 0f; }
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
