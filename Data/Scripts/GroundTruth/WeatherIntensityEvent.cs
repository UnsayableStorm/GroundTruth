using System;
using System.Collections.Generic;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.ObjectBuilders;
using VRage.Utils;

namespace GroundTruth
{
    // A custom Event Controller event: "Weather intensity [Ground Truth]".
    //
    // Lets a player with no scripting wire a weather reading to a beacon, a klaxon and a
    // door, using the vanilla block they already know.
    //
    // WHAT THE WHITELIST ALLOWS, established by probe on 2026-08-09:
    //
    //   MyEventProxyEntityComponent          base class      ALLOWED
    //   IMyEventComponentWithGui             interfaces      ALLOWED
    //   MyComponentType / MyComponentBuilder attributes      ALLOWED
    //   MyObjectBuilder_ComponentBase        our own OB      ALLOWED
    //   MyEventControllerGenericEvent<T>     helper          *** EVERY MEMBER PROHIBITED ***
    //
    // That last line is why this file is longer than it looks like it should be. The
    // stock events delegate trigger state, hysteresis and action firing to the generic
    // helper; mods cannot touch it, so all of that is implemented here against the one
    // thing that IS exposed: IMyEventControllerBlock.TriggerAction(slot).
    //
    // Slot 0 is the "yes" toolbar, slot 1 the "no" toolbar - matching how the vanilla
    // events present a condition and its inverse.

    [ProtoBuf.ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventWeatherIntensity : MyObjectBuilder_ComponentBase
    {
    }

    [MyComponentType(typeof(GTEventWeatherIntensity))]
    [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    [MyComponentBuilder(typeof(MyObjectBuilder_GTEventWeatherIntensity), true)]
    public class GTEventWeatherIntensity : MyEventProxyEntityComponent, IMyEventComponentWithGui
    {
        // Every live instance, so the session ticker can drive them. An event component
        // gets no update callback of its own; stock events hook block events, and a
        // weather reading has none to hook.
        public static readonly List<GTEventWeatherIntensity> Live = new List<GTEventWeatherIntensity>();

        private readonly List<IMyTerminalBlock> _observed = new List<IMyTerminalBlock>();

        // Aggregate condition state. null means "not yet evaluated", so the first
        // evaluation after placement does not fire an action for a condition that was
        // already true before the player set it up.
        private bool? _state;

        public override string ComponentTypeDebugString { get { return "GT_WeatherIntensity"; } }

        public MyStringId EventDisplayName
        {
            get { return MyStringId.GetOrCompute("Weather intensity [Ground Truth]"); }
        }

        // MUST match <UniqueSelectionId> in Data/EntityComponents.sbc. Vanilla occupies
        // 0-22; 1001 leaves Keen room to add events without colliding with ours.
        //
        // Never change it once published - saved Event Controller blocks store the id of
        // the event they were set to, and changing it silently repoints or orphans them.
        public const long SelectionId = 1001L;
        public long UniqueSelectionId { get { return SelectionId; } }

        public bool IsSelected { get; set; }
        public bool IsThresholdUsed { get { return true; } }
        public bool IsConditionSelectionUsed { get { return true; } }
        public bool IsBlocksListUsed { get { return true; } }

        public string YesNoToolbarYesDescription { get { return "Weather intensity reached"; } }
        public string YesNoToolbarNoDescription { get { return "Weather intensity dropped"; } }

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

        // Only Ground Truth weather instruments are offered. Listing every block on the
        // grid and then reading -1 off most of them would be worse than not listing them.
        public bool IsBlockValidForList(IMyTerminalBlock block)
        {
            return Role(block) == 3f;   // RoleWeather
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

        // Threshold or condition changed in the terminal. Forget the latched state so
        // the new setting is evaluated fresh rather than compared against the old one.
        public void NotifyValuesChanged()
        {
            _state = null;
        }

        public void RefreshDetailedInfo() { }

        // Threshold, condition and block list are standard controls; the Event Controller
        // supplies them because the flags above are true. Nothing custom to add.
        public void CreateTerminalInterfaceControls<T>() where T : IMyTerminalBlock { }

        // Driven once a second by the session ticker. Ground Truth recomputes on the same
        // cadence, so a faster tick would only re-read an unchanged cache.
        public void Tick()
        {
            var ev = Entity as IMyEventControllerBlock;
            if (ev == null || !ev.IsWorking || _observed.Count == 0) return;

            var ingame = ev as Sandbox.ModAPI.Ingame.IMyEventControllerBlock;
            if (ingame == null) return;

            float threshold = ingame.Threshold;
            bool lowerOrEqual = ingame.IsLowerOrEqualCondition;
            bool andMode = ingame.IsAndModeEnabled;

            bool any = false, all = true, sawReading = false;

            for (int i = _observed.Count - 1; i >= 0; i--)
            {
                var b = _observed[i];
                if (b == null || b.Closed) { _observed.RemoveAt(i); continue; }

                // Both 0-1. The slider is LABELLED 0-100 while Threshold returns a
                // fraction - setting 60 yields 0.6 - so the reading is compared as
                // published and NOT scaled. Scaling it is why an early test fired on
                // light fog at threshold 80: the comparison was 100 against 0.8.
                float raw = b.GetValueFloat("GT_WxIntensity");
                if (raw < 0f) continue;               // sentinel: no reading available

                sawReading = true;
                float value = raw;
                bool met = lowerOrEqual ? value <= threshold : value >= threshold;

                if (met) any = true; else all = false;
            }

            if (!sawReading) return;

            bool now = andMode ? all : any;

            // Fire only on a change of state. A condition that stays true must not
            // re-trigger every second - that would make a klaxon useless and a door
            // unusable.
            if (_state.HasValue && _state.Value == now) return;

            bool first = !_state.HasValue;
            _state = now;

            // On the first evaluation, latch the state without firing. Otherwise placing
            // the block during a storm would immediately trigger the alarm for an event
            // the player never saw happen.
            if (first) return;

            try { ev.TriggerAction(now ? 0 : 1); }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole("GT event trigger: " + e); }
        }

        private static float Role(IMyTerminalBlock block)
        {
            if (block == null) return 0f;
            try { return block.GetValueFloat("GT_SysBlockRole"); }
            catch { return 0f; }   // property absent: not one of ours
        }
    }

}
