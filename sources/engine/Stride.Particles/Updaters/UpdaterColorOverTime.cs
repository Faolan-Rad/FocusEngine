// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Xenko.Core;
using Xenko.Core.Annotations;
using Xenko.Core.Mathematics;
using Xenko.Animations;
using Xenko.Particles.Modules;

namespace Xenko.Particles.Updaters
{
    /// <summary>
    /// Updater which sets the particle's color to a fixed value sampled based on the particle's normalized life value
    /// </summary>
    [DataContract("UpdaterColorOverTime")]
    [Display("Color Animation")]
    public class UpdaterColorOverTime : ParticleUpdater
    {
        /// <summary>
        /// Default constructor which also registers the fields required by this updater
        /// </summary>
        public UpdaterColorOverTime()
        {
            RequiredFields.Add(ParticleFields.Color);

            var curve = new ComputeAnimationCurveColor4();
            SamplerMain.Curve = curve;
        }

        /// <inheritdoc />
        [DataMemberIgnore]
        public override bool IsPostUpdater => true;

        /// <summary>
        /// The main curve sampler. Particles will change their value based on the sampled values
        /// </summary>
        /// <userdoc>
        /// The main curve sampler. Particles will change their value based on the sampled values
        /// </userdoc>
        [DataMember(100)]
        [NotNull]
        [Display("Main")]
        public ComputeCurveSampler<Color4> SamplerMain { get; set; } = new ComputeCurveSamplerColor4();

        /// <summary>
        /// Optional sampler. If present, particles will pick a random value between the two sampled curves
        /// </summary>
        /// <userdoc>
        /// Optional sampler. If present, particles will pick a random value between the two sampled curves
        /// </userdoc>
        [DataMember(200)]
        [Display("Optional")]
        public ComputeCurveSampler<Color4> SamplerOptional { get; set; }

        /// <summary>
        /// Seed offset. You can use this offset to bind the randomness to other random values, or to make them completely unrelated
        /// </summary>
        /// <userdoc>
        /// Seed offset. You can use this offset to bind the randomness to other random values, or to make them completely unrelated
        /// </userdoc>
        [DataMember(300)]
        [Display("Random Seed")]
        public uint SeedOffset { get; set; } = 0;

        /// <inheritdoc />
        public override void PreUpdate()
        {
            base.PreUpdate();

            SamplerMain?.UpdateChanges();
            SamplerOptional?.UpdateChanges();
        }

        /// <inheritdoc />
        public override void Update(float dt, ParticlePool pool)
        {
            if (!pool.FieldExists(ParticleFields.Color) || !pool.FieldExists(ParticleFields.Life))
                return;

            if (SamplerOptional == null)
            {
                UpdateSingleSampler(pool);
                return;
            }

            UpdateDoubleSampler(pool);
        }

        /// <summary>
        /// Updates the field by sampling a single value over the particle's lifetime
        /// </summary>
        /// <param name="pool">Target <see cref="ParticlePool"/></param>
        private unsafe void UpdateSingleSampler(ParticlePool pool)
        {
            var colorField = pool.GetField(ParticleFields.Color);
            var lifeField  = pool.GetField(ParticleFields.Life);

            int count = pool.NextFreeIndex;
            for(int i = 0; i < count; i++)
            {
                Particle particle = pool.FromIndex(i);

                var life = 1f - (*((float*)particle[lifeField]));   // The Life field contains remaining life, so for sampling we take (1 - life)

                var color = SamplerMain.Evaluate(life);

                // preserve any colors?
                if (pool.SpecificColors != null)
                {
                    if (color.A < 0f) color.A = pool.SpecificColors[i].A;
                    if (color.R < 0f) color.R = pool.SpecificColors[i].R;
                    if (color.G < 0f) color.G = pool.SpecificColors[i].G;
                    if (color.B < 0f) color.B = pool.SpecificColors[i].B;
                }

                // Premultiply alpha
                color.R *= color.A;
                color.G *= color.A;
                color.B *= color.A;

                (*((Color4*)particle[colorField])) = color;
            }
        }

        /// <summary>
        /// Updates the field by interpolating between two sampled values over the particle's lifetime
        /// </summary>
        /// <param name="pool">Target <see cref="ParticlePool"/></param>
        private unsafe void UpdateDoubleSampler(ParticlePool pool)
        {
            var colorField = pool.GetField(ParticleFields.Color);
            var lifeField  = pool.GetField(ParticleFields.Life);
            var randField  = pool.GetField(ParticleFields.RandomSeed);

            int count = pool.NextFreeIndex;
            for (int i = 0; i < count; i++)
            {
                Particle particle = pool.FromIndex(i);

                var life = 1f - (*((float*)particle[lifeField]));   // The Life field contains remaining life, so for sampling we take (1 - life)

                var randSeed = particle.Get(randField);
                var lerp = randSeed.GetFloat(RandomOffset.Offset1A + SeedOffset);

                var colorMin = SamplerMain.Evaluate(life);
                var colorMax = SamplerOptional.Evaluate(life);                
                var color    =  Color4.Lerp(colorMin, colorMax, lerp);

                // preserve any colors?
                if (pool.SpecificColors != null)
                {
                    if (color.A < 0f) color.A = pool.SpecificColors[i].A;
                    if (color.R < 0f) color.R = pool.SpecificColors[i].R;
                    if (color.G < 0f) color.G = pool.SpecificColors[i].G;
                    if (color.B < 0f) color.B = pool.SpecificColors[i].B;
                }

                // Premultiply alpha
                color.R *= color.A;
                color.G *= color.A;
                color.B *= color.A;

                (*((Color4*)particle[colorField])) = color;
            }
        }
    }
}
