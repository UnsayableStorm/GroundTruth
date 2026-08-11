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

        // null means not yet evaluated. The first evaluation latches without firing, so
        // building a controller while the condition is already true does not trigger an
        // alarm for something the player never saw happen.
        private bool? _state;

        /// <summary>Which instrument this event watches. See Instruments.Role*.</summary>
        protected abstract float WantedRole { get; }

        /// <summary>The question. Null means this block cannot answer right now.</summary>
        protected abstract bool? Evaluate(IMyTerminalBlock instrument);

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
            base.OnBeforeRemovedFromContainer();
        }

        // Only instruments that can answer the question are offered. Listing every block
        // on the grid and returning nothing for most is worse than not listing them.
        public bool IsBlockValidForList(IMyTerminalBlock block)
        {
            if (block == null) return false;
            try { return block.GetValueFloat("GT_SysBlockRole") == WantedRole; }
            catch { return false; }
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

        // Nothing custom: the block list is a standard control and the flags above ask
        // for it. There is no threshold or condition to draw.
        public void CreateTerminalInterfaceControls<T>() where T : IMyTerminalBlock { }

        public void Tick()
        {
            var ev = Entity as IMyEventControllerBlock;
            if (ev == null || !ev.IsWorking || _observed.Count == 0) return;

            var ingame = ev as Sandbox.ModAPI.Ingame.IMyEventControllerBlock;
            bool andMode = ingame != null && ingame.IsAndModeEnabled;

            bool any = false, all = true, answered = false;

            for (int i = _observed.Count - 1; i >= 0; i--)
            {
                var b = _observed[i];
                if (b == null || b.Closed) { _observed.RemoveAt(i); continue; }

                // A powered-down instrument is not the same as a false reading, and must
                // never clear an alarm by going dark.
                if (!b.IsWorking) continue;

                bool? answer = Evaluate(b);
                if (!answer.HasValue) continue;

                answered = true;
                if (answer.Value) any = true; else all = false;
            }

            if (!answered) return;

            bool now = andMode ? all : any;

            if (_state.HasValue && _state.Value == now) return;
            bool first = !_state.HasValue;
            _state = now;
            if (first) return;

            try { ev.TriggerAction(now ? 0 : 1); }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole("GT event trigger: " + e); }
        }

        // Shared reading helper: -1 is the API's "no reading" sentinel, which is not the
        // same as a false answer and must not be treated as one.
        protected static float Read(IMyTerminalBlock b, string property)
        {
            try { return b.GetValueFloat(property); }
            catch { return -1f; }
        }

        protected static bool ReadFlag(IMyTerminalBlock b, string property)
        {
            try { return b.GetValueBool(property); }
            catch { return false; }
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
        protected override bool? Evaluate(IMyTerminalBlock b)
        {
            if (!ReadFlag(b, "GT_RadEnabled")) return null;   // radiation off in this world
            return ReadFlag(b, "GT_RadAccumulates");
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
        protected override bool? Evaluate(IMyTerminalBlock b)
        {
            float state = Read(b, "GT_RadShelterState");
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
        protected override bool? Evaluate(IMyTerminalBlock b)
        {
            return ReadFlag(b, "GT_HabBreached");
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
        protected override bool? Evaluate(IMyTerminalBlock b)
        {
            if (!ReadFlag(b, "GT_WxActive")) return false;
            return ReadFlag(b, "GT_WxHazardActive");
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
        protected override bool? Evaluate(IMyTerminalBlock b)
        {
            float n = Read(b, "GT_BioContacts");
            if (n < 0) return null;
            return n >= 1f;
        }
    }
}
