// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
#if XENKO_GRAPHICS_API_VULKAN
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using SharpVulkan;
using System.Threading;
using ImageLayout = SharpVulkan.ImageLayout;
using Xenko.Core;
using Xenko.Core.Threading;

namespace Xenko.Graphics
{
    /// <summary>
    /// Graphics presenter for SwapChain.
    /// </summary>
    public class SwapChainGraphicsPresenter : GraphicsPresenter
    {
        private Swapchain swapChain = Swapchain.Null;
        private Surface surface;

        private ConcurrentQueue<uint> toPresent = new ConcurrentQueue<uint>();

        private Texture backbuffer;
        private SwapChainImageInfo[] swapchainImages;
        private volatile uint currentBufferIndex;
        private Fence presentFence;

        private struct SwapChainImageInfo
        {
            public SharpVulkan.Image NativeImage;
            public ImageView NativeColorAttachmentView;
        }

        public SwapChainGraphicsPresenter(GraphicsDevice device, PresentationParameters presentationParameters)
            : base(device, presentationParameters)
        {
            PresentInterval = presentationParameters.PresentationInterval;

            backbuffer = new Texture(device);

            CreateSurface();

            // Initialize the swap chain
            CreateSwapChain();
        }

        public override Texture BackBuffer
        {
            get
            {
                return backbuffer;
            }
        }

        public override object NativePresenter
        {
            get
            {
                return swapChain;
            }
        }

        public override bool InternalFullscreen { get; set; }

        private ManualResetEventSlim presentWaiter = new ManualResetEventSlim(false);
        private Thread presenterThread;
        private bool runPresenter;

        private unsafe void PresenterThread() {
            Swapchain swapChainCopy = swapChain;
            uint currentBufferIndexCopy = 0;
            PresentInfo presentInfo = new PresentInfo {
                StructureType = StructureType.PresentInfo,
                SwapchainCount = 1,
                Swapchains = new IntPtr(&swapChainCopy),
                ImageIndices = new IntPtr(&currentBufferIndexCopy),
                WaitSemaphoreCount = 0
            };
            while (runPresenter) {
                // wait until we have a frame to present
                presentWaiter.Wait();

                // are we still OK to present?
                if (runPresenter == false) return;

                while (toPresent.TryDequeue(out currentBufferIndexCopy)) {
                    using (GraphicsDevice.QueueLock.WriteLock())
                    {
                        GraphicsDevice.NativeCommandQueue.Present(ref presentInfo);        
                    }                
                }

                presentWaiter.Reset();
            }
        }

        public override unsafe void Present()
        {
            // collect and let presenter thread know to present
            toPresent.Enqueue(currentBufferIndex);
            presentWaiter.Set();

            // Get next image
            Result r = GraphicsDevice.NativeDevice.AcquireNextImageWithResult(swapChain, ulong.MaxValue, SharpVulkan.Semaphore.Null, presentFence, out currentBufferIndex);

            // reset dummy fence
            fixed (Fence* fences = &presentFence)
            {
                GraphicsDevice.NativeDevice.ResetFences(1, fences);
            }

            // Flip render targets
            backbuffer.SetNativeHandles(swapchainImages[currentBufferIndex].NativeImage, swapchainImages[currentBufferIndex].NativeColorAttachmentView);
        }

        public override void BeginDraw(CommandList commandList)
        {   
            // Backbuffer needs to be cleared
            backbuffer.IsInitialized = false;
        }

        public override void EndDraw(CommandList commandList, bool present)
        {
        }

        protected override void OnNameChanged()
        {
            base.OnNameChanged();
        }

        /// <inheritdoc/>
        protected internal override unsafe void OnDestroyed()
        {
            DestroySwapchain();

            GraphicsDevice.NativeInstance.DestroySurface(surface);
            surface = Surface.Null;

            base.OnDestroyed();
        }

        /// <inheritdoc/>
        public override void OnRecreated()
        {
            base.OnRecreated();

            // not supported
        }

        protected unsafe override void ResizeBackBuffer(int width, int height, PixelFormat format)
        {
            // not supported
        }

        protected override void ResizeDepthStencilBuffer(int width, int height, PixelFormat format)
        {
            // not supported
        }

        private unsafe void DestroySwapchain()
        {
            if (swapChain == Swapchain.Null)
                return;
    
            // stop our presenter thread
            if( presenterThread != null ) {
                runPresenter = false;
                presentWaiter.Set();
                presenterThread.Join();
            }

            GraphicsDevice.NativeDevice.WaitIdle();
            CommandList.ResetAllPools();

            backbuffer.OnDestroyed();

            foreach (var swapchainImage in swapchainImages)
            {
                GraphicsDevice.NativeDevice.DestroyImageView(swapchainImage.NativeColorAttachmentView);
            }
            swapchainImages = null;

            GraphicsDevice.NativeDevice.DestroySwapchain(swapChain);
            swapChain = Swapchain.Null;
        }

        private unsafe void CreateSwapChain()
        {
            // we are destroying the swap chain now, because it causes lots of other things to be reset too (like all commandbufferpools)
            // normally we pass the old swapchain to the create new swapchain Vulkan call... but I haven't figured out a stable way of
            // preserving the old swap chain to be passed during the new swapchain creation, and then destroying just the old swapchain parts.
            // might have to reset the command buffers and pipeline stuff after swapchain handoff... for another day e.g. TODO
            DestroySwapchain();

            var formats = new[] { PixelFormat.B8G8R8A8_UNorm_SRgb, PixelFormat.R8G8B8A8_UNorm_SRgb, PixelFormat.B8G8R8A8_UNorm, PixelFormat.R8G8B8A8_UNorm };

            foreach (var format in formats)
            {
                var nativeFromat = VulkanConvertExtensions.ConvertPixelFormat(format);

                FormatProperties formatProperties;
                GraphicsDevice.NativePhysicalDevice.GetFormatProperties(nativeFromat, out formatProperties);

                if ((formatProperties.OptimalTilingFeatures & FormatFeatureFlags.ColorAttachment) != 0)
                {
                    Description.BackBufferFormat = format;
                    break;
                }
            }

            // Queue
            // TODO VULKAN: Queue family is needed when creating the Device, so here we can just do a sanity check?
            var queueNodeIndex = GraphicsDevice.NativePhysicalDevice.QueueFamilyProperties.
                Where((properties, index) => (properties.QueueFlags & QueueFlags.Graphics) != 0 && GraphicsDevice.NativePhysicalDevice.GetSurfaceSupport((uint)index, surface)).
                Select((properties, index) => index).First();

            // Surface format
            var backBufferFormat = VulkanConvertExtensions.ConvertPixelFormat(Description.BackBufferFormat);

            var surfaceFormats = GraphicsDevice.NativePhysicalDevice.GetSurfaceFormats(surface);
            if ((surfaceFormats.Length != 1 || surfaceFormats[0].Format != Format.Undefined) &&
                !surfaceFormats.Any(x => x.Format == backBufferFormat))
            {
                backBufferFormat = surfaceFormats[0].Format;
            }

            // Create swapchain
            SurfaceCapabilities surfaceCapabilities;
            GraphicsDevice.NativePhysicalDevice.GetSurfaceCapabilities(surface, out surfaceCapabilities);

            // Buffer count
            uint desiredImageCount = Math.Max(surfaceCapabilities.MinImageCount, 4);
            if (surfaceCapabilities.MaxImageCount > 0 && desiredImageCount > surfaceCapabilities.MaxImageCount)
            {
                desiredImageCount = surfaceCapabilities.MaxImageCount;
            }

            // Transform
            SurfaceTransformFlags preTransform;
            if ((surfaceCapabilities.SupportedTransforms & SurfaceTransformFlags.Identity) != 0)
            {
                preTransform = SurfaceTransformFlags.Identity;
            }
            else
            {
                preTransform = surfaceCapabilities.CurrentTransform;
            }

            // Find present mode
            var swapChainPresentMode = PresentMode.Fifo; // Always supported, but slow
            if (Description.PresentationInterval == PresentInterval.Immediate) {
                var presentModes = GraphicsDevice.NativePhysicalDevice.GetSurfacePresentModes(surface);
                if (presentModes.Contains(PresentMode.Mailbox)) swapChainPresentMode = PresentMode.Mailbox;
            }

            // Create swapchain
            var swapchainCreateInfo = new SwapchainCreateInfo
            {
                StructureType = StructureType.SwapchainCreateInfo,
                Surface = surface,
                ImageArrayLayers = 1,
                ImageSharingMode = SharingMode.Exclusive,
                ImageExtent = new Extent2D((uint)Description.BackBufferWidth, (uint)Description.BackBufferHeight),
                ImageFormat = backBufferFormat,
                ImageColorSpace = Description.ColorSpace == ColorSpace.Gamma ? SharpVulkan.ColorSpace.SRgbNonlinear : 0,
                ImageUsage = ImageUsageFlags.ColorAttachment | ImageUsageFlags.TransferDestination | (surfaceCapabilities.SupportedUsageFlags & ImageUsageFlags.TransferSource), // TODO VULKAN: Use off-screen buffer to emulate
                PresentMode = swapChainPresentMode,
                CompositeAlpha = CompositeAlphaFlags.Opaque,
                MinImageCount = desiredImageCount,
                PreTransform = preTransform,
                OldSwapchain = Swapchain.Null,
                Clipped = true
            };

            swapChain = GraphicsDevice.NativeDevice.CreateSwapchain(ref swapchainCreateInfo);

            CreateBackBuffers();

            // resize/create stencil buffers
            var newTextureDescription = DepthStencilBuffer.Description;
            newTextureDescription.Width = Description.BackBufferWidth;
            newTextureDescription.Height = Description.BackBufferHeight;

            // Manually update the texture
            DepthStencilBuffer.OnDestroyed();

            // Put it in our back buffer texture
            DepthStencilBuffer.InitializeFrom(newTextureDescription);

            // start new presentation thread
            runPresenter = true;
            presenterThread = new Thread(new ThreadStart(PresenterThread));
            presenterThread.IsBackground = true;
            presenterThread.Name = "Vulkan Presentation Thread";
            presenterThread.Priority = ThreadPriority.AboveNormal;
            presenterThread.Start();
        }

        private unsafe void CreateSurface()
        {
            // Check for Window Handle parameter
            if (Description.DeviceWindowHandle == null)
            {
                throw new ArgumentException("DeviceWindowHandle cannot be null");
            }
            // Create surface
#if XENKO_UI_SDL
            var control = Description.DeviceWindowHandle.NativeWindow as SDL.Window;

            if (SDL2.SDL.SDL_Vulkan_CreateSurface(control.SdlHandle, GraphicsDevice.NativeInstance.NativeHandle, out ulong surfacePtr) == SDL2.SDL.SDL_bool.SDL_FALSE)
                throw new NotSupportedException("Couldn't create an SDL2 Vulkan surface! SdlHandle:" + control.SdlHandle + ", NativeHandle:" + GraphicsDevice.NativeInstance.NativeHandle);

            surface = new Surface(new IntPtr((long)surfacePtr));
#elif XENKO_PLATFORM_WINDOWS
            var controlHandle = Description.DeviceWindowHandle.Handle;
            if (controlHandle == IntPtr.Zero)
            {
                throw new NotSupportedException($"Form of type [{Description.DeviceWindowHandle.GetType().Name}] is not supported. Only System.Windows.Control are supported");
            }

            var surfaceCreateInfo = new Win32SurfaceCreateInfo
            {
                StructureType = StructureType.Win32SurfaceCreateInfo,
                InstanceHandle = Process.GetCurrentProcess().Handle,
                WindowHandle = controlHandle,
            };
            surface = GraphicsDevice.NativeInstance.CreateWin32Surface(surfaceCreateInfo);
#elif XENKO_PLATFORM_ANDROID
            throw new NotImplementedException();
#elif XENKO_PLATFORM_LINUX
            throw new NotSupportedException("Only SDL is supported for the time being on Linux");
#else
            throw new NotSupportedException();
#endif
        }

        private unsafe void CreateBackBuffers()
        {
            backbuffer.OnDestroyed();

            // Create the texture object
            var backBufferDescription = new TextureDescription
            {
                ArraySize = 1,
                Dimension = TextureDimension.Texture2D,
                Height = Description.BackBufferHeight,
                Width = Description.BackBufferWidth,
                Depth = 1,
                Flags = TextureFlags.RenderTarget,
                Format = Description.BackBufferFormat,
                MipLevels = 1,
                MultisampleCount = MultisampleCount.None,
                Usage = GraphicsResourceUsage.Default
            };
            backbuffer.InitializeWithoutResources(backBufferDescription);

            var createInfo = new ImageViewCreateInfo
            {
                StructureType = StructureType.ImageViewCreateInfo,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.Color, 0, 1, 0, 1),
                Format = backbuffer.NativeFormat,
                ViewType = ImageViewType.Image2D
            };

            // We initialize swapchain images to PresentSource, since we swap them out while in this layout.
            backbuffer.NativeAccessMask = AccessFlags.MemoryRead;
            backbuffer.NativeLayout = ImageLayout.PresentSource;

            var imageMemoryBarrier = new ImageMemoryBarrier
            {
                StructureType = StructureType.ImageMemoryBarrier,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.Color, 0, 1, 0, 1),
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.PresentSource,
                SourceAccessMask = AccessFlags.None,
                DestinationAccessMask = AccessFlags.MemoryRead
            };

            var commandBuffer = GraphicsDevice.NativeCopyCommandBuffer;
            var beginInfo = new CommandBufferBeginInfo { StructureType = StructureType.CommandBufferBeginInfo };
            commandBuffer.Begin(ref beginInfo);

            var buffers = GraphicsDevice.NativeDevice.GetSwapchainImages(swapChain);
            swapchainImages = new SwapChainImageInfo[buffers.Length];

            for (int i = 0; i < buffers.Length; i++)
            {
                // Create image views
                swapchainImages[i].NativeImage = createInfo.Image = buffers[i];
                swapchainImages[i].NativeColorAttachmentView = GraphicsDevice.NativeDevice.CreateImageView(ref createInfo);

                // Transition to default layout
                imageMemoryBarrier.Image = buffers[i];
                commandBuffer.PipelineBarrier(PipelineStageFlags.AllCommands, PipelineStageFlags.AllCommands, DependencyFlags.None, 0, null, 0, null, 1, &imageMemoryBarrier);
            }

            // Close and submit
            commandBuffer.End();

            var submitInfo = new SubmitInfo
            {
                StructureType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                CommandBuffers = new IntPtr(&commandBuffer),
            };
            GraphicsDevice.NativeCommandQueue.Submit(1, &submitInfo, Fence.Null);
            GraphicsDevice.NativeCommandQueue.WaitIdle();
            commandBuffer.Reset(CommandBufferResetFlags.None);
            
            // need to make a fence, but can immediately reset it, as it acts as a dummy
            var fenceCreateInfo = new FenceCreateInfo { StructureType = StructureType.FenceCreateInfo };
            presentFence = GraphicsDevice.NativeDevice.CreateFence(ref fenceCreateInfo);

            currentBufferIndex = GraphicsDevice.NativeDevice.AcquireNextImage(swapChain, ulong.MaxValue, SharpVulkan.Semaphore.Null, presentFence);

            fixed (Fence* fences = &presentFence)
            {
                GraphicsDevice.NativeDevice.ResetFences(1, fences);
            }

            // Apply the first swap chain image to the texture
            backbuffer.SetNativeHandles(swapchainImages[currentBufferIndex].NativeImage, swapchainImages[currentBufferIndex].NativeColorAttachmentView);
        }
    }
}
#endif
