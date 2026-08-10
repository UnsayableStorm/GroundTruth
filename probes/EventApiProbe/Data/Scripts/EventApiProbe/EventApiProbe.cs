using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.ObjectBuilders;

namespace EventApiProbe
{
    // Compile-time whitelist probe for ADDING a custom Event Controller event.
    //
    // Reflection on the game assemblies shows every built-in event has this shape:
    //
    //   [MyComponentType(typeof(Self))]
    //   [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    //   [MyComponentBuilder(typeof(MyObjectBuilder_EventBlockOnOff), true)]
    //   class MyEventBlockOnOff : MyEventProxyEntityComponent, IMyEventComponentWithGui
    //
    // So supplying our own needs four things, and each is probed separately below:
    //
    //   1. the base class      Sandbox.Game.EntityComponents.MyEventProxyEntityComponent
    //   2. the interfaces      Sandbox.ModAPI.IMyEventComponentWithGui
    //   3. the attributes      VRage.Game.Components.MyComponentType / MyComponentBuilder
    //   4. an object builder   our own MyObjectBuilder_ComponentBase subclass
    //
    // The mod whitelist is enforced at COMPILE time and reports EVERY violation, not
    // just the first - so one load produces the complete list of what is blocked.
    //
    // Nothing executes: the calls sit behind a const-false guard. The references are
    // what matter.
    //
    // Read the log for "MOD_ERROR: EventApiProbe".
    // RESULT 2026-08-09: everything below compiles EXCEPT nothing - the base class,
    // the interfaces and all three attributes are reachable from mod code. The only
    // failure was a missing using for MyObjectBuilder_ComponentBase, which lives in
    // VRage.Game.ObjectBuilders.ComponentSystem. Custom events are possible.

    // --- probe 4: can a mod declare its own component object builder? ---
    [ProtoBuf.ProtoContract]
    [MyObjectBuilderDefinition]
    public class MyObjectBuilder_GTProbeEvent : MyObjectBuilder_ComponentBase
    {
    }

    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class EventApiProbeSession : MySessionComponentBase
    {
        private const bool NeverRun = false;

        public override void LoadData()
        {
            base.LoadData();
            if (NeverRun)
            {
                ProbeBaseClass();
                ProbeInterfaces();
                ProbeAttributes();
                ProbeObjectBuilder();
            }
        }

        // 1. The base class every stock event derives from. This is the one most
        //    likely to be blocked, and the one that decides the whole question.
        private void ProbeBaseClass()
        {
            MyEventProxyEntityComponent proxy = null;
            var type = proxy.GetType();
        }

        // 2. The interfaces. Already known to be in Sandbox.ModAPI, but being able to
        //    READ them (BuildInfo does) is not the same as being able to implement them.
        private void ProbeInterfaces()
        {
            IMyEventComponentWithGui gui = null;
            bool blocks = gui.IsBlocksListUsed;
            bool threshold = gui.IsThresholdUsed;
            bool condition = gui.IsConditionSelectionUsed;
            gui.NotifyValuesChanged();

            IMyEventControllerEntityComponent comp = null;
            var name = comp.EventDisplayName;
            long id = comp.UniqueSelectionId;
            bool sel = comp.IsSelected;
        }

        // 3. The registration attributes, referenced as types.
        private void ProbeAttributes()
        {
            var a = typeof(MyComponentTypeAttribute);
            var b = typeof(MyComponentBuilderAttribute);
            var c = typeof(MyEntityDependencyTypeAttribute);
        }

        // 4. An object builder of our own, plus the stock one for comparison.
        private void ProbeObjectBuilder()
        {
            var mine = new MyObjectBuilder_GTProbeEvent();
            var stock = typeof(MyObjectBuilder_EventBlockOnOff);
        }
    }
}
