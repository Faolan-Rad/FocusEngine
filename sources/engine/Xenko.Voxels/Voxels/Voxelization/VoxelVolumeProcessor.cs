using System;
using System.Collections.Generic;
using System.Text;
using Xenko.Engine;
using Xenko.Games;
using Xenko.Extensions;
using Xenko.Core.Mathematics;
using Xenko.Graphics;
using Xenko.Graphics.GeometricPrimitives;
using Xenko.Rendering;
using Xenko.Rendering.Voxels;
using Xenko.Rendering.Shadows;
using Xenko.Rendering.Materials;
using Xenko.Rendering.Materials.ComputeColors;

namespace Xenko.Engine.Processors
{
    public class VoxelVolumeProcessor : EntityProcessor<VoxelVolumeComponent>, IEntityComponentRenderProcessor
    {
        private Dictionary<VoxelVolumeComponent, DataVoxelVolume> renderVoxelVolumes = new Dictionary<VoxelVolumeComponent, DataVoxelVolume>();
        public Dictionary<VoxelVolumeComponent, ProcessedVoxelVolume> processedVoxelVolumes = new Dictionary<VoxelVolumeComponent, ProcessedVoxelVolume>();

        public VisibilityGroup VisibilityGroup { get; set; }
        public RenderGroup RenderGroup { get; set; }
        protected override void OnSystemAdd()
        {
            base.OnSystemAdd();

            VisibilityGroup.Tags.Set(VoxelRenderer.CurrentRenderVoxelVolumes, renderVoxelVolumes);
            VisibilityGroup.Tags.Set(VoxelRenderer.CurrentProcessedVoxelVolumes, processedVoxelVolumes);
            VisibilityGroup.Tags.Set(VoxelRenderFeature.CurrentProcessedVoxelVolumes, processedVoxelVolumes);
        }

        public override void Draw(RenderContext context)
        {
            RegenerateVoxelVolumes();
        }
        
        public ProcessedVoxelVolume GetProcessedVolumeForComponent(VoxelVolumeComponent component)
        {
            if (!processedVoxelVolumes.TryGetValue(component, out var data))
                return null;
            return data;
        }
        public DataVoxelVolume GetRenderVolumeForComponent(VoxelVolumeComponent component)
        {
            if (!renderVoxelVolumes.TryGetValue(component, out var data))
                return null;
            return data;
        }

        private void RegenerateVoxelVolumes()
        {
            renderVoxelVolumes.Clear();
            processedVoxelVolumes.Clear();
            for (int i=0; i<ComponentDataKeys.Count; i++)
            {
                var volume = ComponentDataKeys[i];

                if (!volume.Enabled)
                    continue;

                DataVoxelVolume data;
                    renderVoxelVolumes.Add(volume, data = new DataVoxelVolume());
                processedVoxelVolumes.Add(volume, new ProcessedVoxelVolume());

                data.VolumeTranslation = volume.Entity.Transform.WorldMatrix.TranslationVector;
                data.VolumeSize = new Vector3(volume.VoxelVolumeSize);


                data.Voxelize = volume.Voxelize;
                data.AproxVoxelSize = volume.AproximateVoxelSize;

                data.VoxelGridSnapping = volume.VoxelGridSnapping;
                data.VisualizeVoxels = volume.VisualizeVoxels;
                data.VoxelVisualization = volume.Visualization;
                data.Attributes = volume.Attributes;
                data.Storage = volume.Storage;
                data.VoxelizationMethod = volume.VoxelizationMethod;

                if (volume.Attributes.Count > volume.VisualizeIndex)
                {
                    data.VisualizationAttribute = volume.Attributes[volume.VisualizeIndex];
                }
                else
                {
                    data.VisualizationAttribute = null;
                }
            }
        }
    }
}
