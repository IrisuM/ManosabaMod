using System;

using GigaCreation.Essentials.Localization;

using Naninovel;

namespace ManosabaLoader.Utils
{
    /// <summary>
    /// 运行时本地化工具。通过 Naninovel 引擎获取当前游戏语言设置，
    /// 为 <see cref="ModManager.ModItem.LocalizedString.Resolve"/> 提供基础。
    ///
    /// <para>内部缓存 locale 字符串以避免频繁 IL2CPP 调用（每帧最多查询一次）。</para>
    /// </summary>
    public static class LocaleHelper
    {
        private static string _cachedLocale;
        private static int _cacheFrame = -1;

        /// <summary>当 locale 发生变化时触发，供各模块清理缓存。</summary>
        public static event Action OnLocaleChanged;

        /// <summary>
        /// 获取当前游戏语言（如 "ja", "zh-Hans"）。在引擎尚未初始化时返回上次缓存值或默认 "zh-Hans"。
        /// </summary>
        public static string GetCurrentLocale()
        {
            int frame = UnityEngine.Time.frameCount;
            if (frame == _cacheFrame && _cachedLocale != null)
                return _cachedLocale;

            try
            {
                if (Engine.Initialized)
                {
                    var locManager = Engine.GetService<Naninovel.ILocalizationManager>();
                    if (locManager != null)
                    {
                        string newLocale = locManager.SelectedLocale;
                        if (!string.IsNullOrEmpty(newLocale))
                        {
                            string oldLocale = _cachedLocale;
                            // 先更新缓存再触发事件，防止事件处理器调用 GetCurrentLocale() 导致无限递归
                            _cachedLocale = newLocale;
                            _cacheFrame = frame;
                            if (oldLocale != null && oldLocale != newLocale)
                                OnLocaleChanged?.Invoke();
                            return _cachedLocale;
                        }
                    }
                }
            }
            catch
            {
                // Engine not ready yet — fall through to default
            }

            return _cachedLocale ?? "zh-Hans";
        }

        /// <summary>
        /// 将当前 locale 字符串映射为 <see cref="LocaleKind"/> 枚举。
        /// </summary>
        public static LocaleKind GetCurrentLocaleKind()
        {
            return GetCurrentLocale() switch
            {
                "ja" => LocaleKind.Ja,
                "en-US" => LocaleKind.EnUs,
                "zh-Hans" => LocaleKind.ZhHans,
                "zh-Hant" => LocaleKind.ZhHant,
                "ko" => LocaleKind.Ko,
                "fr" => LocaleKind.Fr,
                "es" => LocaleKind.Es,
                _ => LocaleKind.ZhHans
            };
        }
    }
}
