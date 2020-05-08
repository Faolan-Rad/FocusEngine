// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using Xenko.Core.Assets.Templates;
using Xenko.Core.IO;

namespace Xenko.Core.Assets.Editor.ViewModel
{
    public class NewSessionParameters
    {
        public TemplateDescription TemplateDescription;
        public string OutputName;
        public UDirectory OutputDirectory;
        public string SolutionName;
        public UDirectory SolutionLocation;
    }
}
