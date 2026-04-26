using System;
using System.Collections.Generic;
using System.IO;

using HarmonyLib;

using ManosabaLoader.ModManager;

using Naninovel;

using UnityEngine;
using UnityEngine.UI;

using WitchTrials.Views;

namespace ManosabaLoader
{
    /// <summary>
    /// Mod 自定义 cut-in 加载器（hijack-and-swap，全部基于同步方法）。
    ///
    /// ⚠ 关键约束（参见 ModMovieLoader.cs 顶部注释）：
    ///   IL2CPP 下 Harmony 不能 Patch 任何返回 UniTask / UniTask&lt;T&gt; 的方法，
    ///   即使 Prefix 不返回 false 也会破坏异步虚表，导致 MethodAccessException 崩溃。
    ///   因此本加载器**完全不触碰** GosubToObjectionCutIn.Execute。
    ///
    /// 策略：
    ///   1. 在 GosubToObjectionCutIn.Execute 内部，原版会调用同步方法
    ///        CustomVariableManager.SetVariableValue("objectionCutInSpawnPath",
    ///                                               "ObjectionCutIn_&lt;Kind&gt;")
    ///      然后再 gosub 到子例程做 @spawn。我们 Postfix 这个 SetVariableValue，
    ///      若 &lt;Kind&gt; 命中已注册 mod ID：
    ///        - 暂存 mod 条目（pendingEntry）；
    ///        - 用同一 manager 重新 SetVariableValue 把变量值改写为
    ///          "ObjectionCutIn_Hiro"，让原版 spawn 流程拿到现成的 Hiro prefab。
    ///      （重入由 insideRewrite 标志保护；ExtractKindFromCutInValue 同时兼容
    ///        裸 Kind 与带 "CutIn/" 前缀两种形式以防变量格式变化）
    ///   2. 在 ObjectionCutIn.SetSpawnParameters 的 Postfix 中（同步方法），
    ///      若 pendingEntry 非空，按 sprite 名称替换该实例上的 Image / SpriteRenderer，
    ///      然后清空 pendingEntry。
    ///
    /// 完全避开 UniTask 方法，与 ModClueLoader.SpawnableClueSpawn_Patch 同模式：
    /// 通过同步方法的 Postfix 在 Naninovel spawn 流程中介入。
    ///
    /// 优点：
    /// - 不 patch 任何 UniTask 方法，async 虚表安全
    /// - 无 prefab 克隆 / 无 watcher / 无每帧轮询
    /// - 无私有字段反射
    /// - 复用原版动画时间轴和 per-index 可见性切换
    ///
    /// ===== 性能优化（2026-04） =====
    ///
    /// 原始实现每次 spawn 做 7 次 GetComponentsInChildren 全树扫描，
    /// 加上每次 spawn 重新解析 entry.Shaders 的 HTML 颜色 / 可空浮点。
    /// 优化后：
    /// - **Texture 预加载**：Awake() 时同步读盘 + 解码所有已注册 mod sprite 的 Texture2D，
    ///   避免首次 swap 时同步阻塞 cut-in 帧。
    /// - **ResolvedShaders 预烘焙**：Awake() 时把 entry.Shaders 解析成 5 个 per-role
    ///   MaterialPropertyBlock 实例（null = 该 role 无覆盖）。运行时只需 SetPropertyBlock。
    /// - **InstanceCache 复用**：Naninovel 通过 GetOrSpawnAsync 复用同一 ObjectionCutIn 实例。
    ///   首次见到该实例时一次性扫描 Image / SpriteRenderer，并按 shader 名分桶；
    ///   后续 spawn 直接迭代缓存，0 次 GetComponentsInChildren。
    /// - **Per-instance dirty mask**：5 个 shader role 的 dirty 状态从静态全局
    ///   迁移到 InstanceCache 里，更精确（按实例独立追踪）。
    /// - **DumpReplaceableLayers 仅在 isDebug 时运行**：诊断功能默认关闭，
    ///   mod 作者通过 BepInEx config Debug.OpenDebug = true 启用。
    ///
    /// ===== Mod 作者使用注意 =====
    ///
    /// Hiro prefab 的 _objectLibrary[] 在不同 Index 下激活不同 pose，且映射非字面 1↔1。
    /// 经实测（仅记录 Hiro 模板，CreatureHiro / Ema 未验证）：
    ///   @gosubCutIn "..." Index:1  →  Hiro_CutIn_002 被激活
    ///   @gosubCutIn "..." Index:2  →  Hiro_CutIn_003 被激活
    ///   @gosubCutIn "..." Index:3  →  Hiro_CutIn_001 被激活
    ///
    /// 关键观察：激活是**累积 / 非互斥**的——同一 ObjectionCutIn 实例会被
    /// `GetOrSpawnAsync` 缓存复用，每次调用只 SetActive(true) 当前 Index 对应组，
    /// 既往激活并未被关闭。原版没问题是因为 _001/_002/_003 三张图轮廓**像素级一致**，
    /// 仅面部表情不同，所以即使同时渲染也能完美重叠看起来像一个人物。
    ///
    /// 这意味着 Mod 作者**只覆盖一张图会露馅**——其他未替换的原版 Hiro 轮廓
    /// 与 mod 图轮廓不一致，叠加时会有重影 / 双人物效果。
    ///
    /// **建议**：
    ///   1. 提供 3 张轮廓一致、仅表情不同的 mod 图，分别覆盖 _001 / _002 / _003
    ///      （保留 per-Index 表情效果，最接近原版手感）；
    ///   2. 或者三个槽位都填同一张 mod 图（视觉干净但失去 per-Index 表达力）。
    ///
    /// 启用 BepInEx config Debug.OpenDebug = true 后，第一次触发 mod cut-in 时
    /// loader 会把当前 ObjectionCutIn 实例上所有可替换 sprite 名打印到 BepInEx 日志，
    /// 作为 Sprites 字典 key 的一手参考。
    ///
    /// ===== Shader 参数覆盖 (Shaders) =====
    ///
    /// Cut-in prefab 内多个 SpriteRenderer 使用专属 Shader Graph shader 渲染：
    ///   全屏噪点背景       BackGround                     Shader Graphs/Background_0Fix
    ///   彩色玻璃碎片       RefuteCutIn_StainedGlass_001/2/3 Shader Graphs/Glasses_0Fix
    ///   彩色玻璃发光       RefuteCutIn_StainedGlass_luminescence Shader Graphs/Iuminescence_dezolve_0Fix
    ///   角色阴影           RefuteCutIn_Hiro_Shadow         Shader Graphs/Shadow_Fix
    ///   角色发光           RefuteCutIn_Hiro_luminescence   Shader Graphs/Iuminescence_Silhouette_0Fix
    /// 不同角色（Hiro/Ema 等）用不同 _角色 后缀的材质实例（如 Background_Hiro），
    /// 各自有不同的默认 _BackgroundA / _Color 等（这就是 "color tied to the cut-in"）。
    ///
    /// 实现：通过 <c>MaterialPropertyBlock</c> per-property 覆盖 SpriteRenderer 的
    /// shader 属性。MPB 不实例化材质，未覆盖的属性自动落到 sharedMaterial 默认值，
    /// 所以 mod 只需声明想改的字段即可，其余保持角色专属默认。
    ///
    /// 状态机（每个 shader role 独立，**按 ObjectionCutIn 实例独立追踪**）：
    ///   - mod cut-in 且对应 sub-config 含任意非空字段 → 用预烘焙 MPB 调用 SetPropertyBlock
    ///   - 任何其它 cut-in（含 vanilla）→ 若上次置过覆盖，则 SetPropertyBlock(null)
    ///     恢复 sharedMaterial 默认（角色原色）。
    ///
    /// 匹配规则：按 <c>SpriteRenderer.sharedMaterial.shader.name</c> 匹配，
    /// 而非按 GameObject 路径或材质名 — shader 名跨角色稳定，材质名带 _Hiro 后缀。
    ///
    /// 注：DigitalGlitch 是 Naninovel 内置的另一种 FX，与本游戏的 cut-in 无关
    /// （实测 BepInEx 日志中 cut-in 流程下从未触发 DigitalGlitch.SetSpawnParameters）。
    /// </summary>
    public static class ModObjectionCutInLoader
    {
        public static Action<string> CutInLogMessage;
        public static Action<string> CutInLogInfo;
        public static Action<string> CutInLogDebug;
        public static Action<string> CutInLogWarning;
        public static Action<string> CutInLogError;

        /// <summary>v1 仅支持 Hiro 模板（重写为 "Hiro" 让原版 spawn 命中现有 prefab）。</summary>
        public const string HiroTemplate = "Hiro";

        /// <summary>原版 GosubToObjectionCutIn 在 Execute 中写入的脚本变量名。</summary>
        private const string ObjectionCutInSpawnPathVar = "objectionCutInSpawnPath";

        /// <summary>Shader role 索引（用于 ResolvedShaders.Roles[] / InstanceCache.RenderersByRole[] / AppliedRoles[]）。</summary>
        private const int RoleBackground       = 0;
        private const int RoleStainedGlass     = 1;
        private const int RoleStainedGlassGlow = 2;
        private const int RoleCharShadow       = 3;
        private const int RoleCharGlow         = 4;
        private const int ShaderRoleCount      = 5;

        /// <summary>Shader 名常量（与 SpriteRenderer.sharedMaterial.shader.name 匹配）。</summary>
        private const string ShaderBackground        = "Shader Graphs/Background_0Fix";
        private const string ShaderGlasses           = "Shader Graphs/Glasses_0Fix";
        private const string ShaderGlassLuminescence = "Shader Graphs/Iuminescence_dezolve_0Fix";
        private const string ShaderCharShadow        = "Shader Graphs/Shadow_Fix";
        private const string ShaderCharLuminescence  = "Shader Graphs/Iuminescence_Silhouette_0Fix";

        private static readonly string[] RoleShaderNames =
        {
            ShaderBackground, ShaderGlasses, ShaderGlassLuminescence, ShaderCharShadow, ShaderCharLuminescence
        };

        private static readonly string[] RoleNames =
        {
            "Background", "StainedGlass", "StainedGlassGlow", "CharacterShadow", "CharacterGlow"
        };

        /// <summary>已注册的 mod cut-in：ID → 条目 + mod 根 + 预烘焙 shader MPB。</summary>
        private sealed class RegistryEntry
        {
            public ModItem.ModObjectionCutIn Entry;
            public string ModRoot;
            public ResolvedShaders Resolved;
        }

        /// <summary>预烘焙的 shader 覆盖。每个 role 一个 MPB（null = 无覆盖）。</summary>
        private sealed class ResolvedShaders
        {
            public readonly MaterialPropertyBlock[] Roles = new MaterialPropertyBlock[ShaderRoleCount];
        }

        /// <summary>
        /// 每个 ObjectionCutIn 实例的组件缓存。Naninovel 通过 GetOrSpawnAsync 复用
        /// 同一实例，所以 prefab 子树稳定，缓存可长期持有。
        /// </summary>
        private sealed class InstanceCache
        {
            public List<Image> Images = new();
            public List<string> ImageVanillaNames = new();
            public List<SpriteRenderer> Sprites = new();
            public List<string> SpriteVanillaNames = new();
            public List<SpriteRenderer>[] RenderersByRole = new List<SpriteRenderer>[ShaderRoleCount];
            /// <summary>该实例上每个 role 是否曾被 mod 覆盖（用于回滚到 vanilla）。</summary>
            public bool[] AppliedRoles = new bool[ShaderRoleCount];
        }

        private static readonly Dictionary<string, RegistryEntry> registry = new();

        /// <summary>Awake 时预加载的 (modId, vanillaName) → Texture2D。</summary>
        private static readonly Dictionary<(string, string), Texture2D> textureCache = new();

        /// <summary>(modId, vanillaSpriteName) → Sprite 缓存（首次 swap 时基于 vanilla pivot/PPU 创建）。</summary>
        private static readonly Dictionary<(string, string), Sprite> spriteCache = new();

        /// <summary>每个 ObjectionCutIn GameObject InstanceID → 组件缓存。</summary>
        private static readonly Dictionary<int, InstanceCache> instanceCaches = new();

        /// <summary>暂存：上次 SetVariableValue 命中的 mod 条目，供 SetSpawnParameters Postfix 消费。</summary>
        private static RegistryEntry pendingEntry = null;

        /// <summary>SetVariableValue 重入保护：避免我们自己的 mgr.SetVariableValue 调用再次进入本 Postfix 逻辑。</summary>
        private static bool insideRewrite = false;

        /// <summary>是否已 dump 过克隆图层（仅在第一次替换时打印一次，且仅 isDebug 时）。</summary>
        private static bool layersDumped = false;

        public static void Init(Harmony harmony)
        {
            harmony.PatchAll(typeof(CustomVariableManager_SetVariableValue_Patch));
            harmony.PatchAll(typeof(ObjectionCutIn_SetSpawnParameters_Patch));
            harmony.PatchAll(typeof(CutIn_TitleUi_Patch));
            CutInLogInfo("ModObjectionCutInLoader initialized.");
        }

        /// <summary>
        /// TitleUi.Awake 后调用：扫描所有 mod 的 CutIns 条目，预加载 Texture2D，
        /// 预烘焙 ResolvedShaders。
        /// </summary>
        public static void Awake()
        {
            registry.Clear();
            // textureCache / spriteCache 不清：跨 mod 切换时已解析的资源仍然有效（key 包含 modId）
            // instanceCaches 必须清：Title→Trial 间 ObjectionCutIn GameObject 已销毁，旧 InstanceID 失效
            instanceCaches.Clear();

            int count = 0;
            foreach (var kv in ModManager.ModManager.Items)
            {
                var desc = kv.Value?.Description;
                if (desc?.CutIns == null) continue;
                string modRoot = Path.Combine(Plugin.Instance.ModRootPath, kv.Key);
                foreach (var entry in desc.CutIns)
                {
                    if (RegisterEntry(entry, modRoot)) count++;
                }
            }

            if (ScriptWorkingManager.IsEnabled && ScriptWorkingManager.ModInfo?.Description?.CutIns != null)
            {
                foreach (var entry in ScriptWorkingManager.ModInfo.Description.CutIns)
                {
                    if (RegisterEntry(entry, ScriptWorkingManager.WorkspacePath)) count++;
                }
            }

            // 进入 Title 时清空暂存，避免上一局残留状态
            pendingEntry = null;
            insideRewrite = false;
            // 重置 layersDumped，让每次 Title→Trial 都能拿到一份新的诊断 dump
            layersDumped = false;

            if (count == 0)
                CutInLogDebug("No mod cut-ins configured; hijack patches inactive.");
            else
                CutInLogInfo($"Registered {count} mod cut-in(s). Will hijack via SetVariableValue at runtime.");
        }

        private static bool RegisterEntry(ModItem.ModObjectionCutIn entry, string modRoot)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Id)) return false;
            if (!string.Equals(entry.BaseTemplate, HiroTemplate, StringComparison.OrdinalIgnoreCase))
            {
                CutInLogWarning($"Cut-in '{entry.Id}' uses unsupported BaseTemplate '{entry.BaseTemplate}'; v1 only supports 'Hiro'. Skipping.");
                return false;
            }

            var reg = new RegistryEntry
            {
                Entry = entry,
                ModRoot = modRoot,
                Resolved = BuildResolvedShaders(entry),
            };
            registry[entry.Id] = reg;

            PreloadTextures(entry.Id, modRoot, entry);
            return true;
        }

        /// <summary>
        /// 把 entry.Shaders 解析成 5 个 per-role MaterialPropertyBlock。
        /// 颜色解析失败 / 字段全空 → 该 role 留 null（运行时不施加覆盖）。
        /// </summary>
        private static ResolvedShaders BuildResolvedShaders(ModItem.ModObjectionCutIn entry)
        {
            var resolved = new ResolvedShaders();
            var sh = entry.Shaders;
            if (sh == null) return resolved;

            // Background_0Fix
            if (sh.Background != null)
            {
                var cfg = sh.Background;
                MaterialPropertyBlock mpb = null;
                if (TryAddColor(ref mpb, "_BackgroundA", cfg.PrimaryColor,   entry.Id, "Background.PrimaryColor"))   { }
                if (TryAddColor(ref mpb, "_BackgroundB", cfg.SecondaryColor, entry.Id, "Background.SecondaryColor")) { }
                if (cfg.BlendFactor.HasValue)
                {
                    mpb ??= new MaterialPropertyBlock();
                    mpb.SetFloat("_Float", cfg.BlendFactor.Value);
                }
                resolved.Roles[RoleBackground] = mpb;
            }

            // Glasses_0Fix
            if (sh.StainedGlass != null)
            {
                var cfg = sh.StainedGlass;
                MaterialPropertyBlock mpb = null;
                TryAddColor(ref mpb, "_EclipseFlame", cfg.FlameColor, entry.Id, "StainedGlass.FlameColor");
                AddFloat(ref mpb, "_Fader",    cfg.Fader);
                AddFloat(ref mpb, "_Fader2",   cfg.Fader2);
                AddFloat(ref mpb, "_Speed",    cfg.Speed);
                AddFloat(ref mpb, "_Tick",     cfg.Tick);
                AddFloat(ref mpb, "_EdgeSize", cfg.EdgeSize);
                resolved.Roles[RoleStainedGlass] = mpb;
            }

            // Iuminescence_dezolve_0Fix
            if (sh.StainedGlassGlow != null)
            {
                var cfg = sh.StainedGlassGlow;
                MaterialPropertyBlock mpb = null;
                TryAddColor(ref mpb, "_Color",        cfg.Color,      entry.Id, "StainedGlassGlow.Color");
                TryAddColor(ref mpb, "_EclipseFlame", cfg.FlameColor, entry.Id, "StainedGlassGlow.FlameColor");
                AddFloat(ref mpb, "_Tick", cfg.Tick);
                resolved.Roles[RoleStainedGlassGlow] = mpb;
            }

            // Shadow_Fix
            if (sh.CharacterShadow != null)
            {
                var cfg = sh.CharacterShadow;
                MaterialPropertyBlock mpb = null;
                TryAddColor(ref mpb, "_Color", cfg.Color, entry.Id, "CharacterShadow.Color");
                resolved.Roles[RoleCharShadow] = mpb;
            }

            // Iuminescence_Silhouette_0Fix
            if (sh.CharacterGlow != null)
            {
                var cfg = sh.CharacterGlow;
                MaterialPropertyBlock mpb = null;
                TryAddColor(ref mpb, "_Color", cfg.Color, entry.Id, "CharacterGlow.Color");
                AddFloat(ref mpb, "_Tick", cfg.Tick);
                resolved.Roles[RoleCharGlow] = mpb;
            }

            return resolved;
        }

        /// <summary>解析并写入颜色。空字符串 = 跳过（不算错误）；非空但解析失败 = 警告。</summary>
        private static bool TryAddColor(ref MaterialPropertyBlock mpb, string property, string raw, string entryId, string fieldName)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            if (!TryParseHtmlColor(raw, out var color))
            {
                CutInLogWarning($"Cut-in '{entryId}' {fieldName}: invalid color '{raw}', ignored.");
                return false;
            }
            mpb ??= new MaterialPropertyBlock();
            mpb.SetColor(property, color);
            return true;
        }

        private static void AddFloat(ref MaterialPropertyBlock mpb, string property, float? value)
        {
            if (!value.HasValue) return;
            mpb ??= new MaterialPropertyBlock();
            mpb.SetFloat(property, value.Value);
        }

        /// <summary>
        /// Awake 时预加载 entry.Sprites 中所有图片为 Texture2D。
        /// 把 file IO + 解码移出 cut-in 帧。Sprite.Create 仍延迟到首次 swap（需要 vanilla pivot/PPU）。
        /// </summary>
        private static void PreloadTextures(string modId, string modRoot, ModItem.ModObjectionCutIn entry)
        {
            if (entry.Sprites == null || entry.Sprites.Count == 0) return;
            if (string.IsNullOrEmpty(modRoot))
            {
                CutInLogWarning($"Cut-in '{modId}': mod root empty; cannot preload sprites.");
                return;
            }

            foreach (var pair in entry.Sprites)
            {
                string vanillaName = pair.Key;
                string relPath = pair.Value;
                var key = (modId, vanillaName);
                if (textureCache.ContainsKey(key)) continue;

                string fullPath = Path.Combine(modRoot, relPath ?? "");
                if (!File.Exists(fullPath))
                {
                    CutInLogWarning($"Cut-in '{modId}': sprite file not found for vanilla '{vanillaName}': {fullPath}");
                    continue;
                }

                try
                {
                    var bytes = File.ReadAllBytes(fullPath);
                    var tex = new Texture2D(2, 2);
                    if (!ImageConversion.LoadImage(tex, bytes))
                    {
                        CutInLogError($"Cut-in '{modId}': failed to decode '{vanillaName}': {fullPath}");
                        UnityEngine.Object.Destroy(tex);
                        continue;
                    }
                    tex.name = $"ModCutIn_{modId}_{vanillaName}";
                    tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    textureCache[key] = tex;
                }
                catch (Exception ex)
                {
                    CutInLogError($"Cut-in '{modId}': sprite preload error for '{vanillaName}': {ex}");
                }
            }
        }

        /// <summary>
        /// CustomVariableManager.SetVariableValue 的 Postfix：检测 mod cut-in 标识并改写为 Hiro。
        ///
        /// 实际 game data 表明该变量的值并非完整路径 "CutIn/ObjectionCutIn_&lt;Kind&gt;"，
        /// 而可能是其中一种简化形式（取决于 Subroutine 脚本内的拼接方式）：
        ///   - 仅 Kind：     "MyMod_TestCutIn"
        ///   - spawn 名：    "ObjectionCutIn_MyMod_TestCutIn"
        ///   - 完整资源路径："CutIn/ObjectionCutIn_MyMod_TestCutIn"
        ///
        /// 从 MultipliableSpawn.Spawn(path) 的实际错误日志看到 path = "ObjectionCutIn_&lt;Kind&gt;"，
        /// 即 spawn 名形式。我们用 ExtractKindFromCutInValue 兼容三种形式，
        /// 然后用 String.Replace(kind → Hiro) 保留原始前缀结构。
        /// </summary>
        internal static void HandleSetVariableValuePostfix(CustomVariableManager mgr, string name, CustomVariableValue value)
        {
            if (insideRewrite) return;
            if (name != ObjectionCutInSpawnPathVar) return;
            if (registry.Count == 0) return;

            try
            {
                string val = value.String;
                if (string.IsNullOrEmpty(val))
                {
                    pendingEntry = null;
                    return;
                }

                string kind = ExtractKindFromCutInValue(val);
                if (string.IsNullOrEmpty(kind))
                {
                    pendingEntry = null;
                    return;
                }

                if (!registry.TryGetValue(kind, out var reg))
                {
                    // 原生模板（Hiro / Ema / CreatureHiro）：清空残留暂存
                    pendingEntry = null;
                    return;
                }

                // 命中 mod ID：暂存条目并把变量值改写为 Hiro 等价形式
                pendingEntry = reg;
                string newVal = val.Replace(kind, HiroTemplate);
                insideRewrite = true;
                try
                {
                    mgr.SetVariableValue(name, new CustomVariableValue(newVal));
                    CutInLogDebug($"Hijacked cut-in: '{val}' → '{newVal}'; sprite swap queued for next SetSpawnParameters.");
                }
                finally
                {
                    insideRewrite = false;
                }
            }
            catch (Exception ex)
            {
                CutInLogError($"HandleSetVariableValuePostfix failed: {ex}");
                pendingEntry = null;
                insideRewrite = false;
            }
        }

        /// <summary>从变量值中抽出 Cut-in Kind。兼容三种形式。</summary>
        private static string ExtractKindFromCutInValue(string val)
        {
            if (string.IsNullOrEmpty(val)) return null;
            const string marker = "ObjectionCutIn_";
            int idx = val.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                string after = val.Substring(idx + marker.Length);
                int slash = after.IndexOf('/');
                return slash > 0 ? after.Substring(0, slash) : after;
            }
            // 没有 "ObjectionCutIn_" 前缀：把整个值当作 Kind（如 "Hiro" / "MyMod_TestCutIn"）
            int slash2 = val.IndexOf('/');
            return slash2 > 0 ? val.Substring(0, slash2) : val;
        }

        /// <summary>
        /// 在 ObjectionCutIn.SetSpawnParameters Postfix 调用：
        ///   - 取/建该实例的 InstanceCache（一次性扫描，后续复用）
        ///   - mod cut-in 时：按缓存替换 sprite + 第一次 spawn 时 dump 图层（仅 isDebug）
        ///   - 任何 cut-in：根据 entry 状态更新 5 个 shader role 的 PropertyBlock 覆盖 / 回滚
        /// </summary>
        internal static void HandleSetSpawnParametersPostfix(ObjectionCutIn instance)
        {
            var reg = pendingEntry;
            pendingEntry = null;
            if (instance == null || instance.gameObject == null) return;

            try
            {
                int instId = instance.gameObject.GetInstanceID();
                if (!instanceCaches.TryGetValue(instId, out var cache))
                {
                    cache = BuildInstanceCache(instance.gameObject);
                    instanceCaches[instId] = cache;
                }

                // mod 专属：sprite 替换 + 第一次 dump
                if (reg != null)
                {
                    int swapped = SwapSpritesFromCache(cache, reg);
                    CutInLogDebug($"Cut-in '{reg.Entry.Id}': swapped {swapped} sprite(s).");

                    if (!layersDumped && Plugin.Instance != null && Plugin.Instance.isDebug)
                    {
                        layersDumped = true;
                        DumpReplaceableLayers(instance.gameObject, cache);
                    }
                }

                // 始终运行：根据 entry 决定各 shader role 的覆盖 / 回滚
                ApplyShaderOverridesFromCache(cache, reg?.Resolved);
            }
            catch (Exception ex)
            {
                CutInLogError($"HandleSetSpawnParametersPostfix failed for '{reg?.Entry?.Id ?? "<vanilla>"}': {ex}");
            }
        }

        /// <summary>
        /// 一次性扫描该 ObjectionCutIn 实例的子树，缓存：
        ///   - 所有有非空 sprite 名的 Image / SpriteRenderer（潜在 swap 目标）
        ///   - 按 shader 名分桶的 SpriteRenderer（潜在 shader-override 目标）
        /// </summary>
        private static InstanceCache BuildInstanceCache(GameObject root)
        {
            var cache = new InstanceCache();
            for (int i = 0; i < ShaderRoleCount; i++)
            {
                cache.RenderersByRole[i] = new List<SpriteRenderer>();
            }

            try
            {
                var images = root.GetComponentsInChildren<Image>(true);
                if (images != null)
                {
                    foreach (var img in images)
                    {
                        if (img == null) continue;
                        var sp = img.sprite;
                        if (sp == null) continue;
                        string name = sp.name;
                        if (string.IsNullOrEmpty(name)) continue;
                        cache.Images.Add(img);
                        cache.ImageVanillaNames.Add(name);
                    }
                }

                var renderers = root.GetComponentsInChildren<SpriteRenderer>(true);
                if (renderers != null)
                {
                    foreach (var sr in renderers)
                    {
                        if (sr == null) continue;

                        // sprite swap 资格
                        var sp = sr.sprite;
                        if (sp != null && !string.IsNullOrEmpty(sp.name))
                        {
                            cache.Sprites.Add(sr);
                            cache.SpriteVanillaNames.Add(sp.name);
                        }

                        // shader role 分桶
                        var mat = sr.sharedMaterial;
                        if (mat == null || mat.shader == null) continue;
                        int role = RoleForShaderName(mat.shader.name);
                        if (role >= 0) cache.RenderersByRole[role].Add(sr);
                    }
                }
            }
            catch (Exception ex)
            {
                CutInLogError($"BuildInstanceCache failed: {ex}");
            }

            return cache;
        }

        private static int RoleForShaderName(string name)
        {
            if (name == ShaderBackground)        return RoleBackground;
            if (name == ShaderGlasses)           return RoleStainedGlass;
            if (name == ShaderGlassLuminescence) return RoleStainedGlassGlow;
            if (name == ShaderCharShadow)        return RoleCharShadow;
            if (name == ShaderCharLuminescence)  return RoleCharGlow;
            return -1;
        }

        /// <summary>
        /// 按缓存的 Image / SpriteRenderer 列表替换 sprite。返回成功替换数量。
        /// </summary>
        private static int SwapSpritesFromCache(InstanceCache cache, RegistryEntry reg)
        {
            var entry = reg.Entry;
            if (entry.Sprites == null || entry.Sprites.Count == 0) return 0;

            int count = 0;
            try
            {
                for (int i = 0; i < cache.Images.Count; i++)
                {
                    var img = cache.Images[i];
                    if (img == null) continue;
                    string name = cache.ImageVanillaNames[i];
                    if (!entry.Sprites.TryGetValue(name, out _)) continue;
                    var newSprite = GetOrCreateSprite(reg.Entry.Id, name, img.sprite);
                    if (newSprite != null)
                    {
                        img.sprite = newSprite;
                        count++;
                    }
                }

                for (int i = 0; i < cache.Sprites.Count; i++)
                {
                    var sr = cache.Sprites[i];
                    if (sr == null) continue;
                    string name = cache.SpriteVanillaNames[i];
                    if (!entry.Sprites.TryGetValue(name, out _)) continue;
                    var newSprite = GetOrCreateSprite(reg.Entry.Id, name, sr.sprite);
                    if (newSprite != null)
                    {
                        sr.sprite = newSprite;
                        count++;
                    }
                }
            }
            catch (Exception ex)
            {
                CutInLogError($"SwapSpritesFromCache for '{entry.Id}' failed: {ex}");
            }
            return count;
        }

        /// <summary>
        /// 从预加载的 Texture2D 创建 / 取出缓存的 Sprite。复用 vanilla sprite 的 pivot 和 pixelsPerUnit。
        /// </summary>
        private static Sprite GetOrCreateSprite(string modId, string vanillaName, Sprite vanillaSprite)
        {
            var key = (modId, vanillaName);
            if (spriteCache.TryGetValue(key, out var cached)) return cached;

            if (!textureCache.TryGetValue(key, out var tex) || tex == null)
            {
                spriteCache[key] = null; // 已在 Awake 警告，这里不再刷屏
                return null;
            }

            try
            {
                var rect = vanillaSprite.rect;
                var pivot = (rect.width > 0 && rect.height > 0)
                    ? vanillaSprite.pivot / new Vector2(rect.width, rect.height)
                    : new Vector2(0.5f, 0.5f);

                var sprite = Sprite.Create(
                    tex,
                    new Rect(0, 0, tex.width, tex.height),
                    pivot,
                    vanillaSprite.pixelsPerUnit > 0 ? vanillaSprite.pixelsPerUnit : 100f);
                sprite.name = $"ModCutIn_{modId}_{vanillaName}";
                sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
                spriteCache[key] = sprite;
                return sprite;
            }
            catch (Exception ex)
            {
                CutInLogError($"Cut-in '{modId}': Sprite.Create failed for '{vanillaName}': {ex}");
                spriteCache[key] = null;
                return null;
            }
        }

        /// <summary>
        /// 用预烘焙的 ResolvedShaders 应用 / 回滚 5 个 shader role。
        /// 每个实例独立追踪 dirty，互不影响。
        /// </summary>
        private static void ApplyShaderOverridesFromCache(InstanceCache cache, ResolvedShaders resolved)
        {
            for (int role = 0; role < ShaderRoleCount; role++)
            {
                var mpb = resolved?.Roles[role];
                bool hasOverride = mpb != null;
                if (!hasOverride && !cache.AppliedRoles[role]) continue;

                var renderers = cache.RenderersByRole[role];
                if (renderers == null || renderers.Count == 0)
                {
                    CutInLogDebug($"Shaders.{RoleNames[role]}: no SpriteRenderer with shader '{RoleShaderNames[role]}' found; skipped.");
                    continue;
                }

                int affected = 0;
                try
                {
                    foreach (var sr in renderers)
                    {
                        if (sr == null) continue;
                        if (hasOverride) sr.SetPropertyBlock(mpb);
                        else             sr.SetPropertyBlock(null);
                        affected++;
                    }
                }
                catch (Exception ex)
                {
                    CutInLogError($"Shaders.{RoleNames[role]} apply failed: {ex}");
                    continue;
                }

                if (affected > 0)
                {
                    cache.AppliedRoles[role] = hasOverride;
                    CutInLogDebug($"Shaders.{RoleNames[role]}: {(hasOverride ? "applied" : "cleared")} on {affected} renderer(s).");
                }
            }
        }

        /// <summary>
        /// 解析 HTML 颜色字符串（"#RRGGBB" 或 "#RRGGBBAA"，可省略 #）。
        /// 复用 UnityEngine.ColorUtility.TryParseHtmlString 以保证与编辑器一致。
        /// </summary>
        private static bool TryParseHtmlColor(string raw, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(raw)) return false;
            string s = raw.Trim();
            if (!s.StartsWith("#")) s = "#" + s;
            return ColorUtility.TryParseHtmlString(s, out color);
        }

        /// <summary>
        /// 列出实例上所有可替换 sprite 图层，便于 mod 作者参考 key。仅在 isDebug=true 时被调用，
        /// 且每个 Title→Trial 周期只打印一次。复用 InstanceCache 避免重复 GetComponentsInChildren。
        /// </summary>
        private static void DumpReplaceableLayers(GameObject root, InstanceCache cache)
        {
            try
            {
                CutInLogInfo("First mod cut-in spawn — replaceable layers on this ObjectionCutIn instance:");

                for (int i = 0; i < cache.Images.Count; i++)
                {
                    var img = cache.Images[i];
                    if (img == null) continue;
                    string spriteName = img.sprite != null ? img.sprite.name : "<null>";
                    var c = img.color;
                    string colorHex = $"#{(byte)(c.r * 255):X2}{(byte)(c.g * 255):X2}{(byte)(c.b * 255):X2}{(byte)(c.a * 255):X2}";
                    string matName = "<null>";
                    string matTex = "<null>";
                    try
                    {
                        var mat = img.material;
                        if (mat != null)
                        {
                            matName = mat.name ?? "?";
                            if (mat.HasProperty("_MainTex"))
                            {
                                var t = mat.mainTexture;
                                matTex = t != null ? t.name : "<null>";
                            }
                        }
                    }
                    catch { /* material access can throw on default mats */ }
                    CutInLogInfo($"  Image           {GetTransformPath(img.transform, root.transform)} -> sprite={spriteName} color={colorHex} mat={matName} tex={matTex}");
                }

                for (int i = 0; i < cache.Sprites.Count; i++)
                {
                    var sr = cache.Sprites[i];
                    if (sr == null) continue;
                    string spriteName = sr.sprite != null ? sr.sprite.name : "<null>";
                    var c = sr.color;
                    string colorHex = $"#{(byte)(c.r * 255):X2}{(byte)(c.g * 255):X2}{(byte)(c.b * 255):X2}{(byte)(c.a * 255):X2}";
                    string matName = "<null>";
                    string matShader = "<null>";
                    string matTex = "<null>";
                    try
                    {
                        // sharedMaterial 不会触发实例化，对诊断更安全
                        var mat = sr.sharedMaterial;
                        if (mat != null)
                        {
                            matName = mat.name ?? "?";
                            if (mat.shader != null) matShader = mat.shader.name ?? "?";
                            if (mat.HasProperty("_MainTex"))
                            {
                                var t = mat.mainTexture;
                                matTex = t != null ? t.name : "<null>";
                            }
                        }
                    }
                    catch { /* ignore material access errors */ }
                    CutInLogInfo($"  SpriteRenderer  {GetTransformPath(sr.transform, root.transform)} -> sprite={spriteName} color={colorHex} mat={matName} shader={matShader} tex={matTex}");
                }

                // 同时列出无 Image / SpriteRenderer 但有其它渲染组件的 GameObject（如 ParticleSystem / RawImage / 自定义 shader）。
                // 这一步需要一次 Transform 全树扫描 + per-component IL2CPP 反射，但因为只在 isDebug + 每 Title→Trial 一次，可以接受。
                var allTransforms = root.GetComponentsInChildren<Transform>(true);
                if (allTransforms != null)
                {
                    foreach (var t in allTransforms)
                    {
                        if (t == null || t == root.transform) continue;
                        if (t.GetComponent<Image>() != null) continue;
                        if (t.GetComponent<SpriteRenderer>() != null) continue;

                        var comps = t.GetComponents<Component>();
                        if (comps == null || comps.Length == 0) continue;
                        var interesting = new List<string>();
                        foreach (var comp in comps)
                        {
                            if (comp == null) continue;
                            string typeName = comp.GetIl2CppType()?.Name ?? "?";
                            if (typeName == "Transform" || typeName == "RectTransform") continue;
                            interesting.Add(typeName);
                        }
                        if (interesting.Count > 0)
                        {
                            CutInLogInfo($"  Other           {GetTransformPath(t, root.transform)} -> [{string.Join(", ", interesting)}]");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CutInLogWarning($"DumpReplaceableLayers failed: {ex.Message}");
            }
        }

        private static string GetTransformPath(Transform t, Transform root)
        {
            if (t == null) return "";
            var parts = new List<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                parts.Add(cur.name ?? "?");
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }

    [HarmonyPatch]
    static class CustomVariableManager_SetVariableValue_Patch
    {
        [HarmonyPatch(typeof(CustomVariableManager), nameof(CustomVariableManager.SetVariableValue))]
        [HarmonyPostfix]
        static void Postfix(CustomVariableManager __instance, string name, CustomVariableValue value)
        {
            ModObjectionCutInLoader.HandleSetVariableValuePostfix(__instance, name, value);
        }
    }

    [HarmonyPatch]
    static class ObjectionCutIn_SetSpawnParameters_Patch
    {
        [HarmonyPatch(typeof(ObjectionCutIn), nameof(ObjectionCutIn.SetSpawnParameters))]
        [HarmonyPostfix]
        static void Postfix(ObjectionCutIn __instance)
        {
            ModObjectionCutInLoader.HandleSetSpawnParametersPostfix(__instance);
        }
    }

    [HarmonyPatch]
    static class CutIn_TitleUi_Patch
    {
        [HarmonyPatch(typeof(WitchTrials.Views.TitleUi), "Awake")]
        [HarmonyPostfix]
        static void TitleUi_Awake_PostfixForCutIn()
        {
            ModObjectionCutInLoader.Awake();
        }
    }
}
