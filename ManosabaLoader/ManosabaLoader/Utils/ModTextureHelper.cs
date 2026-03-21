using System;
using System.Collections.Generic;
using System.IO;

using GigaCreation.Essentials.AddressablesUtils;

using UnityEngine;

using WitchTrials.Views;

namespace ManosabaLoader.Utils
{
    /// <summary>
    /// Mod 纹理加载/缓存/Addressables 注册的共享工具。
    /// 供 ModClueLoader 和 ModProfileLoader 共同使用。
    /// </summary>
    static class ModTextureHelper
    {
        public static Action<string> TextureHelperLogMessage;
        public static Action<string> TextureHelperLogInfo;
        public static Action<string> TextureHelperLogDebug;
        public static Action<string> TextureHelperLogWarning;
        public static Action<string> TextureHelperLogError;

        /// <summary>
        /// 从本地文件加载纹理（带缓存）。
        /// </summary>
        public static Texture2D LoadTexture(
            string id,
            string namePrefix,
            Dictionary<string, string> pathMap,
            Dictionary<string, Texture2D> cache)
        {
            if (cache.TryGetValue(id, out var cached) && cached != null && cached.Pointer != IntPtr.Zero)
                return cached;

            if (!pathMap.TryGetValue(id, out var path))
                return null;

            try
            {
                var bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2);
                if (ImageConversion.LoadImage(tex, bytes))
                {
                    tex.name = $"{namePrefix}{id}";
                    tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    cache[id] = tex;
                    return tex;
                }
                TextureHelperLogError($"Failed to decode texture '{id}'");
            }
            catch (Exception ex)
            {
                TextureHelperLogError($"Failed to load texture '{id}': {ex}");
            }
            return null;
        }

        /// <summary>
        /// 查找 AddressablesManager 实例并调用 <paramref name="registerAction"/>。
        /// 依次尝试从 <typeparamref name="TPage"/> 和 WitchBookPageBase 获取 _addressableAssetLoader。
        /// </summary>
        public static void EnsureRegisteredInAddressables<TPage>(
            Action<IntPtr> registerAction) where TPage : UnityEngine.Object
        {
            try
            {
                var pages = Resources.FindObjectsOfTypeAll<TPage>();
                if (pages != null && pages.Length > 0)
                {
                    IntPtr loaderPtr = Il2CppFieldHelper.GetReferenceField(pages[0], "_addressableAssetLoader");
                    if (loaderPtr != IntPtr.Zero) { registerAction(loaderPtr); return; }
                }

                var pageBases = Resources.FindObjectsOfTypeAll<WitchBookPageBase>();
                if (pageBases != null)
                {
                    foreach (var page in pageBases)
                    {
                        IntPtr ptr = Il2CppFieldHelper.GetReferenceField(page, "_addressableAssetLoader");
                        if (ptr != IntPtr.Zero) { registerAction(ptr); return; }
                    }
                }

                TextureHelperLogDebug($"AddressablesManager not yet available (page type: {typeof(TPage).Name}).");
            }
            catch (Exception ex)
            {
                TextureHelperLogError($"EnsureRegisteredInAddressables<{typeof(TPage).Name}> failed: {ex}");
            }
        }

        /// <summary>
        /// 将纹理批量注册到 AddressablesManager._loadedAssets 字典。
        /// 通过采样检查实现幂等。
        /// </summary>
        /// <param name="managerPtr">AddressablesManager 实例指针</param>
        /// <param name="ids">要注册的 ID 集合</param>
        /// <param name="buildAddress">id → Addressables 地址</param>
        /// <param name="loadTexture">id → Texture2D</param>
        /// <param name="extraAction">注册后的附加操作（如更新反查映射），可为 null</param>
        /// <param name="label">日志标签</param>
        public static void RegisterTexturesInManager(
            IntPtr managerPtr,
            IEnumerable<string> ids,
            Func<string, string> buildAddress,
            Func<string, Texture2D> loadTexture,
            Action<string, string> extraAction,
            string label)
        {
            try
            {
                var manager = new AddressablesManager(managerPtr);
                IntPtr dictPtr = Il2CppFieldHelper.GetReferenceField(manager, "_loadedAssets");
                if (dictPtr == IntPtr.Zero)
                {
                    TextureHelperLogWarning($"_loadedAssets is null ({label}).");
                    return;
                }

                var dict = new Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object>(dictPtr);

                // 采样检查：对比第一个 ID 的缓存纹理与 _loadedAssets 中的引用是否一致。
                // 当纹理被 Destroy 后重建（如回到标题后重新开始），指针不同，需要重新注册。
                foreach (var id in ids)
                {
                    string sampleAddr = buildAddress(id);
                    if (dict.ContainsKey(sampleAddr))
                    {
                        var freshTex = loadTexture(id);
                        if (freshTex != null)
                        {
                            try
                            {
                                var existing = dict[sampleAddr];
                                if (existing != null && existing.Pointer == freshTex.Pointer)
                                {
                                    TextureHelperLogDebug($"{label} textures already registered.");
                                    return;
                                }
                            }
                            catch { /* stale reference, fall through to re-register */ }
                        }
                        TextureHelperLogDebug($"{label} stale texture entries detected, re-registering.");
                    }
                    break;
                }

                int count = 0;
                foreach (var id in ids)
                {
                    var tex = loadTexture(id);
                    if (tex == null) continue;

                    string address = buildAddress(id);
                    dict[address] = tex.Cast<Il2CppSystem.Object>();
                    extraAction?.Invoke(id, address);
                    count++;
                }

                TextureHelperLogInfo($"Registered {count} {label} textures in Addressables cache.");
            }
            catch (Exception ex)
            {
                TextureHelperLogError($"RegisterTexturesInManager ({label}) failed: {ex}");
            }
        }

        /// <summary>销毁缓存中的所有纹理并清空字典。</summary>
        public static void DestroyAndClearCache(Dictionary<string, Texture2D> cache)
        {
            foreach (var tex in cache.Values)
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
            cache.Clear();
        }
    }
}
