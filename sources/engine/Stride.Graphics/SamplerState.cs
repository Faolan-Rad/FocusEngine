// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Xenko.Core.ReferenceCounting;

namespace Xenko.Graphics
{
    public partial class SamplerState : GraphicsResourceBase
    {
        /// <summary>
        /// Gets the sampler state description.
        /// </summary>
        public readonly SamplerStateDescription Description;

        // For FakeSamplerState.
        protected SamplerState()
        {
        }

        // For FakeSamplerState.
        private SamplerState(SamplerStateDescription description)
        {
            Description = description;
        }

        public static SamplerState New(GraphicsDevice graphicsDevice, SamplerStateDescription samplerStateDescription)
        {
            // Store SamplerState in a cache (D3D seems to have quite bad concurrency when using CreateSampler while rendering)
            SamplerState samplerState;

            if (GraphicsDevice.Platform == GraphicsPlatform.Vulkan) {
                if (graphicsDevice.CachedSamplerStates.TryGetValue(samplerStateDescription, out samplerState)) {
                    // TODO: Appropriate destroy
                    samplerState.AddReferenceInternal();
                } else {
                    graphicsDevice.CachedSamplerStates.TryAdd(samplerStateDescription, samplerState = new SamplerState(graphicsDevice, samplerStateDescription));
                }
            } else {
                lock (graphicsDevice.CachedSamplerStates) {
                    if (graphicsDevice.CachedSamplerStates.TryGetValue(samplerStateDescription, out samplerState)) {
                        // TODO: Appropriate destroy
                        samplerState.AddReferenceInternal();
                    } else {
                        samplerState = new SamplerState(graphicsDevice, samplerStateDescription);
                        graphicsDevice.CachedSamplerStates.TryAdd(samplerStateDescription, samplerState);
                    }
                }
            }

            return samplerState;
        }
        
        /// <summary>
        /// Create a new fake sampler state for serialization.
        /// </summary>
        /// <param name="description">The description of the sampler state</param>
        /// <returns>The fake sampler state</returns>
        public static SamplerState NewFake(SamplerStateDescription description)
        {
            return new SamplerState(description);
        }

        protected override void Destroy()
        {
            if (GraphicsDevice.Platform == GraphicsPlatform.Vulkan) {
                GraphicsDevice.CachedSamplerStates.TryRemove(Description, out SamplerState junk);
            } else {
                lock (GraphicsDevice.CachedSamplerStates) {
                    GraphicsDevice.CachedSamplerStates.TryRemove(Description, out SamplerState junk);
                }
            }

            base.Destroy();
        }
    }
}
