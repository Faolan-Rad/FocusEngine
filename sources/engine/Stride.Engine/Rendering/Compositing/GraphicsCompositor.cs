// Copyright (c) Stride contributors (https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Collections;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering.Images;
using Stride.Rendering.Lights;

namespace Stride.Rendering.Compositing
{
    [DataSerializerGlobal(typeof(ReferenceSerializer<GraphicsCompositor>), Profile = "Content")]
    [ReferenceSerializer, ContentSerializer(typeof(DataContentSerializerWithReuse<GraphicsCompositor>))]
    [DataContract]
    // Needed for indirect serialization of RenderSystem.RenderStages and RenderSystem.RenderFeatures
    // TODO: we would like an attribute to specify that serializing through the interface type is fine in this case (bypass type detection)
    [DataSerializerGlobal(null, typeof(FastTrackingCollection<RenderStage>))]
    [DataSerializerGlobal(null, typeof(FastTrackingCollection<RootRenderFeature>))]
    public class GraphicsCompositor : RendererBase
    {
        private readonly List<SceneInstance> initializedSceneInstances = new List<SceneInstance>();

        /// <summary>
        /// Gets the render system used with this graphics compositor.
        /// </summary>
        [DataMemberIgnore]
        public RenderSystem RenderSystem { get; } = new RenderSystem();

        /// <summary>
        /// Gets the cameras used by this composition.
        /// </summary>
        /// <value>The cameras.</value>
        /// <userdoc>The list of cameras used in the graphic pipeline</userdoc>
        [DataMember(10)]
        [Category]
        [MemberCollection(NotNullItems = true)]
        public SceneCameraSlotCollection Cameras { get; } = new SceneCameraSlotCollection();

        /// <summary>
        /// The list of render stages.
        /// </summary>
        [DataMember(20)]
        [Category]
        [MemberCollection(NotNullItems = true)]
        public IList<RenderStage> RenderStages => RenderSystem.RenderStages;

        /// <summary>
        /// The list of render features.
        /// </summary>
        [DataMember(30)]
        [Category]
        [MemberCollection(NotNullItems = true)]
        public IList<RootRenderFeature> RenderFeatures => RenderSystem.RenderFeatures;

        /// <summary>
        /// The entry point for the game compositor.
        /// </summary>
        public ISceneRenderer Game { get; set; }

        /// <summary>
        /// The entry point for a compositor that can render a single view.
        /// </summary>
        public ISceneRenderer SingleView { get; set; }

        /// <summary>
        /// The entry point for a compositor used by the scene editor.
        /// </summary>
        public ISceneRenderer Editor { get; set; }

        [DataMemberIgnore]
        private List<PostProcessingEffects> cachedProcessor;

        private void gatherPostProcessors(ISceneRenderer renderer)
        {
            if (renderer is SceneCameraRenderer scr)
            {
                gatherPostProcessors(scr.Child);
            }
            else if (renderer is SceneRendererCollection src)
            {
                List<ISceneRenderer> renderers = src.Children;
                for (int i = 0; i < renderers.Count; i++)
                    gatherPostProcessors(renderers[i]);
            }
            else if (renderer is ForwardRenderer fr)
            {
                IPostProcessingEffects check = fr.PostEffects;
                if (check != null && check is PostProcessingEffects c)
                {
                    cachedProcessor.Add(c);
                }
            }
        }

        /// <summary>
        /// Shortcut to getting post processing effects
        /// </summary>
        [DataMemberIgnore]
        public List<PostProcessingEffects> PostProcessing {
            get {
                if (cachedProcessor == null) {
                    // find them
                    cachedProcessor = new List<PostProcessingEffects>();
                    gatherPostProcessors(Game);
                }
                return cachedProcessor;
            }
        }

        /// <summary>
        /// Shortcut to setting VR settings on renderers to enable or disable. Only renderers with Required APIs set will be enabled via this method.
        /// </summary>
        /// <param name="enable">Whether to enable or disable VR settings on renderers</param>
        public void SetVRRenderers(bool enable)
        {
            recursiveVRSet(Game, enable);
        }

        private void recursiveVRSet(ISceneRenderer r, bool enable)
        {
            if (r is ForwardRenderer fr)
            {
                fr.VRSettings.Enabled = enable && fr.VRSettings.RequiredApis.Count > 0;
            }
            else if (r is SceneCameraRenderer scr)
            {
                recursiveVRSet(scr.Child, enable);
            }
            else if (r is SceneRendererCollection src)
            {
                foreach (ISceneRenderer isr in src.Children)
                    recursiveVRSet(isr, enable);
            }
        }

        /// <inheritdoc/>
        protected override void InitializeCore()
        {
            base.InitializeCore();

            // any settings to apply from a meshrenderfeature?
            foreach (RootRenderFeature rrf in RenderFeatures)
            {
                if (rrf is MeshRenderFeature mrf)
                {
                    LightClusteredPointSpotGroupRenderer.UseLinearLighting = mrf.LinearLightAttenuation;
                    break;
                }
            }

            RenderSystem.Initialize(Context);
        }

        /// <inheritdoc/>
        protected override void Destroy()
        {
            // Dispose renderers
            Game?.Dispose();

            // Cleanup created visibility groups
            foreach (var sceneInstance in initializedSceneInstances)
            {
                for (var i = 0; i < sceneInstance.VisibilityGroups.Count; i++)
                {
                    var visibilityGroup = sceneInstance.VisibilityGroups[i];
                    if (visibilityGroup.RenderSystem == RenderSystem)
                    {
                        sceneInstance.VisibilityGroups.RemoveAt(i);
                        break;
                    }
                }
            }

            RenderSystem.Dispose();

            base.Destroy();
        }

        /// <inheritdoc/>
        protected override void DrawCore(RenderDrawContext context)
        {
            if (Game != null)
            {
                // Get or create VisibilityGroup for this RenderSystem + SceneInstance
                var sceneInstance = SceneInstance.GetCurrent(context.RenderContext);
                VisibilityGroup visibilityGroup = null;
                if (sceneInstance != null)
                {
                    // Find if VisibilityGroup
                    foreach (var currentVisibilityGroup in sceneInstance.VisibilityGroups)
                    {
                        if (currentVisibilityGroup.RenderSystem == RenderSystem)
                        {
                            visibilityGroup = currentVisibilityGroup;
                            break;
                        }
                    }

                    // If first time, let's create and register it
                    if (visibilityGroup == null)
                    {
                        sceneInstance.VisibilityGroups.Add(visibilityGroup = new VisibilityGroup(RenderSystem));
                        initializedSceneInstances.Add(sceneInstance);
                    }

                    // Reset & cleanup
                    visibilityGroup.Reset();
                }

                using (context.RenderContext.PushTagAndRestore(SceneInstance.CurrentVisibilityGroup, visibilityGroup))
                using (context.RenderContext.PushTagAndRestore(SceneInstance.CurrentRenderSystem, RenderSystem))
                using (context.RenderContext.PushTagAndRestore(SceneCameraSlotCollection.Current, Cameras))
                {
                    // Set render system
                    context.RenderContext.RenderSystem = RenderSystem;
                    context.RenderContext.VisibilityGroup = visibilityGroup;

                    // Set start states for viewports and output (it will be used during the Collect phase)
                    var renderOutputs = new RenderOutputDescription();
                    renderOutputs.CaptureState(context.CommandList);
                    context.RenderContext.RenderOutput = renderOutputs;

                    var viewports = new ViewportState();
                    viewports.CaptureState(context.CommandList);
                    context.RenderContext.ViewportState = viewports;

                    try
                    {
                        // Collect in the game graphics compositor: Setup features/stages, enumerate views and populates VisibilityGroup
                        Game.Collect(context.RenderContext);

                        // Collect in render features
                        RenderSystem.Collect(context.RenderContext);

                        // Collect visibile objects from each view (that were not properly collected previously)
                        if (visibilityGroup != null)
                        {
                            foreach (var view in RenderSystem.Views)
                                visibilityGroup.TryCollect(view);
                        }

                        // Extract
                        RenderSystem.Extract(context.RenderContext);

                        // Prepare
                        RenderSystem.Prepare(context);

                        // Draw using the game graphics compositor
                        Game.Draw(context);

                        // Flush
                        RenderSystem.Flush(context);
                    }
                    finally
                    {
                        // Reset render context data
                        RenderSystem.Reset();
                    }
                }
            }
        }
    }
}
