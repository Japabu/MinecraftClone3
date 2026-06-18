using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Util
{
    public static class ExtensionHelper
    {
        public static Vector4 ToVector4(this Color4 color) => new Vector4(color.R, color.G, color.B, color.A);

        public static List<T> ZipMerge<T>(List<T>[] lists)
        {
            var ret = new List<T>();

            var listMax = lists.Aggregate(0, (max, list) => Math.Max(max, list.Count));
            for(var i = 0; i < listMax; i++)
                ret.AddRange(lists.Where(list => list.Count > i).Select(list => list[i]));

            return ret;
        }
    }
}
