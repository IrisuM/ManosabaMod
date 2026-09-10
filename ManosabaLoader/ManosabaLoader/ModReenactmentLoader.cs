using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using HarmonyLib;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

using ManosabaLoader.ModManager;
using ManosabaLoader.Utils;

using Naninovel;

using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

using WitchTrials.Views;

namespace ManosabaLoader
{
    /// <summary>
    /// Mod 自定义再现演出（Reenactment）加载器。
    /// Mod 作者文档见 docs/reenactment.zh-Hans.md / docs/reenactment.en.md，本注释只记录实现层面的约束与结构。
    ///
    /// ===== 原版结构 =====
    /// 原版每段再现是一个 spawn prefab <c>Reenact_ChXX_EYY_SZZ</c>（Addressables 地址 <c>Naninovel/Spawn/Reenact_...</c>）：
    ///   根                         挂 <see cref="SpawnableReenactment"/>（Spawn.IParameterized / IAwaitable / DestroySpawned.IAwaitable）
    ///   ├ Image_kari               开发期占位整图（"仮"），不用
    ///   ├ Background_nomal         舞台背景
    ///   ├ Story                    舞台图层（角色 / 道具 / 箭头 / 背景），由 Story Timeline 驱动
    ///   ├ BackgroundBlack          黑边（black / black_L / black_R）
    ///   ├ Curtain                  骨骼动画幕布
    ///   └ Timeline                 CurtainOpen / CurtainClose / Story 三个 PlayableDirector
    /// <see cref="SpawnableReenactment"/> 的 AwaitSpawn = 播放 _openingDirector 再播放 _storiesDirector[_currenIndex]，
    /// AwaitDestroy = 播放 _closingDirector；等待方式是 Cysharp UniTask 盯着 director 结束。
    ///
    /// ===== 本加载器做法 =====
    ///   1. TitleUi.Awake 时通过 SpawnManager 私有的 ResourceLoader&lt;GameObject&gt; 异步加载模板 prefab（Load + holder 常驻），
    ///      每帧轮询 IsLoaded（不碰 UniTask）。
    ///   2. 模板就绪后，为每个 mod 再现 Instantiate 一份克隆（保持 active 但关掉全部渲染器并停放到远处，DontDestroyOnLoad；
    ///      实例化后由 SetSpawnParameters Postfix 还原位置并打开渲染器）：
    ///      - 删除 Story 下原版图层、Image_kari、Background_nomal；销毁原版 Story director 组件
    ///      - 按 info.json 在 Story 下创建 SpriteRenderer 图层（排序层 / 材质 / layer 从原版图层复制）
    ///      - 每个步骤创建一个 PlayableDirector + 运行时生成的固定时长空 TimelineAsset，写回 _storiesDirector
    ///      - 挂一个 <c>__ModReenact:&lt;Id&gt;</c> 标记子节点，实例化后据此识别
    ///   3. 克隆注册进 VirtualResourceProvider，其 ProvisionSource 插到 spawn loader 最前面，
    ///      剧本 <c>@spawn "&lt;Id&gt;" params:N</c> 走原版流程（预加载 / 实例化 / 等待 / 存档还原全部原生）。
    ///   4. Postfix <see cref="SpawnableReenactment.SetSpawnParameters"/>（同步方法，安全）：识别 mod 实例，激活对象，
    ///      解析步骤序号并强制写入 _currenIndex，应用该步骤 t=0 的姿态，登记到驱动列表。
    ///   5. <see cref="ModReenactmentTicker"/> 每帧：对登记的实例读取当前步骤 director 的 state / time，
    ///      按关键帧求值；director 停止后定格到步骤末尾姿态。asap（存档还原 / 快进）直接定格到末尾。
    ///
    /// ⚠ 不能 Harmony patch AwaitSpawn / AwaitDestroy / RunTimelineAsync（返回 UniTask / Task 的异步方法），
    ///   本加载器完全不碰它们，等待逻辑全部交给原版组件。
    /// </summary>
    public static class ModReenactmentLoader
    {
        public static Action<string> ReenactLogMessage;
        public static Action<string> ReenactLogInfo;
        public static Action<string> ReenactLogDebug;
        public static Action<string> ReenactLogWarning;
        public static Action<string> ReenactLogError;

        /// <summary>spawn loader 的资源路径前缀（与 <see cref="SpawnConfiguration.DefaultPathPrefix"/> 一致）。</summary>
        public const string SpawnPathPrefix = "Spawn";
        /// <summary>1 世界单位 = 100 像素（原版舞台图层的 Sprite PPU）。</summary>
        public const float PixelsPerUnit = 100f;
        /// <summary>克隆根下的标记子节点名前缀，后接再现 Id。</summary>
        private const string MarkerPrefix = "__ModReenact:";
        private const string StoryNodeName = "Story";
        private const string TimelineNodeName = "Timeline";
        private const string StoryDirectorPrefix = "ModStory_";
        /// <summary>克隆时直接删掉的原版节点（舞台内容由 mod 自己提供）。</summary>
        private static readonly string[] VanillaNodesToRemove = { "Image_kari", "Background_nomal" };
        /// <summary>模板加载超时（秒），超时只提示一次。</summary>
        private const float TemplateLoadTimeout = 30f;
        /// <summary>模板克隆的停放偏移（远离镜头）。</summary>
        private static readonly Vector3 ParkOffset = new(0f, -5000f, 0f);
        /// <summary>spawn loader 尚未就绪时的重试间隔（帧）。</summary>
        private const int RegisterRetryFrames = 30;

        // ------------------------------------------------------------------
        // 运行时数据结构
        // ------------------------------------------------------------------

        private enum EaseKind { Linear, InQuad, OutQuad, InOutQuad, InCubic, OutCubic, InOutCubic, InSine, OutSine, InOutSine, Step }

        private sealed class KeyRuntime
        {
            public float Time;
            public Vector2? Position;
            public Vector2? Scale;
            public float? Rotation;
            public float? Alpha;
            public Color? Tint;
            public bool? Visible;
            public Sprite Sprite;
            public bool HasSprite;
            public EaseKind Ease;
        }

        private sealed class TrackRuntime
        {
            public string Layer;
            public KeyRuntime[] Keys;
        }

        private sealed class StepRuntime
        {
            public float Duration;
            public TrackRuntime[] Tracks;
        }

        private sealed class SceneDef
        {
            public string Id;
            public string ModKey;
            public string ModPath;
            public string Template;
            public ModItem.ModReenactment Entry;
            public StepRuntime[] Steps;
            /// <summary>贴图缓存：图片路径 → Texture2D。</summary>
            public readonly Dictionary<string, Texture2D> Textures = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>Sprite 缓存："路径|轴心" → Sprite。</summary>
            public readonly Dictionary<string, Sprite> Sprites = new(StringComparer.OrdinalIgnoreCase);
            public bool Built;
            public GameObject Prefab;
            /// <summary>模板根节点原始 localPosition（克隆被停放到远处，实例化后要还原）。</summary>
            public Vector3 TemplateLocalPos;
            public readonly HashSet<string> WarnedLayers = new(StringComparer.Ordinal);
        }

        private sealed class TemplateState
        {
            public string Name;
            public bool Requested;
            public float RequestedAt;
            public bool TimeoutWarned;
            public bool Failed;
            public GameObject Prefab;
        }

        private sealed class LayerRuntime
        {
            public ModItem.ModReenactmentLayer Def;
            public Transform Tf;
            public SpriteRenderer Sr;
            public Color Tint = Color.white;
            public float Alpha = 1f;
        }

        private sealed class InstanceState
        {
            public int InstanceId;
            public SceneDef Def;
            public SpawnableReenactment Comp;
            public GameObject Root;
            public readonly Dictionary<string, LayerRuntime> Layers = new(StringComparer.Ordinal);
            public PlayableDirector[] Directors;
            public int Step;
            public bool ObservedPlaying;
            public float ArmedAt;
        }

        private static readonly Dictionary<string, SceneDef> scenes = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, TemplateState> templates = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, InstanceState> instances = new();
        private static readonly List<InstanceState> active = new();

        private static VirtualResourceProvider virtualProvider;
        private static ProvisionSource provisionSource;
        private static ResourceLoader<GameObject> spawnLoader;
        private static Il2CppSystem.Object holder;
        private static bool tickerCreated;
        private static bool loaderNullWarned;
        /// <summary>ProvisionSource 已确认在当前 spawn loader 里；false 时 Tick 会定期重试 EnsureProvisionSource。</summary>
        private static bool provisionInserted;

        // ------------------------------------------------------------------
        // 初始化 / 数据加载
        // ------------------------------------------------------------------

        public static void Init(Harmony harmony)
        {
            harmony.PatchAll(typeof(SpawnableReenactment_SetSpawnParameters_Patch));
            ReenactLogDebug("ModReenactmentLoader initialized.");
        }

        /// <summary>读取一个 mod 的 Reenactments 条目，预加载贴图并生成 Sprite。TitleUi.Awake 时对所有 mod 调用一次。</summary>
        public static void LoadModData(string modKey, string modPath, ModItem modItem)
        {
            var list = modItem?.Description?.Reenactments;
            if (list == null || list.Length == 0) return;

            foreach (var entry in list)
            {
                if (entry == null) continue;
                if (string.IsNullOrWhiteSpace(entry.Id))
                {
                    ReenactLogWarning($"Mod '{modKey}': a Reenactments entry has no Id; skipped.");
                    continue;
                }
                if (scenes.ContainsKey(entry.Id))
                {
                    ReenactLogWarning($"Mod '{modKey}': reenactment '{entry.Id}' already registered (duplicate Id); skipped.");
                    continue;
                }
                if (entry.Layers == null || entry.Layers.Length == 0)
                    ReenactLogWarning($"Reenactment '{entry.Id}' has no Layers; the stage will be empty.");

                var def = new SceneDef
                {
                    Id = entry.Id,
                    ModKey = modKey,
                    ModPath = modPath,
                    Template = string.IsNullOrWhiteSpace(entry.Template) ? "Reenact_Ch01_E01_S02" : entry.Template.Trim(),
                    Entry = entry,
                };

                try
                {
                    // 图层贴图
                    var layerNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var layer in entry.Layers ?? [])
                    {
                        if (layer == null) continue;
                        if (string.IsNullOrWhiteSpace(layer.Name))
                        {
                            ReenactLogWarning($"Reenactment '{entry.Id}': a layer has no Name; it cannot be animated.");
                            continue;
                        }
                        if (!layerNames.Add(layer.Name))
                            ReenactLogWarning($"Reenactment '{entry.Id}': duplicate layer name '{layer.Name}'.");
                        if (!string.IsNullOrWhiteSpace(layer.Sprite))
                            GetOrCreateSprite(def, layer.Sprite, layer);
                    }

                    // 步骤 / 关键帧
                    def.Steps = BuildSteps(def, entry, layerNames);
                }
                catch (Exception ex)
                {
                    ReenactLogError($"Reenactment '{entry.Id}' failed to load: {ex}");
                    continue;
                }

                scenes[def.Id] = def;
                if (!templates.ContainsKey(def.Template))
                    templates[def.Template] = new TemplateState { Name = def.Template };
                ReenactLogInfo($"Loaded reenactment '{def.Id}' from mod '{modKey}': {entry.Layers?.Length ?? 0} layer(s), {def.Steps.Length} step(s), template '{def.Template}'.");
            }
        }

        private static StepRuntime[] BuildSteps(SceneDef def, ModItem.ModReenactment entry, HashSet<string> layerNames)
        {
            var steps = entry.Steps;
            if (steps == null || steps.Length == 0)
                steps = [new ModItem.ModReenactmentStep { Duration = 1f, Tracks = [] }];

            var result = new StepRuntime[steps.Length];
            for (int s = 0; s < steps.Length; s++)
            {
                var step = steps[s] ?? new ModItem.ModReenactmentStep();
                float duration = step.Duration;
                if (!(duration > 0.01f))
                {
                    ReenactLogWarning($"Reenactment '{def.Id}' step {s}: Duration must be > 0; using 0.01.");
                    duration = 0.01f;
                }

                var tracks = new List<TrackRuntime>();
                foreach (var track in step.Tracks ?? [])
                {
                    if (track == null) continue;
                    if (string.IsNullOrWhiteSpace(track.Layer))
                    {
                        ReenactLogWarning($"Reenactment '{def.Id}' step {s}: a track has no Layer; skipped.");
                        continue;
                    }
                    if (!layerNames.Contains(track.Layer))
                        ReenactLogWarning($"Reenactment '{def.Id}' step {s}: track refers to unknown layer '{track.Layer}'.");

                    var keys = new List<KeyRuntime>();
                    foreach (var key in track.Keys ?? [])
                    {
                        if (key == null) continue;
                        var kr = new KeyRuntime
                        {
                            Time = Mathf.Max(0f, key.Time),
                            Position = ToVector2(key.Position, null),
                            Scale = ToScale(key.Scale, null),
                            Rotation = key.Rotation,
                            Alpha = key.Alpha,
                            Visible = key.Visible,
                            Ease = ParseEase(key.Ease, def.Id, s),
                        };
                        if (!string.IsNullOrEmpty(key.Tint))
                        {
                            var c = ParseColor(key.Tint, def.Id);
                            if (c.HasValue) kr.Tint = c.Value;
                        }
                        if (!string.IsNullOrWhiteSpace(key.Sprite))
                        {
                            var layerDef = FindLayerDef(entry, track.Layer);
                            kr.Sprite = GetOrCreateSprite(def, key.Sprite, layerDef);
                            kr.HasSprite = kr.Sprite != null;
                        }
                        if (kr.Time > duration + 0.0001f)
                            ReenactLogWarning($"Reenactment '{def.Id}' step {s}: key at {kr.Time}s on '{track.Layer}' is beyond the step Duration ({duration}s).");
                        keys.Add(kr);
                    }
                    keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                    tracks.Add(new TrackRuntime { Layer = track.Layer, Keys = keys.ToArray() });
                }

                result[s] = new StepRuntime { Duration = duration, Tracks = tracks.ToArray() };
            }
            return result;
        }

        private static ModItem.ModReenactmentLayer FindLayerDef(ModItem.ModReenactment entry, string name)
        {
            foreach (var l in entry.Layers ?? [])
                if (l != null && l.Name == name) return l;
            return null;
        }

        private static Vector2? ToVector2(float[] arr, Vector2? fallback)
        {
            if (arr == null || arr.Length == 0) return fallback;
            if (arr.Length == 1) return new Vector2(arr[0], arr[0]);
            return new Vector2(arr[0], arr[1]);
        }

        private static Vector2? ToScale(float[] arr, Vector2? fallback)
        {
            if (arr == null || arr.Length == 0) return fallback;
            if (arr.Length == 1) return new Vector2(arr[0], arr[0]);
            return new Vector2(arr[0], arr[1]);
        }

        private static EaseKind ParseEase(string ease, string sceneId, int step)
        {
            if (string.IsNullOrWhiteSpace(ease)) return EaseKind.Linear;
            if (Enum.TryParse<EaseKind>(ease.Trim(), true, out var kind)) return kind;
            ReenactLogWarning($"Reenactment '{sceneId}' step {step}: unknown Ease '{ease}', using Linear.");
            return EaseKind.Linear;
        }

        private static Color? ParseColor(string html, string sceneId)
        {
            string s = html.Trim();
            if (!s.StartsWith("#")) s = "#" + s;
            if (ColorUtility.TryParseHtmlString(s, out var c)) return c;
            ReenactLogWarning($"Reenactment '{sceneId}': invalid color '{html}'.");
            return null;
        }

        /// <summary>从 mod 目录加载图片并按图层轴心生成 Sprite（带缓存）。失败返回 null 并记录警告。</summary>
        private static Sprite GetOrCreateSprite(SceneDef def, string relPath, ModItem.ModReenactmentLayer layerDef)
        {
            float px = 0.5f, py = 0.5f;
            if (layerDef?.Pivot != null && layerDef.Pivot.Length >= 2) { px = layerDef.Pivot[0]; py = layerDef.Pivot[1]; }
            float ppu = layerDef != null && layerDef.PixelsPerUnit > 0f ? layerDef.PixelsPerUnit : PixelsPerUnit;
            string cacheKey = $"{relPath}|{px:R},{py:R}|{ppu:R}";
            if (def.Sprites.TryGetValue(cacheKey, out var cached) && cached != null) return cached;

            var tex = GetOrLoadTexture(def, relPath);
            if (tex == null) return null;

            try
            {
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(px, py), ppu);
                sprite.name = $"ModReenact_{def.Id}_{Path.GetFileNameWithoutExtension(relPath)}";
                sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
                def.Sprites[cacheKey] = sprite;
                return sprite;
            }
            catch (Exception ex)
            {
                ReenactLogError($"Reenactment '{def.Id}': Sprite.Create failed for '{relPath}': {ex}");
                return null;
            }
        }

        private static Texture2D GetOrLoadTexture(SceneDef def, string relPath)
        {
            if (def.Textures.TryGetValue(relPath, out var cached) && cached != null) return cached;

            string full = Path.Combine(def.ModPath, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                ReenactLogWarning($"Reenactment '{def.Id}': image not found: '{relPath}' (resolved to {full}).");
                return null;
            }
            try
            {
                var bytes = File.ReadAllBytes(full);
                var tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, bytes))
                {
                    ReenactLogWarning($"Reenactment '{def.Id}': failed to decode image '{relPath}'.");
                    return null;
                }
                tex.name = $"ModReenact_{def.Id}_{Path.GetFileNameWithoutExtension(relPath)}";
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                def.Textures[relPath] = tex;
                return tex;
            }
            catch (Exception ex)
            {
                ReenactLogError($"Reenactment '{def.Id}': failed to load image '{relPath}': {ex}");
                return null;
            }
        }

        // ------------------------------------------------------------------
        // 注册（spawn loader / 模板加载 / 克隆）
        // ------------------------------------------------------------------

        /// <summary>每个 Title→Trial 周期开始时调用：清掉上一轮的实例记录，确保 provision source 与模板加载已启动。</summary>
        public static void OnTitleAwake()
        {
            instances.Clear();
            active.Clear();
            if (scenes.Count == 0) return;

            EnsureTicker();
            EnsureProvisionSource();
            RequestTemplates();
        }

        private static void EnsureTicker()
        {
            if (tickerCreated) return;
            try
            {
                Plugin.Instance.AddComponent<ModReenactmentTicker>();
                tickerCreated = true;
                ReenactLogDebug("ModReenactmentTicker created.");
            }
            catch (Exception ex)
            {
                ReenactLogError($"Could not create ModReenactmentTicker: {ex}");
            }
        }

        /// <summary>
        /// 把 VirtualResourceProvider 的 ProvisionSource 插到 SpawnManager.loader 的最前面（幂等）。
        /// 语言切换等导致 loader 重建时由 ReInjectModProvisionSources 再次调用。
        /// </summary>
        internal static void EnsureProvisionSource()
        {
            if (scenes.Count == 0) return;
            try
            {
                if (!Engine.Initialized) return;
                var svc = Engine.GetService<ISpawnManager>();
                if (svc == null)
                {
                    ReenactLogWarning("ISpawnManager not available yet.");
                    return;
                }
                var mgr = svc.Cast<SpawnManager>();
                // 游戏实际服务是 GigaCreation 的 MultipliableSpawn，它用自己的 _loader，基类 SpawnManager.loader 始终为 null。
                IntPtr loaderPtr = Il2CppFieldHelper.GetReferenceField(mgr, "_loader");
                if (loaderPtr == IntPtr.Zero)
                    loaderPtr = Il2CppFieldHelper.GetReferenceField(mgr, "loader");
                if (loaderPtr == IntPtr.Zero)
                {
                    if (!loaderNullWarned)
                    {
                        loaderNullWarned = true;
                        ReenactLogWarning("Spawn manager loader (_loader / loader) is null; will keep retrying.");
                    }
                    return;
                }
                loaderNullWarned = false;

                bool sameLoader = spawnLoader != null && spawnLoader.Pointer == loaderPtr;
                spawnLoader = new ResourceLoader<GameObject>(loaderPtr);

                if (virtualProvider == null)
                {
                    virtualProvider = new VirtualResourceProvider();
                    provisionSource = new ProvisionSource(virtualProvider.Cast<IResourceProvider>(), SpawnPathPrefix);
                    holder = new Il2CppSystem.Object();
                }

                var sources = spawnLoader.ProvisionSources;
                bool present = false;
                for (int i = 0; i < sources.Count; i++)
                {
                    var s = sources[i];
                    // 注意：ProvisionSource 定义了 ==/!= 运算符，经 interop 与 null 字面量比较会 NRE，必须用 is null
                    if (s is not null && s.Pointer == provisionSource.Pointer) { present = true; break; }
                }
                if (!present)
                {
                    sources.System_Collections_IList_Insert(0, provisionSource);
                    ReenactLogInfo($"Inserted mod reenactment provision source into spawn loader (prefix '{SpawnPathPrefix}').");
                }
                provisionInserted = true;

                if (!sameLoader)
                {
                    // loader 换了：还没拿到 prefab 的模板需要重新请求加载
                    foreach (var t in templates.Values)
                        if (t.Prefab == null) t.Requested = false;
                }
            }
            catch (Exception ex)
            {
                ReenactLogError($"EnsureProvisionSource failed: {ex}");
            }
        }

        private static void RequestTemplates()
        {
            if (spawnLoader == null) return;
            foreach (var t in templates.Values)
            {
                if (t.Requested || t.Prefab != null) continue;
                try
                {
                    // 异步加载并常驻（holder 永不释放）；返回的 UniTask 不接，之后每帧轮询 IsLoaded。
                    spawnLoader.Load(t.Name, holder);
                    t.Requested = true;
                    t.RequestedAt = Time.realtimeSinceStartup;
                    t.TimeoutWarned = false;
                    ReenactLogInfo($"Requested template prefab '{t.Name}' from spawn loader.");
                }
                catch (Exception ex)
                {
                    ReenactLogError($"Failed to request template '{t.Name}': {ex}");
                    t.Failed = true;
                }
            }
        }

        /// <summary>每帧：轮询模板加载、驱动活动实例。</summary>
        private static int retryCountdown;

        internal static void Tick()
        {
            if (scenes.Count == 0) return;
            try
            {
                if (spawnLoader == null || !provisionInserted)
                {
                    // TitleUi.Awake 时 spawn loader 可能还没建好，或上次注册中途失败：定期重试
                    if (--retryCountdown <= 0)
                    {
                        retryCountdown = RegisterRetryFrames;
                        EnsureProvisionSource();
                        RequestTemplates();
                    }
                    if (spawnLoader == null) return;
                }
                PollTemplates();
                if (active.Count > 0) DriveActive();
            }
            catch (Exception ex)
            {
                ReenactLogError($"Tick failed: {ex}");
            }
        }

        private static void PollTemplates()
        {
            foreach (var t in templates.Values)
            {
                if (!t.Requested || t.Prefab != null || t.Failed) continue;

                bool loaded;
                try { loaded = spawnLoader.IsLoaded(t.Name); }
                catch (Exception ex)
                {
                    ReenactLogError($"IsLoaded('{t.Name}') failed: {ex}");
                    t.Failed = true;
                    continue;
                }

                if (!loaded)
                {
                    if (!t.TimeoutWarned && Time.realtimeSinceStartup - t.RequestedAt > TemplateLoadTimeout)
                    {
                        t.TimeoutWarned = true;
                        ReenactLogWarning($"Template '{t.Name}' still not loaded after {TemplateLoadTimeout}s. Check the Template name (e.g. Reenact_Ch01_E01_S02).");
                    }
                    continue;
                }

                GameObject prefab = null;
                try
                {
                    var res = spawnLoader.GetLoaded(t.Name);
                    UnityEngine.Object obj = res is not null ? res.Object : null;
                    prefab = obj != null ? obj.TryCast<GameObject>() : null;
                }
                catch (Exception ex)
                {
                    ReenactLogError($"GetLoaded('{t.Name}') failed: {ex}");
                }

                if (prefab == null)
                {
                    ReenactLogWarning($"Template '{t.Name}' loaded but is not a GameObject; mod reenactments using it are disabled.");
                    t.Failed = true;
                    continue;
                }

                t.Prefab = prefab;
                ReenactLogInfo($"Template prefab '{t.Name}' loaded.");
                if (Plugin.Instance != null && Plugin.Instance.isDebug)
                    DumpHierarchy(prefab, $"template '{t.Name}'");

                foreach (var def in scenes.Values)
                {
                    if (def.Built || def.Template != t.Name) continue;
                    if (BuildScene(def, prefab))
                    {
                        string path = $"{SpawnPathPrefix}/{def.Id}";
                        try
                        {
                            virtualProvider.SetResource<GameObject>(path, def.Prefab);
                        }
                        catch
                        {
                            virtualProvider.AddResource<GameObject>(path, def.Prefab);
                        }
                        def.Built = true;
                        ReenactLogMessage($"Registered mod reenactment '{def.Id}' at '{path}' (template '{t.Name}').");
                    }
                }
            }
        }

        /// <summary>从模板 prefab 克隆并改造出一个 mod 再现 prefab（保持 inactive，DontDestroyOnLoad）。</summary>
        private static bool BuildScene(SceneDef def, GameObject templatePrefab)
        {
            GameObject root = null;
            try
            {
                root = UnityEngine.Object.Instantiate(templatePrefab);
                root.name = $"ModReenact_{def.Id}";
                UnityEngine.Object.DontDestroyOnLoad(root);
                // 模板保持 active（Instantiate 出的实例会继承 inactive 状态，导致 director 不更新、脚本卡死），
                // 改为关掉所有渲染器并停放到远处；实例化后在 SetSpawnParameters Postfix 里还原。
                def.TemplateLocalPos = root.transform.localPosition;
                root.transform.localPosition = def.TemplateLocalPos + ParkOffset;

                var comp = root.GetComponentInChildren<SpawnableReenactment>(true);
                if (comp == null)
                {
                    ReenactLogError($"Template '{def.Template}' has no SpawnableReenactment component; cannot build '{def.Id}'.");
                    UnityEngine.Object.DestroyImmediate(root);
                    return false;
                }

                // 标记
                var marker = new GameObject(MarkerPrefix + def.Id);
                marker.transform.SetParent(root.transform, false);

                // 舞台节点
                var story = root.transform.Find(StoryNodeName);
                if (story == null)
                {
                    ReenactLogWarning($"Template '{def.Template}' has no '{StoryNodeName}' node; creating one at root.");
                    var storyGo = new GameObject(StoryNodeName);
                    storyGo.transform.SetParent(root.transform, false);
                    story = storyGo.transform;
                }

                // 从原版舞台图层复制渲染设置
                int sortingLayerId = 0, baseOrder = 0, unityLayer = root.layer;
                Material vanillaMat = null;
                var vanillaSrs = story.GetComponentsInChildren<SpriteRenderer>(true);
                SpriteRenderer sample = vanillaSrs != null && vanillaSrs.Length > 0 ? vanillaSrs[0] : null;
                if (sample == null)
                {
                    var any = root.GetComponentsInChildren<SpriteRenderer>(true);
                    if (any != null && any.Length > 0) sample = any[0];
                }
                if (sample != null)
                {
                    sortingLayerId = sample.sortingLayerID;
                    baseOrder = sample.sortingOrder;
                    vanillaMat = sample.sharedMaterial;
                    unityLayer = sample.gameObject.layer;
                }
                ReenactLogDebug($"'{def.Id}': sortingLayer={sortingLayerId} baseOrder={baseOrder} layer={unityLayer} material={(vanillaMat != null ? vanillaMat.name : "<null>")}");

                // 删除原版舞台内容
                for (int i = story.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.DestroyImmediate(story.GetChild(i).gameObject);
                foreach (var name in VanillaNodesToRemove)
                {
                    var t = root.transform.Find(name);
                    if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);
                }

                // 销毁原版 Story director 组件（保留其 GameObject 无妨）
                IntPtr oldArrPtr = Il2CppFieldHelper.GetReferenceField(comp, "_storiesDirector");
                if (oldArrPtr != IntPtr.Zero)
                {
                    var oldArr = new Il2CppReferenceArray<PlayableDirector>(oldArrPtr);
                    for (int i = 0; i < oldArr.Length; i++)
                    {
                        var d = oldArr[i];
                        if (d != null) UnityEngine.Object.DestroyImmediate(d);
                    }
                }

                PlayableDirector opening = null;
                IntPtr openingPtr = Il2CppFieldHelper.GetReferenceField(comp, "_openingDirector");
                if (openingPtr != IntPtr.Zero) opening = new PlayableDirector(openingPtr);
                else ReenactLogWarning($"Template '{def.Template}': _openingDirector is null.");

                var timelineNode = root.transform.Find(TimelineNodeName) ?? root.transform;

                // 步骤 director
                int n = def.Steps.Length;
                var arr = new Il2CppReferenceArray<PlayableDirector>(n);
                for (int i = 0; i < n; i++)
                {
                    var go = new GameObject($"{StoryDirectorPrefix}{i}");
                    go.transform.SetParent(timelineNode, false);
                    var dir = go.AddComponent<PlayableDirector>();
                    dir.playOnAwake = false;
                    dir.extrapolationMode = DirectorWrapMode.None;
                    if (opening != null) dir.timeUpdateMode = opening.timeUpdateMode;
                    dir.playableAsset = CreateTimeline(def.Id, i, def.Steps[i].Duration);
                    arr[i] = dir;
                }
                Il2CppFieldHelper.SetReferenceField(comp, "_storiesDirector", arr.Pointer);
                Il2CppFieldHelper.SetIntField(comp, "_currenIndex", 0);

                // 图层
                foreach (var layer in def.Entry.Layers ?? [])
                {
                    if (layer == null || string.IsNullOrWhiteSpace(layer.Name)) continue;
                    var go = new GameObject(layer.Name);
                    go.layer = unityLayer;
                    go.transform.SetParent(story, false);
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sortingLayerID = sortingLayerId;
                    sr.sortingOrder = baseOrder + layer.Order;
                    if (vanillaMat != null) sr.sharedMaterial = vanillaMat;
                    if (!string.IsNullOrWhiteSpace(layer.Sprite))
                        sr.sprite = GetOrCreateSprite(def, layer.Sprite, layer);

                    var rt = new LayerRuntime { Def = layer, Tf = go.transform, Sr = sr };
                    ApplyLayerDefaults(rt, def);
                }

                SetRenderersEnabled(root, false);
                def.Prefab = root;
                ReenactLogDebug($"Built reenactment prefab '{root.name}': {def.Entry.Layers?.Length ?? 0} layer(s), {n} director(s).");
                return true;
            }
            catch (Exception ex)
            {
                ReenactLogError($"BuildScene('{def.Id}') failed: {ex}");
                try { if (root != null) UnityEngine.Object.DestroyImmediate(root); } catch { }
                return false;
            }
        }

        private static PlayableAsset CreateTimeline(string sceneId, int step, float duration)
        {
            var so = ScriptableObject.CreateInstance(Il2CppType.Of<TimelineAsset>());
            var asset = so.Cast<TimelineAsset>();
            asset.name = $"ModReenact_{sceneId}_Step{step}";
            asset.hideFlags = HideFlags.DontUnloadUnusedAsset;
            asset.durationMode = TimelineAsset.DurationMode.FixedLength;
            asset.fixedDuration = duration;
            return asset;
        }

        private static void ApplyLayerDefaults(LayerRuntime rt, SceneDef def)
        {
            var layer = rt.Def;
            var pos = ToVector2(layer.Position, Vector2.zero) ?? Vector2.zero;
            var scale = ToScale(layer.Scale, Vector2.one) ?? Vector2.one;
            rt.Tf.localPosition = new Vector3(pos.x, pos.y, 0f);
            rt.Tf.localScale = new Vector3(scale.x, scale.y, 1f);
            rt.Tf.localRotation = Quaternion.Euler(0f, 0f, layer.Rotation);
            rt.Tint = Color.white;
            if (!string.IsNullOrEmpty(layer.Tint))
            {
                var c = ParseColor(layer.Tint, def.Id);
                if (c.HasValue) rt.Tint = c.Value;
            }
            rt.Alpha = Mathf.Clamp01(layer.Alpha);
            PushColor(rt);
            rt.Sr.enabled = layer.Visible;
        }

        private static void PushColor(LayerRuntime rt)
        {
            rt.Sr.color = new Color(rt.Tint.r, rt.Tint.g, rt.Tint.b, rt.Tint.a * rt.Alpha);
        }

        // ------------------------------------------------------------------
        // 实例：SetSpawnParameters Postfix
        // ------------------------------------------------------------------

        internal static void HandleSetSpawnParameters(SpawnableReenactment inst, Il2CppSystem.Collections.Generic.IReadOnlyList<string> parameters, bool asap)
        {
            try
            {
                if (inst == null) return;
                var go = inst.gameObject;
                var pars = ToManaged(parameters);
                string paramsStr = string.Join(", ", pars);
                int vanillaIdx = Il2CppFieldHelper.GetIntField(inst, "_currenIndex", -1);
                // 有意用 Info 级别：这是确认原版 params 语义的一手诊断信息，每次审判只有几行
                ReenactLogInfo($"SetSpawnParameters on '{go.name}': params=[{paramsStr}] asap={asap} _currenIndex={vanillaIdx}");

                var rootGo = FindMarkedRoot(go.transform, out string sceneId);
                if (rootGo == null) return; // 原版再现
                if (!scenes.TryGetValue(sceneId, out var def))
                {
                    ReenactLogWarning($"Instance marked as mod reenactment '{sceneId}' but no definition is loaded.");
                    return;
                }

                // 还原模板停放前的状态（位置 / 渲染器）；@spawn 自带的 pos: 会在之后由原版流程覆盖
                if (!rootGo.activeSelf) rootGo.SetActive(true);
                rootGo.transform.localPosition = def.TemplateLocalPos;
                SetRenderersEnabled(rootGo, true);
                go = rootGo;

                int step = 0;
                if (pars.Count > 0)
                {
                    string first = pars[0];
                    if (!int.TryParse(first, out step))
                    {
                        ReenactLogWarning($"Reenactment '{sceneId}': params[0]='{first}' is not a step index; using 0.");
                        step = 0;
                    }
                }
                if (step < 0 || step >= def.Steps.Length)
                {
                    ReenactLogWarning($"Reenactment '{sceneId}': step {step} out of range (0..{def.Steps.Length - 1}); clamped.");
                    step = Mathf.Clamp(step, 0, def.Steps.Length - 1);
                }
                Il2CppFieldHelper.SetIntField(inst, "_currenIndex", step);

                var state = GetOrCreateInstanceState(inst, go, def);
                if (state == null) return;
                state.Step = step;
                state.ObservedPlaying = false;
                state.ArmedAt = Time.realtimeSinceStartup;

                Evaluate(state, step, 0f);
                if (asap)
                {
                    Evaluate(state, step, def.Steps[step].Duration);
                    active.Remove(state);
                    ReenactLogInfo($"Mod reenactment '{sceneId}' step {step}: asap → snapped to end pose.");
                }
                else
                {
                    if (!active.Contains(state)) active.Add(state);
                    ReenactLogInfo($"Mod reenactment '{sceneId}' step {step} armed ({def.Steps[step].Duration}s).");
                }
            }
            catch (Exception ex)
            {
                ReenactLogError($"HandleSetSpawnParameters failed: {ex}");
            }
        }

        /// <summary>IL2CPP IReadOnlyList&lt;string&gt; → 托管 List（Count 在 IReadOnlyCollection 接口上）。</summary>
        private static List<string> ToManaged(Il2CppSystem.Collections.Generic.IReadOnlyList<string> parameters)
        {
            var list = new List<string>();
            if (parameters == null) return list;
            try
            {
                int count = parameters.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<string>>().Count;
                for (int i = 0; i < count; i++) list.Add(parameters[i]);
            }
            catch (Exception ex)
            {
                ReenactLogWarning($"Could not read spawn parameters: {ex.Message}");
            }
            return list;
        }

        /// <summary>开关根下所有 Renderer（含 SpriteRenderer / 骨骼蒙皮渲染器）。</summary>
        private static void SetRenderersEnabled(GameObject root, bool enabled)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers == null) return;
            foreach (var r in renderers)
                if (r != null) r.enabled = enabled;
        }

        /// <summary>从组件所在节点向上找带标记子节点的根（SpawnableReenactment 可能不在克隆根上）。</summary>
        private static GameObject FindMarkedRoot(Transform start, out string sceneId)
        {
            for (var t = start; t != null; t = t.parent)
            {
                sceneId = FindMarker(t);
                if (sceneId != null) return t.gameObject;
            }
            sceneId = null;
            return null;
        }

        private static string FindMarker(Transform root)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c.name.StartsWith(MarkerPrefix, StringComparison.Ordinal))
                    return c.name.Substring(MarkerPrefix.Length);
            }
            return null;
        }

        private static InstanceState GetOrCreateInstanceState(SpawnableReenactment inst, GameObject go, SceneDef def)
        {
            int id = go.GetInstanceID();
            if (instances.TryGetValue(id, out var existing))
            {
                bool alive;
                try { alive = existing.Root != null && existing.Comp != null; } catch { alive = false; }
                if (alive && existing.Def == def) return existing;
                instances.Remove(id);
                active.Remove(existing);
            }

            var state = new InstanceState { InstanceId = id, Def = def, Comp = inst, Root = go };

            var story = go.transform.Find(StoryNodeName);
            if (story == null)
            {
                ReenactLogError($"Mod reenactment '{def.Id}' instance has no '{StoryNodeName}' node.");
                return null;
            }
            foreach (var layer in def.Entry.Layers ?? [])
            {
                if (layer == null || string.IsNullOrWhiteSpace(layer.Name)) continue;
                var t = story.Find(layer.Name);
                if (t == null)
                {
                    ReenactLogWarning($"Mod reenactment '{def.Id}': layer node '{layer.Name}' missing on instance.");
                    continue;
                }
                var sr = t.GetComponent<SpriteRenderer>();
                if (sr == null) continue;
                var rt = new LayerRuntime { Def = layer, Tf = t, Sr = sr };
                // 实例状态从 prefab 默认值重建（克隆不带托管字段）
                rt.Tint = Color.white;
                if (!string.IsNullOrEmpty(layer.Tint))
                {
                    var c = ParseColor(layer.Tint, def.Id);
                    if (c.HasValue) rt.Tint = c.Value;
                }
                rt.Alpha = Mathf.Clamp01(layer.Alpha);
                state.Layers[layer.Name] = rt;
            }

            var dirs = new PlayableDirector[def.Steps.Length];
            var all = go.GetComponentsInChildren<PlayableDirector>(true);
            int found = 0;
            if (all != null)
            {
                foreach (var d in all)
                {
                    if (d == null) continue;
                    string name = d.gameObject.name;
                    if (!name.StartsWith(StoryDirectorPrefix, StringComparison.Ordinal)) continue;
                    if (int.TryParse(name.Substring(StoryDirectorPrefix.Length), out int idx) && idx >= 0 && idx < dirs.Length)
                    {
                        dirs[idx] = d;
                        found++;
                    }
                }
            }
            if (found != dirs.Length)
                ReenactLogWarning($"Mod reenactment '{def.Id}': expected {dirs.Length} step director(s), found {found}.");
            state.Directors = dirs;

            instances[id] = state;
            return state;
        }

        // ------------------------------------------------------------------
        // 每帧驱动
        // ------------------------------------------------------------------

        private static void DriveActive()
        {
            for (int i = active.Count - 1; i >= 0; i--)
            {
                var st = active[i];
                bool alive;
                try { alive = st.Root != null && st.Comp != null; } catch { alive = false; }
                if (!alive)
                {
                    active.RemoveAt(i);
                    instances.Remove(st.InstanceId);
                    continue;
                }

                var step = st.Def.Steps[st.Step];
                var dir = st.Directors != null && st.Step < st.Directors.Length ? st.Directors[st.Step] : null;
                if (dir == null)
                {
                    // 没有 director 可盯：按实时时间推进
                    float t = Time.realtimeSinceStartup - st.ArmedAt;
                    Evaluate(st, st.Step, Mathf.Min(t, step.Duration));
                    if (t >= step.Duration) active.RemoveAt(i);
                    continue;
                }

                PlayState ps;
                double time;
                try { ps = dir.state; time = dir.time; }
                catch { active.RemoveAt(i); continue; }

                if (ps == PlayState.Playing)
                {
                    st.ObservedPlaying = true;
                    Evaluate(st, st.Step, (float)time);
                    continue;
                }

                bool currentIsOurs = false;
                try
                {
                    IntPtr cur = Il2CppFieldHelper.GetReferenceField(st.Comp, "_current");
                    currentIsOurs = cur != IntPtr.Zero && cur == dir.Pointer;
                }
                catch { }

                bool ended = st.ObservedPlaying
                    || (currentIsOurs && time >= step.Duration - 0.02)
                    || (Time.realtimeSinceStartup - st.ArmedAt > step.Duration + 8f);
                if (ended)
                {
                    Evaluate(st, st.Step, step.Duration);
                    active.RemoveAt(i);
                    ReenactLogDebug($"Mod reenactment '{st.Def.Id}' step {st.Step} finished.");
                }
            }
        }

        /// <summary>按关键帧求值并写入图层。每个属性在相邻定义它的关键帧之间插值，首帧前保持首帧值，末帧后保持末帧值。</summary>
        private static void Evaluate(InstanceState st, int stepIndex, float t)
        {
            var step = st.Def.Steps[stepIndex];
            foreach (var track in step.Tracks)
            {
                if (!st.Layers.TryGetValue(track.Layer, out var layer))
                {
                    if (st.Def.WarnedLayers.Add(track.Layer))
                        ReenactLogWarning($"Mod reenactment '{st.Def.Id}': track layer '{track.Layer}' not found on instance.");
                    continue;
                }
                var keys = track.Keys;
                if (keys.Length == 0) continue;

                bool colorDirty = false;

                // 连续属性
                if (TryInterpolate(keys, t, k => k.Position, out Vector2 pos))
                    layer.Tf.localPosition = new Vector3(pos.x, pos.y, 0f);
                if (TryInterpolate(keys, t, k => k.Scale, out Vector2 scale))
                    layer.Tf.localScale = new Vector3(scale.x, scale.y, 1f);
                if (TryInterpolate(keys, t, k => k.Rotation, out float rot))
                    layer.Tf.localRotation = Quaternion.Euler(0f, 0f, rot);
                if (TryInterpolate(keys, t, k => k.Alpha, out float alpha))
                {
                    layer.Alpha = Mathf.Clamp01(alpha);
                    colorDirty = true;
                }
                if (TryInterpolate(keys, t, k => k.Tint, out Color tint))
                {
                    layer.Tint = tint;
                    colorDirty = true;
                }
                if (colorDirty) PushColor(layer);

                // 离散属性
                var visKey = FindDiscrete(keys, t, k => k.Visible.HasValue);
                if (visKey != null) layer.Sr.enabled = visKey.Visible.Value;
                var spriteKey = FindDiscrete(keys, t, k => k.HasSprite);
                if (spriteKey != null && layer.Sr.sprite != spriteKey.Sprite) layer.Sr.sprite = spriteKey.Sprite;
            }
        }

        /// <summary>离散属性：取最后一个 Time &lt;= t 且定义了该属性的关键帧；t 在首个定义帧之前则取首个定义帧。</summary>
        private static KeyRuntime FindDiscrete(KeyRuntime[] keys, float t, Func<KeyRuntime, bool> defined)
        {
            KeyRuntime last = null, first = null;
            foreach (var k in keys)
            {
                if (!defined(k)) continue;
                first ??= k;
                if (k.Time <= t) last = k;
                else break;
            }
            return last ?? first;
        }

        private static bool TryInterpolate(KeyRuntime[] keys, float t, Func<KeyRuntime, Vector2?> get, out Vector2 value)
        {
            if (!FindSegment(keys, t, k => get(k).HasValue, out var k0, out var k1, out float u))
            {
                value = default;
                return false;
            }
            if (k0 == null) { value = get(k1).Value; return true; }
            if (k1 == null) { value = get(k0).Value; return true; }
            value = Vector2.LerpUnclamped(get(k0).Value, get(k1).Value, u);
            return true;
        }

        private static bool TryInterpolate(KeyRuntime[] keys, float t, Func<KeyRuntime, float?> get, out float value)
        {
            if (!FindSegment(keys, t, k => get(k).HasValue, out var k0, out var k1, out float u))
            {
                value = default;
                return false;
            }
            if (k0 == null) { value = get(k1).Value; return true; }
            if (k1 == null) { value = get(k0).Value; return true; }
            value = Mathf.LerpUnclamped(get(k0).Value, get(k1).Value, u);
            return true;
        }

        private static bool TryInterpolate(KeyRuntime[] keys, float t, Func<KeyRuntime, Color?> get, out Color value)
        {
            if (!FindSegment(keys, t, k => get(k).HasValue, out var k0, out var k1, out float u))
            {
                value = default;
                return false;
            }
            if (k0 == null) { value = get(k1).Value; return true; }
            if (k1 == null) { value = get(k0).Value; return true; }
            value = Color.LerpUnclamped(get(k0).Value, get(k1).Value, u);
            return true;
        }

        /// <summary>
        /// 找到 t 两侧定义了该属性的关键帧 k0（Time &lt;= t）与 k1（Time &gt; t），并按 k1 的缓动算出插值系数 u。
        /// 没有任何定义该属性的关键帧时返回 false。
        /// </summary>
        private static bool FindSegment(KeyRuntime[] keys, float t, Func<KeyRuntime, bool> defined, out KeyRuntime k0, out KeyRuntime k1, out float u)
        {
            k0 = null; k1 = null; u = 0f;
            foreach (var k in keys)
            {
                if (!defined(k)) continue;
                if (k.Time <= t) k0 = k;
                else { k1 = k; break; }
            }
            if (k0 == null && k1 == null) return false;
            if (k0 != null && k1 != null)
            {
                float span = k1.Time - k0.Time;
                float raw = span > 1e-5f ? Mathf.Clamp01((t - k0.Time) / span) : 1f;
                u = ApplyEase(k1.Ease, raw);
            }
            return true;
        }

        private static float ApplyEase(EaseKind ease, float u)
        {
            switch (ease)
            {
                case EaseKind.InQuad: return u * u;
                case EaseKind.OutQuad: return 1f - (1f - u) * (1f - u);
                case EaseKind.InOutQuad: return u < 0.5f ? 2f * u * u : 1f - Mathf.Pow(-2f * u + 2f, 2f) / 2f;
                case EaseKind.InCubic: return u * u * u;
                case EaseKind.OutCubic: return 1f - Mathf.Pow(1f - u, 3f);
                case EaseKind.InOutCubic: return u < 0.5f ? 4f * u * u * u : 1f - Mathf.Pow(-2f * u + 2f, 3f) / 2f;
                case EaseKind.InSine: return 1f - Mathf.Cos(u * Mathf.PI / 2f);
                case EaseKind.OutSine: return Mathf.Sin(u * Mathf.PI / 2f);
                case EaseKind.InOutSine: return -(Mathf.Cos(Mathf.PI * u) - 1f) / 2f;
                case EaseKind.Step: return u >= 1f ? 1f : 0f;
                default: return u;
            }
        }

        // ------------------------------------------------------------------
        // 诊断
        // ------------------------------------------------------------------

        /// <summary>Debug.OpenDebug 时把模板 prefab 的层级（节点 / 组件 / Sprite / 排序 / director）打到日志。</summary>
        private static void DumpHierarchy(GameObject root, string label)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"===== Hierarchy dump: {label} =====");
                DumpNode(root.transform, 0, sb);
                var comp = root.GetComponentInChildren<SpawnableReenactment>(true);
                if (comp != null)
                {
                    IntPtr arrPtr = Il2CppFieldHelper.GetReferenceField(comp, "_storiesDirector");
                    int count = arrPtr != IntPtr.Zero ? new Il2CppReferenceArray<PlayableDirector>(arrPtr).Length : -1;
                    sb.AppendLine($"SpawnableReenactment on '{comp.gameObject.name}': _storiesDirector.Length={count}, _currenIndex={Il2CppFieldHelper.GetIntField(comp, "_currenIndex", -1)}");
                }
                sb.Append("===== end dump =====");
                ReenactLogMessage(sb.ToString());
            }
            catch (Exception ex)
            {
                ReenactLogError($"DumpHierarchy failed: {ex}");
            }
        }

        private static void DumpNode(Transform t, int depth, StringBuilder sb)
        {
            var go = t.gameObject;
            sb.Append(' ', depth * 2).Append(go.activeSelf ? "[+] " : "[-] ").Append(go.name);
            sb.Append($"  pos={Fmt(t.localPosition)} scale={Fmt(t.localScale)} layer={go.layer}");

            var comps = go.GetComponents<Component>();
            if (comps != null)
            {
                var names = new List<string>();
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    string n = c.GetIl2CppType().Name;
                    if (n == "Transform") continue;
                    names.Add(n);
                }
                if (names.Count > 0) sb.Append("  {").Append(string.Join(", ", names)).Append('}');
            }

            var sr = go.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                string spriteName = sr.sprite != null ? sr.sprite.name : "<none>";
                sb.Append($"  sprite='{spriteName}' sortingLayer={sr.sortingLayerID} order={sr.sortingOrder} color=#{ToHex(sr.color)} enabled={sr.enabled}");
                if (sr.sharedMaterial != null) sb.Append($" mat='{sr.sharedMaterial.name}'");
            }
            var dir = go.GetComponent<PlayableDirector>();
            if (dir != null)
            {
                string asset = dir.playableAsset != null ? dir.playableAsset.name : "<none>";
                sb.Append($"  director: asset='{asset}' duration={dir.duration:0.###} wrap={dir.extrapolationMode} update={dir.timeUpdateMode} playOnAwake={dir.playOnAwake}");
            }
            sb.AppendLine();

            for (int i = 0; i < t.childCount; i++)
                DumpNode(t.GetChild(i), depth + 1, sb);
        }

        private static string Fmt(Vector3 v) => $"({v.x:0.###},{v.y:0.###},{v.z:0.###})";

        private static string ToHex(Color c)
        {
            int r = Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f);
            int g = Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f);
            int b = Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f);
            int a = Mathf.RoundToInt(Mathf.Clamp01(c.a) * 255f);
            return $"{r:X2}{g:X2}{b:X2}{a:X2}";
        }
    }

    /// <summary>每帧驱动 mod 再现演出（模板轮询 + 关键帧求值）。挂在 Plugin 对象上，全局唯一。</summary>
    public class ModReenactmentTicker : MonoBehaviour
    {
        void Update()
        {
            ModReenactmentLoader.Tick();
        }
    }

    // SetSpawnParameters 是同步方法，可以安全 Postfix（AwaitSpawn 等 UniTask 方法绝不能 patch）。
    static class SpawnableReenactment_SetSpawnParameters_Patch
    {
        [HarmonyPatch(typeof(SpawnableReenactment), nameof(SpawnableReenactment.SetSpawnParameters))]
        [HarmonyPostfix]
        static void Postfix(SpawnableReenactment __instance, Il2CppSystem.Collections.Generic.IReadOnlyList<string> parameters, bool asap)
        {
            ModReenactmentLoader.HandleSetSpawnParameters(__instance, parameters, asap);
        }
    }
}
