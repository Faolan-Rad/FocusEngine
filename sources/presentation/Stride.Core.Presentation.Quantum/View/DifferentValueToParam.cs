// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Globalization;
using Xenko.Core.Presentation.Quantum.ViewModels;
using Xenko.Core.Presentation.ValueConverters;

namespace Xenko.Core.Presentation.Quantum.View
{
    public class DifferentValueToParam : ValueConverterBase<DifferentValueToParam>
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value != NodeViewModel.DifferentValues ? value : parameter;
        }

        public override object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value;
        }
    }
}
