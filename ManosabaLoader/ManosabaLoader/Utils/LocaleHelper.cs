using System;

using Naninovel;

namespace ManosabaLoader.Utils
{
    /// <summary>
    /// 运行时本地化工具。通过 Naninovel 引擎获取当前游戏语言设置。
    /// 内部缓存 locale 字符串以避免频繁 IL2CPP 调用（每帧最多查询一次）。
    /// </summary>
    public static class LocaleHelper
    {
        private static string _cachedLocale;
        private static int _cacheFrame = -1;

        /// <summary>当 locale 发生变化时触发，供各模块清理缓存或重新注入资源。</summary>
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
    }
}
