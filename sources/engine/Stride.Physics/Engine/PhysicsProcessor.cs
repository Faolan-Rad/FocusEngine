// Copyright (c) Stride contributors (https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Physics.Engine;
using Stride.Rendering;
using System;

namespace Stride.Physics
{
    public class PhysicsProcessor : EntityProcessor<PhysicsComponent, PhysicsProcessor.AssociatedData>
    {
        public class AssociatedData
        {
            public PhysicsComponent PhysicsComponent;
            public TransformComponent TransformComponent;
            public ModelComponent ModelComponent; //not mandatory, could be null e.g. invisible triggers
            public bool BoneMatricesUpdated;
        }

        private readonly HashSet<PhysicsComponent> elements = new HashSet<PhysicsComponent>();
        private readonly HashSet<PhysicsSkinnedComponentBase> boneElements = new HashSet<PhysicsSkinnedComponentBase>();
        private readonly HashSet<CharacterComponent> characters = new HashSet<CharacterComponent>();

        private PhysicsSystem physicsSystem;
        private SceneSystem sceneSystem;
        private Scene debugScene;

        private bool colliderShapesRendering;

        private PhysicsShapesRenderingService debugShapeRendering;

        public PhysicsProcessor()
            : base(typeof(TransformComponent))
        {
            Order = 0xFFFF;
        }

        public Simulation Simulation { get; private set; }

        internal void RenderColliderShapes(bool enabled)
        {
            debugShapeRendering.Enabled = enabled;

            colliderShapesRendering = enabled;

            if (!colliderShapesRendering)
            {
                if (debugScene != null)
                {
                    debugScene.Dispose();

                    foreach (var element in elements)
                    {
                        element.RemoveDebugEntity(debugScene);
                    }

                    sceneSystem.SceneInstance.RootScene.Children.Remove(debugScene);
                }
            }
            else
            {
                debugScene = new Scene();

                foreach (var element in elements)
                {
                    if (element.Enabled)
                    {
                        element.AddDebugEntity(debugScene, Simulation.ColliderShapesRenderGroup);
                    }
                }

                sceneSystem.SceneInstance.RootScene.Children.Add(debugScene);
            }
        }

        protected override AssociatedData GenerateComponentData(Entity entity, PhysicsComponent component)
        {
            var data = new AssociatedData
            {
                PhysicsComponent = component,
                TransformComponent = entity.Transform,
                ModelComponent = entity.Get<ModelComponent>(),
            };

            data.PhysicsComponent.Simulation = Simulation;
            data.PhysicsComponent.DebugShapeRendering = debugShapeRendering;

            return data;
        }

        protected override bool IsAssociatedDataValid(Entity entity, PhysicsComponent physicsComponent, AssociatedData associatedData)
        {
            return
                physicsComponent == associatedData.PhysicsComponent &&
                entity.Transform == associatedData.TransformComponent &&
                entity.Get<ModelComponent>() == associatedData.ModelComponent;
        }

        protected override void OnEntityComponentAdding(Entity entity, PhysicsComponent component, AssociatedData data)
        {
            // wait, are we already added?
            if (elements.Contains(component))
            {
                // make sure we are not removing it
                lock (currentFrameRemovals)
                {
                    currentFrameRemovals.Remove(component);
                }
                return;
            }

            component.Attach(data);

            var character = component as CharacterComponent;
            if (character != null)
            {
                lock (characters)
                {
                    characters.Add(character);
                }
            }

            if (colliderShapesRendering)
            {
                component.AddDebugEntity(debugScene, Simulation.ColliderShapesRenderGroup);
            }

            elements.Add(component);

            if (component.BoneIndex != -1)
            {
                lock (boneElements)
                {
                    boneElements.Add((PhysicsSkinnedComponentBase)component);
                }
            }
        }

        private void ComponentRemoval(PhysicsComponent component)
        {
            Simulation.CleanContacts(component);

            if (component.BoneIndex != -1)
            {
                lock (boneElements)
                {
                    boneElements.Remove((PhysicsSkinnedComponentBase)component);
                }
            }

            elements.Remove(component);

            if (colliderShapesRendering)
            {
                component.RemoveDebugEntity(debugScene);
            }

            var character = component as CharacterComponent;
            if (character != null)
            {
                lock (characters)
                {
                    characters.Remove(character);
                }
            }

            component.Detach();
        }

        private readonly HashSet<PhysicsComponent> currentFrameRemovals = new HashSet<PhysicsComponent>();

        protected override void OnEntityComponentRemoved(Entity entity, PhysicsComponent component, AssociatedData data)
        {
            lock (currentFrameRemovals)
            {
                currentFrameRemovals.Add(component);
            }
        }

        protected internal override void OnSystemAdd()
        {
            physicsSystem = (PhysicsSystem)Services.GetService<IPhysicsSystem>();
            if (physicsSystem == null)
            {
                physicsSystem = new PhysicsSystem(Services);
                Services.AddService<IPhysicsSystem>(physicsSystem);
                var gameSystems = Services.GetSafeServiceAs<IGameSystemCollection>();
                gameSystems.Add(physicsSystem);
            }

            ((IReferencable)physicsSystem).AddReference();

            // Check if PhysicsShapesRenderingService is created (and check if rendering is enabled with IGraphicsDeviceService)
            if (Services.GetService<Graphics.IGraphicsDeviceService>() != null && Services.GetService<PhysicsShapesRenderingService>() == null)
            {
                debugShapeRendering = new PhysicsShapesRenderingService(Services);
                var gameSystems = Services.GetSafeServiceAs<IGameSystemCollection>();
                gameSystems.Add(debugShapeRendering);
            }

            Simulation = OverlapTest.mySimulation = physicsSystem.Create(this) as Simulation;

            sceneSystem = Services.GetSafeServiceAs<SceneSystem>();
        }

        protected internal override void OnSystemRemove()
        {
            physicsSystem.Release(this);
            ((IReferencable)physicsSystem).Release();
        }

        internal void UpdateCharacters()
        {
            var charactersProfilingState = Profiler.Begin(PhysicsProfilingKeys.CharactersProfilingKey);
            var activeCharacters = 0;
            //characters need manual updating
            lock (characters)
            {
                foreach (var element in characters)
                {
                    if (!element.Enabled || element.ColliderShape == null) continue;

                    var worldTransform = Matrix.RotationQuaternion(element.Orientation) * element.PhysicsWorldTransform;
                    element.UpdateTransformationComponent(ref worldTransform);

                    if (element.DebugEntity != null)
                    {
                        Vector3 scale, pos;
                        Quaternion rot;
                        worldTransform.Decompose(out scale, out rot, out pos);
                        element.DebugEntity.Transform.Position = pos;
                        element.DebugEntity.Transform.Rotation = rot;
                    }

                    charactersProfilingState.Mark();
                    activeCharacters++;
                }
            }
            charactersProfilingState.End("Active characters: {0}", activeCharacters);
        }

        public override void Draw(RenderContext context)
        {
            if (Simulation.DisableSimulation) return;

            foreach (var element in boneElements)
            {
                element.UpdateDraw();
            }
        }

        internal void UpdateBones()
        {
            lock (boneElements)
            {
                foreach (var element in boneElements)
                {
                    element.UpdateBones();
                }
            }
        }

        public void UpdateContacts()
        {
            for (int i=0; i<ComponentDataValues.Count; i++)
            {
                try
                {
                    var data = ComponentDataValues[i];
                    if (data != null)
                    {
                        var shouldProcess = data.PhysicsComponent.ProcessCollisionsSlim || data.PhysicsComponent.ProcessCollisions || ((data.PhysicsComponent as PhysicsTriggerComponentBase)?.IsTrigger ?? false);
                        if (data.PhysicsComponent.Enabled && shouldProcess && data.PhysicsComponent.ColliderShape != null)
                        {
                            Simulation.ContactTest(data.PhysicsComponent);
                        }
                    }
                }
                catch (Exception e)
                {
                    // simple, rare threading blip, just ignore it for this frame and continue
                }
            }
        }

        public void UpdateRemovals()
        {
            lock (currentFrameRemovals)
            {
                foreach (var currentFrameRemoval in currentFrameRemovals)
                {
                    ComponentRemoval(currentFrameRemoval);
                }

                currentFrameRemovals.Clear();
            }

            SceneSystem.physicsDoNotDisposeNextRemoval = false;
        }
    }
}
