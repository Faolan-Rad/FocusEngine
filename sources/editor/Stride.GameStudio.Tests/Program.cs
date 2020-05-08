// Copyright (c) Stride contributors (https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Threading;

namespace Stride.GameStudio.Tests
{
    class Program
    {
        static void Main()
        {
            var test = new TestThumbnails();
            test.Run();

            //if (result == BuildResultCode.BuildError)
            //    Console.WriteLine("The build failed");
            
            while (true)
                Thread.Sleep(1000);
        }
    }
}
