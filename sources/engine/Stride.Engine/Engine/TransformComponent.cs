// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Xenko.Core;
using Xenko.Core.Collections;
using Xenko.Core.Mathematics;
using Xenko.Core.Serialization;
using Xenko.Engine.Design;
using Xenko.Engine.Processors;
using Xenko.VirtualReality;

namespace Xenko.Engine
{
    /// <summary>
    /// Defines Position, Rotation and Scale of its <see cref="Entity"/>.
    /// </summary>
    [DataContract("TransformComponent")]
    [DataSerializerGlobal(null, typeof(FastCollection<TransformComponent>))]
    [DefaultEntityComponentProcessor(typeof(TransformProcessor))]
    [Display("Transform", Expand = ExpandRule.Once)]
    [ComponentOrder(0)]
    public sealed class TransformComponent : EntityComponent //, IEnumerable<TransformComponent> Check why this is not working
    {
        private static readonly TransformOperation[] EmptyTransformOperations = new TransformOperation[0];

        // When false, transformation should be computed in TransformProcessor (no dependencies).
        // When true, transformation is computed later by another system.
        // This is useful for scenario such as binding a node to a bone, where it first need to run TransformProcessor for the hierarchy,
        // run MeshProcessor to update ModelViewHierarchy, copy Node/Bone transformation to another Entity with special root and then update its children transformations.
        private bool useTRS = true;
        private TransformComponent parent;

        private readonly TransformChildrenCollection children;

        internal bool IsMovingInsideRootScene;

        /// <summary>
        /// This is where we can register some custom work to be done after world matrix has been computed, such as updating model node hierarchy or physics for local node.
        /// </summary>
        [DataMemberIgnore]
        public FastListStruct<TransformOperation> PostOperations = new FastListStruct<TransformOperation>(EmptyTransformOperations);

        /// <summary>
        /// The world matrix.
        /// Its value is automatically recomputed at each frame from the local and the parent matrices.
        /// One can use <see cref="UpdateWorldMatrix"/> to force the update to happen before next frame.
        /// </summary>
        /// <remarks>The setter should not be used and is accessible only for performance purposes.</remarks>
        [DataMemberIgnore]
        public Matrix WorldMatrix = Matrix.Identity;

        /// <summary>
        /// The local matrix.
        /// Its value is automatically recomputed at each frame from the position, rotation and scale.
        /// One can use <see cref="UpdateLocalMatrix"/> to force the update to happen before next frame.
        /// </summary>
        /// <remarks>The setter should not be used and is accessible only for performance purposes.</remarks>
        [DataMemberIgnore]
        public Matrix LocalMatrix = Matrix.Identity;

        /// <summary>
        /// The translation relative to the parent transformation.
        /// </summary>
        /// <userdoc>The translation of the entity with regard to its parent</userdoc>
        [DataMember(10)]
        public Vector3 Position;

        /// <summary>
        /// The rotation relative to the parent transformation.
        /// </summary>
        /// <userdoc>The rotation of the entity with regard to its parent</userdoc>
        [DataMember(20)]
        public Quaternion Rotation;

        /// <summary>
        /// The scaling relative to the parent transformation.
        /// </summary>
        /// <userdoc>The scale of the entity with regard to its parent</userdoc>
        [DataMember(30)]
        public Vector3 Scale;

        /// <summary>
        /// Should this entity track with a VR hand?
        /// </summary>
        [DataMember(40)]
        public VirtualReality.TouchControllerHand TrackVRHand = TouchControllerHand.None;

        /// <summary>
        /// If in VR, do we want to override the normally tracked TransformComponent to point at UI elements?
        /// This is useful if we want to adjust our pointer with a child TransformComponent.
        /// </summary>
        static public TransformComponent OverrideLeftHandUIPointer, OverrideRightHandUIPointer;

        /// <summary>
        /// Last left VR hand tracked. Useful for quick access to left hand and internal UI picking
        /// </summary>
        static public TransformComponent LastLeftHandTracked { get; private set; }

        /// <summary>
        /// Last right VR hand tracked. Useful for quick access to right hand and internal UI picking
        /// </summary>
        static public TransformComponent LastRightHandTracked { get; private set; }

        [DataMemberIgnore]
        public TransformLink TransformLink;

        /// <summary>
        /// Initializes a new instance of the <see cref="TransformComponent" /> class.
        /// </summary>
        public TransformComponent()
        {
            children = new TransformChildrenCollection(this);

            UseTRS = true;
            Scale = Vector3.One;
            Rotation = Quaternion.Identity;
        }

        /// <summary>
        /// Gets or sets a value indicating whether to use the Translation/Rotation/Scale.
        /// </summary>
        /// <value><c>true</c> if [use TRS]; otherwise, <c>false</c>.</value>
        [DataMemberIgnore]
        [Display(Browsable = false)]
        [DefaultValue(true)]
        public bool UseTRS
        {
            get { return useTRS; }
            set { useTRS = value; }
        }

        /// <summary>
        /// Gets the children of this <see cref="TransformComponent"/>.
        /// </summary>
        public FastCollection<TransformComponent> Children => children;

        /// <summary>
        /// Gets or sets the euler rotation, with XYZ order.
        /// Not stable: setting value and getting it again might return different value as it is internally encoded as a <see cref="Quaternion"/> in <see cref="Rotation"/>.
        /// </summary>
        /// <value>
        /// The euler rotation.
        /// </value>
        [DataMemberIgnore]
        public Vector3 RotationEulerXYZ
        {
            // Unfortunately it is not possible to factorize the following code with Quaternion.RotationYawPitchRoll because Z axis direction is inversed
            get
            {
                var rotation = Rotation;
                Vector3 rotationEuler;

                // Equivalent to:
                //  Matrix rotationMatrix;
                //  Matrix.Rotation(ref cachedRotation, out rotationMatrix);
                //  rotationMatrix.DecomposeXYZ(out rotationEuler);

                float xx = rotation.X * rotation.X;
                float yy = rotation.Y * rotation.Y;
                float zz = rotation.Z * rotation.Z;
                float xy = rotation.X * rotation.Y;
                float zw = rotation.Z * rotation.W;
                float zx = rotation.Z * rotation.X;
                float yw = rotation.Y * rotation.W;
                float yz = rotation.Y * rotation.Z;
                float xw = rotation.X * rotation.W;

                rotationEuler.Y = (float)Math.Asin(2.0f * (yw - zx));
                double test = Math.Cos(rotationEuler.Y);
                if (test > 1e-6f)
                {
                    rotationEuler.Z = (float)Math.Atan2(2.0f * (xy + zw), 1.0f - (2.0f * (yy + zz)));
                    rotationEuler.X = (float)Math.Atan2(2.0f * (yz + xw), 1.0f - (2.0f * (yy + xx)));
                }
                else
                {
                    rotationEuler.Z = (float)Math.Atan2(2.0f * (zw - xy), 2.0f * (zx + yw));
                    rotationEuler.X = 0.0f;
                }
                return rotationEuler;
            }
            set
            {
                // Equilvalent to:
                //  Quaternion quatX, quatY, quatZ;
                //  
                //  Quaternion.RotationX(value.X, out quatX);
                //  Quaternion.RotationY(value.Y, out quatY);
                //  Quaternion.RotationZ(value.Z, out quatZ);
                //  
                //  rotation = quatX * quatY * quatZ;

                var halfAngles = value * 0.5f;

                var fSinX = (float)Math.Sin(halfAngles.X);
                var fCosX = (float)Math.Cos(halfAngles.X);
                var fSinY = (float)Math.Sin(halfAngles.Y);
                var fCosY = (float)Math.Cos(halfAngles.Y);
                var fSinZ = (float)Math.Sin(halfAngles.Z);
                var fCosZ = (float)Math.Cos(halfAngles.Z);

                var fCosXY = fCosX * fCosY;
                var fSinXY = fSinX * fSinY;

                Rotation.X = fSinX * fCosY * fCosZ - fSinZ * fSinY * fCosX;
                Rotation.Y = fSinY * fCosX * fCosZ + fSinZ * fSinX * fCosY;
                Rotation.Z = fSinZ * fCosXY - fSinXY * fCosZ;
                Rotation.W = fCosZ * fCosXY + fSinXY * fSinZ;
            }
        }

        /// <summary>
        /// Gets or sets the parent of this <see cref="TransformComponent"/>.
        /// </summary>
        /// <value>
        /// The parent.
        /// </value>
        [DataMemberIgnore]
        public TransformComponent Parent
        {
            get { return parent; }
            set
            {
                TransformComponent oldParent = Parent;
                if (oldParent == value)
                    return;

                Scene newParentScene = value?.Entity?.Scene;
                Scene entityScene = Entity?.Scene;

                // Get to root scene
                while (entityScene?.Parent != null)
                    entityScene = entityScene.Parent;
                while (newParentScene?.Parent != null)
                    newParentScene = newParentScene.Parent;

                // Check if root scene didn't change
                IsMovingInsideRootScene = (newParentScene != null && newParentScene == entityScene);

                // Add/Remove
                if (oldParent == null) {
                    entityScene?.Entities.Remove(Entity);
                } else oldParent.Children.Remove(this);
                if (value != null) {
                    // normal procedure of adding to another transform
                    value.Children.Add(this);
                } else if (entityScene != null && Entity.Scene != entityScene) {
                    // special case where we are just going to root scene
                    Entity.Scene = entityScene;
                }

                IsMovingInsideRootScene = false;
            }
        }

        private void Entities_CollectionChanged(object sender, TrackingCollectionChangedEventArgs e) {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Updates the local matrix.
        /// If <see cref="UseTRS"/> is true, <see cref="LocalMatrix"/> will be updated from <see cref="Position"/>, <see cref="Rotation"/> and <see cref="Scale"/>.
        /// </summary>
        public void UpdateLocalMatrix()
        {
            // do we need to update with a VR hand?
            if (TrackVRHand != VirtualReality.TouchControllerHand.None && VRDeviceSystem.VRActive)
            {
                TouchController vrController = VRDeviceSystem.GetSystem.GetController(TrackVRHand);

                if (vrController != null && vrController.State != DeviceState.Invalid)
                {
                    Position = vrController.Position;
                    Rotation = vrController.Rotation;

                    if (TrackVRHand == TouchControllerHand.Left)
                    {
                        LastLeftHandTracked = this;
                    }
                    else
                    {
                        LastRightHandTracked = this;
                    }
                }
            }

            if (UseTRS)
            {
                Matrix.Transformation(ref Scale, ref Rotation, ref Position, out LocalMatrix);
            }
        }

        /// <summary>
        /// Updates the local matrix based on the world matrix and the parent entity's or containing scene's world matrix.
        /// </summary>
        public void UpdateLocalFromWorld()
        {
            if (Parent == null)
            {
                var scene = Entity?.Scene;
                if (scene != null)
                {
                    Matrix.Invert(ref scene.WorldMatrix, out var inverseSceneTransform);
                    Matrix.Multiply(ref WorldMatrix, ref inverseSceneTransform, out LocalMatrix);
                }
                else
                {
                    LocalMatrix = WorldMatrix;
                }
            }
            else
            {
                //We are not root so we need to derive the local matrix as well
                Matrix.Invert(ref Parent.WorldMatrix, out var inverseParent);
                Matrix.Multiply(ref WorldMatrix, ref inverseParent, out LocalMatrix);
            }
        }

        /// <summary>
        /// Updates the world matrix.
        /// It will first call <see cref="UpdateLocalMatrix"/> on self, and <see cref="UpdateWorldMatrix"/> on <see cref="Parent"/> if not null.
        /// Then <see cref="WorldMatrix"/> will be updated by multiplying <see cref="LocalMatrix"/> and parent <see cref="WorldMatrix"/> (if any).
        /// </summary>
        public void UpdateWorldMatrix()
        {
            UpdateLocalMatrix();
            UpdateWorldMatrixInternal(true);
        }

        /// <summary>
        /// Gets the world position.
        /// Default call does not recalcuate the position. It just gets the last frame's position quickly.
        /// If you pass true to this function, it will update the world position (which is a costly procedure) to get the most up-to-date position.
        /// </summary>
        public Vector3 WorldPosition(bool recalculate = false)
        {
            if (recalculate) UpdateWorldMatrix();
            return parent == null ? Position : WorldMatrix.TranslationVector;
        }

        /// <summary>
        /// Gets the world scale.
        /// Default call does not recalcuate the scale. It just gets the last frame's scale quickly.
        /// If you pass true to this function, it will update the world position (which is a costly procedure) to get the most up-to-date scale.
        /// </summary>
        public Vector3 WorldScale(bool recalculate = false)
        {
            if (recalculate) UpdateWorldMatrix();
            if (parent == null) return Scale;
            WorldMatrix.GetScale(out Vector3 scale);
            return scale;
        }

        /// <summary>
        /// Gets the world rotation.
        /// Default call does not recalcuate the rotation. It just gets the last frame's rotation (relatively) quickly.
        /// If you pass true to this function, it will update the world position (which is a costly procedure) to get the most up-to-date rotation.
        /// </summary>
        public Quaternion WorldRotation(bool recalculate = false)
        {
            if (recalculate) UpdateWorldMatrix();
            if (parent != null && WorldMatrix.GetRotationQuaternion(out Quaternion q)) {
                return q;
            } else {
                return Rotation;
            }
        }

        /// <summary>
        /// Gets Forward vector for transform
        /// </summary>
        public Vector3 Forward(bool worldForward = false, bool recalculateWorld = false) {
            return RotationMatrix(worldForward, recalculateWorld).Forward;
        }

        /// <summary>
        /// Gets Left vector for transform
        /// </summary>
        public Vector3 Left(bool worldLeft = false, bool recalculateWorld = false) {
            return RotationMatrix(worldLeft, recalculateWorld).Left;
        }

        /// <summary>
        /// Gets Up vector for transform
        /// </summary>
        public Vector3 Up(bool worldUp = false, bool recalculateWorld = false) {
            return RotationMatrix(worldUp, recalculateWorld).Up;
        }

        /// <summary>
        /// Gets a rotation matrix for this transform.
        /// </summary>
        /// <param name="world">World rotation, or just local rotation?</param>
        /// <param name="recalculate">Recalculate world (which is slow), or use last frame info?</param>
        /// <returns>Rotation matrix</returns>
        public Matrix RotationMatrix(bool world = false, bool recalculate = false) {
            if (recalculate) UpdateWorldMatrix();
            return Matrix.RotationQuaternion(world ? WorldRotation() : Rotation);
        }

        internal void UpdateWorldMatrixInternal(bool recursive)
        {
            if (TransformLink != null)
            {
                Matrix linkMatrix;
                TransformLink.ComputeMatrix(recursive, out linkMatrix);
                Matrix.Multiply(ref LocalMatrix, ref linkMatrix, out WorldMatrix);
            }
            else if (Parent != null)
            {
                if (recursive)
                    Parent.UpdateWorldMatrix();
                Matrix.Multiply(ref LocalMatrix, ref Parent.WorldMatrix, out WorldMatrix);
            }
            else
            {
                var scene = Entity?.Scene;
                if (scene != null)
                {
                    if (recursive)
                    {
                        scene.UpdateWorldMatrix();
                    }

                    Matrix.Multiply(ref LocalMatrix, ref scene.WorldMatrix, out WorldMatrix);
                }
                else
                {
                    WorldMatrix = LocalMatrix;
                }
            }

            foreach (var transformOperation in PostOperations)
            {
                transformOperation.Process(this);
            }
        }

        [DataContract]
        public class TransformChildrenCollection : FastCollection<TransformComponent>
        {
            TransformComponent transform;
            Entity Entity => transform.Entity;

            public TransformChildrenCollection(TransformComponent transformParam)
            {
                transform = transformParam;
            }

            private void OnTransformAdded(TransformComponent item)
            {
                if (item.Parent != null)
                    throw new InvalidOperationException("This TransformComponent already has a Parent, detach it first.");

                item.parent = transform;

                Entity?.EntityManager?.OnHierarchyChanged(item.Entity);
                Entity?.EntityManager?.GetProcessor<TransformProcessor>().NotifyChildrenCollectionChanged(item, true);
            }
            private void OnTransformRemoved(TransformComponent item)
            {
                if (item.Parent != transform)
                    throw new InvalidOperationException("This TransformComponent's parent is not the expected value.");

                item.parent = null;

                Entity?.EntityManager?.OnHierarchyChanged(item.Entity);
                Entity?.EntityManager?.GetProcessor<TransformProcessor>().NotifyChildrenCollectionChanged(item, false);
            }
            
            /// <inheritdoc/>
            protected override void InsertItem(int index, TransformComponent item)
            {
                base.InsertItem(index, item);
                OnTransformAdded(item);
            }

            /// <inheritdoc/>
            protected override void RemoveItem(int index)
            {
                OnTransformRemoved(this[index]);
                base.RemoveItem(index);
            }

            /// <inheritdoc/>
            protected override void ClearItems()
            {
                for (var i = Count - 1; i >= 0; --i)
                    OnTransformRemoved(this[i]);
                base.ClearItems();
            }

            /// <inheritdoc/>
            protected override void SetItem(int index, TransformComponent item)
            {
                OnTransformRemoved(this[index]);

                base.SetItem(index, item);

                OnTransformAdded(this[index]);
            }
        }
    }
}
