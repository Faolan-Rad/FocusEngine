// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Xenko.Core;
using Xenko.Core.Mathematics;
using Xenko.Graphics;
using Xenko.UI.Controls;

namespace Xenko.UI.Renderers
{
    /// <summary>
    /// The default renderer for <see cref="ModalElement"/>.
    /// </summary>
    internal class DefaultModalElementRenderer : ElementRenderer
    {
        private Matrix identity = Matrix.Identity;

        private readonly DepthStencilStateDescription noStencilNoDepth;

        public DefaultModalElementRenderer(IServiceRegistry services)
            : base(services)
        {
            noStencilNoDepth = new DepthStencilStateDescription(false, false);
        }

        public override void RenderColor(UIElement element, UIRenderingContext context, UIBatch Batch)
        {
            var modalElement = (ModalElement)element;

            // end the current UI image batching so that the overlay is written over it with correct transparency
            Batch.End();

            var uiResolution = new Vector3(context.Resolution.X, context.Resolution.Y, 0);
            Batch.Begin(context.GraphicsContext, ref context.ViewProjectionMatrix, BlendStates.AlphaBlend, noStencilNoDepth, 0);
            Batch.DrawRectangle(ref identity, ref uiResolution, ref modalElement.OverlayColorInternal, context.DepthBias);
            Batch.End(); // ensure that overlay is written before possible next transparent element.

            // restart the image batch session
            Batch.Begin(context.GraphicsContext, ref context.ViewProjectionMatrix, BlendStates.AlphaBlend, KeepStencilValueState, context.StencilTestReferenceValue);

            context.DepthBias += 1;

            base.RenderColor(element, context, Batch);
        }

        protected override void Destroy()
        {
            base.Destroy();
        }
    }
}
