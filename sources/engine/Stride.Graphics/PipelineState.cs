// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Xenko.Core.ReferenceCounting;
using System.Threading;

namespace Xenko.Graphics
{
    public partial class PipelineState : GraphicsResourceBase
    {
        public enum PIPELINE_STATE {
            LOADING = 0,
            READY = 1,
            ERROR = 2
        };

        public int InputBindingCount { get; private set; }
        internal long storedHash;

        public static PipelineState New(GraphicsDevice graphicsDevice, ref PipelineStateDescription pipelineStateDescription, PipelineState existingState)
        {
            // Hash the current state
            long hashedState = pipelineStateDescription.GetLongHashCode();

            // do we even need to check the cache? We already have this?
            if (existingState != null && existingState.storedHash == hashedState)
                return existingState;

            PipelineState pipelineState = null;

            // check if it is in the cache, or being worked on...
            bool foundInCache = false;

            lock (graphicsDevice.CachedPipelineStates) {
                foundInCache = graphicsDevice.CachedPipelineStates.TryGetValue(hashedState, out pipelineState);
                if (!foundInCache) {
                    pipelineState = new PipelineState(graphicsDevice); // mark we will work on this pipeline (which is just blank right now)
                    pipelineState.storedHash = hashedState;
                    graphicsDevice.CachedPipelineStates[hashedState] = pipelineState;
                }
            }

            // if we have this cached, wait until it is ready to return
            if (foundInCache) {
                pipelineState.AddReferenceInternal();
                return pipelineState;
            }

            if (GraphicsDevice.Platform == GraphicsPlatform.Vulkan) {
                // if we are using Vulkan, just make a new pipeline without locking
                pipelineState.Prepare(pipelineStateDescription);
            } else {
                // D3D seems to have quite bad concurrency when using CreateSampler while rendering
                lock (graphicsDevice.CachedPipelineStates) {
                    pipelineState.Prepare(pipelineStateDescription);
                }
            }

            return pipelineState;
        }
    }
}
