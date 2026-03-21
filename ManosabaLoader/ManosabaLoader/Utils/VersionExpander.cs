using System;
using System.Collections.Generic;

namespace ManosabaLoader.Utils
{
    /// <summary>
    /// 版本补齐工具：为每个唯一 ID 填充版本 1 ~ max(已定义) + Padding，
    /// 使用最高已定义版本的数据填充空缺版本，确保 @update 升级后仍能匹配 VersionedItem。
    /// </summary>
    static class VersionExpander
    {
        public const int DefaultPadding = 4;

        /// <summary>
        /// 通用版本补齐。
        /// <paramref name="keySelector"/> 提取 (id, version)；
        /// <paramref name="cloneWithVersion"/> 基于最高版本数据创建指定版本的副本。
        /// </summary>
        public static void Expand<T>(
            List<T> items,
            HashSet<(string, int)> seen,
            Func<T, (string id, int version)> keySelector,
            Func<T, int, T> cloneWithVersion,
            int padding = DefaultPadding)
        {
            var maxVer = new Dictionary<string, int>();
            var highestData = new Dictionary<string, T>();
            foreach (var item in items)
            {
                var (id, ver) = keySelector(item);
                if (!maxVer.ContainsKey(id) || ver > maxVer[id])
                {
                    maxVer[id] = ver;
                    highestData[id] = item;
                }
            }
            foreach (var kvp in maxVer)
            {
                int paddedMax = kvp.Value + padding;
                for (int v = 1; v <= paddedMax; v++)
                {
                    if (seen.Contains((kvp.Key, v))) continue;
                    seen.Add((kvp.Key, v));
                    items.Add(cloneWithVersion(highestData[kvp.Key], v));
                }
            }
        }
    }
}
