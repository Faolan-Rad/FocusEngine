// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using Xenko.Core;
using Xenko.Core.Yaml;
using Xenko.Core.Yaml.Events;
using Xenko.Core.Yaml.Serialization;

namespace Xenko.Core.Assets.Serializers
{
    /// <summary>
    /// A Yaml serializer for <see cref="PackageVersionRange"/>
    /// </summary>
    [YamlSerializerFactory(YamlAssetProfile.Name)]
    internal class PackageVersionRangeSerializer : AssetScalarSerializerBase
    {
        public override bool CanVisit(Type type)
        {
            return typeof(PackageVersionRange).IsAssignableFrom(type);
        }

        public override object ConvertFrom(ref ObjectContext context, Scalar fromScalar)
        {
            PackageVersionRange versionRange;
            if (!PackageVersionRange.TryParse(fromScalar.Value, out versionRange))
            {
                throw new YamlException(fromScalar.Start, fromScalar.End, "Invalid version dependency format. Unable to decode [{0}]".ToFormat(fromScalar.Value));
            }
            return versionRange;
        }

        public override string ConvertTo(ref ObjectContext objectContext)
        {
            return objectContext.Instance.ToString();
        }
    }
}
