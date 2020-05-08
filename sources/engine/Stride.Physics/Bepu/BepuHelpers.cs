using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using BepuUtilities.Memory;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering.Rendering;

namespace Stride.Physics.Bepu
{
    public class BepuHelpers
    {
        internal static PhysicsSystem physicsSystem;

        /// <summary>
        /// Good to call this at the start of your application. Will automatically get called in some situations, but not be soon enough.
        /// </summary>
        public static void AssureBepuSystemCreated()
        {
            if (physicsSystem == null)
            {
                physicsSystem = ServiceRegistry.instance.GetService<PhysicsSystem>();
                if (physicsSystem == null)
                {
                    physicsSystem = new PhysicsSystem(ServiceRegistry.instance);
                    ServiceRegistry.instance.AddService<IPhysicsSystem>(physicsSystem);
                    var gameSystems = ServiceRegistry.instance.GetSafeServiceAs<IGameSystemCollection>();
                    gameSystems.Add(physicsSystem);
                    ((IReferencable)physicsSystem).AddReference();
                    physicsSystem.Create(null, PhysicsEngineFlags.None, true);
                }
                else if (physicsSystem.HasSimulation<BepuSimulation>() == false)
                {
                    physicsSystem.Create(null, PhysicsEngineFlags.None, true);
                }
            }
        }

        private static Vector3 getBounds(Entity e, out Vector3 center)
        {
            ModelComponent mc = e.Get<ModelComponent>();
            center = new Vector3();
            if (mc == null || mc.Model == null || mc.Model.Meshes.Count <= 0f) return Vector3.Zero;

            Vector3 biggest = new Vector3(0.05f, 0.05f, 0.05f);
            int count = mc.Model.Meshes.Count;
            for (int i=0; i<count; i++)
            {
                Stride.Rendering.Mesh m = mc.Model.Meshes[i];
                BoundingBox bb = m.BoundingBox;
                Vector3 extent = bb.Extent;
                if (extent.X > biggest.X) biggest.X = extent.X;
                if (extent.Y > biggest.Y) biggest.Y = extent.Y;
                if (extent.Z > biggest.Z) biggest.Z = extent.Z;
                center += bb.Center;
            }
            center /= count;
            return biggest * e.Transform.WorldScale();
        }

        /// <summary>
        /// Is this an OK shape? Checks for 0 or negative sizes, or compounds with no children etc...
        /// </summary>
        /// <param name="shape">Shape to check</param>
        /// <returns>true is this shape is sane, false if it has problems</returns>
        public static bool SanityCheckShape(IShape shape)
        {
            if (shape is Box box)
                return box.HalfHeight > 0f && box.HalfLength > 0f && box.HalfWidth > 0f;

            if (shape is Sphere sphere)
                return sphere.Radius > 0f;

            if (shape is Cylinder cylinder)
                return cylinder.Radius > 0f && cylinder.HalfLength > 0f;

            if (shape is Capsule capsule)
                return capsule.HalfLength > 0f && capsule.Radius > 0f;

            if (shape is Triangle triangle)
                return triangle.A != triangle.B && triangle.A != triangle.C && triangle.B != triangle.C;

            if (shape is ICompoundShape compound)
                return compound.ChildCount > 0;

            if (shape is Mesh mesh)
                return mesh.ChildCount > 0;

            return shape != null;
        }

        public static IShape OffsetSingleShape(IConvexShape shape, Vector3? offset = null, Quaternion? rotation = null, bool kinematic = false)
        {
            if (offset.HasValue == false && rotation.HasValue == false) return shape;

            if (shape is ICompoundShape) throw new InvalidOperationException("Cannot offset a compound shape. Can't support nested compounds.");

            using (var compoundBuilder = new CompoundBuilder(BepuSimulation.safeBufferPool, BepuSimulation.instance.internalSimulation.Shapes, 1))
            {
                if (kinematic)
                {
                    compoundBuilder.AddForKinematicEasy(shape, new BepuPhysics.RigidPose(ToBepu(offset ?? Vector3.Zero), ToBepu(rotation ?? Quaternion.Identity)), 1f);
                }
                else
                {
                    compoundBuilder.AddEasy(shape, new BepuPhysics.RigidPose(ToBepu(offset ?? Vector3.Zero), ToBepu(rotation ?? Quaternion.Identity)), 1f);
                }

                return compoundBuilder.BuildCompleteCompoundShape(BepuSimulation.instance.internalSimulation.Shapes, BepuSimulation.safeBufferPool, kinematic);
            }
        }

        public static IShape GenerateBoxOfEntity(Entity e, float scale = 1f, bool allowOffsetCompound = true)
        {
            Vector3 b = getBounds(e, out Vector3 center) * scale * 2f;
            var box = new Box(b.X, b.Y, b.Z);
            if (allowOffsetCompound && center.LengthSquared() > 0.01f) return OffsetSingleShape(box, center);
            return box;
        }

        public static IShape GenerateSphereOfEntity(Entity e, float scale = 1f, bool allowOffsetCompound = true)
        {
            Vector3 b = getBounds(e, out Vector3 center);
            var box = new Sphere(Math.Max(b.Z, Math.Max(b.X, b.Y)) * scale);
            if (allowOffsetCompound && center.LengthSquared() > 0.01f) return OffsetSingleShape(box, center);
            return box;
        }

        public static IShape GenerateCapsuleOfEntity(Entity e, float scale = 1f, bool XZradius = true, bool allowOffsetCompound = true)
        {
            Vector3 b = getBounds(e, out Vector3 center) * scale;
            var box = XZradius ? new Capsule(Math.Max(b.X, b.Z), b.Y * 2f) : new Capsule(b.Y, 2f * Math.Max(b.X, b.Z));
            if (allowOffsetCompound && center.LengthSquared() > 0.01f) return OffsetSingleShape(box, center);
            return box;
        }

        public static IShape GenerateCylinderOfEntity(Entity e, float scale = 1f, bool XZradius = true, bool allowOffsetCompound = true)
        {
            Vector3 b = getBounds(e, out Vector3 center) * scale;
            var box = XZradius ? new Cylinder(Math.Max(b.X, b.Z), b.Y * 2f) : new Cylinder(b.Y, 2f * Math.Max(b.X, b.Z));
            if (allowOffsetCompound && center.LengthSquared() > 0.01f) return OffsetSingleShape(box, center);
            return box;
        }

        /// <summary>
        /// Since you can't have non-convex shapes (e.g. mesh's) in a compound object, this helper will generate a bunch of individual static components to attach to an entity, with each shape.
        /// </summary>
        /// <param name="e">Entity to add static components to</param>
        /// <param name="shapes">shapes that will generate a static component for each</param>
        /// <param name="offsets">optional offset for each</param>
        /// <param name="rotations">optional rotation for each</param>
        public static void GenerateStaticComponents(Entity e, List<IShape> shapes, List<Vector3> offsets = null, List<Quaternion> rotations = null,
                                                    CollisionFilterGroups group = CollisionFilterGroups.DefaultFilter, CollisionFilterGroupFlags collidesWith = CollisionFilterGroupFlags.AllFilter,
                                                    float FrictionCoefficient = 0.5f, float MaximumRecoverableVelocity = 2f, SpringSettings? springSettings = null)
        {
            for (int i=0; i<shapes.Count; i++)
            {
                BepuStaticColliderComponent sc = new BepuStaticColliderComponent();
                sc.ColliderShape = shapes[i];
                if (offsets != null && offsets.Count > i) sc.Position = offsets[i];
                if (rotations != null && rotations.Count > i) sc.Rotation = rotations[i];
                sc.CanCollideWith = collidesWith;
                sc.CollisionGroup = group;
                sc.FrictionCoefficient = FrictionCoefficient;
                sc.MaximumRecoveryVelocity = MaximumRecoverableVelocity;
                if (springSettings.HasValue) sc.SpringSettings = springSettings.Value;
                e.Add(sc);
            }
        }

        /// <summary>
        /// Easily makes a Compound shape for you, given a list of individual shapes and how they should be offset.
        /// </summary>
        /// <param name="shapes">List of convex shapes</param>
        /// <param name="offsets">Matching length list of offsets of bodies, can be null if nothing has an offset</param>
        /// <param name="rotations">Matching length list of rotations of bodies, can be null if nothing is rotated</param>
        /// <param name="isDynamic">True if intended to use in a dynamic situation, false if kinematic or static</param>
        /// <returns></returns>
        public static ICompoundShape MakeCompound(List<IConvexShape> shapes, List<Vector3> offsets = null, List<Quaternion> rotations = null, bool isDynamic = true, int bigThreshold = 5)
        {
            using (var compoundBuilder = new CompoundBuilder(BepuSimulation.safeBufferPool, BepuSimulation.instance.internalSimulation.Shapes, shapes.Count))
            {
                bool allConvex = true;

                //All allocations from the buffer pool used for the final compound shape will be disposed when the demo is disposed. Don't have to worry about leaks in these demos.
                for (int i=0; i<shapes.Count; i++)
                {
                    if (shapes[i] is ICompoundShape) throw new InvalidOperationException("Cannot include compounds in another compound shape.");

                    if (isDynamic)
                    {
                        compoundBuilder.AddEasy(shapes[i] as IConvexShape, new BepuPhysics.RigidPose(ToBepu(offsets?[i] ?? Vector3.Zero), ToBepu(rotations?[i] ?? Quaternion.Identity)), 1f);
                    } 
                    else
                    {
                        if (shapes[i] is IConvexShape == false) allConvex = false;

                        compoundBuilder.AddForKinematicEasy(shapes[i], new BepuPhysics.RigidPose(ToBepu(offsets?[i] ?? Vector3.Zero), ToBepu(rotations?[i] ?? Quaternion.Identity)), 1f);
                    }
                }

                return compoundBuilder.BuildCompleteCompoundShape(BepuSimulation.instance.internalSimulation.Shapes, BepuSimulation.safeBufferPool, isDynamic, allConvex ? bigThreshold : int.MaxValue);
            }
        }

        /// <summary>
        /// Goes through the whole scene and adds bepu physics objects to the simulation. Only will add if AllowHelperToAdd is true (which is set to true by default)
        /// and if the body isn't added already.
        /// </summary>
        /// <param name="rootScene"></param>
        public static void SetBodiesInSimulation(Scene rootScene, bool add = true)
        {
            foreach (Entity e in rootScene.Entities)
                SetBodiesInSimulation(e, add);
        }

        /// <summary>
        /// Goes through the entity and children and adds/removes bepu physics objects to the simulation. Only will add/remove if AllowHelperToManage is true (which is set to true by default)
        /// and if the body isn't added already.
        /// </summary>
        /// <param name="rootEntity"></param>
        public static void SetBodiesInSimulation(Entity rootEntity, bool add = true)
        {
            foreach (BepuPhysicsComponent pc in rootEntity.GetAll<BepuPhysicsComponent>())
                if (pc.AutomaticAdd) pc.AddedToScene = add;
            foreach (Entity e in rootEntity.GetChildren())
                SetBodiesInSimulation(e, add);
        }

        /// <summary>
        /// Shortcut to clearing the simulation of all bodies. Optionally clears all the buffers too (e.g. mesh colliders), which is enabled by default
        /// </summary>
        public static void ClearSimulation(bool clearBuffers = true)
        {
            BepuSimulation.instance.Clear(clearBuffers);
        }

        private static unsafe bool getMeshOutputs(Stride.Rendering.Mesh modelMesh, out List<Vector3> positions, out List<int> indicies)
        {
            if (modelMesh.Draw is StagedMeshDraw)
            {
                StagedMeshDraw smd = modelMesh.Draw as StagedMeshDraw;

                object verts = smd.Verticies;

                if (verts is VertexPositionNormalColor[])
                {
                    VertexPositionNormalColor[] vpnc = verts as VertexPositionNormalColor[];
                    positions = new List<Vector3>(vpnc.Length);
                    for (int k = 0; k < vpnc.Length; k++)
                        positions.Add(vpnc[k].Position);
                }
                else if (verts is VertexPositionNormalTexture[])
                {
                    VertexPositionNormalTexture[] vpnc = verts as VertexPositionNormalTexture[];
                    positions = new List<Vector3>(vpnc.Length);
                    for (int k = 0; k < vpnc.Length; k++)
                        positions.Add(vpnc[k].Position);
                }
                else if (verts is VertexPositionNormalTextureTangent[])
                {
                    VertexPositionNormalTextureTangent[] vpnc = verts as VertexPositionNormalTextureTangent[];
                    positions = new List<Vector3>(vpnc.Length);
                    for (int k = 0; k < vpnc.Length; k++)
                        positions.Add(vpnc[k].Position);
                }
                else
                {
                    positions = null;
                    indicies = null;
                    return false;
                }

                // take care of indicies
                indicies = new List<int>(smd.Indicies.Length);
                for (int i = 0; i < smd.Indicies.Length; i++)
                    indicies.Add((int)smd.Indicies[i]);
            }
            else
            {
                Stride.Graphics.Buffer buf = modelMesh.Draw?.VertexBuffers[0].Buffer;
                Stride.Graphics.Buffer ibuf = modelMesh.Draw?.IndexBuffer.Buffer;
                if (buf == null || buf.VertIndexData == null ||
                    ibuf == null || ibuf.VertIndexData == null)
                {
                    positions = null;
                    indicies = null;
                    return false;
                }

                if (ModelBatcher.UnpackRawVertData(buf.VertIndexData, modelMesh.Draw.VertexBuffers[0].Declaration,
                                                   out Vector3[] arraypositions, out Core.Mathematics.Vector3[] normals, out Core.Mathematics.Vector2[] uvs,
                                                   out Color4[] colors, out Vector4[] tangents) == false)
                {
                    positions = null;
                    indicies = null;
                    return false;
                }

                // indicies
                fixed (byte* pdst = ibuf.VertIndexData)
                {
                    if (modelMesh.Draw.IndexBuffer.Is32Bit)
                    {
                        var dst = (uint*)pdst;

                        int numIndices = ibuf.VertIndexData.Length / sizeof(uint);
                        indicies = new List<int>(numIndices);
                        for (var k = 0; k < numIndices; k++)
                        {
                            // Offset indices
                            indicies.Add((int)dst[k]);
                        }
                    }
                    else
                    {
                        var dst = (ushort*)pdst;

                        int numIndices = ibuf.VertIndexData.Length / sizeof(ushort);
                        indicies = new List<int>(numIndices);
                        for (var k = 0; k < numIndices; k++)
                        {
                            // Offset indices
                            indicies.Add(dst[k]);
                        }
                    }
                }

                // take care of positions
                positions = new List<Vector3>(arraypositions);
            }

            return true;
        }

        /// <summary>
        /// Generate a mesh collider from a given mesh. The mesh must have a readable buffer behind it to generate veriticies from
        /// </summary>
        /// <returns>Returns false if no mesh could be made</returns>
        public static unsafe bool GenerateMeshShape(Stride.Rendering.Mesh modelMesh, out BepuPhysics.Collidables.Mesh outMesh, out BepuUtilities.Memory.BufferPool poolUsed, Vector3? scale = null)
        {
            if (getMeshOutputs(modelMesh, out var positions, out var indicies) == false)
            {
                outMesh = default;
                poolUsed = null;
                return false;
            }

            return GenerateMeshShape(positions, indicies, out outMesh, out poolUsed, scale);
        }

        /// <summary>
        /// Generate a mesh collider from all meshes in an entity. The meshes must have a readable buffer behind it to generate veriticies from.
        /// </summary>
        /// <returns>Returns false if no mesh could be made</returns>
        public static unsafe bool GenerateMeshShape(Entity e, out BepuPhysics.Collidables.Mesh outMesh, out BepuUtilities.Memory.BufferPool poolUsed)
        {
            // get all meshes
            List<Stride.Rendering.Mesh> meshes = new List<Stride.Rendering.Mesh>();
            CollectMeshes(e, meshes);
            List<Vector3> allPositions = new List<Vector3>();
            List<int> allIndicies = new List<int>();
            for (int i = 0; i < meshes.Count; i++)
            {
                getMeshOutputs(meshes[i], out var pos, out var indicies);
                for (int j = 0; j < indicies.Count; j++)
                {
                    allIndicies.Add(indicies[j] + allPositions.Count);
                }
                allPositions.AddRange(pos);
            }

            if (allIndicies.Count == 0 || allPositions.Count == 0)
            {
                outMesh = default;
                poolUsed = null;
                return false;
            }

            return GenerateMeshShape(allPositions, allIndicies, out outMesh, out poolUsed, e.Transform.WorldScale());
        }

        private static void CollectMeshes(Entity e, List<Stride.Rendering.Mesh> meshes)
        {
            foreach(ModelComponent mc in e.GetAll<ModelComponent>())
            {
                if (mc.Model == null) continue;
                for (int i=0;i<mc.Model.Meshes.Count; i++)
                {
                    meshes.Add(mc.Model.Meshes[i]);
                }
            }
            foreach (Entity child in e.GetChildren())
                CollectMeshes(child, meshes);
        }

        public static void DisposeAllMeshes(Entity e)
        {
            foreach (BepuStaticColliderComponent scc in e.GetAll<BepuStaticColliderComponent>())
                scc.DisposeMesh();
            foreach (Entity child in e.GetChildren())
                DisposeAllMeshes(child);
        }

        /// <summary>
        /// Generate a mesh collider from all meshes in an entity. The meshes must have a readable buffer behind it to generate veriticies from
        /// </summary>
        /// <returns>Returns false if no mesh could be made</returns>
        public static unsafe bool GenerateBigMeshStaticColliders(Entity e, CollisionFilterGroups group = CollisionFilterGroups.DefaultFilter, CollisionFilterGroupFlags collidesWith = CollisionFilterGroupFlags.AllFilter,
                                                                 float friction = 0.5f, float maximumRecoverableVelocity = 1f, SpringSettings? springSettings = null, bool disposeOnDetach = false)
        {
            // get all meshes
            List<Stride.Rendering.Mesh> meshes = new List<Stride.Rendering.Mesh>();
            CollectMeshes(e, meshes);
            List<Vector3> allPositions = new List<Vector3>();
            List<int> allIndicies = new List<int>();
            for (int i=0; i<meshes.Count; i++)
            {
                getMeshOutputs(meshes[i], out var pos, out var indicies);
                for (int j=0; j<indicies.Count; j++)
                {
                    allIndicies.Add(indicies[j] + allPositions.Count);
                }
                allPositions.AddRange(pos);
            }

            if (allIndicies.Count == 0 || allPositions.Count == 0) return false;

            GenerateBigMeshStaticColliders(e, allPositions, allIndicies, e.Transform.WorldScale(), group, collidesWith, friction, maximumRecoverableVelocity, springSettings, disposeOnDetach);

            return true;
        }

        /// <summary>
        /// Generate a mesh collider from a given mesh. The mesh must have a readable buffer behind it to generate veriticies from
        /// </summary>
        /// <returns>Returns false if no mesh could be made</returns>
        public static unsafe bool GenerateBigMeshStaticColliders(Entity e, Stride.Rendering.Mesh modelMesh, Vector3? scale = null,
                                                                 CollisionFilterGroups group = CollisionFilterGroups.DefaultFilter, CollisionFilterGroupFlags collidesWith = CollisionFilterGroupFlags.AllFilter,
                                                                 float friction = 0.5f, float maximumRecoverableVelocity = 1f, SpringSettings? springSettings = null, bool disposeOnDetach = false)
        {
            if (getMeshOutputs(modelMesh, out var positions, out var indicies) == false)
            {
                return false;
            }

            GenerateBigMeshStaticColliders(e, positions, indicies, scale, group, collidesWith, friction, maximumRecoverableVelocity, springSettings, disposeOnDetach);

            return true;
        }

        public static unsafe bool GenerateMeshShape(List<Vector3> positions, List<int> indicies, out BepuPhysics.Collidables.Mesh outMesh, out BepuUtilities.Memory.BufferPool poolUsed, Vector3? scale = null)
        {
            poolUsed = BepuSimulation.safeBufferPool;

            // ok, should have what we need to make triangles
            int triangleCount = indicies.Count / 3;

            BepuUtilities.Memory.Buffer<Triangle> triangles;
            lock (poolUsed)
            {
                poolUsed.Take<Triangle>(triangleCount, out triangles);
            }

            Stride.Core.Threading.Dispatcher.For(0, triangleCount, (i) =>
            {
                int shiftedi = i * 3;
                triangles[i].A = ToBepu(positions[indicies[shiftedi]]);
                triangles[i].B = ToBepu(positions[indicies[shiftedi + 1]]);
                triangles[i].C = ToBepu(positions[indicies[shiftedi + 2]]);
            });

            lock (poolUsed)
            {
                outMesh = new Mesh(triangles, new System.Numerics.Vector3(scale?.X ?? 1f, scale?.Y ?? 1f, scale?.Z ?? 1f), poolUsed);
            }

            return true;
        }

        public static unsafe void GenerateBigMeshStaticColliders(Entity e, List<Vector3> positions, List<int> indicies, Vector3? scale = null,
                                                                 CollisionFilterGroups group = CollisionFilterGroups.DefaultFilter, CollisionFilterGroupFlags collidesWith = CollisionFilterGroupFlags.AllFilter,
                                                                 float friction = 0.5f, float maximumRecoverableVelocity = 1f, SpringSettings? springSettings = null, bool disposeOnDetach = false)
        {
            // ok, should have what we need to make triangles
            int triangleCount = indicies.Count / 3;

            if (triangleCount < Stride.Core.Threading.Dispatcher.MaxDegreeOfParallelism * 2f)
            {
                // if we have a really small mesh, doesn't make sense to split it up a bunch
                var scc = new BepuStaticColliderComponent();
                scc.CanCollideWith = collidesWith;
                scc.CollisionGroup = group;
                scc.FrictionCoefficient = friction;
                scc.DisposeMeshOnDetach = disposeOnDetach;
                scc.MaximumRecoveryVelocity = maximumRecoverableVelocity;
                if (springSettings.HasValue) scc.SpringSettings = springSettings.Value;
                GenerateMeshShape(positions, indicies, out var outMesh, out scc.PoolUsedForMesh, scale);
                scc.ColliderShape = outMesh;
                e.Add(scc);
            }
            else
            {
                int trianglesPerThread = 1 + (triangleCount / Stride.Core.Threading.Dispatcher.MaxDegreeOfParallelism);
                BepuStaticColliderComponent[] scs = new BepuStaticColliderComponent[Stride.Core.Threading.Dispatcher.MaxDegreeOfParallelism];
                var scalev3 = new System.Numerics.Vector3(scale?.X ?? 1f, scale?.Y ?? 1f, scale?.Z ?? 1f);
                Stride.Core.Threading.Dispatcher.For(0, scs.Length, (index) =>
                {
                    var pool = BepuSimulation.safeBufferPool;
                    int triangleStart = index * trianglesPerThread;
                    int triangleEnd = Math.Min(triangleStart + trianglesPerThread, triangleCount);
                    int triangleLen = triangleEnd - triangleStart;
                    if (triangleLen <= 0) return;
                    BepuUtilities.Memory.Buffer<Triangle> buf;
                    lock (pool)
                    {
                        pool.Take<Triangle>(triangleLen, out buf);
                    }
                    int pos = 0;
                    for (int i=triangleStart; i<triangleEnd; i++)
                    {
                        int shiftedi = i * 3;
                        buf[pos].A = ToBepu(positions[indicies[shiftedi]]);
                        buf[pos].B = ToBepu(positions[indicies[shiftedi + 1]]);
                        buf[pos].C = ToBepu(positions[indicies[shiftedi + 2]]);
                        pos++;
                    }
                    scs[index] = new BepuStaticColliderComponent();
                    ref var sc = ref scs[index];
                    sc.PoolUsedForMesh = pool;
                    sc.CanCollideWith = collidesWith;
                    sc.CollisionGroup = group;
                    sc.FrictionCoefficient = friction;
                    sc.DisposeMeshOnDetach = disposeOnDetach;
                    sc.MaximumRecoveryVelocity = maximumRecoverableVelocity;
                    if (springSettings.HasValue) sc.SpringSettings = springSettings.Value;

                    lock (sc.PoolUsedForMesh)
                    {
                        sc.ColliderShape = new Mesh(buf, scalev3, sc.PoolUsedForMesh);
                    }
                });

                for (int i = 0; i < scs.Length; i++)
                    if (scs[i] != null) e.Add(scs[i]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe System.Numerics.Vector3 ToBepu(Stride.Core.Mathematics.Vector3 v)
        {
            return *((System.Numerics.Vector3*)(void*)&v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe Stride.Core.Mathematics.Vector3 ToStride(System.Numerics.Vector3 v)
        {
            return *((Stride.Core.Mathematics.Vector3*)(void*)&v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe Stride.Core.Mathematics.Quaternion ToStride(System.Numerics.Quaternion q)
        {
            return *((Stride.Core.Mathematics.Quaternion*)(void*)&q);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe System.Numerics.Quaternion ToBepu(Stride.Core.Mathematics.Quaternion q)
        {
            return *((System.Numerics.Quaternion*)(void*)&q);
        }
    }
}
