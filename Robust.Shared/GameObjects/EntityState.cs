using Robust.Shared.Serialization;
using System;
using JetBrains.Annotations;
using Robust.Shared.Interfaces.GameObjects;
using Robust.Shared.IoC;

namespace Robust.Shared.GameObjects
{
    [Serializable, NetSerializable]
    public sealed class EntityState
    {
        public EntityUid Uid { get; }
        public ComponentChanged[]? ComponentChanges { get; }
        public ComponentState[]? ComponentStates { get; }

        public EntityState(EntityUid uid, ComponentChanged[]? changedComponents, ComponentState[]? componentStates)
        {
            Uid = uid;

            // empty lists are 5 bytes each
            ComponentChanges = changedComponents == null || changedComponents.Length == 0 ? null : changedComponents;
            ComponentStates = componentStates == null || componentStates.Length == 0 ? null : componentStates;
        }
    }

    [Serializable, NetSerializable]
    public readonly struct ComponentChanged
    {
        // 15ish bytes to create a component (strings are big), 5 bytes to remove one

        /// <summary>
        ///     Was the component added or removed from the entity.
        /// </summary>
        public readonly bool Deleted;

        /// <summary>
        ///     The Network ID of the component to remove.
        /// </summary>
        public readonly uint NetID;

        /// <summary>
        ///     The prototype name of the component to add.
        /// </summary>
        public string ComponentName => IoCManager.Resolve<IComponentFactory>().GetRegistration(NetID).Name;

        public ComponentChanged(bool deleted, uint netId)
        {
            Deleted = deleted;
            NetID = netId;
        }

        public override string ToString()
        {
            return $"{(Deleted ? "D" : "C")} {NetID} {ComponentName}";
        }

        public static ComponentChanged Added(uint netId)
        {
            return new ComponentChanged(false, netId);
        }

        public static ComponentChanged Removed(uint netId)
        {
            return new ComponentChanged(true, netId);
        }
    }
}
