// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
#if XENKO_GRAPHICS_API_VULKAN
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;
using System.Collections.Concurrent;

using Xenko.Core;
using Xenko.Core.Threading;

namespace Xenko.Graphics
{
    public partial class GraphicsDevice
    {
        internal int ConstantBufferDataPlacementAlignment;

        internal readonly ConcurrentPool<List<VkDescriptorPool>> DescriptorPoolLists = new ConcurrentPool<List<VkDescriptorPool>>(() => new List<VkDescriptorPool>());
        internal readonly ConcurrentPool<List<Texture>> StagingResourceLists = new ConcurrentPool<List<Texture>>(() => new List<Texture>());

        private const GraphicsPlatform GraphicPlatform = GraphicsPlatform.Vulkan;
        internal GraphicsProfile RequestedProfile;

        private bool simulateReset = false;
        private string rendererName;

        private VkDevice nativeDevice;
        internal VkQueue NativeCommandQueue;
        internal ReaderWriterLockSlim QueueLock = new ReaderWriterLockSlim();
        internal ReaderWriterLockSlim FenceLock = new ReaderWriterLockSlim();

        internal VkCommandPool NativeCopyCommandPool;
        internal VkCommandBuffer NativeCopyCommandBuffer;
        private NativeResourceCollector nativeResourceCollector;
        private GraphicsResourceLinkCollector graphicsResourceLinkCollector;

        private VkBuffer nativeUploadBuffer;
        private VkDeviceMemory nativeUploadBufferMemory;
        private IntPtr nativeUploadBufferStart;
        private int nativeUploadBufferSize;
        private int nativeUploadBufferOffset;

        private List<KeyValuePair<long, VkFence>> nativeFences = new List<KeyValuePair<long, VkFence>>();
        internal long lastCompletedFence;
        internal long NextFenceValue = 1;

        internal HeapPool DescriptorPools;
        internal const uint MaxDescriptorSetCount = 256;
        internal readonly uint[] MaxDescriptorTypeCounts = new uint[DescriptorSetLayout.DescriptorTypeCount]
        {
            256, // Sampler
            0, // CombinedImageSampler
            512, // SampledImage
            0, // StorageImage
            64, // UniformTexelBuffer
            0, // StorageTexelBuffer
            512, // UniformBuffer
            0, // StorageBuffer
            0, // UniformBufferDynamic
            0, // StorageBufferDynamic
            0 // InputAttachment
        };

        internal Buffer EmptyTexelBuffer;
        internal Texture EmptyTexture;

        internal VkPhysicalDevice NativePhysicalDevice => Adapter.GetPhysicalDevice(IsDebugMode);

        internal VkInstance NativeInstance => GraphicsAdapterFactory.GetInstance(IsDebugMode).NativeInstance;

        internal struct BufferInfo
        {
            public long FenceValue;

            public VkBuffer Buffer;

            public VkDeviceMemory Memory;

            public BufferInfo(long fenceValue, VkBuffer buffer, VkDeviceMemory memory)
            {
                FenceValue = fenceValue;
                Buffer = buffer;
                Memory = memory;
            }
        }

        /// <summary>
        /// The tick frquency of timestamp queries in Hertz.
        /// </summary>
        public long TimestampFrequency { get; private set; }

        /// <summary>
        ///     Gets the status of this device.
        /// </summary>
        /// <value>The graphics device status.</value>
        public GraphicsDeviceStatus GraphicsDeviceStatus
        {
            get
            {
                if (simulateReset)
                {
                    simulateReset = false;
                    return GraphicsDeviceStatus.Reset;
                }

                //var result = NativeDevice.DeviceRemovedReason;
                //if (result == SharpDX.DXGI.ResultCode.DeviceRemoved)
                //{
                //    return GraphicsDeviceStatus.Removed;
                //}

                //if (result == SharpDX.DXGI.ResultCode.DeviceReset)
                //{
                //    return GraphicsDeviceStatus.Reset;
                //}

                //if (result == SharpDX.DXGI.ResultCode.DeviceHung)
                //{
                //    return GraphicsDeviceStatus.Hung;
                //}

                //if (result == SharpDX.DXGI.ResultCode.DriverInternalError)
                //{
                //    return GraphicsDeviceStatus.InternalError;
                //}

                //if (result == SharpDX.DXGI.ResultCode.InvalidCall)
                //{
                //    return GraphicsDeviceStatus.InvalidCall;
                //}

                //if (result.Code < 0)
                //{
                //    return GraphicsDeviceStatus.Reset;
                //}

                return GraphicsDeviceStatus.Normal;
            }
        }

        /// <summary>
        ///     Gets the native device.
        /// </summary>
        /// <value>The native device.</value>
        internal VkDevice NativeDevice
        {
            get { return nativeDevice; }
        }

        /// <summary>
        ///     Marks context as active on the current thread.
        /// </summary>
        public void Begin()
        {
            FrameTriangleCount = 0;
            FrameDrawCalls = 0;
        }

        /// <summary>
        /// Enables profiling.
        /// </summary>
        /// <param name="enabledFlag">if set to <c>true</c> [enabled flag].</param>
        public void EnableProfile(bool enabledFlag)
        {
        }

        /// <summary>
        ///     Unmarks context as active on the current thread.
        /// </summary>
        public void End()
        {
            CleanupFences();
        }

        /// <summary>
        /// Executes a deferred command list.
        /// </summary>
        /// <param name="commandList">The deferred command list.</param>
        public void ExecuteCommandList(CompiledCommandList commandList)
        {
            ExecuteCommandListInternal(commandList);
        }

        /// <summary>
        /// Executes multiple deferred command lists.
        /// </summary>
        /// <param name="count">Number of command lists to execute.</param>
        /// <param name="commandLists">The deferred command lists.</param>
        public unsafe void ExecuteCommandLists(int count, CompiledCommandList[] commandLists)
        {
            if (commandLists == null) throw new ArgumentNullException(nameof(commandLists));
            if (count > commandLists.Length) throw new ArgumentOutOfRangeException(nameof(count));

            var fenceValue = NextFenceValue++;

            // Create a fence
            var fenceCreateInfo = new VkFenceCreateInfo { sType = VkStructureType.FenceCreateInfo };
            vkCreateFence(nativeDevice, &fenceCreateInfo, null, out var fence);

            using (FenceLock.WriteLock())
            {
                nativeFences.Add(new KeyValuePair<long, VkFence>(fenceValue, fence));
            }

            // Collect resources
            var commandBuffers = stackalloc VkCommandBuffer[count];
            for (int i = 0; i < count; i++)
            {
                commandBuffers[i] = commandLists[i].NativeCommandBuffer;
                RecycleCommandListResources(commandLists[i], fenceValue);
            }

            // Submit commands
            var pipelineStageFlags = VkPipelineStageFlags.BottomOfPipe;
            var submitInfo = new VkSubmitInfo
            {
                sType = VkStructureType.SubmitInfo,
                commandBufferCount = (uint)count,
                pCommandBuffers = commandBuffers,
                waitSemaphoreCount = 0U,
                pWaitSemaphores = null,
                pWaitDstStageMask = &pipelineStageFlags,
            };

            using (QueueLock.ReadLock())
            {
                vkQueueSubmit(NativeCommandQueue, 1, &submitInfo, fence);
            }

            nativeResourceCollector.Release();
            graphicsResourceLinkCollector.Release();
        }

        private void InitializePostFeatures()
        {
        }

        private string GetRendererName()
        {
            return rendererName;
        }

        public void SimulateReset()
        {
            simulateReset = true;
        }

        /// <summary>
        ///     Initializes the specified device.
        /// </summary>
        /// <param name="graphicsProfiles">The graphics profiles.</param>
        /// <param name="deviceCreationFlags">The device creation flags.</param>
        /// <param name="windowHandle">The window handle.</param>
        private unsafe void InitializePlatformDevice(GraphicsProfile[] graphicsProfiles, DeviceCreationFlags deviceCreationFlags, object windowHandle)
        {
            if (nativeDevice != VkDevice.Null)
            {
                // Destroy previous device
                ReleaseDevice();
            }

            rendererName = Adapter.Description;

            vkGetPhysicalDeviceProperties(NativePhysicalDevice, out var physicalDeviceProperties);
            ConstantBufferDataPlacementAlignment = (int)physicalDeviceProperties.limits.minUniformBufferOffsetAlignment;
            TimestampFrequency = (long)(1.0e9 / physicalDeviceProperties.limits.timestampPeriod); // Resolution in nanoseconds

            RequestedProfile = graphicsProfiles.Last();

            var queueProperties = vkGetPhysicalDeviceQueueFamilyProperties(NativePhysicalDevice);
            //IsProfilingSupported = queueProperties[0].TimestampValidBits > 0;

            // Command lists are thread-safe and execute deferred
            IsDeferred = true;

            // TODO VULKAN
            // Create Vulkan device based on profile
            float queuePriorities = 0;
            var queueCreateInfo = new VkDeviceQueueCreateInfo
            {
                sType = VkStructureType.DeviceQueueCreateInfo,
                queueFamilyIndex = 0,
                queueCount = 1,
                pQueuePriorities = &queuePriorities,
            };

            vkGetPhysicalDeviceFeatures(NativePhysicalDevice, out VkPhysicalDeviceFeatures features);

            var enabledFeature = new VkPhysicalDeviceFeatures
            {
                fillModeNonSolid = features.fillModeNonSolid,
                shaderClipDistance = features.shaderClipDistance,
                shaderCullDistance = features.shaderCullDistance,
                samplerAnisotropy = features.samplerAnisotropy,
                depthClamp = features.depthClamp,
            };

            var extensionProperties = vkEnumerateDeviceExtensionProperties(NativePhysicalDevice);
            var availableExtensionNames = new List<string>();
            var desiredExtensionNames = new List<string>();

            for (int index = 0; index < extensionProperties.Length; index++)
            {
                fixed (VkExtensionProperties* extensionPropertiesPtr = extensionProperties)
                {
                    var namePointer = new IntPtr(extensionPropertiesPtr[index].extensionName);
                    var name = Marshal.PtrToStringAnsi(namePointer);
                    availableExtensionNames.Add(name);
                }
            }

            desiredExtensionNames.Add("VK_KHR_swapchain");
            if (!availableExtensionNames.Contains("VK_KHR_swapchain"))
                throw new InvalidOperationException();

            if (availableExtensionNames.Contains("VK_EXT_debug_marker") && IsDebugMode)
            {
                desiredExtensionNames.Add("VK_EXT_debug_marker");
                IsProfilingSupported = true;
            }

            var enabledExtensionNames = desiredExtensionNames.Select(Marshal.StringToHGlobalAnsi).ToArray();

            try
            {
                var deviceCreateInfo = new VkDeviceCreateInfo
                {
                    sType = VkStructureType.DeviceCreateInfo,
                    queueCreateInfoCount = 1,
                    pQueueCreateInfos = &queueCreateInfo,
                    enabledExtensionCount = (uint)enabledExtensionNames.Length,
                    ppEnabledExtensionNames = enabledExtensionNames.Length > 0 ? (byte**)Core.Interop.Fixed(enabledExtensionNames) : null,
                    pEnabledFeatures = &enabledFeature,
                };

                vkCreateDevice(NativePhysicalDevice, &deviceCreateInfo, null, out nativeDevice);
            }
            finally
            {
                foreach (var enabledExtensionName in enabledExtensionNames)
                {
                    Marshal.FreeHGlobal(enabledExtensionName);
                }
            }

            vkGetDeviceQueue(nativeDevice, 0, 0, out NativeCommandQueue);

            //// Prepare copy command list (start it closed, so that every new use start with a Reset)
            var commandPoolCreateInfo = new VkCommandPoolCreateInfo
            {
                sType = VkStructureType.CommandPoolCreateInfo,
                queueFamilyIndex = 0, //device.NativeCommandQueue.FamilyIndex
                flags = VkCommandPoolCreateFlags.ResetCommandBuffer
            };
            vkCreateCommandPool(NativeDevice, &commandPoolCreateInfo, null, out NativeCopyCommandPool);

            var commandBufferAllocationInfo = new VkCommandBufferAllocateInfo
            {
                sType = VkStructureType.CommandBufferAllocateInfo,
                level = VkCommandBufferLevel.Primary,
                commandPool = NativeCopyCommandPool,
                commandBufferCount = 1
            };
            VkCommandBuffer nativeCommandBuffer;
            vkAllocateCommandBuffers(NativeDevice, &commandBufferAllocationInfo, &nativeCommandBuffer);
            NativeCopyCommandBuffer = nativeCommandBuffer;

            DescriptorPools = new HeapPool(this);

            nativeResourceCollector = new NativeResourceCollector(this);
            graphicsResourceLinkCollector = new GraphicsResourceLinkCollector(this);

            EmptyTexelBuffer = Buffer.Typed.New(this, 1, PixelFormat.R32G32B32A32_Float);
            EmptyTexture = Texture.New2D(this, 1, 1, PixelFormat.R8G8B8A8_UNorm_SRgb, TextureFlags.ShaderResource);
        }

        public struct UploadBuffer
        {
            public IntPtr address;
            public VkDeviceMemory memory;
            public VkBuffer buffer;
            public int size;
        }

        internal uint properType = uint.MaxValue;
        internal unsafe void AllocateOneTimeUploadBuffer(int size, out UploadBuffer uploadBuffer)
        {
            var bufferCreateInfo = new VkBufferCreateInfo
            {
                sType = VkStructureType.BufferCreateInfo,
                size = (ulong)size,
                flags = VkBufferCreateFlags.None,
                usage = VkBufferUsageFlags.TransferSrc,
            };

            uploadBuffer = new UploadBuffer
            {
                size = size
            };

            vkCreateBuffer(NativeDevice, &bufferCreateInfo, null, out uploadBuffer.buffer);

            // figure out the memory type
            if (properType == uint.MaxValue)
            {
                VkMemoryPropertyFlags memoryProperties = VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent;
                vkGetBufferMemoryRequirements(nativeDevice, uploadBuffer.buffer, out var memoryRequirements);
                vkGetPhysicalDeviceMemoryProperties(NativePhysicalDevice, out var physicalDeviceMemoryProperties);
                var typeBits = memoryRequirements.memoryTypeBits;
                for (uint i = 0; i < physicalDeviceMemoryProperties.memoryTypeCount; i++)
                {
                    if ((typeBits & 1) == 1)
                    {
                        // Type is available, does it match user properties?
                        var memoryType = *(&physicalDeviceMemoryProperties.memoryTypes_0 + i);
                        if ((memoryType.propertyFlags & memoryProperties) == memoryProperties)
                        {
                            properType = i;
                            break;
                        }
                    }
                    typeBits >>= 1;
                }
            }

            var allocateInfo = new VkMemoryAllocateInfo
            {
                sType = VkStructureType.MemoryAllocateInfo,
                allocationSize = (ulong)size,
                memoryTypeIndex = properType
            };

            fixed (VkDeviceMemory* nativeUploadBufferMemoryPtr = &uploadBuffer.memory)
                vkAllocateMemory(NativeDevice, &allocateInfo, null, nativeUploadBufferMemoryPtr);

            vkBindBufferMemory(NativeDevice, uploadBuffer.buffer, uploadBuffer.memory, 0);

            fixed (IntPtr* nativeUploadBufferStartPtr = &uploadBuffer.address)
                vkMapMemory(NativeDevice, uploadBuffer.memory, 0, (ulong)size, VkMemoryMapFlags.None, (void**)nativeUploadBufferStartPtr);
        }

        internal unsafe void FreeOneTimeUploadBuffer(UploadBuffer uploadBuffer)
        {
            vkUnmapMemory(NativeDevice, uploadBuffer.memory);
            vkFreeMemory(NativeDevice, uploadBuffer.memory, null);
            vkDestroyBuffer(NativeDevice, uploadBuffer.buffer, null);
        }

        internal unsafe IntPtr AllocateUploadBuffer(int size, out VkBuffer resource, out int offset)
        {
            if (nativeUploadBuffer == VkBuffer.Null || nativeUploadBufferOffset + size > nativeUploadBufferSize)
            {
                if (nativeUploadBuffer != VkBuffer.Null)
                {
                    vkUnmapMemory(NativeDevice, nativeUploadBufferMemory);
                    Collect(nativeUploadBuffer);
                    Collect(nativeUploadBufferMemory);
                }

                // Allocate new buffer
                // TODO D3D12 recycle old ones (using fences to know when GPU is done with them)
                // TODO D3D12 ResourceStates.CopySource not working?
                nativeUploadBufferSize = Math.Max(4 * 1024 * 1024, size);

                var bufferCreateInfo = new VkBufferCreateInfo
                {
                    sType = VkStructureType.BufferCreateInfo,
                    size = (ulong)nativeUploadBufferSize,
                    flags = VkBufferCreateFlags.None,
                    usage = VkBufferUsageFlags.TransferSrc,
                };
                vkCreateBuffer(NativeDevice, &bufferCreateInfo, null, out nativeUploadBuffer);
                AllocateMemory(VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);

                fixed (IntPtr* nativeUploadBufferStartPtr = &nativeUploadBufferStart)
                    vkMapMemory(NativeDevice, nativeUploadBufferMemory, 0, (ulong)nativeUploadBufferSize, VkMemoryMapFlags.None, (void**)nativeUploadBufferStartPtr);
                nativeUploadBufferOffset = 0;
            }

            // Bump allocate
            resource = nativeUploadBuffer;
            offset = nativeUploadBufferOffset;
            nativeUploadBufferOffset += size;
            return nativeUploadBufferStart + offset;
        }

        protected unsafe void AllocateMemory(VkMemoryPropertyFlags memoryProperties)
        {
            vkGetBufferMemoryRequirements(nativeDevice, nativeUploadBuffer, out var memoryRequirements);

            if (memoryRequirements.size == 0)
                return;

            var allocateInfo = new VkMemoryAllocateInfo
            {
                sType = VkStructureType.MemoryAllocateInfo,
                allocationSize = memoryRequirements.size,
            };

            vkGetPhysicalDeviceMemoryProperties(NativePhysicalDevice, out var physicalDeviceMemoryProperties);
            var typeBits = memoryRequirements.memoryTypeBits;
            for (uint i = 0; i < physicalDeviceMemoryProperties.memoryTypeCount; i++)
            {
                if ((typeBits & 1) == 1)
                {
                    // Type is available, does it match user properties?
                    var memoryType = *(&physicalDeviceMemoryProperties.memoryTypes_0 + i);
                    if ((memoryType.propertyFlags & memoryProperties) == memoryProperties)
                    {
                        allocateInfo.memoryTypeIndex = i;
                        break;
                    }
                }
                typeBits >>= 1;
            }

            fixed (VkDeviceMemory* nativeUploadBufferMemoryPtr = &nativeUploadBufferMemory)
                vkAllocateMemory(NativeDevice, &allocateInfo, null, nativeUploadBufferMemoryPtr);

            vkBindBufferMemory(NativeDevice, nativeUploadBuffer, nativeUploadBufferMemory, 0);
        }

        private void AdjustDefaultPipelineStateDescription(ref PipelineStateDescription pipelineStateDescription)
        {
        }

        protected void DestroyPlatformDevice()
        {
            ReleaseDevice();
        }

        private unsafe void ReleaseDevice()
        {
            EmptyTexelBuffer.Dispose();
            EmptyTexelBuffer = null;

            EmptyTexture.Dispose();
            EmptyTexture = null;

            // Wait for all queues to be idle
            vkDeviceWaitIdle(nativeDevice);

            // Destroy all remaining fences
            GetCompletedValue();

            // Mark upload buffer for destruction
            if (nativeUploadBuffer != VkBuffer.Null)
            {
                vkUnmapMemory(NativeDevice, nativeUploadBufferMemory);
                nativeResourceCollector.Add(lastCompletedFence, nativeUploadBuffer);
                nativeResourceCollector.Add(lastCompletedFence, nativeUploadBufferMemory);

                nativeUploadBuffer = VkBuffer.Null;
                nativeUploadBufferMemory = VkDeviceMemory.Null;
            }

            // Release fenced resources
            nativeResourceCollector.Dispose();
            DescriptorPools.Dispose();

            vkDestroyCommandPool(nativeDevice, NativeCopyCommandPool, null);
            vkDestroyDevice(nativeDevice, null);
        }

        internal void OnDestroyed()
        {
        }

        internal unsafe long ExecuteCommandListInternal(CompiledCommandList commandList)
        {
            //if (nativeUploadBuffer != VkBuffer.Null)
            //{
            //    NativeDevice.UnmapMemory(nativeUploadBufferMemory);
            //    TemporaryResources.Enqueue(new BufferInfo(NextFenceValue, nativeUploadBuffer, nativeUploadBufferMemory));

            //    nativeUploadBuffer = VkBuffer.Null;
            //    nativeUploadBufferMemory = VkDeviceMemory.Null;
            //}

            var fenceValue = NextFenceValue++;

            // Create new fence
            var fenceCreateInfo = new VkFenceCreateInfo { sType = VkStructureType.FenceCreateInfo };
            vkCreateFence(nativeDevice, &fenceCreateInfo, null, out var fence);

            using (FenceLock.WriteLock())
            {
                nativeFences.Add(new KeyValuePair<long, VkFence>(fenceValue, fence));
            }

            // Collect resources
            RecycleCommandListResources(commandList, fenceValue);

            // Submit commands
            var nativeCommandBufferCopy = commandList.NativeCommandBuffer;
            var pipelineStageFlags = VkPipelineStageFlags.BottomOfPipe;

            var submitInfo = new VkSubmitInfo
            {
                sType = VkStructureType.SubmitInfo,
                commandBufferCount = 1,
                pCommandBuffers = &nativeCommandBufferCopy,
                waitSemaphoreCount = 0U,
                pWaitSemaphores = null,
                pWaitDstStageMask = &pipelineStageFlags,
            };

            using (QueueLock.ReadLock())
            {
                vkQueueSubmit(NativeCommandQueue, 1, &submitInfo, fence);
            }

            nativeResourceCollector.Release();
            graphicsResourceLinkCollector.Release();

            return fenceValue;
        }

        private void RecycleCommandListResources(CompiledCommandList commandList, long fenceValue)
        {
            // Set fence on staging textures
            foreach (var stagingResource in commandList.StagingResources)
            {
                stagingResource.StagingFenceValue = fenceValue;
            }

            StagingResourceLists.Release(commandList.StagingResources);
            commandList.StagingResources.Clear();

            // Recycle all resources
            foreach (var descriptorPool in commandList.DescriptorPools)
            {
                DescriptorPools.RecycleObject(fenceValue, descriptorPool);
            }
            DescriptorPoolLists.Release(commandList.DescriptorPools);
            commandList.DescriptorPools.Clear();

            commandList.Builder.CommandBufferPool.RecycleObject(fenceValue, commandList.NativeCommandBuffer);
        }

        internal bool IsFenceCompleteInternal(long fenceValue)
        {
            // Try to avoid checking the fence if possible
            if (fenceValue > lastCompletedFence)
            {
                GetCompletedValue();
            }

            return fenceValue <= lastCompletedFence;
        }

        internal Queue<VkFence> fencesToDestroy = new Queue<VkFence>();
        internal unsafe void CleanupFences()
        {
            using (FenceLock.WriteLock())
            {
                for (int i=0; i<nativeFences.Count; i++)
                {
                    if (nativeFences[i].Key <= lastCompletedFence)
                    {
                        fencesToDestroy.Enqueue(nativeFences[i].Value);
                        nativeFences.RemoveAt(i);
                        i--;
                    }
                }
            }

            while (fencesToDestroy.Count > 0)
                vkDestroyFence(NativeDevice, fencesToDestroy.Dequeue(), null);
        }

        internal unsafe long GetCompletedValue()
        {
            using (FenceLock.ReadLock())
            {
                for (int i=0; i<nativeFences.Count; i++)
                {
                    if (nativeFences[i].Key <= lastCompletedFence)
                        continue;

                    switch (vkGetFenceStatus(NativeDevice, nativeFences[i].Value))
                    {
                        default:
                            return lastCompletedFence;
                        case VkResult.ErrorDeviceLost:
                            throw new Exception("Vulkan device lost while checking GetCompletedValue()!");
                        case VkResult.Success:
                            if (nativeFences[i].Key > lastCompletedFence)
                                lastCompletedFence = nativeFences[i].Key;
                            break;
                    }                    
                }

                return lastCompletedFence;
            }
        }

        internal unsafe void WaitForFenceInternal(long fenceValue)
        {
            if (IsFenceCompleteInternal(fenceValue))
                return;

            using (FenceLock.ReadLock())
            {
                for (int i=0; i<nativeFences.Count; i++)
                {
                    if (nativeFences[i].Key <= fenceValue)
                    {
                        var fenceCopy = nativeFences[i].Value;

                        vkWaitForFences(NativeDevice, 1, &fenceCopy, true, ulong.MaxValue);

                        if (fenceValue > lastCompletedFence)
                            lastCompletedFence = fenceValue;
                    }
                }

            }
        }

        internal void Collect(NativeResource nativeResource)
        {
            nativeResourceCollector.Add(NextFenceValue, nativeResource);
        }

        internal void TagResource(GraphicsResourceLink resourceLink)
        {
            switch (resourceLink.Resource)
            {
                case Texture texture:
                    if (texture.Usage == GraphicsResourceUsage.Dynamic)
                    {
                        // Increase the reference count until GPU is done with the resource
                        resourceLink.ReferenceCount++;
                        graphicsResourceLinkCollector.Add(NextFenceValue, resourceLink);
                    }
                    break;

                case Buffer buffer:
                    if (buffer.Usage == GraphicsResourceUsage.Dynamic)
                    {
                        // Increase the reference count until GPU is done with the resource
                        resourceLink.ReferenceCount++;
                        graphicsResourceLinkCollector.Add(NextFenceValue, resourceLink);
                    }
                    break;

                case QueryPool _:
                    resourceLink.ReferenceCount++;
                    graphicsResourceLinkCollector.Add(NextFenceValue, resourceLink);
                    break;
            }
        }
    }

   internal abstract class ResourcePool<T> : ComponentBase
    {
        private const int CHECK_COMPLETED_INTERVAL = 8;

        protected readonly GraphicsDevice GraphicsDevice;
        private int checkCompleted;
        private SpinLock spinLock = new SpinLock();
        private readonly Queue<KeyValuePair<long, T>> liveObjects = new Queue<KeyValuePair<long, T>>();

        protected ResourcePool(GraphicsDevice graphicsDevice)
        {
            GraphicsDevice = graphicsDevice;
            checkCompleted = CHECK_COMPLETED_INTERVAL;
        }

        public T GetObject()
        {
            KeyValuePair<long, T>? firstAllocator = null;

            if (Interlocked.Increment(ref checkCompleted) >= CHECK_COMPLETED_INTERVAL)
            {
                checkCompleted = 0;

                // this check is slow, so only do it occassionally
                // this may cause more objects to get created, but that is fine
                // more things will come safely off the queue when this updates
                GraphicsDevice.GetCompletedValue();
            }

            bool lockTaken = false;
            try
            {
                spinLock.Enter(ref lockTaken);

                if (liveObjects.Count > 0)
                {
                    firstAllocator = liveObjects.Peek();

                    if (firstAllocator.Value.Key <= GraphicsDevice.lastCompletedFence)
                    {
                        liveObjects.Dequeue();
                    }
                    else
                    {
                        firstAllocator = null;
                    }
                }
            }
            finally
            {
                if (lockTaken)
                    spinLock.Exit(true);
            }

            if (firstAllocator.HasValue)
            {
                ResetObject(firstAllocator.Value.Value);
                return firstAllocator.Value.Value;
            }

            return CreateObject();
        }

        public void RecycleObject(long fenceValue, T obj)
        {
            bool lockTaken = false;
            try
            {
                spinLock.Enter(ref lockTaken);

                liveObjects.Enqueue(new KeyValuePair<long, T>(fenceValue, obj));
            }
            finally
            {
                if (lockTaken)
                    spinLock.Exit(true);
            }
        }

        protected abstract T CreateObject();

        protected abstract void ResetObject(T obj);

        protected virtual void DestroyObject(T obj)
        {
        }

        public void ResetAll() {
            while (true)
            {
                KeyValuePair<long, T>? toremove = null;

                bool lockTaken = false;
                try
                {
                    spinLock.Enter(ref lockTaken);

                    if (liveObjects.Count == 0) return;

                    toremove = liveObjects.Dequeue();
                }
                finally
                {
                    if (lockTaken)
                        spinLock.Exit(true);
                }

                DestroyObject(toremove.Value.Value);
            }
        }

        protected override void Destroy()
        {
            ResetAll();
            base.Destroy();
        }
    }

    internal class CommandBufferPool : ResourcePool<VkCommandBuffer>
    {
        private readonly VkCommandPool commandPool;

        public unsafe CommandBufferPool(GraphicsDevice graphicsDevice) : base(graphicsDevice)
        {
            var commandPoolCreateInfo = new VkCommandPoolCreateInfo
            {
                sType = VkStructureType.CommandPoolCreateInfo,
                queueFamilyIndex = 0, //device.NativeCommandQueue.FamilyIndex
                flags = VkCommandPoolCreateFlags.ResetCommandBuffer
            };

            vkCreateCommandPool(graphicsDevice.NativeDevice, &commandPoolCreateInfo, null, out commandPool);
        }

        protected override unsafe VkCommandBuffer CreateObject()
        {
            // No allocator ready to be used, let's create a new one
            var commandBufferAllocationInfo = new VkCommandBufferAllocateInfo
            {
                sType = VkStructureType.CommandBufferAllocateInfo,
                level = VkCommandBufferLevel.Primary,
                commandPool = commandPool,
                commandBufferCount = 1,
            };

            VkCommandBuffer commandBuffer;
            vkAllocateCommandBuffers(GraphicsDevice.NativeDevice, &commandBufferAllocationInfo, &commandBuffer);
            return commandBuffer;
        }

        protected override void ResetObject(VkCommandBuffer obj)
        {
            vkResetCommandBuffer(obj, VkCommandBufferResetFlags.None);
        }

        protected override unsafe void Destroy()
        {
            base.Destroy();

            vkDestroyCommandPool(GraphicsDevice.NativeDevice, commandPool, null);
        }
    }

    internal class HeapPool : ResourcePool<VkDescriptorPool>
    {
        public HeapPool(GraphicsDevice graphicsDevice) : base(graphicsDevice)
        {
        }

        protected override unsafe VkDescriptorPool CreateObject()
        {
            // No allocator ready to be used, let's create a new one
            var poolSizes = GraphicsDevice.MaxDescriptorTypeCounts
                .Select((count, index) => new VkDescriptorPoolSize { type = (VkDescriptorType)index, descriptorCount = count })
                .Where(size => size.descriptorCount > 0)
                .ToArray();

            var descriptorPoolCreateInfo = new VkDescriptorPoolCreateInfo
            {
                sType = VkStructureType.DescriptorPoolCreateInfo,
                poolSizeCount = (uint)poolSizes.Length,
                pPoolSizes = (VkDescriptorPoolSize*)Core.Interop.Fixed(poolSizes),
                maxSets = GraphicsDevice.MaxDescriptorSetCount,
            };
            vkCreateDescriptorPool(GraphicsDevice.NativeDevice, &descriptorPoolCreateInfo, null, out var descriptorPool);
            return descriptorPool;
        }

        protected override void ResetObject(VkDescriptorPool obj)
        {
            vkResetDescriptorPool(GraphicsDevice.NativeDevice, obj, VkDescriptorPoolResetFlags.None);
        }

        protected override unsafe void DestroyObject(VkDescriptorPool obj)
        {
            vkDestroyDescriptorPool(GraphicsDevice.NativeDevice, obj, null);
        }
    }

    internal struct NativeResource : IEquatable<NativeResource>
    {
        public VkDebugReportObjectTypeEXT type;

        public ulong handle;

        public bool Equals(NativeResource other) {
            return handle == other.handle;
        }

        public override bool Equals(object other) {
            if (other == null || other is NativeResource == false) return false;
            return Equals((NativeResource)other);
        }

        public override int GetHashCode() {
            return handle.GetHashCode();
        }

        public NativeResource(VkDebugReportObjectTypeEXT type, ulong handle)
        {
            this.type = type;
            this.handle = handle;
        }

        public static unsafe implicit operator NativeResource(VkBuffer handle)
        {
            return new NativeResource(VkDebugReportObjectTypeEXT.BufferEXT, *(ulong*)&handle);
        }

        public static unsafe implicit operator NativeResource(VkBufferView handle)
        {
            return new NativeResource(VkDebugReportObjectTypeEXT.BufferViewEXT, *(ulong*)&handle);
        }

        public static unsafe implicit operator NativeResource(VkImage handle)
        {
            return new NativeResource(VkDebugReportObjectTypeEXT.ImageEXT, *(ulong*)&handle);
        }

        public static unsafe implicit operator NativeResource(VkImageView handle)
        {
            return new NativeResource(VkDebugReportObjectTypeEXT.ImageViewEXT, *(ulong*)&handle);
        }

        public static unsafe implicit operator NativeResource(VkDeviceMemory handle)
        {
            return new NativeResource(VkDebugReportObjectTypeEXT.DeviceMemoryEXT, *(ulong*)&handle);
        }

        public static unsafe implicit operator NativeResource(VkSampler handle)
        {
            return new NativeResource(VkDebugReportObjectTypeEXT.SamplerEXT, *(ulong*)&handle);
        }

        public static unsafe implicit operator NativeResource(VkFramebuffer handle)
        {
            return new NativeResource(VkDebugReportObjectTypeEXT.FramebufferEXT, *(ulong*)&handle);
        }

        public static unsafe implicit operator NativeResource(VkSemaphore handle)
        {
            return new NativeResource(VkDebugReportObjectTypeEXT.SemaphoreEXT, *(ulong*)&handle);
        }

        public static unsafe implicit operator NativeResource(VkFence handle)
        {
            return new NativeResource(VkDebugReportObjectTypeEXT.FenceEXT, *(ulong*)&handle);
        }

        public static unsafe implicit operator NativeResource(VkQueryPool handle)
        {
            return new NativeResource(VkDebugReportObjectTypeEXT.QueryPoolEXT, *(ulong*)&handle);
        }

        public unsafe void Destroy(GraphicsDevice device)
        {
            var handleCopy = handle;

            switch (type)
            {
                case VkDebugReportObjectTypeEXT.BufferEXT:
                    vkDestroyBuffer(device.NativeDevice, *(VkBuffer*)&handleCopy, null);
                    break;
                case VkDebugReportObjectTypeEXT.BufferViewEXT:
                    vkDestroyBufferView(device.NativeDevice, *(VkBufferView*)&handleCopy, null);
                    break;
                case VkDebugReportObjectTypeEXT.ImageEXT:
                    vkDestroyImage(device.NativeDevice, *(VkImage*)&handleCopy, null);
                    break;
                case VkDebugReportObjectTypeEXT.ImageViewEXT:
                    vkDestroyImageView(device.NativeDevice, *(VkImageView*)&handleCopy, null);
                    break;
                case VkDebugReportObjectTypeEXT.DeviceMemoryEXT:
                    vkFreeMemory(device.NativeDevice, *(VkDeviceMemory*)&handleCopy, null);
                    break;
                case VkDebugReportObjectTypeEXT.SamplerEXT:
                    vkDestroySampler(device.NativeDevice, *(VkSampler*)&handleCopy, null);
                    break;
                case VkDebugReportObjectTypeEXT.FramebufferEXT:
                    vkDestroyFramebuffer(device.NativeDevice, *(VkFramebuffer*)&handleCopy, null);
                    break;
                case VkDebugReportObjectTypeEXT.SemaphoreEXT:
                    vkDestroySemaphore(device.NativeDevice, *(VkSemaphore*)&handleCopy, null);
                    break;
                case VkDebugReportObjectTypeEXT.FenceEXT:
                    vkDestroyFence(device.NativeDevice, *(VkFence*)&handleCopy, null);
                    break;
                case VkDebugReportObjectTypeEXT.QueryPoolEXT:
                    vkDestroyQueryPool(device.NativeDevice, *(VkQueryPool*)&handleCopy, null);
                    break;
            }
        }
    }

    internal class GraphicsResourceLinkCollector : TemporaryResourceCollector<GraphicsResourceLink>
    {
        public GraphicsResourceLinkCollector(GraphicsDevice graphicsDevice) : base(graphicsDevice)
        {
        }

        protected override void ReleaseObject(GraphicsResourceLink item)
        {
            item.ReferenceCount--;
        }
    }

    internal class NativeResourceCollector : TemporaryResourceCollector<NativeResource>
    {
        public NativeResourceCollector(GraphicsDevice graphicsDevice) : base(graphicsDevice)
        {
        }

        protected override void ReleaseObject(NativeResource item)
        {
            item.Destroy(GraphicsDevice);
        }
    }
    
    internal abstract class TemporaryResourceCollector<T> : IDisposable
    {
        struct TempResource : IEquatable<TempResource> {
            public T obj;
            public int hash;
            public long fence;

            public bool Equals(TempResource other) {
                return other.GetHashCode() == GetHashCode();
            }

            public override bool Equals(object other) {
                if (other == null || other is TempResource == false) return false;
                return Equals((TempResource)other);
            }

            public override int GetHashCode() {
                if (hash == 0) hash = obj.GetHashCode();
                return hash;
            }
        }

        protected readonly GraphicsDevice GraphicsDevice;
        private readonly HashSet<TempResource> items = new HashSet<TempResource>();

        protected TemporaryResourceCollector(GraphicsDevice graphicsDevice)
        {
            GraphicsDevice = graphicsDevice;
        }

        public void Add(long fenceValue, T item)
        {
            TempResource tr = new TempResource();
            tr.obj = item;
            tr.fence = fenceValue;
            lock (items) {
                items.Add(tr);
            }
        }

        public void Release()
        {
            lock (items) {
                items.RemoveWhere(RemoveDone);
            }
        }

        private bool RemoveDone(TempResource tr) {
            if (GraphicsDevice.IsFenceCompleteInternal(tr.fence)) {
                ReleaseObject(tr.obj);
                return true;
            }            
            return false;
        }

        protected abstract void ReleaseObject(T item);

        public void Dispose()
        {
            foreach (TempResource tr in items) {
                ReleaseObject(tr.obj);
            }
            items.Clear();
        }
    }
}
#endif
