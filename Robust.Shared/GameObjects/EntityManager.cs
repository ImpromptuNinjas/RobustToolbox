﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Robust.Shared.GameObjects.Components;
using Robust.Shared.GameObjects.Components.Transform;
using Robust.Shared.Interfaces.GameObjects;
using Robust.Shared.Interfaces.Map;
using Robust.Shared.Interfaces.Timing;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network.Messages;
using Robust.Shared.Physics;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Robust.Shared.GameObjects
{
    /// <inheritdoc />
    public abstract class EntityManager : IEntityManager
    {
        #region Dependencies

#pragma warning disable 649
        [Dependency] private readonly IEntityNetworkManager EntityNetworkManager;
        [Dependency] private readonly IPrototypeManager PrototypeManager;
        [Dependency] protected readonly IEntitySystemManager EntitySystemManager;
        [Dependency] private readonly IComponentFactory ComponentFactory;
        [Dependency] private readonly IComponentManager _componentManager;
        [Dependency] private readonly IGameTiming _gameTiming;
        [Dependency] private readonly IMapManager _mapManager;
#pragma warning restore 649

        #endregion Dependencies

        /// <inheritdoc />
        public GameTick CurrentTick => _gameTiming.CurTick;

        /// <inheritdoc />
        public IComponentManager ComponentManager => _componentManager;

        /// <inheritdoc />
        public IEntityNetworkManager EntityNetManager => EntityNetworkManager;

        /// <summary>
        /// All entities currently stored in the manager, also used for iteration.
        /// </summary>
        protected readonly ConcurrentDictionary<EntityUid, IEntity> Entities =
            new ConcurrentDictionary<EntityUid, IEntity>();

        /// <summary>
        /// All entities organized by map and location.
        /// </summary>
        protected readonly ConcurrentDictionary<MapId, DynamicTree<IEntity>> MapEntityTrees =
            new ConcurrentDictionary<MapId, DynamicTree<IEntity>>();

        private DynamicTree<IEntity> EntityTreeFactory()
        {
            return new DynamicTree<IEntity>(
                (in IEntity ent)
                    => ent.TryGetComponent<ICollidableComponent>(out var collider)
                        // entities with colliders use their world AABB
                        ? collider.WorldAABB
                        // entities w/o colliders are points
                        : new Box2(ent.Transform.WorldPosition, ent.Transform.WorldPosition),
                // this should be the average number or predicted number of entities on a map
                capacity: 16,
                // this can be tuned to fit the allocation profile
                growthFunc: x => x == 16 ? 3840 : x + 256
            );
        }

        protected readonly Queue<IncomingEntityMessage> NetworkMessageBuffer = new Queue<IncomingEntityMessage>();

        private readonly IEntityEventBus _eventBus = new EntityEventBus();

        /// <inheritdoc />
        public IEventBus EventBus => _eventBus;

        public bool Started { get; protected set; }

        public virtual void Initialize()
        {
            EntityNetworkManager.SetupNetworking();
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
            ProcessMessageBuffer();
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
        public abstract IEntity CreateEntityUninitialized(string prototypeName);

        /// <inheritdoc />
        public abstract IEntity CreateEntityUninitialized(string prototypeName, GridCoordinates coordinates);

        /// <inheritdoc />
        public abstract IEntity CreateEntityUninitialized(string prototypeName, MapCoordinates coordinates);

        /// <inheritdoc />
        public abstract IEntity SpawnEntity(string protoName, GridCoordinates coordinates);

        /// <inheritdoc />
        public abstract IEntity SpawnEntity(string protoName, MapCoordinates coordinates);

        /// <inheritdoc />
        public abstract IEntity SpawnEntityNoMapInit(string protoName, GridCoordinates coordinates);

        /// <summary>
        /// Returns an entity by id
        /// </summary>
        /// <param name="uid"></param>
        /// <returns>Entity or null if entity id doesn't exist</returns>
        public IEntity GetEntity(EntityUid uid)
        {
            return Entities[uid];
        }

        /// <summary>
        /// Attempt to get an entity, returning whether or not an entity was gotten.
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="entity">The requested entity or null if the entity couldn't be found.</param>
        /// <returns>True if a value was returned, false otherwise.</returns>
        public bool TryGetEntity(EntityUid uid, out IEntity entity)
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

        public IEnumerable<IEntity> GetEntitiesAt(Vector2 position)
        {
            foreach (var entity in GetEntities())
            {
                var transform = entity.Transform;
                if (FloatMath.CloseTo(transform.GridPosition.X, position.X) && FloatMath.CloseTo(transform.GridPosition.Y, position.Y))
                {
                    yield return entity;
                }
            }
        }

        public IEnumerable<IEntity> GetEntities()
        {
            // will not abort if collection modified
            foreach ( var entity in Entities.Values )
            {
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
            foreach (IEntity e in GetEntities())
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
            var entity = AllocEntity(uid);

            if (String.IsNullOrWhiteSpace(prototypeName))
                return entity;

            var prototype = PrototypeManager.Index<EntityPrototype>(prototypeName);
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
            var tc = _componentManager.AddComponent<TransformComponent>(entity);

            Entities[entity.Uid] = entity;

            return entity;
        }

        public DynamicTree<IEntity> GetEntityTreeForMap(MapId mapId)
            => MapEntityTrees.GetOrAdd(mapId, _ => EntityTreeFactory());

        public void RemoveFromEntityTree(IEntity entity)
        {
            foreach (var mapId in _mapManager.GetAllMapIds())
            {
                GetEntityTreeForMap(mapId).Remove(entity);
            }
        }

        /// <summary>
        ///     Allocates an entity and loads components but does not do initialization.
        /// </summary>
        private protected Entity CreateEntity(string prototypeName, EntityUid? uid = null)
        {
            if (prototypeName == null)
                return AllocEntity(uid);

            var entity = AllocEntity(prototypeName, uid);
            EntityPrototype.LoadEntity(entity.Prototype, entity, ComponentFactory, null);
            return entity;
        }

        private protected void LoadEntity(Entity entity, IEntityLoadContext context)
        {
            EntityPrototype.LoadEntity(entity.Prototype, entity, ComponentFactory, context);
        }

        private protected static void InitializeAndStartEntity(Entity entity)
        {
            InitializeEntity(entity);
            StartEntity(entity);
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

            foreach ( var entity in Entities.Values)
            {
                if (!entity.Deleted)
                {
                    continue;
                }

                Entities.Remove(entity.Uid, out _);

                RemoveFromEntityTree(entity);
            }
        }

        #endregion Entity Management

        #region message processing

        /// <inheritdoc />
        public void HandleEntityNetworkMessage(MsgEntity msg)
        {
            var incomingEntity = new IncomingEntityMessage(msg);

            if (!Started)
            {
                if (incomingEntity.Message.Type != EntityMessageType.Error) NetworkMessageBuffer.Enqueue(incomingEntity);
                return;
            }

            if (!Entities.TryGetValue(incomingEntity.Message.EntityUid, out var entity))
                NetworkMessageBuffer.Enqueue(incomingEntity);
            else
                ProcessEntityMessage(incomingEntity.Message);
        }

        private void ProcessMessageBuffer()
        {
            if (!Started) return;

            if (NetworkMessageBuffer.Count == 0) return;

            var misses = new List<IncomingEntityMessage>();

            while (NetworkMessageBuffer.Count != 0)
            {
                var incomingEntity = NetworkMessageBuffer.Dequeue();
                if (!Entities.TryGetValue(incomingEntity.Message.EntityUid, out var entity))
                {
                    incomingEntity.LastProcessingAttempt = DateTime.Now;
                    if ((incomingEntity.LastProcessingAttempt - incomingEntity.ReceivedTime).TotalSeconds > incomingEntity.Expires)
                        misses.Add(incomingEntity);
                }
                else
                {
                    ProcessEntityMessage(incomingEntity.Message);
                }
            }

            foreach (var miss in misses)
            {
                NetworkMessageBuffer.Enqueue(miss);
            }
        }

        private void ProcessEntityMessage(MsgEntity msgEntity)
        {
            switch (msgEntity.Type)
            {
                case EntityMessageType.ComponentMessage:
                    DispatchComponentMessage(msgEntity);
                    break;
            }
        }

        private void DispatchComponentMessage(MsgEntity msgEntity)
        {
            var compMsg = msgEntity.ComponentMessage;
            var compChannel = msgEntity.MsgChannel;
            compMsg.Remote = true;

            var uid = msgEntity.EntityUid;
            if (compMsg.Directed)
            {
                if (_componentManager.TryGetComponent(uid, msgEntity.NetId, out var component))
                    component.HandleMessage(compMsg, compChannel);
            }
            else
            {
                foreach (var component in _componentManager.GetComponents(uid))
                {
                    component.HandleMessage(compMsg, compChannel);
                }
            }
        }

        #endregion message processing

        protected abstract EntityUid GenerateEntityUid();

        #region Spatial Queries

        /// <inheritdoc />
        public bool AnyEntitiesIntersecting(MapId mapId, Box2 box)
            => GetEntityTreeForMap(mapId).Query((ref IEntity ent) => ent.Deleted, box);

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesIntersecting(MapId mapId, Box2 box)
        {
            foreach (var ent in GetEntityTreeForMap(mapId).Query(box))
            {
                if (!ent.Deleted)
                {
                    yield return ent;
                }
            }
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesIntersecting(MapId mapId, Vector2 position)
        {
            foreach (var ent in GetEntityTreeForMap(mapId).Query(position)) {
                if (!ent.Deleted)
                    yield return ent;
            }
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesIntersecting(MapCoordinates position)
        {
            return GetEntitiesIntersecting(position.MapId, position.Position);
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesIntersecting(GridCoordinates position)
        {
            var mapPos = position.ToMap(_mapManager);
            return GetEntitiesIntersecting(mapPos.MapId, mapPos.Position);
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesIntersecting(IEntity entity)
        {
            if (entity.TryGetComponent<ICollidableComponent>(out var component))
            {
                return GetEntitiesIntersecting(entity.Transform.MapID, component.WorldAABB);
            }

            return GetEntitiesIntersecting(entity.Transform.GridPosition);
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesInRange(GridCoordinates position, float range)
        {
            var aabb = new Box2(position.Position - new Vector2(range / 2, range / 2), position.Position + new Vector2(range / 2, range / 2));
            return GetEntitiesIntersecting(_mapManager.GetGrid(position.GridID).ParentMapId, aabb);
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesInRange(MapId mapId, Box2 box, float range)
        {
            var aabb = Box2.Grow(box, range);
            return GetEntitiesIntersecting(mapId, aabb);
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesInRange(IEntity entity, float range)
        {
            if (entity.TryGetComponent<ICollidableComponent>(out var component))
            {
                return GetEntitiesInRange(entity.Transform.MapID, component.WorldAABB, range);
            }
            else
            {
                GridCoordinates coords = entity.Transform.GridPosition;
                return GetEntitiesInRange(coords, range);
            }
        }

        /// <inheritdoc />
        public IEnumerable<IEntity> GetEntitiesInArc(GridCoordinates coordinates, float range, Angle direction, float arcWidth)
        {
            var entities = GetEntitiesInRange(coordinates, range*2);
            var position = coordinates.ToMap(_mapManager).Position;

            foreach (var entity in entities)
            {
                var angle = new Angle(entity.Transform.WorldPosition - position);
                if (angle.Degrees < direction.Degrees + arcWidth / 2 && angle.Degrees > direction.Degrees - arcWidth / 2)
                    yield return entity;
            }
        }

        #endregion
    }

    public enum EntityMessageType
    {
        Error = 0,
        ComponentMessage,
        EntityMessage,
        SystemMessage
    }
}
