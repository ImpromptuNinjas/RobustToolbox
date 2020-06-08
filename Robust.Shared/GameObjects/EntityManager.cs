﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Robust.Shared.GameObjects.Components;
using Robust.Shared.GameObjects.Components.Transform;
using Robust.Shared.Interfaces.GameObjects;
using Robust.Shared.Interfaces.GameObjects.Components;
using Robust.Shared.Interfaces.Map;
using Robust.Shared.Interfaces.Timing;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Players;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.Shared.GameObjects
{
    /// <inheritdoc />
    public abstract class EntityManager : IEntityManager
    {
        #region Dependencies

        [Dependency] private readonly IEntityNetworkManager EntityNetworkManager = default!;
        [Dependency] private readonly IPrototypeManager PrototypeManager = default!;
        [Dependency] protected readonly IEntitySystemManager EntitySystemManager = default!;
        [Dependency] private readonly IComponentFactory ComponentFactory = default!;
        [Dependency] private readonly IComponentManager _componentManager = default!;
        [Dependency] private readonly IGameTiming _gameTiming = default!;
        [Dependency] private readonly IMapManager _mapManager = default!;

        #endregion Dependencies

        /// <inheritdoc />
        public GameTick CurrentTick => _gameTiming.CurTick;

        /// <inheritdoc />
        public IComponentManager ComponentManager => _componentManager;

        /// <inheritdoc />
        public IEntityNetworkManager EntityNetManager => EntityNetworkManager;

        /// <summary>
        ///     All entities currently stored in the manager.
        /// </summary>
        protected readonly Dictionary<EntityUid, IEntity> Entities = new Dictionary<EntityUid, IEntity>();

        protected readonly List<IEntity> AllEntities = new List<IEntity>();

        private readonly IEntityEventBus _eventBus = new EntityEventBus();

        /// <inheritdoc />
        public IEventBus EventBus => _eventBus;

        public bool Started { get; protected set; }

        public virtual void Initialize()
        {
            EntityNetworkManager.SetupNetworking();
            EntityNetworkManager.ReceivedComponentMessage += (sender, compMsg) => DispatchComponentMessage(compMsg);
            EntityNetworkManager.ReceivedSystemMessage += (sender, systemMsg) => EventBus.RaiseEvent(EventSource.Network, systemMsg);

            ComponentManager.Initialize();
            _componentManager.ComponentRemoved += (sender, args) => _eventBus.UnsubscribeEvents(args.Component);
        }

        public virtual void Startup()
        {
        }

        public virtual void Shutdown()
        {
            FlushEntities();
            EntitySystemManager.Shutdown();
            Started = false;
            _componentManager.Clear();
        }

        public virtual void Update(float frameTime)
        {
            EntityNetworkManager.Update();
            EntitySystemManager.Update(frameTime);
            _eventBus.ProcessEventQueue();
            CullDeletedEntities();
        }

        public virtual void FrameUpdate(float frameTime)
        {
            EntitySystemManager.FrameUpdate(frameTime);
        }

        #region Entity Management

        /// <inheritdoc />
        public abstract IEntity CreateEntityUninitialized(string? prototypeName);

        /// <inheritdoc />
        public abstract IEntity CreateEntityUninitialized(string? prototypeName, GridCoordinates coordinates);

        /// <inheritdoc />
        public abstract IEntity CreateEntityUninitialized(string? prototypeName, MapCoordinates coordinates);

        /// <inheritdoc />
        public abstract IEntity SpawnEntity(string? protoName, GridCoordinates coordinates);

        /// <inheritdoc />
        public abstract IEntity SpawnEntity(string? protoName, MapCoordinates coordinates);

        /// <inheritdoc />
        public abstract IEntity SpawnEntityNoMapInit(string? protoName, GridCoordinates coordinates);

        /// <summary>
        /// Returns an entity by id
        /// </summary>
        /// <param name="uid"></param>
        /// <returns>Entity or null if entity id doesn't exist</returns>
        public virtual IEntity GetEntity(EntityUid uid)
        {
            return Entities[uid];
        }

        /// <summary>
        /// Attempt to get an entity, returning whether or not an entity was gotten.
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="entity">The requested entity or null if the entity couldn't be found.</param>
        /// <returns>True if a value was returned, false otherwise.</returns>
        public bool TryGetEntity(EntityUid uid, [NotNullWhen(true)] out IEntity? entity)
        {
            if (Entities.TryGetValue(uid, out entity) && !entity.Deleted)
            {
                return true;
            }

            // entity might get assigned if it's deleted but still found,
            // prevent somebody from being "smart".
            entity = null;
            return false;
        }

        public IEnumerable<IEntity> GetEntities(IEntityQuery query)
        {
            return query.Match(this);
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesAt(MapId mapId, Vector2 position, bool approximate = false)
        {
            foreach (var entity in _entityTreesPerMap[mapId].Query(position, approximate))
            {
                var transform = entity.Transform;
                if (FloatMath.CloseTo(transform.GridPosition.X, position.X) &&
                    FloatMath.CloseTo(transform.GridPosition.Y, position.Y))
                {
                    yield return entity;
                }
            }
        }

        public IEnumerable<IEntity> GetEntities()
        {
            // Need to do an iterator loop to avoid issues with concurrent access.
            // ReSharper disable once ForCanBeConvertedToForeach
            for (var i = 0; i < AllEntities.Count; i++)
            {
                var entity = AllEntities[i];
                if (entity.Deleted)
                {
                    continue;
                }

                yield return entity;
            }
        }

        /// <summary>
        /// Shuts-down and removes given Entity. This is also broadcast to all clients.
        /// </summary>
        /// <param name="e">Entity to remove</param>
        public virtual void DeleteEntity(IEntity e)
        {
            e.Shutdown();
        }

        public void DeleteEntity(EntityUid uid)
        {
            if (TryGetEntity(uid, out var entity))
            {
                DeleteEntity(entity);
                UpdateEntityTree(entity);
            }
        }

        public bool EntityExists(EntityUid uid)
        {
            return TryGetEntity(uid, out var _);
        }

        /// <summary>
        /// Disposes all entities and clears all lists.
        /// </summary>
        public void FlushEntities()
        {
            foreach (var e in GetEntities())
            {
                e.Shutdown();
            }

            CullDeletedEntities();
        }

        /// <summary>
        ///     Allocates an entity and stores it but does not load components or do initialization.
        /// </summary>
        private protected Entity AllocEntity(string prototypeName, EntityUid? uid = null)
        {
            EntityPrototype? prototype = null;
            if (!string.IsNullOrWhiteSpace(prototypeName))
            {
                // If the prototype doesn't exist then we throw BEFORE we allocate the entity.
                prototype = PrototypeManager.Index<EntityPrototype>(prototypeName);
            }

            var entity = AllocEntity(uid);

            entity.Prototype = prototype;

            return entity;
        }

        /// <summary>
        ///     Allocates an entity and stores it but does not load components or do initialization.
        /// </summary>
        private protected Entity AllocEntity(EntityUid? uid = null)
        {
            if (uid == null)
            {
                uid = GenerateEntityUid();
            }

            if (EntityExists(uid.Value))
            {
                throw new InvalidOperationException($"UID already taken: {uid}");
            }

            var entity = new Entity();

            entity.SetManagers(this);
            entity.SetUid(uid.Value);

            // allocate the required MetaDataComponent
            _componentManager.AddComponent<MetaDataComponent>(entity);

            // allocate the required TransformComponent
            _componentManager.AddComponent<TransformComponent>(entity);

            Entities[entity.Uid] = entity;
            AllEntities.Add(entity);

            return entity;
        }

        /// <summary>
        ///     Allocates an entity and loads components but does not do initialization.
        /// </summary>
        private protected Entity CreateEntity(string prototypeName, EntityUid? uid = null)
        {
            if (prototypeName == null)
                return AllocEntity(uid);

            var entity = AllocEntity(prototypeName, uid);
            try
            {
                EntityPrototype.LoadEntity(entity.Prototype, entity, ComponentFactory, null);
                return entity;
            }
            catch (Exception e)
            {
                // Exception during entity loading.
                // Need to delete the entity to avoid corrupt state causing crashes later.
                DeleteEntity(entity);
                throw new EntityCreationException("Exception inside CreateEntity", e);
            }
        }

        private protected void LoadEntity(Entity entity, IEntityLoadContext context)
        {
            EntityPrototype.LoadEntity(entity.Prototype, entity, ComponentFactory, context);
        }

        private protected void InitializeAndStartEntity(Entity entity)
        {
            try
            {
                InitializeEntity(entity);
                StartEntity(entity);
            }
            catch (Exception e)
            {
                DeleteEntity(entity);
                throw new EntityCreationException("Exception inside InitializeAndStartEntity", e);
            }
        }

        private protected static void InitializeEntity(Entity entity)
        {
            entity.InitializeComponents();
        }

        private protected static void StartEntity(Entity entity)
        {
            entity.StartAllComponents();
        }

        private void CullDeletedEntities()
        {
            // Culling happens in updates.
            // It doesn't matter because to-be culled entities can't be accessed.
            // This should prevent most cases of "somebody is iterating while we're removing things"
            for (var i = 0; i < AllEntities.Count; i++)
            {
                var entity = AllEntities[i];
                if (!entity.Deleted)
                {
                    continue;
                }

                AllEntities.RemoveSwap(i);
                Entities.Remove(entity.Uid);
                RemoveFromEntityTrees(entity);

                // Process the one we just swapped next.
                i--;
            }
        }

        #endregion Entity Management

        private void DispatchComponentMessage(NetworkComponentMessage netMsg)
        {
            var compMsg = netMsg.Message;
            var compChannel = netMsg.Channel;
            var session = netMsg.Session;
            compMsg.Remote = true;

            var uid = netMsg.EntityUid;
            if (compMsg.Directed)
            {
                if (_componentManager.TryGetComponent(uid, netMsg.NetId, out var component))
                    component.HandleNetworkMessage(compMsg, compChannel, session);
            }
            else
            {
                foreach (var component in _componentManager.GetComponents(uid))
                {
                    component.HandleNetworkMessage(compMsg, compChannel, session);
                }
            }
        }

        protected abstract EntityUid GenerateEntityUid();

        #region Spatial Queries

        /// <inheritdoc />
        public bool AnyEntitiesIntersecting(MapId mapId, Box2 box, bool approximate = false) =>
            _entityTreesPerMap[mapId].Query(box, approximate).Any(ent => !ent.Deleted);

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesIntersecting(MapId mapId, Box2 position, bool approximate = false)
        {
            if (mapId == MapId.Nullspace)
            {
                yield break;
            }

            var newResults = _entityTreesPerMap[mapId].Query(position, approximate); // .ToArray();

            foreach (var entity in newResults)
            {
                if (entity.Deleted)
                {
                    continue;
                }

                yield return entity;
            }
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesIntersecting(MapId mapId, Vector2 position, bool approximate = false)
        {
            const float range = .00001f / 2;
            var aabb = new Box2(position, position).Enlarged(range);

            if (mapId == MapId.Nullspace)
            {
                yield break;
            }

            var newResults = _entityTreesPerMap[mapId].Query(aabb, approximate);


            foreach (var entity in newResults)
            {
                if (entity.TryGetComponent(out ICollidableComponent component))
                {
                    if (component.WorldAABB.Contains(position))
                        yield return entity;
                }
                else
                {
                    var transform = entity.Transform;
                    var entPos = transform.WorldPosition;
                    if (FloatMath.CloseTo(entPos.X, position.X)
                        && FloatMath.CloseTo(entPos.Y, position.Y))
                    {
                        yield return entity;
                    }
                }
            }
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesIntersecting(MapCoordinates position, bool approximate = false)
        {
            return GetEntitiesIntersecting(position.MapId, position.Position, approximate);
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesIntersecting(GridCoordinates position, bool approximate = false)
        {
            var mapPos = position.ToMap(_mapManager);
            return GetEntitiesIntersecting(mapPos.MapId, mapPos.Position, approximate);
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesIntersecting(IEntity entity, bool approximate = false)
        {
            if (entity.TryGetComponent<ICollidableComponent>(out var component))
            {
                return GetEntitiesIntersecting(entity.Transform.MapID, component.WorldAABB, approximate);
            }

            return GetEntitiesIntersecting(entity.Transform.GridPosition, approximate);
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesInRange(GridCoordinates position, float range, bool approximate = false)
        {
            var aabb = new Box2(position.Position - new Vector2(range / 2, range / 2),
                position.Position + new Vector2(range / 2, range / 2));
            return GetEntitiesIntersecting(_mapManager.GetGrid(position.GridID).ParentMapId, aabb, approximate);
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesInRange(MapId mapId, Box2 box, float range, bool approximate = false)
        {
            var aabb = box.Enlarged(range);
            return GetEntitiesIntersecting(mapId, aabb, approximate);
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesInRange(MapId mapId, Vector2 point, float range, bool approximate = false)
        {
            var aabb = new Box2(point, point).Enlarged(range);
            return GetEntitiesIntersecting(mapId, aabb, approximate);
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesInRange(IEntity entity, float range, bool approximate = false)
        {
            if (entity.TryGetComponent<ICollidableComponent>(out var component))
            {
                return GetEntitiesInRange(entity.Transform.MapID, component.WorldAABB, range, approximate);
            }

            return GetEntitiesInRange(entity.Transform.GridPosition, range, approximate);
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesInArc(GridCoordinates coordinates, float range, Angle direction,
            float arcWidth, bool approximate = false)
        {
            var position = coordinates.ToMap(_mapManager).Position;

            foreach (var entity in GetEntitiesInRange(coordinates, range * 2, approximate))
            {
                var angle = new Angle(entity.Transform.WorldPosition - position);
                if (angle.Degrees < direction.Degrees + arcWidth / 2 &&
                    angle.Degrees > direction.Degrees - arcWidth / 2)
                    yield return entity;
            }
        }

        #endregion


        #region Entity DynamicTree

        private readonly ConcurrentDictionary<MapId, DynamicTree<IEntity>> _entityTreesPerMap =
            new ConcurrentDictionary<MapId, DynamicTree<IEntity>>();

        public virtual bool UpdateEntityTree(IEntity entity)
        {
            if (entity.Deleted)
            {
                RemoveFromEntityTrees(entity);
                return true;
            }

            if (!entity.Initialized || !Entities.ContainsKey(entity.Uid))
            {
                return false;
            }

            var transform = entity.TryGetComponent(out ITransformComponent tx) ? tx : null;

            if (transform == null || !transform.Initialized)
            {
                RemoveFromEntityTrees(entity);
                return true;
            }

            var mapId = transform.MapID;

            var entTree = _entityTreesPerMap.GetOrAdd(mapId, EntityTreeFactory);

            // for debugging
            var necessary = 0;

            if (entTree.AddOrUpdate(entity))
            {
                ++necessary;
            }

            foreach (var childTx in entity.Transform.Children)
            {
                if (UpdateEntityTree(childTx.Owner))
                {
                    ++necessary;
                }
            }

            return necessary > 0;
        }

        public bool RemoveFromEntityTree(IEntity entity, MapId mapId)
        {
            if (_entityTreesPerMap.TryGetValue(mapId, out var tree))
            {
                return tree.Remove(entity);
            }

            return false;
        }

        private void RemoveFromEntityTrees(IEntity entity)
        {
            foreach (var mapId in _mapManager.GetAllMapIds())
            {
                if (_entityTreesPerMap.TryGetValue(mapId, out var entTree))
                {
                    entTree.Remove(entity);
                }
            }
        }

        private static DynamicTree<IEntity> EntityTreeFactory(MapId _) =>
            new DynamicTree<IEntity>(
                GetWorldAabbFromEntity,
                capacity: 16,
                growthFunc: x => x == 16 ? 3840 : x + 256
            );

        protected static Box2 GetWorldAabbFromEntity(in IEntity ent)
        {
            if (ent.Deleted)
                return new Box2(0, 0, 0, 0);

            if (ent.TryGetComponent(out ICollidableComponent collider))
                return collider.WorldAABB;

            var pos = ent.Transform.WorldPosition;
            return new Box2(pos, pos);
        }

        #endregion

        public virtual void Update()
        {
    }

    }

    public enum EntityMessageType
    {
        Error = 0,
        ComponentMessage,
        SystemMessage
    }
}
