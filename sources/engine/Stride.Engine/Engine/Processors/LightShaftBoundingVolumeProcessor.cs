// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Xenko.Core.Mathematics;
using Xenko.Games;
using Xenko.Rendering;
using Xenko.Rendering.Images;

namespace Xenko.Engine.Processors
{
    public class LightShaftBoundingVolumeProcessor : EntityProcessor<LightShaftBoundingVolumeComponent>
    {
        private Dictionary<LightShaftComponent, List<RenderLightShaftBoundingVolume>> volumesPerLightShaft = new Dictionary<LightShaftComponent, List<RenderLightShaftBoundingVolume>>();
        private bool isDirty;

        public override void Update(GameTime time)
        {
            RegenerateVolumesPerLightShaft();
        }

        public IReadOnlyList<RenderLightShaftBoundingVolume> GetBoundingVolumesForComponent(LightShaftComponent component)
        {
            if (!volumesPerLightShaft.TryGetValue(component, out var data))
                return null;
            return data;
        }

        protected override void OnEntityComponentAdding(Entity entity, LightShaftBoundingVolumeComponent component, LightShaftBoundingVolumeComponent data)
        {
            component.LightShaftChanged += ComponentOnLightShaftChanged;
            component.ModelChanged += ComponentOnModelChanged;
            component.EnabledChanged += ComponentOnEnabledChanged;
            isDirty = true;
        }

        protected override void OnEntityComponentRemoved(Entity entity, LightShaftBoundingVolumeComponent component, LightShaftBoundingVolumeComponent data)
        {
            component.LightShaftChanged -= ComponentOnLightShaftChanged;
            component.ModelChanged -= ComponentOnModelChanged;
            component.EnabledChanged -= ComponentOnEnabledChanged;
            isDirty = true;
        }

        private void ComponentOnEnabledChanged(object sender, EventArgs eventArgs)
        {
            isDirty = true;
        }

        private void ComponentOnModelChanged(object sender, EventArgs eventArgs)
        {
            isDirty = true;
        }

        private void ComponentOnLightShaftChanged(object sender, EventArgs eventArgs)
        {
            isDirty = true;
        }

        private void RegenerateVolumesPerLightShaft()
        {
            // Clear
            if (isDirty)
            {
                volumesPerLightShaft.Clear();
            }
            // Keep existing collections
            else
            {
                foreach (var lightShaft in volumesPerLightShaft)
                {
                    lightShaft.Value.Clear();
                }
            }

            for (int i=0; i<ComponentDataKeys.Count; i++)
            {
                var pairKey = ComponentDataKeys[i];
                if (!pairKey.Enabled)
                    continue;

                var lightShaft = pairKey.LightShaft;
                if (lightShaft == null)
                    continue;

                List<RenderLightShaftBoundingVolume> data;
                if (!volumesPerLightShaft.TryGetValue(lightShaft, out data))
                    volumesPerLightShaft.Add(lightShaft, data = new List<RenderLightShaftBoundingVolume>());

                data.Add(new RenderLightShaftBoundingVolume
                {
                    World = pairKey.Entity.Transform.WorldMatrix,
                    Model = pairKey.Model,
                });
            }

            isDirty = false;
        }
    }
}
