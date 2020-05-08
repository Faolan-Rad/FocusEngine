// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
#if XENKO_GRAPHICS_API_VULKAN
using System;
using System.Collections.Generic;
using System.Linq;
using SharpVulkan;
using System.Runtime.ExceptionServices;
using System.Security;
using Xenko.Core;
using Xenko.Core.Threading;
using Xenko.Core.Collections;
using Xenko.Core.Serialization;
using Xenko.Shaders;
using System.Threading;
using Encoding = System.Text.Encoding;

namespace Xenko.Graphics
{
    public partial class PipelineState
    {
        internal SharpVulkan.DescriptorSetLayout NativeDescriptorSetLayout;
        internal uint[] DescriptorTypeCounts;
        internal DescriptorSetLayout DescriptorSetLayout;

        internal bool errorDuringCreate;
        internal PipelineLayout NativeLayout;
        internal Pipeline NativePipeline = Pipeline.Null;
        internal RenderPass NativeRenderPass;
        internal int[] ResourceGroupMapping;
        internal int ResourceGroupCount;
        internal PipelineStateDescription Description;

        // GLSL converter always outputs entry point main()
        private static readonly byte[] defaultEntryPoint = Encoding.UTF8.GetBytes("main\0");

        // State exposed by the CommandList
        private static readonly DynamicState[] dynamicStates =
        {
            DynamicState.Viewport,
            DynamicState.Scissor,
            DynamicState.BlendConstants,
            DynamicState.StencilReference,
        };

        public PIPELINE_STATE CurrentState() {
            if (errorDuringCreate) return PIPELINE_STATE.ERROR;
            return NativePipeline != Pipeline.Null ? PIPELINE_STATE.READY : PIPELINE_STATE.LOADING;
        }

        internal PipelineState(GraphicsDevice graphicsDevice) : base(graphicsDevice) {
            // just return a memory address to Prepare later
        }

        internal void Prepare(PipelineStateDescription pipelineStateDescription)
        {

            Description = pipelineStateDescription.Clone();
            Recreate();
        }

        //[HandleProcessCorruptedStateExceptionsAttribute, SecurityCriticalAttribute]
        private unsafe void Recreate()
        {
            errorDuringCreate = false;

            if (Description.RootSignature == null)
                return;

            PipelineShaderStageCreateInfo[] stages;

            // create render pass
            bool hasDepthStencilAttachment = Description.Output.DepthStencilFormat != PixelFormat.None;

            var renderTargetCount = Description.Output.RenderTargetCount;

            var attachmentCount = renderTargetCount;
            if (hasDepthStencilAttachment)
                attachmentCount++;

            var attachments = new AttachmentDescription[attachmentCount];
            var colorAttachmentReferences = new AttachmentReference[renderTargetCount];

            fixed (PixelFormat* renderTargetFormat = &Description.Output.RenderTargetFormat0)
            fixed (BlendStateRenderTargetDescription* blendDescription = &Description.BlendState.RenderTarget0)
            {
                for (int i = 0; i < renderTargetCount; i++)
                {
                    var currentBlendDesc = Description.BlendState.IndependentBlendEnable ? (blendDescription + i) : blendDescription;

                    attachments[i] = new AttachmentDescription
                    {
                        Format = VulkanConvertExtensions.ConvertPixelFormat(*(renderTargetFormat + i)),
                        Samples = SampleCountFlags.Sample1,
                        LoadOperation = currentBlendDesc->BlendEnable ? AttachmentLoadOperation.Load : AttachmentLoadOperation.DontCare, // TODO VULKAN: Only if any destination blend?
                        StoreOperation = AttachmentStoreOperation.Store,
                        StencilLoadOperation = AttachmentLoadOperation.DontCare,
                        StencilStoreOperation = AttachmentStoreOperation.DontCare,
                        InitialLayout = ImageLayout.ColorAttachmentOptimal,
                        FinalLayout = ImageLayout.ColorAttachmentOptimal,
                    };

                    colorAttachmentReferences[i] = new AttachmentReference
                    {
                        Attachment = (uint)i,
                        Layout = ImageLayout.ColorAttachmentOptimal,
                    };
                }
            }

            if (hasDepthStencilAttachment)
            {
                attachments[attachmentCount - 1] = new AttachmentDescription
                {
                    Format = Texture.GetFallbackDepthStencilFormat(GraphicsDevice, VulkanConvertExtensions.ConvertPixelFormat(Description.Output.DepthStencilFormat)),
                    Samples = SampleCountFlags.Sample1,
                    LoadOperation = AttachmentLoadOperation.Load, // TODO VULKAN: Only if depth read enabled?
                    StoreOperation = AttachmentStoreOperation.Store, // TODO VULKAN: Only if depth write enabled?
                    StencilLoadOperation = AttachmentLoadOperation.DontCare, // TODO VULKAN: Handle stencil
                    StencilStoreOperation = AttachmentStoreOperation.DontCare,
                    InitialLayout = ImageLayout.DepthStencilAttachmentOptimal,
                    FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
                };
            }

            var depthAttachmentReference = new AttachmentReference
            {
                Attachment = (uint)attachments.Length - 1,
                Layout = ImageLayout.DepthStencilAttachmentOptimal,
            };

            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = (uint)renderTargetCount,
                ColorAttachments = colorAttachmentReferences.Length > 0 ? new IntPtr(Interop.Fixed(colorAttachmentReferences)) : IntPtr.Zero,
                DepthStencilAttachment = hasDepthStencilAttachment ? new IntPtr(&depthAttachmentReference) : IntPtr.Zero,
            };

            var renderPassCreateInfo = new RenderPassCreateInfo
            {
                StructureType = StructureType.RenderPassCreateInfo,
                AttachmentCount = (uint)attachmentCount,
                Attachments = attachments.Length > 0 ? new IntPtr(Interop.Fixed(attachments)) : IntPtr.Zero,
                SubpassCount = 1,
                Subpasses = new IntPtr(&subpass)
            };

            // create pipeline layout
            // Remap descriptor set indices to those in the shader. This ordering generated by the ShaderCompiler
            var resourceGroups = Description.EffectBytecode.Reflection.ResourceBindings.Select(x => x.ResourceGroup ?? "Globals").Distinct().ToList();
            ResourceGroupCount = resourceGroups.Count;

            var layouts = Description.RootSignature.EffectDescriptorSetReflection.Layouts;
            
            // Get binding indices used by the shader
            var destinationBindings = Description.EffectBytecode.Stages
                .SelectMany(x => BinarySerialization.Read<ShaderInputBytecode>(x.Data).ResourceBindings)
                .GroupBy(x => x.Key, x => x.Value)
                .ToDictionary(x => x.Key, x => x.First());

            var maxBindingIndex = destinationBindings.Max(x => x.Value);
            var destinationEntries = new DescriptorSetLayoutBuilder.Entry[maxBindingIndex + 1];

            DescriptorBindingMapping = new List<DescriptorSetInfo>();

            for (int i = 0; i < resourceGroups.Count; i++)
            {
                var resourceGroupName = resourceGroups[i] == "Globals" ? Description.RootSignature.EffectDescriptorSetReflection.DefaultSetSlot : resourceGroups[i];
                var layoutIndex = resourceGroups[i] == null ? 0 : layouts.FindIndex(x => x.Name == resourceGroupName);

                // Check if the resource group is used by the shader
                if (layoutIndex == -1)
                    continue;

                var sourceEntries = layouts[layoutIndex].Layout.Entries;

                for (int sourceBinding = 0; sourceBinding < sourceEntries.Count; sourceBinding++)
                {
                    var sourceEntry = sourceEntries[sourceBinding];

                    int destinationBinding;
                    if (destinationBindings.TryGetValue(sourceEntry.Key.Name, out destinationBinding))
                    {
                        destinationEntries[destinationBinding] = sourceEntry;

                        // No need to umpdate immutable samplers
                        if (sourceEntry.Class == EffectParameterClass.Sampler && sourceEntry.ImmutableSampler != null)
                        {
                            continue;
                        }

                        DescriptorBindingMapping.Add(new DescriptorSetInfo
                        {
                            SourceSet = layoutIndex,
                            SourceBinding = sourceBinding,
                            DestinationBinding = destinationBinding,
                            DescriptorType = VulkanConvertExtensions.ConvertDescriptorType(sourceEntry.Class, sourceEntry.Type)
                        });
                    }
                }
            }

            // Create default sampler, used by texture and buffer loads
            destinationEntries[0] = new DescriptorSetLayoutBuilder.Entry
            {
                Class = EffectParameterClass.Sampler,
                Type = EffectParameterType.Sampler,
                ImmutableSampler = GraphicsDevice.SamplerStates.PointWrap,
                ArraySize = 1,
            };

            // Create descriptor set layout
            NativeDescriptorSetLayout = DescriptorSetLayout.CreateNativeDescriptorSetLayout(GraphicsDevice, destinationEntries, out DescriptorTypeCounts);

            // Create pipeline layout
            var nativeDescriptorSetLayout = NativeDescriptorSetLayout;
            var pipelineLayoutCreateInfo = new PipelineLayoutCreateInfo
            {
                StructureType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                SetLayouts = new IntPtr(&nativeDescriptorSetLayout)
            };

            // Create shader stages
            Dictionary<int, string> inputAttributeNames;

            // Note: important to pin this so that stages[x].Name is valid during this whole function
            void* defaultEntryPointData = Interop.Fixed(defaultEntryPoint);
            stages = CreateShaderStages(Description, out inputAttributeNames);

            var inputAttributes = new VertexInputAttributeDescription[Description.InputElements.Length];
            int inputAttributeCount = 0;
            var inputBindings = new VertexInputBindingDescription[inputAttributes.Length];
            int inputBindingCount = 0;

            for (int inputElementIndex = 0; inputElementIndex < inputAttributes.Length; inputElementIndex++)
            {
                var inputElement = Description.InputElements[inputElementIndex];
                var slotIndex = inputElement.InputSlot;

                if (inputElement.InstanceDataStepRate > 1)
                {
                    throw new NotImplementedException();
                }

                Format format;
                int size;
                bool isCompressed;
                VulkanConvertExtensions.ConvertPixelFormat(inputElement.Format, out format, out size, out isCompressed);

                var location = inputAttributeNames.FirstOrDefault(x => x.Value == inputElement.SemanticName && inputElement.SemanticIndex == 0 || x.Value == inputElement.SemanticName + inputElement.SemanticIndex);
                if (location.Value != null)
                {
                    inputAttributes[inputAttributeCount++] = new VertexInputAttributeDescription
                    {
                        Format = format,
                        Offset = (uint)inputElement.AlignedByteOffset,
                        Binding = (uint)inputElement.InputSlot,
                        Location = (uint)location.Key
                    };
                }

                inputBindings[slotIndex].Binding = (uint)slotIndex;
                inputBindings[slotIndex].InputRate = inputElement.InputSlotClass == InputClassification.Vertex ? VertexInputRate.Vertex : VertexInputRate.Instance;

                // TODO VULKAN: This is currently an argument to Draw() overloads.
                if (inputBindings[slotIndex].Stride < inputElement.AlignedByteOffset + size)
                    inputBindings[slotIndex].Stride = (uint)(inputElement.AlignedByteOffset + size);

                if (inputElement.InputSlot >= inputBindingCount)
                    inputBindingCount = inputElement.InputSlot + 1;
            }

            var inputAssemblyState = new PipelineInputAssemblyStateCreateInfo
            {
                StructureType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = VulkanConvertExtensions.ConvertPrimitiveType(Description.PrimitiveType),
                PrimitiveRestartEnable = VulkanConvertExtensions.ConvertPrimitiveRestart(Description.PrimitiveType),
            };

            // TODO VULKAN: Tessellation and multisampling
            var multisampleState = new PipelineMultisampleStateCreateInfo
            {
                StructureType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Sample1
            };

            //var tessellationState = new PipelineTessellationStateCreateInfo();

            var rasterizationState = new PipelineRasterizationStateCreateInfo
            {
                StructureType = StructureType.PipelineRasterizationStateCreateInfo,
                CullMode = VulkanConvertExtensions.ConvertCullMode(Description.RasterizerState.CullMode),
                FrontFace = Description.RasterizerState.FrontFaceCounterClockwise ? FrontFace.CounterClockwise : FrontFace.Clockwise,
                PolygonMode = VulkanConvertExtensions.ConvertFillMode(Description.RasterizerState.FillMode),
                DepthBiasEnable = true, // TODO VULKAN
                DepthBiasConstantFactor = Description.RasterizerState.DepthBias,
                DepthBiasSlopeFactor = Description.RasterizerState.SlopeScaleDepthBias,
                DepthBiasClamp = Description.RasterizerState.DepthBiasClamp,
                LineWidth = 1.0f,
                DepthClampEnable = !Description.RasterizerState.DepthClipEnable,
                RasterizerDiscardEnable = false,
            };

            var depthStencilState = new PipelineDepthStencilStateCreateInfo
            {
                StructureType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = Description.DepthStencilState.DepthBufferEnable,
                StencilTestEnable = Description.DepthStencilState.StencilEnable,
                DepthWriteEnable = Description.DepthStencilState.DepthBufferWriteEnable,

                MinDepthBounds = 0.0f,
                MaxDepthBounds = 1.0f,
                DepthCompareOperation = VulkanConvertExtensions.ConvertComparisonFunction(Description.DepthStencilState.DepthBufferFunction),
                Front = new StencilOperationState
                {
                    CompareOperation = VulkanConvertExtensions.ConvertComparisonFunction(Description.DepthStencilState.FrontFace.StencilFunction),
                    DepthFailOperation = VulkanConvertExtensions.ConvertStencilOperation(Description.DepthStencilState.FrontFace.StencilDepthBufferFail),
                    FailOperation = VulkanConvertExtensions.ConvertStencilOperation(Description.DepthStencilState.FrontFace.StencilFail),
                    PassOperation = VulkanConvertExtensions.ConvertStencilOperation(Description.DepthStencilState.FrontFace.StencilPass),
                    CompareMask = Description.DepthStencilState.StencilMask,
                    WriteMask = Description.DepthStencilState.StencilWriteMask
                },
                Back = new StencilOperationState
                {
                    CompareOperation = VulkanConvertExtensions.ConvertComparisonFunction(Description.DepthStencilState.BackFace.StencilFunction),
                    DepthFailOperation = VulkanConvertExtensions.ConvertStencilOperation(Description.DepthStencilState.BackFace.StencilDepthBufferFail),
                    FailOperation = VulkanConvertExtensions.ConvertStencilOperation(Description.DepthStencilState.BackFace.StencilFail),
                    PassOperation = VulkanConvertExtensions.ConvertStencilOperation(Description.DepthStencilState.BackFace.StencilPass),
                    CompareMask = Description.DepthStencilState.StencilMask,
                    WriteMask = Description.DepthStencilState.StencilWriteMask
                }
            };

            var description = Description.BlendState;

            var colorBlendAttachments = new PipelineColorBlendAttachmentState[renderTargetCount];

            var renderTargetBlendState = &description.RenderTarget0;
            for (int i = 0; i < renderTargetCount; i++)
            {
                colorBlendAttachments[i] = new PipelineColorBlendAttachmentState
                {
                    BlendEnable = renderTargetBlendState->BlendEnable,
                    AlphaBlendOperation = VulkanConvertExtensions.ConvertBlendFunction(renderTargetBlendState->AlphaBlendFunction),
                    ColorBlendOperation = VulkanConvertExtensions.ConvertBlendFunction(renderTargetBlendState->ColorBlendFunction),
                    DestinationAlphaBlendFactor = VulkanConvertExtensions.ConvertBlend(renderTargetBlendState->AlphaDestinationBlend),
                    DestinationColorBlendFactor = VulkanConvertExtensions.ConvertBlend(renderTargetBlendState->ColorDestinationBlend),
                    SourceAlphaBlendFactor = VulkanConvertExtensions.ConvertBlend(renderTargetBlendState->AlphaSourceBlend),
                    SourceColorBlendFactor = VulkanConvertExtensions.ConvertBlend(renderTargetBlendState->ColorSourceBlend),
                    ColorWriteMask = VulkanConvertExtensions.ConvertColorWriteChannels(renderTargetBlendState->ColorWriteChannels),
                };

                if (description.IndependentBlendEnable)
                    renderTargetBlendState++;
            }

            var viewportState = new PipelineViewportStateCreateInfo
            {
                StructureType = StructureType.PipelineViewportStateCreateInfo,
                ScissorCount = 1,
                ViewportCount = 1,
            };

            fixed (void* dynamicStatesPointer = dynamicStates.Length == 0 ? null : dynamicStates,
                         inputAttributesPointer = inputAttributes.Length == 0 ? null : inputAttributes,
                         inputBindingsPointer = inputBindings.Length == 0 ? null : inputBindings,
                         colorBlendAttachmentsPointer = colorBlendAttachments.Length == 0 ? null : colorBlendAttachments,
                         stagesPointer = stages.Length == 0 ? null : stages)
            {
                var vertexInputState = new PipelineVertexInputStateCreateInfo
                {
                    StructureType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexAttributeDescriptionCount = (uint)inputAttributeCount,
                    VertexAttributeDescriptions = (IntPtr)inputAttributesPointer,
                    VertexBindingDescriptionCount = (uint)inputBindingCount,
                    VertexBindingDescriptions = (IntPtr)inputBindingsPointer,
                };

                var colorBlendState = new PipelineColorBlendStateCreateInfo
                {
                    StructureType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = (uint)renderTargetCount,
                    Attachments = (IntPtr)colorBlendAttachmentsPointer,
                };

                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    StructureType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = (uint)dynamicStates.Length,
                    DynamicStates = (IntPtr)dynamicStatesPointer,
                };
                
                var createInfo = new GraphicsPipelineCreateInfo
                {
                    StructureType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = (uint)stages.Length,
                    Stages = (IntPtr)stagesPointer,
                    //TessellationState = new IntPtr(&tessellationState),
                    VertexInputState = new IntPtr(&vertexInputState),
                    InputAssemblyState = new IntPtr(&inputAssemblyState),
                    RasterizationState = new IntPtr(&rasterizationState),
                    MultisampleState = new IntPtr(&multisampleState),
                    DepthStencilState = new IntPtr(&depthStencilState),
                    ColorBlendState = new IntPtr(&colorBlendState),
                    DynamicState = new IntPtr(&dynamicState),
                    ViewportState = new IntPtr(&viewportState),
                    Subpass = 0,
                };

                using (GraphicsDevice.QueueLock.ReadLock())
                {
                    NativeRenderPass = GraphicsDevice.NativeDevice.CreateRenderPass(ref renderPassCreateInfo);
                    NativeLayout = GraphicsDevice.NativeDevice.CreatePipelineLayout(ref pipelineLayoutCreateInfo);

                    createInfo.Layout = NativeLayout;
                    createInfo.RenderPass = NativeRenderPass;

                    try {
                        NativePipeline = GraphicsDevice.NativeDevice.CreateGraphicsPipelines(PipelineCache.Null, 1, &createInfo);
                    } catch (Exception e) {
                        errorDuringCreate = true;
                        NativePipeline = Pipeline.Null;
                    }
                }
            }

            // Cleanup shader modules
            for (int i=0; i<stages.Length; i++)
            {
                GraphicsDevice.NativeDevice.DestroyShaderModule(stages[i].Module);
            }
        }

        /// <inheritdoc/>
        protected internal override bool OnRecreate()
        {
            Recreate();

            return true;
        }

        /// <inheritdoc/>
        protected internal override unsafe void OnDestroyed()
        {
            if (NativePipeline != Pipeline.Null)
            {
                GraphicsDevice.NativeDevice.DestroyRenderPass(NativeRenderPass);
                GraphicsDevice.NativeDevice.DestroyPipeline(NativePipeline);
                GraphicsDevice.NativeDevice.DestroyPipelineLayout(NativeLayout);

                GraphicsDevice.NativeDevice.DestroyDescriptorSetLayout(NativeDescriptorSetLayout);

                NativePipeline = Pipeline.Null;
            }

            base.OnDestroyed();
        }

        internal struct DescriptorSetInfo
        {
            public int SourceSet;
            public int SourceBinding;
            public int DestinationBinding;
            public DescriptorType DescriptorType;
        }

        internal List<DescriptorSetInfo> DescriptorBindingMapping;

        private unsafe PipelineShaderStageCreateInfo[] CreateShaderStages(PipelineStateDescription pipelineStateDescription, out Dictionary<int, string> inputAttributeNames)
        {
            var stages = pipelineStateDescription.EffectBytecode.Stages;
            var nativeStages = new PipelineShaderStageCreateInfo[stages.Length];

            inputAttributeNames = null;

            for (int i = 0; i < stages.Length; i++)
            {
                var shaderBytecode = BinarySerialization.Read<ShaderInputBytecode>(stages[i].Data);
                if (stages[i].Stage == ShaderStage.Vertex)
                    inputAttributeNames = shaderBytecode.InputAttributeNames;

                fixed (byte* entryPointPointer = &defaultEntryPoint[0])
                fixed (byte* codePointer = &shaderBytecode.Data[0])
                {
                    // Create shader module
                    var moduleCreateInfo = new ShaderModuleCreateInfo
                    {
                        StructureType = StructureType.ShaderModuleCreateInfo,
                        Code = new IntPtr(codePointer),
                        CodeSize = shaderBytecode.Data.Length
                    };

                    // Create stage
                    nativeStages[i] = new PipelineShaderStageCreateInfo
                    {
                        StructureType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = VulkanConvertExtensions.Convert(stages[i].Stage),
                        Name = new IntPtr(entryPointPointer),
                        Module = GraphicsDevice.NativeDevice.CreateShaderModule(ref moduleCreateInfo)
                    };
                }
            };

            return nativeStages;
        }
    }
}

#endif
