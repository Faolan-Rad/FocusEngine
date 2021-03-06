﻿// <auto-generated>
// Do not edit this file yourself!
//
// This code was generated by Xenko Shader Mixin Code Generator.
// To generate it yourself, please install Xenko.VisualStudio.Package .vsix
// and re-save the associated .xkfx.
// </auto-generated>

using System;
using Xenko.Core;
using Xenko.Rendering;
using Xenko.Graphics;
using Xenko.Shaders;
using Xenko.Core.Mathematics;
using Buffer = Xenko.Graphics.Buffer;

namespace Xenko.Rendering.Voxels.Debug
{
    internal static partial class ShaderMixins
    {
        internal partial class VoxelVisualizationRawEffect  : IShaderMixinBuilder
        {
            public void Generate(ShaderMixinSource mixin, ShaderMixinContext context)
            {
                context.Mixin(mixin, "VoxelVisualizationRawShader");
                if (context.GetParam(VoxelVisualizationRawShaderKeys.Attribute) != null)
                {

                    {
                        var __mixinToCompose__ = context.GetParam(VoxelVisualizationRawShaderKeys.Attribute);
                        var __subMixin = new ShaderMixinSource();
                        context.PushComposition(mixin, "Attribute", __subMixin);
                        context.Mixin(__subMixin, __mixinToCompose__);
                        context.PopComposition();
                    }
                }
            }

            [ModuleInitializer]
            internal static void __Initialize__()

            {
                ShaderMixinManager.Register("VoxelVisualizationRawEffect", new VoxelVisualizationRawEffect());
            }
        }
    }
}
