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
    // Threshold Event Controller events.
    //
    // THE THRESHOLD IS A FRACTION, NOT A PERCENTAGE
    //
    // The slider is labelled 0-100 and IMyEventControllerBlock.Threshold returns 0-1.
    // Setting 60 yields 0.6. Measured 2026-08-10:
    //
    //   GT threshold GT_Oxygen: value=89.67 thr=0.6 lower=True  ->  never met
    //
    // So a reading must be compared in the SAME units the slider reports: fractions for
    // anything percentage-like, and NOT scaled to 0-100.
    //
    // This also explains why an early weather-intensity test fired on light fog at
    // threshold 80 - the comparison was 100 against 0.8, so any weather tripped it. Two
    // bugs looked like one roughly-correct behaviour.
    //
    // Readings that are NOT percentages - minutes, counts of animals - cannot be
    // expressed on a 0-1 slider directly. Those map the fraction onto a stated full-scale
    // range, named in the event title because a player cannot infer it.
    //
    // The above/below selector is left ON. Naming an event "below" would bake in a
    // direction the player might not want, and the vanilla events all offer the choice.
    public abstract class GTThresholdEvent : MyEventProxyEntityComponent, IMyEventComponentWithGui
    {
        public static readonly List<GTThresholdEvent> Live = new List<GTThresholdEvent>();

        private readonly List<IMyTerminalBlock> _observed = new List<IMyTerminalBlock>();
        private readonly EventPane _pane = new EventPane();
        private double _lastBeat = -1e9;
        private bool? _state;

        /// <summary>Instrument role to accept, or 0 for any Ground Truth instrument.</summary>
        protected abstract float WantedRole { get; }

        /// <summary>
        /// The reading on the SAME 0-1 scale the threshold slider reports. Percentages
        /// pass through unscaled; counts and durations divide by a stated full scale.
        /// Null if this block cannot answer. Reads BlockState, never GT_ properties -
        /// see EventSupport for why.
        /// </summary>
        protected abstract float? Value(GroundTruthSession.BlockState s);

        public abstract MyStringId EventDisplayName { get; }
        public abstract long UniqueSelectionId { get; }
        public abstract string YesNoToolbarYesDescription { get; }
        public abstract string YesNoToolbarNoDescription { get; }

        public bool IsSelected { get; set; }
        public bool IsThresholdUsed { get { return true; } }
        public bool IsConditionSelectionUsed { get { return true; } }
        public bool IsBlocksListUsed { get { return true; } }

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
            float role = EventSupport.RoleOf(block);
            return WantedRole == 0f ? role > 0f : role == WantedRole;
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

        // Threshold, condition, AND mode or the controller's On/Off changed.
        //
        // This used to clear the latched state, so the new setting was judged fresh -
        // and a fresh judgement never fires. Dragging the slider across the reading,
        // which is exactly how a player tests a new controller, therefore did nothing.
        // Vanilla re-evaluates against the state it already holds and fires if the new
        // setting flips it (MyEventControllerGenericEvent.NotifyValuesChanged, decompiled
        // 2026-10-05). Keeping the latch gives the same behaviour on the next tick.
        public void NotifyValuesChanged() { }

        // Called by the controller after its block list changes.
        public void RefreshDetailedInfo() { }

        public void CreateTerminalInterfaceControls<T>() where T : IMyTerminalBlock { }

        public void Tick()
        {
            var ev = Entity as IMyEventControllerBlock;
            if (ev == null) return;

            if ((object)ev.SelectedEvent != this) { _pane.Clear(ev); return; }

            var ingame = ev as Sandbox.ModAPI.Ingame.IMyEventControllerBlock;
            if (ingame == null) return;

            float threshold = ingame.Threshold;
            bool lowerOrEqual = ingame.IsLowerOrEqualCondition;
            bool andMode = ingame.IsAndModeEnabled;

            var info = _pane.Begin();
            string transition = null;
            info.Append("Event: ").Append(EventDisplayName.String).Append('\n');
            info.Append(lowerOrEqual ? "Condition: below or equal" : "Condition: above or equal").Append('\n');
            info.Append("Threshold: ").Append(EventSupport.Percent(threshold)).Append('\n');

            if (!ev.IsWorking)
            {
                info.Append("Event Controller is off or unpowered.\n");
                EventSupport.TraceTick(ev, transition, info, ref _lastBeat);
                _pane.Commit(ev);
                return;
            }

            if (_observed.Count == 0) info.Append("No instruments selected.\n");

            bool any = false, all = true, answered = false;

            for (int i = _observed.Count - 1; i >= 0; i--)
            {
                var b = _observed[i];
                if (b == null || b.Closed) { _observed.RemoveAt(i); continue; }

                info.Append(b.CustomName).Append(": ");

                // A dark instrument is not a reading of zero and must not clear an alarm.
                // Enabled, functional AND powered: IsWorking alone ignores power on an
                // UpgradeModule, so an unpowered instrument used to keep answering.
                string offline = Instruments.OfflineReason(b);
                if (offline != null) { info.Append("offline - ").Append(offline.ToLowerInvariant()).Append('\n'); continue; }

                var s = EventSupport.State(b);
                float? v = s == null ? null : Value(s);
                if (!v.HasValue) { info.Append("no reading\n"); continue; }

                info.Append(EventSupport.Percent(v.Value)).Append('\n');

                answered = true;
                bool met = lowerOrEqual ? v.Value <= threshold : v.Value >= threshold;
                if (met) any = true; else all = false;
            }

            if (answered)
            {
                bool now = andMode ? all : any;

                if (!_state.HasValue || _state.Value != now)
                {
                    bool first = !_state.HasValue;
                    _state = now;

                    // The first evaluation latches without firing, like vanilla's
                    // AddBlocks: a controller built while the condition already holds
                    // does not fire for something the player never saw happen.
                    transition = first ? "LATCH " + (now ? "yes" : "no") : "FIRE " + (now ? "slot 1 (yes)" : "slot 2 (no)");
                    if (!first) EventSupport.Fire(ev, now);
                }
            }

            info.Append(EventSupport.Output(_state, YesNoToolbarYesDescription, YesNoToolbarNoDescription));
            EventSupport.TraceTick(ev, transition, info, ref _lastBeat);
            _pane.Commit(ev);
        }
    }

    // ------------------------------------------------------------------
    // 1008 - solar output, as a percentage of unobstructed

    [ProtoBuf.ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventSolarOutput : MyObjectBuilder_ComponentBase { }

    [MyComponentType(typeof(GTEventSolarOutput))]
    [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    [MyComponentBuilder(typeof(MyObjectBuilder_GTEventSolarOutput), true)]
    public class GTEventSolarOutput : GTThresholdEvent
    {
        public const long SelectionId = 1008L;

        public override string ComponentTypeDebugString { get { return "GT_SolarOutput"; } }
        public override MyStringId EventDisplayName
        { get { return MyStringId.GetOrCompute("Solar output percent [Ground Truth]"); } }
        public override long UniqueSelectionId { get { return SelectionId; } }
        public override string YesNoToolbarYesDescription { get { return "Solar output threshold reached"; } }
        public override string YesNoToolbarNoDescription { get { return "Solar output recovered"; } }

        protected override float WantedRole { get { return Instruments.RoleWeather; } }

        // Weather AND daylight. The weather multiplier alone reads 100% at midnight,
        // which is true of the sky and useless as a trigger for starting a reactor.
        protected override float? Value(GroundTruthSession.BlockState s)
        {
            float mult = s.SolarMult;                       // GT_WxSolarMult
            if (mult < 0) return null;

            bool sunUp = s.Env.SunElevation > 0;            // GT_SunUp
            float elevation = (float)s.Env.SunElevation;    // GT_SunElevation

            // In space there is no horizon; elevation reports -999 and weather does not
            // apply, so the weather figure stands alone.
            // Clamped to 1.0: HeatWave declares 1.35, and a slider that cannot express
            // more than 1.0 could otherwise never represent "full output".
            if (elevation < -900f) return Math.Min(1f, mult);

            return sunUp ? Math.Min(1f, mult) : 0f;
        }
    }

    // ------------------------------------------------------------------
    // 1009 - breathable oxygen

    [ProtoBuf.ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventOxygen : MyObjectBuilder_ComponentBase { }

    [MyComponentType(typeof(GTEventOxygen))]
    [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    [MyComponentBuilder(typeof(MyObjectBuilder_GTEventOxygen), true)]
    public class GTEventOxygen : GTThresholdEvent
    {
        public const long SelectionId = 1009L;

        public override string ComponentTypeDebugString { get { return "GT_Oxygen"; } }
        public override MyStringId EventDisplayName
        { get { return MyStringId.GetOrCompute("Outside oxygen percent [Ground Truth]"); } }
        public override long UniqueSelectionId { get { return SelectionId; } }
        public override string YesNoToolbarYesDescription { get { return "Oxygen threshold reached"; } }
        public override string YesNoToolbarNoDescription { get { return "Oxygen recovered"; } }

        // Any instrument: oxygen is a property of the position, not of which sensor asks.
        protected override float WantedRole { get { return 0f; } }

        // Breathable oxygen outside, weather already applied. AlienFog drives this to
        // zero, which is the case worth automating against.
        protected override float? Value(GroundTruthSession.BlockState s)
        {
            // Already 0-1, exactly what the slider reports. No scaling.
            float oxy = (float)s.Env.Oxygen;                // GT_EnvOxygen
            return oxy < 0 ? (float?)null : oxy;
        }
    }
}
