// Copyright (c) Stride contributors (https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.


using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.UI.Controls;

namespace Stride.UI.Renderers
{
    /// <summary>
    /// The default renderer for <see cref="Border"/>.
    /// </summary>
    public class DefaultBorderRenderer : ElementRenderer
    {
        public DefaultBorderRenderer(IServiceRegistry services)
            : base(services)
        {
        }

        public override void RenderColor(UIElement element, UIRenderingContext context, UIBatch Batch)
        {
            base.RenderColor(element, context, Batch);

            Vector3 offsets;
            Vector3 borderSize;

            var border = (Border)element;

            var borderColor = border.RenderOpacity * border.BorderColorInternal;
            // optimization: don't draw the border if transparent
            if (borderColor == new Color())
                return;

            var borderThickness = border.BorderThickness;
            var elementHalfBorders = borderThickness / 2;
            var elementSize = element.RenderSizeInternal;
            var elementHalfSize = elementSize / 2;

            // left/front
            offsets = new Vector3(-elementHalfSize.X + elementHalfBorders.Left, 0, -elementHalfSize.Z + elementHalfBorders.Front);
            borderSize = new Vector3(borderThickness.Left, elementSize.Y, borderThickness.Front);
            DrawBorder(border, ref offsets, ref borderSize, ref borderColor, context, Batch);
            
            // right/front
            offsets = new Vector3(elementHalfSize.X - elementHalfBorders.Right, 0, -elementHalfSize.Z + elementHalfBorders.Front);
            borderSize = new Vector3(borderThickness.Right, elementSize.Y, borderThickness.Front);
            DrawBorder(border, ref offsets, ref borderSize, ref borderColor, context, Batch);
            
            // top/front
            offsets = new Vector3(0, -elementHalfSize.Y + elementHalfBorders.Top, -elementHalfSize.Z + elementHalfBorders.Front);
            borderSize = new Vector3(elementSize.X, borderThickness.Top, borderThickness.Front);
            DrawBorder(border, ref offsets, ref borderSize, ref borderColor, context, Batch);
            
            // bottom/front
            offsets = new Vector3(0, elementHalfSize.Y - elementHalfBorders.Bottom, -elementHalfSize.Z + elementHalfBorders.Front);
            borderSize = new Vector3(elementSize.X, borderThickness.Bottom, borderThickness.Back);
            DrawBorder(border, ref offsets, ref borderSize, ref borderColor, context, Batch);

            // if the element is 3D draw the extra borders
            if (element.ActualDepth > MathUtil.ZeroTolerance)
            {
                // left/back
                offsets = new Vector3(-elementHalfSize.X + elementHalfBorders.Left, 0, elementHalfSize.Z - elementHalfBorders.Back);
                borderSize = new Vector3(borderThickness.Left, elementSize.Y, borderThickness.Back);
                DrawBorder(border, ref offsets, ref borderSize, ref borderColor, context, Batch);
                
                // right/back
                offsets = new Vector3(elementHalfSize.X - elementHalfBorders.Right, 0, elementHalfSize.Z - elementHalfBorders.Back);
                borderSize = new Vector3(borderThickness.Right, elementSize.Y, borderThickness.Back);
                DrawBorder(border, ref offsets, ref borderSize, ref borderColor, context, Batch);
                
                // top/back
                offsets = new Vector3(0, -elementHalfSize.Y + elementHalfBorders.Top, elementHalfSize.Z - elementHalfBorders.Back);
                borderSize = new Vector3(elementSize.X, borderThickness.Top, borderThickness.Back);
                DrawBorder(border, ref offsets, ref borderSize, ref borderColor, context, Batch);
                
                // bottom/back
                offsets = new Vector3(0, elementHalfSize.Y - elementHalfBorders.Bottom, elementHalfSize.Z - elementHalfBorders.Back);
                borderSize = new Vector3(elementSize.X, borderThickness.Bottom, borderThickness.Back);
                DrawBorder(border, ref offsets, ref borderSize, ref borderColor, context, Batch);
                
                // left/top
                offsets = new Vector3(-elementHalfSize.X + elementHalfBorders.Left, -elementHalfSize.Y + elementHalfBorders.Top, 0);
                borderSize = new Vector3(borderThickness.Left, borderThickness.Top, elementSize.Z);
                DrawBorder(border, ref offsets, ref borderSize, ref borderColor, context, Batch);
                
                // right/top
                offsets = new Vector3(elementHalfSize.X - elementHalfBorders.Right, -elementHalfSize.Y + elementHalfBorders.Top, 0);
                borderSize = new Vector3(borderThickness.Right, borderThickness.Top, elementSize.Z);
                DrawBorder(border, ref offsets, ref borderSize, ref borderColor, context, Batch);
                
                // left/bottom
                offsets = new Vector3(-elementHalfSize.X + elementHalfBorders.Left, elementHalfSize.Y - elementHalfBorders.Bottom, 0);
                borderSize = new Vector3(borderThickness.Left, borderThickness.Bottom, elementSize.Z);
                DrawBorder(border, ref offsets, ref borderSize, ref borderColor, context, Batch);
                
                // right/bottom
                offsets = new Vector3(elementHalfSize.X - elementHalfBorders.Right, elementHalfSize.Y - elementHalfBorders.Bottom, 0);
                borderSize = new Vector3(borderThickness.Right, borderThickness.Bottom, elementSize.Z);
                DrawBorder(border, ref offsets, ref borderSize, ref borderColor, context, Batch);
            }
        }

        private void DrawBorder(Border border, ref Vector3 offsets, ref Vector3 borderSize, ref Color borderColor, UIRenderingContext context, UIBatch Batch)
        {
            var worldMatrix = border.WorldMatrixInternal;
            worldMatrix.M41 += worldMatrix.M11 * offsets.X + worldMatrix.M21 * offsets.Y + worldMatrix.M31 * offsets.Z;
            worldMatrix.M42 += worldMatrix.M12 * offsets.X + worldMatrix.M22 * offsets.Y + worldMatrix.M32 * offsets.Z;
            worldMatrix.M43 += worldMatrix.M13 * offsets.X + worldMatrix.M23 * offsets.Y + worldMatrix.M33 * offsets.Z;
            Batch.DrawCube(ref worldMatrix, ref borderSize, ref borderColor, context.DepthBias);
        }
    }
}
