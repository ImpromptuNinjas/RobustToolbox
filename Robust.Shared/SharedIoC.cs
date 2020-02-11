using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Exceptions;
using Robust.Shared.GameObjects;
using Robust.Shared.Interfaces.Configuration;
using Robust.Shared.Interfaces.GameObjects;
using Robust.Shared.Interfaces.Log;
using Robust.Shared.Interfaces.Map;
using Robust.Shared.Interfaces.Network;
using Robust.Shared.Interfaces.Physics;
using Robust.Shared.Interfaces.Random;
using Robust.Shared.Interfaces.Serialization;
using Robust.Shared.Interfaces.Timers;
using Robust.Shared.Interfaces.Timing;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Random;
using Robust.Shared.Serialization;
using Robust.Shared.Timers;
using Robust.Shared.Timing;

namespace Robust.Shared
{
    internal static class SharedIoC
    {
        public static void RegisterIoC()
        {
            IoCManager.Register<IComponentManager, ComponentManager>();
            IoCManager.Register<IConfigurationManager, ConfigurationManager>();
            IoCManager.Register<IDynamicTypeFactory, DynamicTypeFactory>();
            IoCManager.Register<IEntitySystemManager, EntitySystemManager>();
            IoCManager.Register<IGameTiming, GameTiming>();
            IoCManager.Register<ILocalizationManager, LocalizationManager>();
            IoCManager.Register<ILogManager, LogManager>();
            IoCManager.Register<IMapManager, MapManager>();
            IoCManager.Register<IMapManagerInternal, MapManager>();
            IoCManager.Register<IModLoader, ModLoader>();
            IoCManager.Register<INetManager, NetManager>();
            IoCManager.Register<IPhysicsManager, PhysicsManager>();
            IoCManager.Register<IRuntimeLog, RuntimeLog>();
            IoCManager.Register<ITaskManager, TaskManager>();
            IoCManager.Register<ITimerManager, TimerManager>();
            IoCManager.Register<IRobustRandom, RobustRandom>();
            IoCManager.RegisterAssembly<IRobustSerializer>("Robust.Serializer");
        }
    }
}
