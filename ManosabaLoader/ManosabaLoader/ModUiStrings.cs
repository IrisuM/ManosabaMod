using System.Collections.Generic;
using System.Linq;

namespace ManosabaLoader
{
    /// <summary>
    /// Mod 框架内部 UI 字符串的本地化。
    /// 添加新语言只需在每个条目的字典中添加对应 locale key 即可。
    /// </summary>
    internal static class ModUiStrings
    {
        private static readonly Dictionary<string, Dictionary<string, string>> Strings = new()
        {
            ["VanillaTitle"] = new() {
                ["zh-Hans"] = "原版游戏剧情",
                ["ja"] = "オリジナルストーリー",
            },
            ["VanillaDescription"] = new() {
                ["zh-Hans"] = "原汁原味的游戏内容。",
                ["ja"] = "オリジナルのゲーム内容。",
            },
            ["WorkspacePrefix"] = new() {
                ["zh-Hans"] = "工作区：",
                ["ja"] = "ワークスペース：",
            },
            ["PrevPage"] = new() {
                ["zh-Hans"] = "上一页",
                ["ja"] = "前のページ",
            },
            ["NextPage"] = new() {
                ["zh-Hans"] = "下一页",
                ["ja"] = "次のページ",
            },
            ["ModNameLabel"] = new() {
                ["zh-Hans"] = "Mod名称：",
                ["ja"] = "Mod名前：",
            },
            ["ModDescriptionLabel"] = new() {
                ["zh-Hans"] = "Mod说明：",
                ["ja"] = "Mod説明：",
            },
            ["ModVersionLabel"] = new() {
                ["zh-Hans"] = "Mod版本：",
                ["ja"] = "Modバージョン：",
            },
        };

        /// <summary>
        /// 根据当前 locale 获取本地化字符串。
        /// 回退顺序：当前 locale → zh-Hans → 任意可用值 → key 本身。
        /// </summary>
        public static string Get(string key)
        {
            if (!Strings.TryGetValue(key, out var locales)) return key;
            var locale = Utils.LocaleHelper.GetCurrentLocale();
            if (locales.TryGetValue(locale, out var text)) return text;
            if (locales.TryGetValue("zh-Hans", out text)) return text;
            return locales.Values.FirstOrDefault() ?? key;
        }
    }
}
