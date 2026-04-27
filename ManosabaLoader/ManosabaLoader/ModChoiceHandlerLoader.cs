using System;
using System.Collections.Generic;
using System.IO;

using HarmonyLib;

using Il2CppInterop.Runtime;

using ManosabaLoader.ModManager;
using ManosabaLoader.Utils;

using Naninovel;
using Naninovel.UI;

using UnityEngine;
using UnityEngine.UI;

using WitchTrials.Views;

namespace ManosabaLoader
{
    /// <summary>
    /// Mod 自定义 @choice handler 加载器。
    ///
    /// 策略：克隆原版 TrialChoicePanel@Ema / TrialChoicePanel@Hiro 面板，
    /// 替换其中的人物立绘 Sprite，注入到 VirtualResourceProvider，
    /// 并向 ChoiceHandlersConfiguration.Metadata 添加新条目。
    ///
    /// IL2CPP 约束：仅依赖同步 API；不 patch 任何 UniTask 异步方法。
    ///
    /// 生命周期与 ModClueLoader 等数据加载器对齐：
    ///   - <see cref="LoadModData"/> 由 <c>ModResourceLoader.LoadSelectedModData</c> 按 mod 分发，
    ///     收集条目并预加载 Sprite。
    ///   - <see cref="TryFinalizeRegistration"/> 由 <see cref="TrialChoiceHandlerPanel_Awake_Patch"/>
    ///     在源面板 Awake 时调用，执行克隆 + Metadata 注册（idempotent）。
    ///   - <see cref="TryTriggerSourcePanelLoad"/> 由 <c>ModResourceLoader.Awake</c>
    ///     （TitleUi.Awake 后）触发原版面板 fire-and-forget 加载。
    /// </summary>
    public static class ModChoiceHandlerLoader
    {
        public static Action<string> HandlerLogMessage;
        public static Action<string> HandlerLogInfo;
        public static Action<string> HandlerLogDebug;
        public static Action<string> HandlerLogWarning;
        public static Action<string> HandlerLogError;

        /// <summary>VirtualResourceProvider 在 providersMap 中的注册键。</summary>
        public const string ProviderKey = "ModChoiceHandlers";

        /// <summary>克隆面板的 PathPrefix（与 ProviderKey 同名以保持简单）。</summary>
        public const string PathPrefix = "ModChoiceHandlers";

        /// <summary>所有待注册的 mod handler 条目（来自 info.json）。</summary>
        private static readonly List<ModItem.ModChoiceHandler> pendingHandlers = new();

        /// <summary>已注册过的 handler ID（避免重复注册）。</summary>
        private static readonly HashSet<string> registeredIds = new();

        /// <summary>Sprite 缓存：handler ID → Sprite。</summary>
        private static readonly Dictionary<string, Sprite> spriteCache = new();

        /// <summary>是否已完成注册。</summary>
        private static bool registered = false;

        /// <summary>
        /// TryFinalizeRegistration 重入保护。
        /// Instantiate 克隆面板会同步触发 cloned panel 的 Awake → 我们的 Postfix
        /// → 再次进入 TryFinalizeRegistration。若不阻断会无限递归直到栈溢出崩溃。
        /// </summary>
        private static bool finalizing = false;

        public static void Init(Harmony harmony)
        {
            harmony.PatchAll(typeof(TrialChoiceHandlerPanel_Awake_Patch));
            HandlerLogInfo("ModChoiceHandlerLoader patches applied.");
        }

        /// <summary>加载指定 mod 的 choice handler 条目并预加载立绘。</summary>
        public static void LoadModData(string modKey, string modPath, ModItem modItem)
        {
            if (modItem?.Description?.ChoiceHandlers == null) return;

            int added = 0;
            foreach (var ch in modItem.Description.ChoiceHandlers)
            {
                if (ch == null || string.IsNullOrEmpty(ch.Id)) continue;
                pendingHandlers.Add(ch);
                LoadSprite(ch, modPath);
                added++;
            }

            if (added > 0)
            {
                HandlerLogMessage($"Loaded {added} mod choice handler(s) for mod: {modKey}");
                // 触发原版源面板的 fire-and-forget 加载；面板 Awake 后由 Harmony patch 完成注册。
                // 同时立刻尝试一次：若源面板恰好已驻留内存，无需等待 Awake 触发。
                TryTriggerSourcePanelLoad();
                TryFinalizeRegistration();
            }
        }

        /// <summary>清除所有 mod handler 缓存（释放 Sprite 与 Texture）。</summary>
        public static void ClearModData()
        {
            pendingHandlers.Clear();
            registeredIds.Clear();
            foreach (var sp in spriteCache.Values)
            {
                if (sp == null) continue;
                var tex = sp.texture;
                UnityEngine.Object.Destroy(sp);
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
            spriteCache.Clear();
            registered = false;
            finalizing = false;
            HandlerLogInfo("ChoiceHandlerLoader data cleared.");
        }

        /// <summary>
        /// 从磁盘加载单个 handler 的立绘并构建 Sprite 缓存。
        /// 与 ModClueLoader.RegisterClueTextures 风格一致，使用直接传入的 modPath 而非反查。
        /// </summary>
        private static void LoadSprite(ModItem.ModChoiceHandler entry, string modPath)
        {
            if (spriteCache.ContainsKey(entry.Id)) return;

            if (string.IsNullOrEmpty(modPath))
            {
                HandlerLogWarning($"Empty mod path for handler '{entry.Id}'; skipping sprite load.");
                return;
            }

            string portraitPath = Path.Combine(modPath, entry.Portrait ?? "");
            if (!File.Exists(portraitPath))
            {
                HandlerLogWarning($"Portrait file not found for handler '{entry.Id}': {portraitPath}");
                return;
            }

            try
            {
                var bytes = File.ReadAllBytes(portraitPath);
                var tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, bytes))
                {
                    HandlerLogError($"Failed to decode portrait image for handler '{entry.Id}': {portraitPath}");
                    UnityEngine.Object.Destroy(tex);
                    return;
                }
                tex.name = $"ModChoicePortrait_{entry.Id}";
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;

                // 使用与原版 ChoicePortrait_Hiro 相同的 pivot / pixelsPerUnit
                var sprite = Sprite.Create(
                    tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.31f, 0.5f),
                    100f);
                sprite.name = $"ModChoicePortrait_{entry.Id}";
                sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;

                spriteCache[entry.Id] = sprite;
                HandlerLogDebug($"Loaded portrait for handler '{entry.Id}' ({tex.width}x{tex.height}).");
            }
            catch (Exception ex)
            {
                HandlerLogError($"Failed to load portrait for handler '{entry.Id}': {ex}");
            }
        }

        /// <summary>
        /// 触发原版 Trial / TrialHiro 面板加载。
        /// 仅 fire-and-forget；UniTask 不在主线程同步等待。
        /// 由 ModResourceLoader.Awake（TitleUi.Awake 后）调用一次。
        /// </summary>
        internal static void TryTriggerSourcePanelLoad()
        {
            if (registered) return;
            if (pendingHandlers.Count == 0) return;

            try
            {
                var chMgr = Engine.GetServiceOrErr<ChoiceHandlerManager>();
                if (chMgr == null) return;

                bool needEma = false, needHiro = false;
                foreach (var entry in pendingHandlers)
                {
                    if (string.Equals(entry.BasePanel, "TrialHiro", StringComparison.OrdinalIgnoreCase))
                        needHiro = true;
                    else
                        needEma = true;
                }

                var metaMap = chMgr.Configuration.Metadata;
                if (needEma && metaMap.ContainsId("Trial"))
                {
                    HandlerLogDebug("Triggering Trial actor construction (fire-and-forget).");
                    var _ = chMgr.GetOrAddActor("Trial");
                }
                if (needHiro && metaMap.ContainsId("TrialHiro"))
                {
                    HandlerLogDebug("Triggering TrialHiro actor construction (fire-and-forget).");
                    var _ = chMgr.GetOrAddActor("TrialHiro");
                }
            }
            catch (Exception ex)
            {
                HandlerLogWarning($"Could not trigger source panel load: {ex.Message}");
            }
        }

        /// <summary>
        /// 在内存中查找已加载的 Trial 面板源。
        /// 通过 gameObject.name 匹配 "@Ema" / "@Hiro" 后缀。
        /// </summary>
        internal static (GameObject ema, GameObject hiro) FindLoadedSourcePanels()
        {
            GameObject ema = null, hiro = null;
            try
            {
                var panels = Resources.FindObjectsOfTypeAll<TrialChoiceHandlerPanel>();
                foreach (var panel in panels)
                {
                    if (panel == null || panel.gameObject == null) continue;
                    string name = panel.gameObject.name ?? "";
                    if (ema == null && name.Contains("@Ema")) ema = panel.gameObject;
                    if (hiro == null && name.Contains("@Hiro")) hiro = panel.gameObject;
                    if (ema != null && hiro != null) break;
                }
            }
            catch (Exception ex)
            {
                HandlerLogWarning($"FindLoadedSourcePanels failed: {ex.Message}");
            }
            return (ema, hiro);
        }

        /// <summary>
        /// 完成所有 mod handler 的注册。
        /// 由 <see cref="TrialChoiceHandlerPanel_Awake_Patch"/> 在每个源面板 Awake 时调用。
        /// 幂等：当源面板齐备且尚未注册时执行一次，之后立即返回 true。
        /// </summary>
        internal static bool TryFinalizeRegistration()
        {
            if (registered) return true;
            // 重入保护：Instantiate 克隆面板会同步触发其 Awake → 触发我们的 Postfix → 再次进入此方法。
            // 若不阻断会无限递归（每次都尝试克隆所有 pendingHandlers）直到栈溢出。
            if (finalizing) return false;
            if (pendingHandlers.Count == 0) return false;

            var (emaSrc, hiroSrc) = FindLoadedSourcePanels();

            // 只检查实际需要的源面板
            bool needEma = false, needHiro = false;
            foreach (var entry in pendingHandlers)
            {
                if (string.Equals(entry.BasePanel, "TrialHiro", StringComparison.OrdinalIgnoreCase))
                    needHiro = true;
                else
                    needEma = true;
            }

            if (needEma && emaSrc == null) return false;
            if (needHiro && hiroSrc == null) return false;

            finalizing = true;
            try
            {
                var rpm = Engine.GetServiceOrErr<ResourceProviderManager>();
                if (rpm == null)
                {
                    HandlerLogWarning("ResourceProviderManager not available yet.");
                    return false;
                }

                var chMgr = Engine.GetServiceOrErr<ChoiceHandlerManager>();
                if (chMgr == null)
                {
                    HandlerLogWarning("ChoiceHandlerManager not available yet.");
                    return false;
                }

                var virtualProvider = new VirtualResourceProvider();
                int registeredCount = 0;

                foreach (var entry in pendingHandlers)
                {
                    if (registeredIds.Contains(entry.Id))
                    {
                        HandlerLogDebug($"Handler '{entry.Id}' already registered; skipping.");
                        continue;
                    }

                    if (!spriteCache.TryGetValue(entry.Id, out var sprite) || sprite == null)
                    {
                        HandlerLogWarning($"No sprite available for handler '{entry.Id}'; skipping.");
                        continue;
                    }

                    var src = string.Equals(entry.BasePanel, "TrialHiro", StringComparison.OrdinalIgnoreCase)
                        ? hiroSrc
                        : emaSrc;
                    if (src == null)
                    {
                        HandlerLogWarning($"Source panel '{entry.BasePanel}' missing for handler '{entry.Id}'.");
                        continue;
                    }

                    // 关键点：克隆模板必须保持 active 状态。
                    // 若 SetActive(false)，Engine.Instantiate 出的新实例也继承 inactive，
                    // Unity 不会在 inactive GameObject 上调用 Awake，导致面板内部
                    // SerializeField / 内部缓存（例如 CanvasGroup 引用）保持未初始化，
                    // 后续 UIChoiceHandler.Initialize 调用 SetVisibility 时 NRE。
                    //
                    // 模板 GameObject 放在 DontDestroyOnLoad 场景且无 Canvas 父级，
                    // 因此即使 active，也不会真正渲染或参与场景 UI 流程。
                    var clone = UnityEngine.Object.Instantiate(src);
                    clone.name = $"TrialChoicePanel@Mod_{entry.Id}";
                    UnityEngine.Object.DontDestroyOnLoad(clone);

                    if (!SwapPortraitSprite(clone, sprite))
                    {
                        HandlerLogWarning($"Failed to find portrait Image in cloned panel for handler '{entry.Id}'.");
                    }

                    string resourcePath = $"{PathPrefix}/{entry.Id}";
                    virtualProvider.AddResource<GameObject>(resourcePath, clone);

                    var meta = new ChoiceHandlerMetadata();
                    // Implementation 是 actor 包装类（UIChoiceHandler），不是面板组件（TrialChoiceHandlerPanel）。
                    // 面板组件由 prefab 上的 [ActorResources(typeof(ChoiceHandlerPanel))] 自动定位。
                    // 直接复用 vanilla Trial 的 Implementation 以确保 implNameToType 静态字典中已存在该类型，
                    // 同时也保证我们使用的是和原版完全一致的 actor 包装类。
                    string implName = "Naninovel.UIChoiceHandler, Elringus.Naninovel.Runtime";
                    var trialMeta = chMgr.Configuration.Metadata.GetMetaById("Trial");
                    if (trialMeta != null && !string.IsNullOrEmpty(trialMeta.Implementation))
                        implName = trialMeta.Implementation;
                    meta.Implementation = implName;
                    meta.Loader = new ResourceLoaderConfiguration();
                    meta.Loader.PathPrefix = PathPrefix;
                    meta.Loader.ProviderTypes = new Il2CppSystem.Collections.Generic.List<string>();
                    meta.Loader.ProviderTypes.Add(ProviderKey);
                    meta.WaitHideOnChoice = false;

                    chMgr.Configuration.Metadata.AddRecord(entry.Id, meta);
                    registeredIds.Add(entry.Id);
                    registeredCount++;
                    HandlerLogInfo($"Registered mod handler '{entry.Id}' (clone of {entry.BasePanel}).");
                }

                // 注册 provider（仅在第一次完成后）
                if (registeredCount > 0)
                {
                    if (!rpm.providersMap.ContainsKey(ProviderKey))
                    {
                        rpm.providersMap.Add(ProviderKey, virtualProvider.Cast<IResourceProvider>());
                        HandlerLogDebug($"Registered VirtualResourceProvider with key '{ProviderKey}'.");
                    }
                    else
                    {
                        HandlerLogDebug($"providersMap already contains '{ProviderKey}'; reusing.");
                    }
                }

                registered = true;
                HandlerLogMessage($"Mod choice handler registration complete: {registeredCount} new entries.");
                return true;
            }
            catch (Exception ex)
            {
                HandlerLogError($"TryFinalizeRegistration failed: {ex}");
                return false;
            }
            finally
            {
                finalizing = false;
            }
        }

        /// <summary>
        /// 在克隆面板内查找立绘 Image 并替换其 sprite。
        /// 优先匹配 sprite 名称以 "ChoicePortrait_" 开头的 Image，
        /// 退化策略：选 RectTransform 高度最大的 Image。
        /// </summary>
        private static bool SwapPortraitSprite(GameObject clone, Sprite modSprite)
        {
            try
            {
                var images = clone.GetComponentsInChildren<Image>(true);
                if (images == null || images.Length == 0) return false;

                Image best = null;
                foreach (var img in images)
                {
                    if (img == null) continue;
                    var sp = img.sprite;
                    if (sp == null) continue;
                    string spName = sp.name ?? "";
                    if (spName.StartsWith("ChoicePortrait_"))
                    {
                        best = img;
                        break;
                    }
                }

                if (best == null)
                {
                    // 退化：选 RectTransform 高度最大的
                    float maxHeight = 0;
                    foreach (var img in images)
                    {
                        if (img == null) continue;
                        var rt = img.rectTransform;
                        if (rt == null) continue;
                        float h = Math.Abs(rt.rect.height);
                        if (h > maxHeight)
                        {
                            maxHeight = h;
                            best = img;
                        }
                    }
                }

                if (best == null) return false;

                best.sprite = modSprite;
                best.SetNativeSize();
                return true;
            }
            catch (Exception ex)
            {
                HandlerLogError($"SwapPortraitSprite failed: {ex}");
                return false;
            }
        }
    }

    /// <summary>
    /// 源面板（TrialChoiceHandlerPanel @Ema/@Hiro）Awake 时尝试 finalize 注册。
    ///
    /// Awake 实际声明在基类 <see cref="ChoiceHandlerPanel"/>（Naninovel 运行时）；
    /// TrialChoiceHandlerPanel / ChoiceHandlerPanelModified 都未重写。Harmony 必须 patch
    /// 实际声明类型上的方法（typeof(TrialChoiceHandlerPanel) 找不到 Awake 会抛
    /// "Undefined target method"）。因此此 Postfix 会在所有 ChoiceHandlerPanel 子类
    /// Awake 时触发，用 TryCast 过滤，仅对 TrialChoiceHandlerPanel 实例响应。
    /// </summary>
    [HarmonyPatch]
    static class TrialChoiceHandlerPanel_Awake_Patch
    {
        [HarmonyPatch(typeof(ChoiceHandlerPanel), "Awake")]
        [HarmonyPostfix]
        static void Postfix(ChoiceHandlerPanel __instance)
        {
            if (__instance == null) return;
            // IL2CPP 安全的类型过滤：非 TrialChoiceHandlerPanel 子类 TryCast 返回 null。
            if (__instance.TryCast<TrialChoiceHandlerPanel>() == null) return;
            ModChoiceHandlerLoader.TryFinalizeRegistration();
        }
    }
}
