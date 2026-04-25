using System;
using System.Collections.Generic;
using System.IO;

using HarmonyLib;

using Il2CppInterop.Runtime;

using ManosabaLoader.ModManager;
using ManosabaLoader.Utils;

using Naninovel;

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
    /// 由于源面板可能尚未加载，使用每帧轮询的 MonoBehaviour 延迟初始化。
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

        public static void Init(Harmony harmony)
        {
            // 仅一个补丁：在 TitleUi.Awake 后挂载延迟注册组件
            harmony.PatchAll(typeof(ChoiceHandler_TitleUi_Patch));
            HandlerLogInfo("ModChoiceHandlerLoader initialized.");
        }

        /// <summary>
        /// TitleUi.Awake 后调用：收集所有 mod 的 handler 条目并启动延迟注册。
        /// </summary>
        public static void Awake()
        {
            if (registered) return;
            CollectPendingHandlers();
            if (pendingHandlers.Count == 0)
            {
                HandlerLogDebug("No mod choice handlers configured; skipping.");
                return;
            }

            HandlerLogInfo($"Found {pendingHandlers.Count} mod choice handler(s); waiting for source panels to load.");

            // 加载所有立绘到 Sprite 缓存
            LoadAllSprites();

            // 触发原版面板加载（异步、丢弃 UniTask）
            TryTriggerSourcePanelLoad();

            // 挂载每帧轮询组件
            try
            {
                var component = Plugin.Instance.AddComponent<ModChoiceHandlerWatcherComponent>();
            }
            catch (Exception ex)
            {
                HandlerLogError($"Failed to mount ModChoiceHandlerWatcherComponent: {ex}");
            }
        }

        /// <summary>
        /// 收集所有 mod（包括工作区）配置的 ChoiceHandlers。
        /// </summary>
        private static void CollectPendingHandlers()
        {
            pendingHandlers.Clear();

            foreach (var mod in ModManager.ModManager.Items.Values)
            {
                if (mod?.Description?.ChoiceHandlers == null) continue;
                foreach (var ch in mod.Description.ChoiceHandlers)
                    if (!string.IsNullOrEmpty(ch?.Id))
                        pendingHandlers.Add(ch);
            }

            if (ScriptWorkingManager.IsEnabled && ScriptWorkingManager.ModInfo?.Description?.ChoiceHandlers != null)
            {
                foreach (var ch in ScriptWorkingManager.ModInfo.Description.ChoiceHandlers)
                    if (!string.IsNullOrEmpty(ch?.Id))
                        pendingHandlers.Add(ch);
            }
        }

        /// <summary>
        /// 从磁盘加载所有立绘并构建 Sprite 缓存。
        /// </summary>
        private static void LoadAllSprites()
        {
            foreach (var entry in pendingHandlers)
            {
                if (spriteCache.ContainsKey(entry.Id)) continue;

                string modPath = ResolveModRootForHandler(entry);
                if (string.IsNullOrEmpty(modPath))
                {
                    HandlerLogWarning($"Could not resolve mod root for handler '{entry.Id}'; skipping sprite load.");
                    continue;
                }

                string portraitPath = Path.Combine(modPath, entry.Portrait ?? "");
                if (!File.Exists(portraitPath))
                {
                    HandlerLogWarning($"Portrait file not found for handler '{entry.Id}': {portraitPath}");
                    continue;
                }

                try
                {
                    var bytes = File.ReadAllBytes(portraitPath);
                    var tex = new Texture2D(2, 2);
                    if (!ImageConversion.LoadImage(tex, bytes))
                    {
                        HandlerLogError($"Failed to decode portrait image for handler '{entry.Id}': {portraitPath}");
                        UnityEngine.Object.Destroy(tex);
                        continue;
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
        }

        /// <summary>
        /// 找到 handler 对应的 mod 目录。
        /// 通过反查 ModManager.Items 找出包含此 ChoiceHandler 条目的 mod。
        /// </summary>
        private static string ResolveModRootForHandler(ModItem.ModChoiceHandler entry)
        {
            // 优先检查工作区
            if (ScriptWorkingManager.IsEnabled && ScriptWorkingManager.ModInfo?.Description?.ChoiceHandlers != null)
            {
                foreach (var ch in ScriptWorkingManager.ModInfo.Description.ChoiceHandlers)
                {
                    if (ReferenceEquals(ch, entry))
                        return ScriptWorkingManager.WorkspacePath;
                }
            }

            foreach (var kv in ModManager.ModManager.Items)
            {
                if (kv.Value?.Description?.ChoiceHandlers == null) continue;
                foreach (var ch in kv.Value.Description.ChoiceHandlers)
                {
                    if (ReferenceEquals(ch, entry))
                        return Path.Combine(Plugin.Instance.ModRootPath, kv.Key);
                }
            }
            return null;
        }

        /// <summary>
        /// 触发原版 Trial / TrialHiro 面板加载。
        /// 仅 fire-and-forget；UniTask 不在主线程同步等待。
        /// </summary>
        private static void TryTriggerSourcePanelLoad()
        {
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

                // 仅当对应 metadata 存在时触发
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
        /// 由 WatcherComponent 在源面板都可见时调用。
        /// </summary>
        internal static bool TryFinalizeRegistration()
        {
            if (registered) return true;

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
    /// 每帧检查源面板是否已加载，一旦可见即完成注册并自毁。
    /// </summary>
    public class ModChoiceHandlerWatcherComponent : MonoBehaviour
    {
        private const int MaxAttempts = 600; // ~10 秒 (60fps)
        private int attempts = 0;
        private int reTriggerInterval = 60; // 每 60 帧重试一次 GetOrAddActor

        void Update()
        {
            attempts++;

            if (ModChoiceHandlerLoader.TryFinalizeRegistration())
            {
                UnityEngine.Object.Destroy(this);
                return;
            }

            // 周期性重试 GetOrAddActor 以防初次失败
            if (attempts % reTriggerInterval == 0)
            {
                try
                {
                    var chMgr = Engine.GetServiceOrErr<ChoiceHandlerManager>();
                    if (chMgr != null)
                    {
                        var (ema, hiro) = ModChoiceHandlerLoader.FindLoadedSourcePanels();
                        if (ema == null && chMgr.Configuration.Metadata.ContainsId("Trial"))
                            { var _ = chMgr.GetOrAddActor("Trial"); }
                        if (hiro == null && chMgr.Configuration.Metadata.ContainsId("TrialHiro"))
                            { var _ = chMgr.GetOrAddActor("TrialHiro"); }
                    }
                }
                catch { /* swallow; we'll retry next frame */ }
            }

            if (attempts >= MaxAttempts)
            {
                ModChoiceHandlerLoader.HandlerLogWarning(
                    $"ModChoiceHandlerWatcherComponent giving up after {attempts} attempts; " +
                    "source panels never appeared. Mod handlers will not be registered.");
                UnityEngine.Object.Destroy(this);
            }
        }
    }

    [HarmonyPatch]
    static class ChoiceHandler_TitleUi_Patch
    {
        [HarmonyPatch(typeof(WitchTrials.Views.TitleUi), "Awake")]
        [HarmonyPostfix]
        static void TitleUi_Awake_PostfixForChoiceHandler()
        {
            ModChoiceHandlerLoader.Awake();
        }
    }
}
