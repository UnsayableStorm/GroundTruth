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
    // Yes/no Event Controller events.
    //
    // Most of what a player wants to automate has no magnitude. "The seal broke, close
    // the doors" needs no number, no comparison and no slider - and those are the events
    // that get used, because they cannot be misconfigured.
    //
    // All four share one base: the machinery for tracking observed blocks, latching
    // state and firing the right toolbar slot is identical, and only the question being
    // asked differs. Adding another boolean event is a subclass and four lines of SBC.
    //
    // Slot 0 is the "yes" toolbar, slot 1 the "no".
    public abstract class GTBooleanEvent : MyEventProxyEntityComponent, IMyEventComponentWithGui
    {
        // Every live instance of every boolean event, so the session can drive them all
        // from one loop on the same cadence as the readings they watch.
        public static readonly List<GTBooleanEvent> Live = new List<GTBooleanEvent>();

        private readonly List<IMyTerminalBlock> _observed = new List<IMyTerminalBlock>();
        private readonly EventPane _pane = new EventPane();
        private double _lastBeat = -1e9;

        // null means not yet evaluated. The first evaluation latches without firing, so
        // building a controller while the condition is already true does not trigger an
        // alarm for something the player never saw happen.
        private bool? _state;

        /// <summary>Which instrument this event watches. See Instruments.Role*.</summary>
        protected abstract float WantedRole { get; }

        /// <summary>
        /// The question. Null means this block cannot answer right now. Reads BlockState,
        /// never GT_ properties - see EventSupport for why.
        /// </summary>
        protected abstract bool? Evaluate(GroundTruthSession.BlockState s);

        public abstract MyStringId EventDisplayName { get; }
        public abstract long UniqueSelectionId { get; }
        public abstract string YesNoToolbarYesDescription { get; }
        public abstract string YesNoToolbarNoDescription { get; }

        public bool IsSelected { get; set; }

        // No threshold and no condition: the question is already yes or no. Leaving these
        // true would draw a slider and an above/below selector that mean nothing.
        public bool IsThresholdUsed { get { return false; } }
        public bool IsConditionSelectionUsed { get { return false; } }
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

        // Only instruments that can answer the question are offered. Listing every block
        // on the grid and returning nothing for most is worse than not listing them.
        public bool IsBlockValidForList(IMyTerminalBlock block)
        {
            return EventSupport.RoleOf(block) == WantedRole;
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

        // AND mode or the controller's On/Off changed. Keep the latch so a change that
        // flips the answer fires on the next tick, as vanilla does - see GTThresholdEvent.
        public void NotifyValuesChanged() { }

        // Called by the controller after its block list changes.
        public void RefreshDetailedInfo() { }

        // Nothing custom: the block list is a standard control and the flags above ask
        // for it. There is no threshold or condition to draw.
        public void CreateTerminalInterfaceControls<T>() where T : IMyTerminalBlock { }

        public void Tick()
        {
            var ev = Entity as IMyEventControllerBlock;
            if (ev == null) return;

            if ((object)ev.SelectedEvent != this) { _pane.Clear(ev); return; }

            var ingame = ev as Sandbox.ModAPI.Ingame.IMyEventControllerBlock;
            bool andMode = ingame != null && ingame.IsAndModeEnabled;

            var info = _pane.Begin();
            string transition = null;
            info.Append("Event: ").Append(EventDisplayName.String).Append('\n');

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

                // A powered-down instrument is not the same as a false reading, and must
                // never clear an alarm by going dark.
                if (!b.IsWorking) { info.Append("not working\n"); continue; }

                var s = EventSupport.State(b);
                bool? answer = s == null ? null : Evaluate(s);
                if (!answer.HasValue) { info.Append("no reading\n"); continue; }

                info.Append(answer.Value ? "yes\n" : "no\n");

                answered = true;
                if (answer.Value) any = true; else all = false;
            }

            if (answered)
            {
                bool now = andMode ? all : any;

                if (!_state.HasValue || _state.Value != now)
                {
                    bool first = !_state.HasValue;
                    _state = now;
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
    // 1003 - radiation is actually building, not merely present

    [ProtoBuf.ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventRadAccumulating : MyObjectBuilder_ComponentBase { }

    [MyComponentType(typeof(GTEventRadAccumulating))]
    [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    [MyComponentBuilder(typeof(MyObjectBuilder_GTEventRadAccumulating), true)]
    public class GTEventRadAccumulating : GTBooleanEvent
    {
        public const long SelectionId = 1003L;

        public override string ComponentTypeDebugString { get { return "GT_RadAccumulating"; } }
        public override MyStringId EventDisplayName
        { get { return MyStringId.GetOrCompute("Radiation accumulating [Ground Truth]"); } }
        public override long UniqueSelectionId { get { return SelectionId; } }
        public override string YesNoToolbarYesDescription { get { return "Radiation started building"; } }
        public override string YesNoToolbarNoDescription { get { return "Radiation stopped building"; } }

        protected override float WantedRole { get { return Instruments.RoleRadiation; } }

        // Exposure and accumulation are different questions. A dose below the engine's
        // ignore threshold is real and never builds, so this watches the one that
        // decides whether the player is in danger.
        protected override bool? Evaluate(GroundTruthSession.BlockState s)
        {
            if (!s.Rad.Enabled) return null;                  // GT_RadEnabled: radiation off in this world
            return s.Rad.Accumulates;                        // GT_RadAccumulates
        }
    }

    // ------------------------------------------------------------------
    // 1004 - no shelter at all

    [ProtoBuf.ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventShelterLost : MyObjectBuilder_ComponentBase { }

    [MyComponentType(typeof(GTEventShelterLost))]
    [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    [MyComponentBuilder(typeof(MyObjectBuilder_GTEventShelterLost), true)]
    public class GTEventShelterLost : GTBooleanEvent
    {
        public const long SelectionId = 1004L;

        public override string ComponentTypeDebugString { get { return "GT_ShelterLost"; } }
        public override MyStringId EventDisplayName
        { get { return MyStringId.GetOrCompute("Shelter lost [Ground Truth]"); } }
        public override long UniqueSelectionId { get { return SelectionId; } }
        public override string YesNoToolbarYesDescription { get { return "Exposed to open sky"; } }
        public override string YesNoToolbarNoDescription { get { return "Shelter regained"; } }

        protected override float WantedRole { get { return Instruments.RoleRadiation; } }

        // Shelter state 0 is exposed; 1 occluded, 2 sealed, 3 atmosphere. Fires on the
        // transition into full exposure, which is when a player wants the warning.
        protected override bool? Evaluate(GroundTruthSession.BlockState s)
        {
            float state = s.Rad.ShelterState;               // GT_RadShelterState
            if (state < 0) return null;
            return state == 0f;
        }
    }

    // ------------------------------------------------------------------
    // 1005 - the seal broke

    [ProtoBuf.ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventSealBreached : MyObjectBuilder_ComponentBase { }

    [MyComponentType(typeof(GTEventSealBreached))]
    [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    [MyComponentBuilder(typeof(MyObjectBuilder_GTEventSealBreached), true)]
    public class GTEventSealBreached : GTBooleanEvent
    {
        public const long SelectionId = 1005L;

        public override string ComponentTypeDebugString { get { return "GT_SealBreached"; } }
        public override MyStringId EventDisplayName
        { get { return MyStringId.GetOrCompute("Seal breached [Ground Truth]"); } }
        public override long UniqueSelectionId { get { return SelectionId; } }
        public override string YesNoToolbarYesDescription { get { return "Pressure seal lost"; } }
        public override string YesNoToolbarNoDescription { get { return "Pressure seal restored"; } }

        protected override float WantedRole { get { return Instruments.RoleHabitat; } }

        // GT_HabBreached is latched: it means "this WAS sealed and is not now", which is
        // a different thing from a block that was never in a sealed room. The latch is
        // what makes this usable as a door trigger.
        protected override bool? Evaluate(GroundTruthSession.BlockState s)
        {
            return s.Breached;                               // GT_HabBreached
        }
    }

    // ------------------------------------------------------------------
    // 1006 - the weather can hurt you, right now

    [ProtoBuf.ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventWeatherHazard : MyObjectBuilder_ComponentBase { }

    [MyComponentType(typeof(GTEventWeatherHazard))]
    [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    [MyComponentBuilder(typeof(MyObjectBuilder_GTEventWeatherHazard), true)]
    public class GTEventWeatherHazard : GTBooleanEvent
    {
        public const long SelectionId = 1006L;

        public override string ComponentTypeDebugString { get { return "GT_WeatherHazard"; } }
        public override MyStringId EventDisplayName
        { get { return MyStringId.GetOrCompute("Weather hazard active [Ground Truth]"); } }
        public override long UniqueSelectionId { get { return SelectionId; } }
        public override string YesNoToolbarYesDescription { get { return "Hazardous weather began"; } }
        public override string YesNoToolbarNoDescription { get { return "Hazardous weather ended"; } }

        protected override float WantedRole { get { return Instruments.RoleWeather; } }

        // True only once the storm reaches the intensity at which its declared hazard
        // starts - injury or radiation. A hazard below its threshold is not hurting
        // anyone yet, and an alarm that fires then is an alarm people learn to ignore.
        //
        // Radiation SHELTER never counts: rain declares -0.60, which protects.
        protected override bool? Evaluate(GroundTruthSession.BlockState s)
        {
            if (string.IsNullOrEmpty(s.Weather)) return false;   // GT_WxActive

            // GT_WxHazardActive, expression for expression.
            return (s.Effect.HasHealth && s.WeatherIntensity >= s.Effect.HealthMinIntensity) ||
                   (s.Effect.HasRadiation && s.Effect.RadiationGain > 0 &&
                    s.WeatherIntensity >= s.Effect.RadiationMinIntensity);
        }
    }

    // ------------------------------------------------------------------
    // 1012 - anything non-biological is here at all
    //
    // The threshold version of this (1011) asks "how many", which is the right
    // question for a siege and the wrong one for a tripwire. Its slider reports 0-1
    // against a full scale of 20 contacts, so 5 on a 0-100 dial means ONE contact -
    // correct, unusable, and not something a player should have to know.
    //
    // This one has no slider. One contact is the whole condition.

    [ProtoBuf.ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventBioContactPresent : MyObjectBuilder_ComponentBase { }

    [MyComponentType(typeof(GTEventBioContactPresent))]
    [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    [MyComponentBuilder(typeof(MyObjectBuilder_GTEventBioContactPresent), true)]
    public class GTEventBioContactPresent : GTBooleanEvent
    {
        public const long SelectionId = 1012L;

        public override string ComponentTypeDebugString { get { return "GT_BioContactPresent"; } }
        public override MyStringId EventDisplayName
        { get { return MyStringId.GetOrCompute("Non-biological contact detected [Ground Truth]"); } }
        public override long UniqueSelectionId { get { return SelectionId; } }
        public override string YesNoToolbarYesDescription { get { return "Something not alive arrived"; } }
        public override string YesNoToolbarNoDescription { get { return "Contacts gone"; } }

        protected override float WantedRole { get { return Instruments.RoleBio; } }

        // -1 means the block cannot answer, which is not the same as zero contacts.
        // Returning null leaves the alarm latched rather than clearing it on a dark
        // instrument.
        protected override bool? Evaluate(GroundTruthSession.BlockState s)
        {
            float n = s.Bio.Valid ? s.Bio.Contacts : -1f;    // GT_BioContacts
            if (n < 0) return null;
            return n >= 1f;
        }
    }

    // ------------------------------------------------------------------
    // 1013 - something alive is here
    //
    // Twin of 1012, for organisms rather than contacts. Both replaced slider events
    // that asked "how many" against a full scale nobody could see: 20 on a 0-100 dial
    // meant 10 animals. "Is there wildlife out there" is the question people actually
    // wire to a door.

    [ProtoBuf.ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventBioLifePresent : MyObjectBuilder_ComponentBase { }

    [MyComponentType(typeof(GTEventBioLifePresent))]
    [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    [MyComponentBuilder(typeof(MyObjectBuilder_GTEventBioLifePresent), true)]
    public class GTEventBioLifePresent : GTBooleanEvent
    {
        public const long SelectionId = 1013L;

        public override string ComponentTypeDebugString { get { return "GT_BioLifePresent"; } }
        public override MyStringId EventDisplayName
        { get { return MyStringId.GetOrCompute("Wildlife detected [Ground Truth]"); } }
        public override long UniqueSelectionId { get { return SelectionId; } }
        public override string YesNoToolbarYesDescription { get { return "Wildlife came into range"; } }
        public override string YesNoToolbarNoDescription { get { return "Wildlife gone"; } }

        protected override float WantedRole { get { return Instruments.RoleBio; } }

        // Organisms only - GT_BioCount excludes bots and armed humanoids, which are
        // counted as contacts and belong to event 1012. A wolf and a soldier should
        // not ring the same bell.
        protected override bool? Evaluate(GroundTruthSession.BlockState s)
        {
            float n = s.Bio.Valid ? s.Bio.Count : -1f;       // GT_BioCount
            if (n < 0) return null;
            return n >= 1f;
        }
    }
}
