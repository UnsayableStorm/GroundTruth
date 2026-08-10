using ProtoBuf;
using VRage.Game;
using VRage.Game.Definitions;
using VRage.ObjectBuilders;

namespace GroundTruth
{
    // The definition half of a custom Event Controller event.
    //
    // The C# component alone is not enough - it compiles, loads, and never appears in
    // the dropdown. The wiki says only "they require programming and EntityComponents
    // sbc to link", which is true and unhelpful; reflecting on MyEventBlockOnOffDefinition
    // gives the actual shape:
    //
    //   [MyDefinitionType(typeof(the object builder))]
    //   class XxxDefinition : MyComponentDefinitionBase { long UniqueSelectionId; }
    //
    // and the SBC entry is matched by xsi:type to the object builder, with TypeId equal
    // to the component type name from [MyComponentType].
    //
    // UniqueSelectionId identifies the event in the block's dropdown and in saved blocks.
    // Vanilla uses 0-22. Ours starts at 1001 to leave Keen room to add events without
    // colliding, and to leave space between our own.

    [ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventWeatherIntensityDefinition : MyObjectBuilder_ComponentDefinitionBase
    {
        [ProtoMember(1)]
        public long UniqueSelectionId;
    }

    [MyDefinitionType(typeof(MyObjectBuilder_GTEventWeatherIntensityDefinition))]
    public class GTEventWeatherIntensityDefinition : MyComponentDefinitionBase
    {
        public long UniqueSelectionId;

        protected override void Init(MyObjectBuilder_DefinitionBase builder)
        {
            base.Init(builder);

            var ob = builder as MyObjectBuilder_GTEventWeatherIntensityDefinition;
            if (ob != null) UniqueSelectionId = ob.UniqueSelectionId;
        }
    }

    [ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventWeatherTypeDefinition : MyObjectBuilder_ComponentDefinitionBase
    {
        [ProtoMember(1)]
        public long UniqueSelectionId;
    }

    [MyDefinitionType(typeof(MyObjectBuilder_GTEventWeatherTypeDefinition))]
    public class GTEventWeatherTypeDefinition : MyComponentDefinitionBase
    {
        public long UniqueSelectionId;

        protected override void Init(MyObjectBuilder_DefinitionBase builder)
        {
            base.Init(builder);
            var ob = builder as MyObjectBuilder_GTEventWeatherTypeDefinition;
            if (ob != null) UniqueSelectionId = ob.UniqueSelectionId;
        }
    }

    [ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventRadAccumulatingDefinition : MyObjectBuilder_ComponentDefinitionBase
    {
        [ProtoMember(1)]
        public long UniqueSelectionId;
    }

    [MyDefinitionType(typeof(MyObjectBuilder_GTEventRadAccumulatingDefinition))]
    public class GTEventRadAccumulatingDefinition : MyComponentDefinitionBase
    {
        public long UniqueSelectionId;

        protected override void Init(MyObjectBuilder_DefinitionBase builder)
        {
            base.Init(builder);
            var ob = builder as MyObjectBuilder_GTEventRadAccumulatingDefinition;
            if (ob != null) UniqueSelectionId = ob.UniqueSelectionId;
        }
    }

    [ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventShelterLostDefinition : MyObjectBuilder_ComponentDefinitionBase
    {
        [ProtoMember(1)]
        public long UniqueSelectionId;
    }

    [MyDefinitionType(typeof(MyObjectBuilder_GTEventShelterLostDefinition))]
    public class GTEventShelterLostDefinition : MyComponentDefinitionBase
    {
        public long UniqueSelectionId;

        protected override void Init(MyObjectBuilder_DefinitionBase builder)
        {
            base.Init(builder);
            var ob = builder as MyObjectBuilder_GTEventShelterLostDefinition;
            if (ob != null) UniqueSelectionId = ob.UniqueSelectionId;
        }
    }

    [ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventSealBreachedDefinition : MyObjectBuilder_ComponentDefinitionBase
    {
        [ProtoMember(1)]
        public long UniqueSelectionId;
    }

    [MyDefinitionType(typeof(MyObjectBuilder_GTEventSealBreachedDefinition))]
    public class GTEventSealBreachedDefinition : MyComponentDefinitionBase
    {
        public long UniqueSelectionId;

        protected override void Init(MyObjectBuilder_DefinitionBase builder)
        {
            base.Init(builder);
            var ob = builder as MyObjectBuilder_GTEventSealBreachedDefinition;
            if (ob != null) UniqueSelectionId = ob.UniqueSelectionId;
        }
    }

    [ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventWeatherHazardDefinition : MyObjectBuilder_ComponentDefinitionBase
    {
        [ProtoMember(1)]
        public long UniqueSelectionId;
    }

    [MyDefinitionType(typeof(MyObjectBuilder_GTEventWeatherHazardDefinition))]
    public class GTEventWeatherHazardDefinition : MyComponentDefinitionBase
    {
        public long UniqueSelectionId;

        protected override void Init(MyObjectBuilder_DefinitionBase builder)
        {
            base.Init(builder);
            var ob = builder as MyObjectBuilder_GTEventWeatherHazardDefinition;
            if (ob != null) UniqueSelectionId = ob.UniqueSelectionId;
        }
    }

    [ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventTimeToCriticalDefinition : MyObjectBuilder_ComponentDefinitionBase
    {
        [ProtoMember(1)]
        public long UniqueSelectionId;
    }

    [MyDefinitionType(typeof(MyObjectBuilder_GTEventTimeToCriticalDefinition))]
    public class GTEventTimeToCriticalDefinition : MyComponentDefinitionBase
    {
        public long UniqueSelectionId;

        protected override void Init(MyObjectBuilder_DefinitionBase builder)
        {
            base.Init(builder);
            var ob = builder as MyObjectBuilder_GTEventTimeToCriticalDefinition;
            if (ob != null) UniqueSelectionId = ob.UniqueSelectionId;
        }
    }

    [ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventSolarOutputDefinition : MyObjectBuilder_ComponentDefinitionBase
    {
        [ProtoMember(1)]
        public long UniqueSelectionId;
    }

    [MyDefinitionType(typeof(MyObjectBuilder_GTEventSolarOutputDefinition))]
    public class GTEventSolarOutputDefinition : MyComponentDefinitionBase
    {
        public long UniqueSelectionId;

        protected override void Init(MyObjectBuilder_DefinitionBase builder)
        {
            base.Init(builder);
            var ob = builder as MyObjectBuilder_GTEventSolarOutputDefinition;
            if (ob != null) UniqueSelectionId = ob.UniqueSelectionId;
        }
    }

    [ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventOxygenDefinition : MyObjectBuilder_ComponentDefinitionBase
    {
        [ProtoMember(1)]
        public long UniqueSelectionId;
    }

    [MyDefinitionType(typeof(MyObjectBuilder_GTEventOxygenDefinition))]
    public class GTEventOxygenDefinition : MyComponentDefinitionBase
    {
        public long UniqueSelectionId;

        protected override void Init(MyObjectBuilder_DefinitionBase builder)
        {
            base.Init(builder);
            var ob = builder as MyObjectBuilder_GTEventOxygenDefinition;
            if (ob != null) UniqueSelectionId = ob.UniqueSelectionId;
        }
    }

    [ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventBioCountDefinition : MyObjectBuilder_ComponentDefinitionBase
    {
        [ProtoMember(1)]
        public long UniqueSelectionId;
    }

    [MyDefinitionType(typeof(MyObjectBuilder_GTEventBioCountDefinition))]
    public class GTEventBioCountDefinition : MyComponentDefinitionBase
    {
        public long UniqueSelectionId;

        protected override void Init(MyObjectBuilder_DefinitionBase builder)
        {
            base.Init(builder);
            var ob = builder as MyObjectBuilder_GTEventBioCountDefinition;
            if (ob != null) UniqueSelectionId = ob.UniqueSelectionId;
        }
    }

    [ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTEventBioContactsDefinition : MyObjectBuilder_ComponentDefinitionBase
    {
        [ProtoMember(1)]
        public long UniqueSelectionId;
    }

    [MyDefinitionType(typeof(MyObjectBuilder_GTEventBioContactsDefinition))]
    public class GTEventBioContactsDefinition : MyComponentDefinitionBase
    {
        public long UniqueSelectionId;

        protected override void Init(MyObjectBuilder_DefinitionBase builder)
        {
            base.Init(builder);
            var ob = builder as MyObjectBuilder_GTEventBioContactsDefinition;
            if (ob != null) UniqueSelectionId = ob.UniqueSelectionId;
        }
    }
}
