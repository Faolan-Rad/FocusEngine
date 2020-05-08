// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Xenko.Games;

namespace Xenko.Physics
{
    public interface IPhysicsSystem : IGameSystemBase
    {
        object Create(PhysicsProcessor processor, PhysicsEngineFlags flags = PhysicsEngineFlags.None, bool bepu = false);
        void Release(PhysicsProcessor processor);
    }
}
