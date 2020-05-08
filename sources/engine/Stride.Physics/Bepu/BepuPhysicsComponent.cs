// Copyright (c) Stride contributors (https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Collections;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Core.MicroThreading;
using Stride.Engine.Design;
using Stride.Extensions;
using Stride.Games;
using Stride.Graphics.GeometricPrimitives;
using Stride.Physics;
using Stride.Physics.Bepu;
using Stride.Physics.Engine;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;

namespace Stride.Engine
{
    [DataContract("BepuPhysicsComponent", Inherited = true)]
    [Display("BepuPhysics", Expand = ExpandRule.Once)]
    [AllowMultipleComponents]
    [ComponentOrder(3100)]
    public abstract class BepuPhysicsComponent : ActivableEntityComponent
    {
        protected static Logger logger = GlobalLogger.GetLogger("BepuPhysicsComponent");

        internal int AddedHandle = -1;

        public int GetHandle()
        {
            return AddedHandle;
        }

        /// <summary>
        /// Allow the physics system to automatically add, based on changes to the entity in the scene?
        /// </summary>
        [DataMember]
        public bool AutomaticAdd { get; set; } = true;

        [DataMemberIgnore]
        public virtual bool AddedToScene { get; set; }

        public BepuPhysicsComponent()
        {
            BepuHelpers.AssureBepuSystemCreated();
        }

        /// <summary>
        /// Gets or sets the collision group.
        /// </summary>
        /// <value>
        /// The collision group.
        /// </value>
        /// <userdoc>
        /// Which collision group the component belongs to. This can't be changed at runtime. The default is DefaultFilter. 
        /// </userdoc>
        /// <remarks>
        /// The collider will still produce events, to allow non trigger rigidbodies or static colliders to act as a trigger if required for certain filtering groups.
        /// </remarks>
        [DataMember(30)]
        [Display("Collision group")]
        [DefaultValue(CollisionFilterGroups.DefaultFilter)]
        public CollisionFilterGroups CollisionGroup { get; set; } = CollisionFilterGroups.DefaultFilter;

        /// <summary>
        /// Gets or sets the can collide with.
        /// </summary>
        /// <value>
        /// The can collide with.
        /// </value>
        /// <userdoc>
        /// Which collider groups this component collides with. With nothing selected, it collides with all groups. This can't be changed at runtime.
        /// </userdoc>
        /// /// <remarks>
        /// The collider will still produce events, to allow non trigger rigidbodies or static colliders to act as a trigger if required for certain filtering groups.
        /// </remarks>
        [DataMember(40)]
        [Display("Collides with...")]
        [DefaultValue(CollisionFilterGroupFlags.AllFilter)]
        public CollisionFilterGroupFlags CanCollideWith { get; set; } = CollisionFilterGroupFlags.AllFilter;

        /// <summary>
        /// Gets or sets the tag.
        /// </summary>
        /// <value>
        /// The tag.
        /// </value>
        [DataMember]
        public string Tag { get; set; }

        [DataMember]
        public float FrictionCoefficient = 0.5f;

        [DataMember]
        public float MaximumRecoveryVelocity = 2f;

        [DataMember]
        public SpringSettings SpringSettings = new SpringSettings(30f, 20f);

        [DataMemberIgnore]
        virtual public IShape ColliderShape { get; set; }

        [DataMemberIgnore]
        virtual public TypedIndex ShapeIndex { get; }

        /// <summary>
        /// Computes the physics transformation from the TransformComponent values
        /// </summary>
        /// <returns></returns>
        internal void DerivePhysicsTransform(ref Matrix fromMatrix, out Matrix outMatrix)
        {
            fromMatrix.Decompose(out Vector3 scale, out Matrix rotation, out Vector3 translation);
            DerivePhysicsTransform(translation, rotation, scale, out outMatrix);
        }

        internal void DerivePhysicsTransform(Vector3? worldPosition, Matrix? worldRotation, Vector3? worldScale, out Matrix outMatrix)
        {
            Vector3 translation = worldPosition ?? Entity.Transform.WorldPosition(), scale;
            Matrix rotation;

            if( worldScale.HasValue ) {
                scale = worldScale.Value;
            } else {
                Entity.Transform.WorldMatrix.GetScale(out scale);
            }

            if (worldRotation.HasValue) {
                rotation = worldRotation.Value;
            } else {
                Entity.Transform.WorldMatrix.GetRotationMatrix(out rotation);
            }

            var translationMatrix = Matrix.Translation(translation);
            Matrix.Multiply(ref rotation, ref translationMatrix, out outMatrix);
        }

        [DataMemberIgnore]
        public virtual Vector3 Position { get; set; }

        [DataMemberIgnore]
        public virtual Quaternion Rotation { get; set; }

        /// <summary>
        /// Is this a "ghost"? Useful for triggers that detect collisions, but don't cause them
        /// </summary>
        [DataMember]
        public bool GhostBody { get; set; }

        /// <summary>
        /// How much ahead-of-time to look for contacts? Defaults to 0.1
        /// </summary>
        [DataMember]
        virtual public float SpeculativeMargin { get; set; } = 0.1f;

        private static Material debugShapeMaterial;
        private static Stride.Rendering.Mesh cubeMesh;

        public Entity AttachDebugShapeAsChild()
        {
            System.Numerics.Vector3 min, max;
            if (ColliderShape is IConvexShape ics)
            {
                ics.ComputeBounds(BepuHelpers.ToBepu(Quaternion.Identity), out min, out max);
            }
            else if (ColliderShape is BepuPhysics.Collidables.Mesh cm)
            {
                cm.ComputeBounds(BepuHelpers.ToBepu(Quaternion.Identity), out min, out max);
            }
            else return null;

            Vector3 centerOffset = BepuHelpers.ToStride(max + min) * 0.5f;

            Game g = ServiceRegistry.instance.GetService<IGame>() as Game;

            if (debugShapeMaterial == null)
            {
                var materialDescription = new MaterialDescriptor
                {
                    Attributes =
                    {
                        DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                        Diffuse = new MaterialDiffuseMapFeature(new ComputeColor { Key = MaterialKeys.DiffuseValue })
                    }
                };

                debugShapeMaterial = Material.New(g.GraphicsDevice, materialDescription);
                debugShapeMaterial.Passes[0].Parameters.Set(MaterialKeys.DiffuseValue, Color.Red);

                var meshDraw = GeometricPrimitive.Cube.New(g.GraphicsDevice, Vector3.One).ToMeshDraw();

                cubeMesh = new Rendering.Mesh { Draw = meshDraw };
            }

            Entity e = new Entity(Entity.Name + "-physicsBB");

            Model m = new Model();
            m.Add(debugShapeMaterial);
            m.Meshes.Add(cubeMesh);

            ModelComponent mc = e.GetOrCreate<ModelComponent>();
            mc.Model = m;

            e.Transform.Scale = new Vector3(max.X - min.X, max.Y - min.Y, max.Z - min.Z) / Entity.Transform.WorldScale();
            e.Transform.Position = centerOffset / Entity.Transform.WorldScale();
            if (this is BepuRigidbodyComponent rb && rb.IgnorePhysicsRotation) e.Transform.Rotation = Rotation;
            e.Transform.Parent = Entity.Transform;

            return e;
        }
    }
}
