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
    ///        - 暂存 mod 条目（pendingModSwap）；
    ///        - 用同一 manager 重新 SetVariableValue 把变量值改写为
    ///          "ObjectionCutIn_Hiro"，让原版 spawn 流程拿到现成的 Hiro prefab。
    ///      （重入由 insideRewrite 标志保护；ExtractKindFromCutInValue 同时兼容
    ///        裸 Kind 与带 "CutIn/" 前缀两种形式以防变量格式变化）
    ///   2. 在 ObjectionCutIn.SetSpawnParameters 的 Postfix 中（同步方法），
    ///      若 pendingModSwap 非空，按 sprite 名称替换该实例上的 Image / SpriteRenderer，
    ///      然后清空 pendingModSwap。
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
    /// 第一次触发 mod cut-in 时 loader 会把当前 ObjectionCutIn 实例上所有可替换
    /// sprite 名打印到 BepInEx 日志，作为 Sprites 字典 key 的一手参考。
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
    /// 状态机（每个 shader role 独立）：
    ///   - mod cut-in 且对应 sub-config 含任意非空字段 → 构建 MPB 写入 SetPropertyBlock
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

        /// <summary>已注册的 mod cut-in：ID → 配置。</summary>
        private static readonly Dictionary<string, ModItem.ModObjectionCutIn> registry = new();

        /// <summary>每个 mod 条目的 mod 根目录（用于解析相对图片路径）。</summary>
        private static readonly Dictionary<string, string> modRootById = new();

        /// <summary>(modId, vanillaSpriteName) → mod sprite 缓存。</summary>
        private static readonly Dictionary<(string, string), Sprite> spriteCache = new();

        /// <summary>暂存：上次 SetVariableValue 命中的 mod 条目，供 SetSpawnParameters Postfix 消费。</summary>
        private static ModItem.ModObjectionCutIn pendingModSwap = null;

        /// <summary>SetVariableValue 重入保护：避免我们自己的 mgr.SetVariableValue 调用再次进入本 Postfix 逻辑。</summary>
        private static bool insideRewrite = false;

        /// <summary>是否已 dump 过克隆图层（仅在第一次替换时打印一次）。</summary>
        private static bool layersDumped = false;

        /// <summary>每个 shader role 是否曾设置过 PropertyBlock 覆盖。
        /// 用于后续非 mod cut-in 中清空 PropertyBlock 恢复 vanilla 状态。</summary>
        private static bool bgDirty, glassDirty, glassGlowDirty, charShadowDirty, charGlowDirty;

        /// <summary>缓存的 MaterialPropertyBlock（避免每帧分配）。</summary>
        private static MaterialPropertyBlock cachedMpb = null;

        /// <summary>Shader 名常量（与 SpriteRenderer.sharedMaterial.shader.name 匹配）。</summary>
        private const string ShaderBackground       = "Shader Graphs/Background_0Fix";
        private const string ShaderGlasses          = "Shader Graphs/Glasses_0Fix";
        private const string ShaderGlassLuminescence = "Shader Graphs/Iuminescence_dezolve_0Fix";
        private const string ShaderCharShadow       = "Shader Graphs/Shadow_Fix";
        private const string ShaderCharLuminescence = "Shader Graphs/Iuminescence_Silhouette_0Fix";

        public static void Init(Harmony harmony)
        {
            harmony.PatchAll(typeof(CustomVariableManager_SetVariableValue_Patch));
            harmony.PatchAll(typeof(ObjectionCutIn_SetSpawnParameters_Patch));
            harmony.PatchAll(typeof(CutIn_TitleUi_Patch));
            CutInLogInfo("ModObjectionCutInLoader initialized.");
        }

        /// <summary>
        /// TitleUi.Awake 后调用：扫描所有 mod 的 CutIns 条目并填充注册表。
        /// </summary>
        public static void Awake()
        {
            registry.Clear();
            modRootById.Clear();
            // 不清 spriteCache：跨 mod 切换时已解析的 sprite 仍然有效（key 包含 modId）

            int count = 0;
            foreach (var kv in ModManager.ModManager.Items)
            {
                var desc = kv.Value?.Description;
                if (desc?.CutIns == null) continue;
                string modRoot = Path.Combine(Plugin.Instance.ModRootPath, kv.Key);
                foreach (var entry in desc.CutIns)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Id)) continue;
                    if (!string.Equals(entry.BaseTemplate, HiroTemplate, StringComparison.OrdinalIgnoreCase))
                    {
                        CutInLogWarning($"Cut-in '{entry.Id}' uses unsupported BaseTemplate '{entry.BaseTemplate}'; v1 only supports 'Hiro'. Skipping.");
                        continue;
                    }
                    registry[entry.Id] = entry;
                    modRootById[entry.Id] = modRoot;
                    count++;
                }
            }

            if (ScriptWorkingManager.IsEnabled && ScriptWorkingManager.ModInfo?.Description?.CutIns != null)
            {
                foreach (var entry in ScriptWorkingManager.ModInfo.Description.CutIns)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Id)) continue;
                    if (!string.Equals(entry.BaseTemplate, HiroTemplate, StringComparison.OrdinalIgnoreCase))
                    {
                        CutInLogWarning($"Workspace cut-in '{entry.Id}' uses unsupported BaseTemplate '{entry.BaseTemplate}'; skipping.");
                        continue;
                    }
                    registry[entry.Id] = entry;
                    modRootById[entry.Id] = ScriptWorkingManager.WorkspacePath;
                    count++;
                }
            }

            // 进入 Title 时清空暂存，避免上一局残留状态
            pendingModSwap = null;
            insideRewrite = false;
            // 重置 layersDumped，让每次 Title→Trial 都能拿到一份新的诊断 dump
            layersDumped = false;
            // 重置所有 shader dirty 标志（Title→Trial 间没有 ObjectionCutIn 实例存活）
            bgDirty = glassDirty = glassGlowDirty = charShadowDirty = charGlowDirty = false;

            if (count == 0)
                CutInLogDebug("No mod cut-ins configured; hijack patches inactive.");
            else
                CutInLogInfo($"Registered {count} mod cut-in(s). Will hijack via SetVariableValue at runtime.");
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
                    pendingModSwap = null;
                    return;
                }

                string kind = ExtractKindFromCutInValue(val);
                if (string.IsNullOrEmpty(kind))
                {
                    pendingModSwap = null;
                    return;
                }

                if (!registry.TryGetValue(kind, out var entry))
                {
                    // 原生模板（Hiro / Ema / CreatureHiro）：清空残留暂存
                    pendingModSwap = null;
                    return;
                }

                // 命中 mod ID：暂存条目并把变量值改写为 Hiro 等价形式
                pendingModSwap = entry;
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
                pendingModSwap = null;
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
        ///   - mod cut-in 时替换 sprite + 第一次 spawn 时 dump 图层（诊断）
        ///   - 任何 cut-in 都根据 entry 状态更新 5 个 shader role 的 PropertyBlock 覆盖 / 回滚
        /// </summary>
        internal static void HandleSetSpawnParametersPostfix(ObjectionCutIn instance)
        {
            var entry = pendingModSwap;
            pendingModSwap = null;
            if (instance == null || instance.gameObject == null) return;

            try
            {
                // mod 专属：sprite 替换 + 第一次 dump
                if (entry != null)
                {
                    if (!layersDumped)
                    {
                        DumpReplaceableLayers(instance.gameObject);
                        layersDumped = true;
                    }
                    int swapped = SwapSprites(instance.gameObject, entry);
                    CutInLogDebug($"Cut-in '{entry.Id}': swapped {swapped} sprite(s).");
                }

                // 始终运行：根据 entry 决定各 shader role 的覆盖 / 回滚
                ApplyAllShaderOverrides(instance.gameObject, entry);
            }
            catch (Exception ex)
            {
                CutInLogError($"HandleSetSpawnParametersPostfix failed for '{entry?.Id ?? "<vanilla>"}': {ex}");
            }
        }

        /// <summary>
        /// 根据 entry.Shaders 决定五个 shader role 的覆盖 / 回滚。
        /// 每个 role 独立追踪 dirty 状态，互不影响。
        /// </summary>
        private static void ApplyAllShaderOverrides(GameObject root, ModItem.ModObjectionCutIn entry)
        {
            var sh = entry?.Shaders;

            // Background_0Fix
            ApplyShaderRole(root, ShaderBackground, ref bgDirty, "Background", sh?.Background, (cfg, mpb) =>
            {
                if (TryParse(cfg.PrimaryColor,   out var c1)) mpb.SetColor("_BackgroundA", c1);
                if (TryParse(cfg.SecondaryColor, out var c2)) mpb.SetColor("_BackgroundB", c2);
                if (cfg.BlendFactor.HasValue)                 mpb.SetFloat("_Float",       cfg.BlendFactor.Value);
            }, cfg => HasAny(cfg.PrimaryColor, cfg.SecondaryColor) || cfg.BlendFactor.HasValue);

            // Glasses_0Fix
            ApplyShaderRole(root, ShaderGlasses, ref glassDirty, "StainedGlass", sh?.StainedGlass, (cfg, mpb) =>
            {
                if (TryParse(cfg.FlameColor, out var c)) mpb.SetColor("_EclipseFlame", c);
                if (cfg.Fader.HasValue)    mpb.SetFloat("_Fader",    cfg.Fader.Value);
                if (cfg.Fader2.HasValue)   mpb.SetFloat("_Fader2",   cfg.Fader2.Value);
                if (cfg.Speed.HasValue)    mpb.SetFloat("_Speed",    cfg.Speed.Value);
                if (cfg.Tick.HasValue)     mpb.SetFloat("_Tick",     cfg.Tick.Value);
                if (cfg.EdgeSize.HasValue) mpb.SetFloat("_EdgeSize", cfg.EdgeSize.Value);
            }, cfg => HasAny(cfg.FlameColor) || cfg.Fader.HasValue || cfg.Fader2.HasValue
                       || cfg.Speed.HasValue || cfg.Tick.HasValue || cfg.EdgeSize.HasValue);

            // Iuminescence_dezolve_0Fix
            ApplyShaderRole(root, ShaderGlassLuminescence, ref glassGlowDirty, "StainedGlassGlow", sh?.StainedGlassGlow, (cfg, mpb) =>
            {
                if (TryParse(cfg.Color,      out var c1)) mpb.SetColor("_Color",        c1);
                if (TryParse(cfg.FlameColor, out var c2)) mpb.SetColor("_EclipseFlame", c2);
                if (cfg.Tick.HasValue)                    mpb.SetFloat("_Tick",         cfg.Tick.Value);
            }, cfg => HasAny(cfg.Color, cfg.FlameColor) || cfg.Tick.HasValue);

            // Shadow_Fix
            ApplyShaderRole(root, ShaderCharShadow, ref charShadowDirty, "CharacterShadow", sh?.CharacterShadow, (cfg, mpb) =>
            {
                if (TryParse(cfg.Color, out var c)) mpb.SetColor("_Color", c);
            }, cfg => HasAny(cfg.Color));

            // Iuminescence_Silhouette_0Fix
            ApplyShaderRole(root, ShaderCharLuminescence, ref charGlowDirty, "CharacterGlow", sh?.CharacterGlow, (cfg, mpb) =>
            {
                if (TryParse(cfg.Color, out var c)) mpb.SetColor("_Color", c);
                if (cfg.Tick.HasValue)              mpb.SetFloat("_Tick",  cfg.Tick.Value);
            }, cfg => HasAny(cfg.Color) || cfg.Tick.HasValue);
        }

        private static bool HasAny(params string[] values)
        {
            if (values == null) return false;
            foreach (var v in values) if (!string.IsNullOrEmpty(v)) return true;
            return false;
        }

        private static bool TryParse(string raw, out Color color) => TryParseHtmlColor(raw, out color);

        /// <summary>
        /// 通用 shader role 应用 / 回滚：
        ///   - cfg 非空且 hasAny(cfg) → 找 prefab 内所有 shader=targetShaderName 的 SpriteRenderer，
        ///     构建 MPB，调用 writeProps(cfg, mpb) 写入想覆盖的字段，SetPropertyBlock(mpb)，dirty=true
        ///   - 否则若 dirty=true → SetPropertyBlock(null) 恢复 sharedMaterial 默认，dirty=false
        ///   - 否则 → no-op
        /// </summary>
        private static void ApplyShaderRole<TConfig>(
            GameObject root,
            string targetShaderName,
            ref bool dirtyFlag,
            string roleName,
            TConfig cfg,
            Action<TConfig, MaterialPropertyBlock> writeProps,
            Func<TConfig, bool> hasAny)
            where TConfig : class
        {
            bool hasOverride = cfg != null && hasAny(cfg);
            if (!hasOverride && !dirtyFlag) return;

            try
            {
                var renderers = root.GetComponentsInChildren<SpriteRenderer>(true);
                if (renderers == null) return;

                int affected = 0;
                foreach (var sr in renderers)
                {
                    if (sr == null) continue;
                    var mat = sr.sharedMaterial;
                    if (mat == null || mat.shader == null) continue;
                    if (mat.shader.name != targetShaderName) continue;

                    if (hasOverride)
                    {
                        if (cachedMpb == null) cachedMpb = new MaterialPropertyBlock();
                        cachedMpb.Clear();
                        writeProps(cfg, cachedMpb);
                        sr.SetPropertyBlock(cachedMpb);
                    }
                    else
                    {
                        sr.SetPropertyBlock(null);
                    }
                    affected++;
                }

                if (affected > 0)
                {
                    dirtyFlag = hasOverride;
                    CutInLogDebug($"Shaders.{roleName}: {(hasOverride ? "applied" : "cleared")} on {affected} renderer(s).");
                }
                else
                {
                    CutInLogDebug($"Shaders.{roleName}: no SpriteRenderer with shader '{targetShaderName}' found; skipped.");
                }
            }
            catch (Exception ex)
            {
                CutInLogError($"Shaders.{roleName} apply failed: {ex}");
            }
        }

        /// <summary>
        /// 按 sprite 名称扫描并替换 Image / SpriteRenderer。返回成功替换数量。
        /// </summary>
        private static int SwapSprites(GameObject root, ModItem.ModObjectionCutIn entry)
        {
            int count = 0;
            if (entry.Sprites == null || entry.Sprites.Count == 0) return 0;

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
                        string name = sp.name ?? "";
                        if (!entry.Sprites.TryGetValue(name, out var relPath)) continue;
                        var newSprite = LoadOrGetSprite(entry.Id, name, relPath, sp);
                        if (newSprite != null)
                        {
                            img.sprite = newSprite;
                            count++;
                        }
                    }
                }

                var renderers = root.GetComponentsInChildren<SpriteRenderer>(true);
                if (renderers != null)
                {
                    foreach (var sr in renderers)
                    {
                        if (sr == null) continue;
                        var sp = sr.sprite;
                        if (sp == null) continue;
                        string name = sp.name ?? "";
                        if (!entry.Sprites.TryGetValue(name, out var relPath)) continue;
                        var newSprite = LoadOrGetSprite(entry.Id, name, relPath, sp);
                        if (newSprite != null)
                        {
                            sr.sprite = newSprite;
                            count++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CutInLogError($"SwapSprites for '{entry.Id}' failed: {ex}");
            }
            return count;
        }

        /// <summary>
        /// 加载 / 缓存 mod sprite。复用原版 sprite 的 pivot 和 pixelsPerUnit。
        /// </summary>
        private static Sprite LoadOrGetSprite(string modId, string vanillaName, string relPath, Sprite vanillaSprite)
        {
            var key = (modId, vanillaName);
            if (spriteCache.TryGetValue(key, out var cached)) return cached;

            if (!modRootById.TryGetValue(modId, out var modRoot) || string.IsNullOrEmpty(modRoot))
            {
                CutInLogWarning($"Cut-in '{modId}': mod root unknown; cannot load sprite '{vanillaName}'.");
                spriteCache[key] = null;
                return null;
            }

            string fullPath = Path.Combine(modRoot, relPath ?? "");
            if (!File.Exists(fullPath))
            {
                CutInLogWarning($"Cut-in '{modId}': sprite file not found for vanilla '{vanillaName}': {fullPath}");
                spriteCache[key] = null;
                return null;
            }

            try
            {
                var bytes = File.ReadAllBytes(fullPath);
                var tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, bytes))
                {
                    CutInLogError($"Cut-in '{modId}': failed to decode '{vanillaName}': {fullPath}");
                    UnityEngine.Object.Destroy(tex);
                    spriteCache[key] = null;
                    return null;
                }
                tex.name = $"ModCutIn_{modId}_{vanillaName}";
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;

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
                CutInLogError($"Cut-in '{modId}': sprite load error for '{vanillaName}': {ex}");
                spriteCache[key] = null;
                return null;
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
        /// 列出实例上所有可替换 sprite 图层，便于 mod 作者参考 key。仅打印一次。
        /// 同时打印每个 Image 的颜色 / 材质 / 主纹理，用于定位 "noise" 之类的色调叠加来源。
        /// </summary>
        private static void DumpReplaceableLayers(GameObject root)
        {
            try
            {
                CutInLogInfo("First mod cut-in spawn — replaceable layers on this ObjectionCutIn instance:");
                var images = root.GetComponentsInChildren<Image>(true);
                if (images != null)
                {
                    foreach (var img in images)
                    {
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
                }
                var renderers = root.GetComponentsInChildren<SpriteRenderer>(true);
                if (renderers != null)
                {
                    foreach (var sr in renderers)
                    {
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
                }

                // 同时列出无 Image / SpriteRenderer 但有其它渲染组件的 GameObject（如 ParticleSystem / RawImage / 自定义 shader）
                var allTransforms = root.GetComponentsInChildren<Transform>(true);
                if (allTransforms != null)
                {
                    foreach (var t in allTransforms)
                    {
                        if (t == null || t == root.transform) continue;
                        if (t.GetComponent<Image>() != null) continue;
                        if (t.GetComponent<SpriteRenderer>() != null) continue;

                        // 报告其它感兴趣的组件名
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
