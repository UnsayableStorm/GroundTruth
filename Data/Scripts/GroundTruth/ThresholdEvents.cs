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
        private bool? _state;

        /// <summary>Instrument role to accept, or 0 for any Ground Truth instrument.</summary>
        protected abstract float WantedRole { get; }

        /// <summary>
        /// The reading on the SAME 0-1 scale the threshold slider reports. Percentages
        /// pass through unscaled; counts and durations divide by a stated full scale.
        /// Null if this block cannot answer.
        /// </summary>
        protected abstract float? Value(IMyTerminalBlock instrument);

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
            base.OnBeforeRemovedFromContainer();
        }

        public bool IsBlockValidForList(IMyTerminalBlock block)
        {
            if (block == null) return false;
            try
            {
                float role = block.GetValueFloat("GT_SysBlockRole");
                return WantedRole == 0f ? role > 0f : role == WantedRole;
            }
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

        // Threshold or condition changed: evaluate the new setting fresh rather than
        // against the old one.
        public void NotifyValuesChanged() { _state = null; }

        public void RefreshDetailedInfo() { }

        public void CreateTerminalInterfaceControls<T>() where T : IMyTerminalBlock { }

        public void Tick()
        {
            var ev = Entity as IMyEventControllerBlock;
            if (ev == null || !ev.IsWorking || _observed.Count == 0) return;

            var ingame = ev as Sandbox.ModAPI.Ingame.IMyEventControllerBlock;
            if (ingame == null) return;

            float threshold = ingame.Threshold;
            bool lowerOrEqual = ingame.IsLowerOrEqualCondition;
            bool andMode = ingame.IsAndModeEnabled;

            bool any = false, all = true, answered = false;

            for (int i = _observed.Count - 1; i >= 0; i--)
            {
                var b = _observed[i];
                if (b == null || b.Closed) { _observed.RemoveAt(i); continue; }

                // A dark instrument is not a reading of zero and must not clear an alarm.
                if (!b.IsWorking) continue;

                float? v = Value(b);
                if (!v.HasValue) continue;

                answered = true;
                bool met = lowerOrEqual ? v.Value <= threshold : v.Value >= threshold;
                if (met) any = true; else all = false;
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
    // 1007 - minutes of life left

    [ProtoBuf.ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventTimeToCritical : MyObjectBuilder_ComponentBase { }

    [MyComponentType(typeof(GTEventTimeToCritical))]
    [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    [MyComponentBuilder(typeof(MyObjectBuilder_GTEventTimeToCritical), true)]
    public class GTEventTimeToCritical : GTThresholdEvent
    {
        public const long SelectionId = 1007L;

        public override string ComponentTypeDebugString { get { return "GT_TimeToCritical"; } }
        public override MyStringId EventDisplayName
        { get { return MyStringId.GetOrCompute("Radiation time to critical, 100 = 30 min [Ground Truth]"); } }
        public override long UniqueSelectionId { get { return SelectionId; } }
        public override string YesNoToolbarYesDescription { get { return "Time to critical reached"; } }
        public override string YesNoToolbarNoDescription { get { return "Time to critical cleared"; } }

        protected override float WantedRole { get { return Instruments.RoleRadiation; } }

        // Minutes, so the slider reads as a decision: set 5, get five minutes of margin.
        //
        // Returns null when nothing is accumulating - there is no time to critical, and
        // reporting a huge number would be as wrong as reporting zero. That also means a
        // "below 5 minutes" alarm stays silent in safety rather than firing on 0.
        // FULL SCALE IS 30 MINUTES. The slider reports 0-1, so 50 on it is 15 minutes
        // and 10 is 3 minutes. Stated in the event name too - it cannot be guessed.
        public const float FullScaleMinutes = 30f;

        protected override float? Value(IMyTerminalBlock b)
        {
            // Not accumulating means time to critical is unbounded, which is the TOP of
            // the scale - not "no reading".
            //
            // Returning null here looked safer and was worse: the event abstained, the
            // latched state never changed, and an alarm set to "below 6 minutes" could
            // never clear once the player reached shelter. Full scale clears it, and
            // still cannot trip a below-threshold alarm.
            if (!ReadFlag(b, "GT_RadEnabled")) return null;   // radiation off: no opinion
            if (!ReadFlag(b, "GT_RadAccumulates")) return 1f;

            float seconds = Read(b, "GT_RadTimeToCritical");
            if (seconds < 0) return 1f;

            return Math.Min(1f, (seconds / 60f) / FullScaleMinutes);
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
        protected override float? Value(IMyTerminalBlock b)
        {
            float mult = Read(b, "GT_WxSolarMult");
            if (mult < 0) return null;

            bool sunUp = ReadFlag(b, "GT_SunUp");
            float elevation = Read(b, "GT_SunElevation");

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
        protected override float? Value(IMyTerminalBlock b)
        {
            // Already 0-1, exactly what the slider reports. No scaling.
            float oxy = Read(b, "GT_EnvOxygen");
            return oxy < 0 ? (float?)null : oxy;
        }
    }

    // ------------------------------------------------------------------
    // 1010 - how much wildlife

    [ProtoBuf.ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventBioCount : MyObjectBuilder_ComponentBase { }

    [MyComponentType(typeof(GTEventBioCount))]
    [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    [MyComponentBuilder(typeof(MyObjectBuilder_GTEventBioCount), true)]
    public class GTEventBioCount : GTThresholdEvent
    {
        public const long SelectionId = 1010L;

        public override string ComponentTypeDebugString { get { return "GT_BioCount"; } }
        public override MyStringId EventDisplayName
        { get { return MyStringId.GetOrCompute("Biosignature count, 100 = 50 [Ground Truth]"); } }
        public override long UniqueSelectionId { get { return SelectionId; } }
        public override string YesNoToolbarYesDescription { get { return "Biosignature count reached"; } }
        public override string YesNoToolbarNoDescription { get { return "Biosignature count dropped"; } }

        protected override float WantedRole { get { return Instruments.RoleBio; } }

        // FULL SCALE IS 50 ANIMALS. 20 on the slider is 10 animals.
        public const float FullScaleCount = 50f;

        protected override float? Value(IMyTerminalBlock b)
        {
            float n = Read(b, "GT_BioCount");
            return n < 0 ? (float?)null : Math.Min(1f, n / FullScaleCount);
        }
    }

    // ------------------------------------------------------------------
    // 1011 - how many things that are not wildlife

    [ProtoBuf.ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventBioContacts : MyObjectBuilder_ComponentBase { }

    [MyComponentType(typeof(GTEventBioContacts))]
    [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    [MyComponentBuilder(typeof(MyObjectBuilder_GTEventBioContacts), true)]
    public class GTEventBioContacts : GTThresholdEvent
    {
        public const long SelectionId = 1011L;

        public override string ComponentTypeDebugString { get { return "GT_BioContacts"; } }
        public override MyStringId EventDisplayName
        { get { return MyStringId.GetOrCompute("Non-biological contacts, 100 = 20 [Ground Truth]"); } }
        public override long UniqueSelectionId { get { return SelectionId; } }
        public override string YesNoToolbarYesDescription { get { return "Contacts threshold reached"; } }
        public override string YesNoToolbarNoDescription { get { return "Contacts cleared"; } }

        protected override float WantedRole { get { return Instruments.RoleBio; } }

        // FULL SCALE IS 20 CONTACTS - tighter than wildlife, because one robot matters
        // and twenty is a siege. 5 on the slider is one contact.
        public const float FullScaleContacts = 20f;

        protected override float? Value(IMyTerminalBlock b)
        {
            float n = Read(b, "GT_BioContacts");
            return n < 0 ? (float?)null : Math.Min(1f, n / FullScaleContacts);
        }
    }
}
