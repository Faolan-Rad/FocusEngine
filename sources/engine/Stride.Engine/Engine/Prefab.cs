// Copyright (c) Stride contributors (https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Collections;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Engine.Design;

namespace Stride.Engine
{
    /// <summary>
    /// A prefab that contains entities.
    /// </summary>
    [DataContract("Prefab")]
    [ContentSerializer(typeof(DataContentSerializerWithReuse<Prefab>))]
    [ReferenceSerializer, DataSerializerGlobal(typeof(ReferenceSerializer<Prefab>), Profile = "Content")]
    public sealed class Prefab
    {
        /// <summary>
        /// The entities.
        /// </summary>
        public List<Entity> Entities { get; } = new List<Entity>();

        /// <summary>
        /// Instantiates entities from a prefab that can be later added to a <see cref="Scene"/>.
        /// </summary>
        /// <returns>A collection of entities extracted from the prefab</returns>
        public List<Entity> Instantiate()
        {
            var newPrefab = EntityCloner.Clone(this);
            return newPrefab.Entities;
        }

        private Entity packed;

        public Prefab() { }

        /// <summary>
        /// Make Prefab at runtime
        /// </summary>
        /// <param name="e"></param>
        public Prefab(Entity e)
        {
            Entities.Add(e);
        }

        /// <summary>
        /// Make Prefab at runtime
        /// </summary>
        /// <param name="e"></param>
        public Prefab(List<Entity> e)
        {
            Entities.AddRange(e);
        }

        /// <summary>
        /// Converts a Prefab into a single Entity that has all entities as children. Makes it easier to use with an EntityPool
        /// </summary>
        /// <param name="p"></param>
        /// <returns></returns>
        public Entity PackToEntity() {
            if (packed == null) {
                List<Entity> roots = new List<Entity>();
                for (int i = 0; i < Entities.Count; i++) {
                    if (Entities[i].Transform.Parent == null)
                        roots.Add(Entities[i]);
                }
                if (roots.Count == 1) {
                    packed = roots[0];
                } else {
                    packed = new Entity();
                    for (int i = 0; i < roots.Count; i++) {
                        roots[i].Transform.Parent = packed.Transform;
                    }
                }
            }
            return packed;
        }
    }
}
