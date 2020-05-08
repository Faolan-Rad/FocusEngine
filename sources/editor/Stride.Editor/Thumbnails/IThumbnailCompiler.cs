// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Xenko.Core.Assets.Compiler;

namespace Xenko.Editor.Thumbnails
{
    public interface IThumbnailCompiler : IAssetCompiler
    {
        int Priority { get; set; }

        bool IsStatic { get; set; }
    }
}
