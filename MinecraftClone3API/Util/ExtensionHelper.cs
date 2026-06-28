using System.Collections.Generic;

namespace MinecraftClone3API.Util
{
    public static class ExtensionHelper
    {
        /// <summary>Round-robin interleaves the source lists into <paramref name="output"/> (cleared
        /// first). LINQ-free and fills a caller-owned list so the hot load loop allocates nothing.</summary>
        public static void ZipMerge<T>(List<List<T>> lists, List<T> output)
        {
            output.Clear();

            var listMax = 0;
            for (var i = 0; i < lists.Count; i++)
                if (lists[i].Count > listMax) listMax = lists[i].Count;

            for (var i = 0; i < listMax; i++)
                for (var j = 0; j < lists.Count; j++)
                    if (lists[j].Count > i) output.Add(lists[j][i]);
        }
    }
}
