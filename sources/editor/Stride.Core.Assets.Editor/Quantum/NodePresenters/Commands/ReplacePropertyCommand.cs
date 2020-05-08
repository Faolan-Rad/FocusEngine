// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Xenko.Core.Presentation.Quantum;
using Xenko.Core.Presentation.Quantum.Presenters;

namespace Xenko.Core.Assets.Editor.Quantum.NodePresenters.Commands
{
    public class ReplacePropertyCommand : PastePropertyCommandBase
    {
        /// <summary>
        /// The name of this command.
        /// </summary>
        public const string CommandName = "ReplaceProperty";

        /// <inheritdoc />
        public override string Name => CommandName;

        /// <inheritdoc />
        public override CombineMode CombineMode => CombineMode.CombineOnlyForAll;

        /// <inheritdoc />
        protected override void ExecuteSync(INodePresenter nodePresenter, object parameter, object preExecuteResult)
        {
            DoPaste(nodePresenter, true);
        }
    }
}
