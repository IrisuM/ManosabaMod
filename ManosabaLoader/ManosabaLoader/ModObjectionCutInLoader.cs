using System;
using System.Collections.Generic;
using System.IO;

using HarmonyLib;

using Il2CppInterop.Runtime.InteropTypes.Arrays;

using ManosabaLoader.ModManager;
using ManosabaLoader.Utils;

using Naninovel;

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

using WitchTrials.Views;

namespace ManosabaLoader
{
    /// <summary>
    /// Mod 自定义 cut-in 加载器（hijack-and-swap，全部基于同步方法）。
    /// Mod 作者文档见 docs/cutin.zh-Hans.md / docs/cutin.en.md，本注释只记录实现层面的约束与结构。
    ///
    /// ⚠ 关键约束（参见 ModMovieLoader.cs 顶部注释）：
    ///   IL2CPP 下 Harmony 不能 Patch 任何返回 UniTask / UniTask&lt;T&gt; 的方法，
    ///   即使 Prefix 不返回 false 也会破坏异步虚表，导致 MethodAccessException 崩溃。
    ///   因此本加载器**完全不触碰** GosubToObjectionCutIn.Execute / ObjectionCutIn.AwaitSpawn。
    ///
    /// ===== 劫持流程 =====
    ///   1. GosubToObjectionCutIn.Execute 内部会同步调用
    ///        CustomVariableManager.SetVariableValue("objectionCutInSpawnPath", "ObjectionCutIn_&lt;Kind&gt;")
    ///      然后 gosub 到 System_Subroutine 做 @spawn。Postfix 该 SetVariableValue：
    ///      若 &lt;Kind&gt; 命中已注册 mod ID → 暂存条目（pendingEntry），并把变量改写为
    ///      "ObjectionCutIn_Hiro"，让原版 spawn 流程拿到现成的 Hiro prefab。
    ///      （重入由 insideRewrite 保护；ExtractKindFromCutInValue 兼容裸 Kind / spawn 名 / 完整路径）
    ///   2. ObjectionCutIn.SetSpawnParameters（同步）的 Postfix 消费 pendingEntry，
    ///      在该实例上做 sprite 替换 + shader 属性覆盖 + 粒子覆盖。
    ///
    /// ===== 实例复用与回滚 =====
    /// Naninovel 通过 GetOrSpawnAsync 按 spawn 路径复用同一个 ObjectionCutIn 实例，
    /// 所以原版 Hiro cut-in 与所有 mod cut-in 共用一个实例。每次 SetSpawnParameters 都要把
    /// 实例状态收敛到"当前条目应有的样子"：
    ///   - Sprite：目标 = 条目覆盖了该层 ? mod sprite : 首次扫描时记录的 vanilla sprite
    ///   - Shader role：有覆盖 → SetPropertyBlock(mpb)；无覆盖但上次置过 → SetPropertyBlock(null)
    ///   - 粒子：同上，另加 Renderer.enabled 的恢复
    ///   - HiddenLayers：目标 = 条目列了该层 ? enabled=false : 首次扫描时记录的 vanilla enabled
    /// InstanceCache 按 GameObject InstanceID 缓存首次扫描结果，后续 spawn 零次 GetComponentsInChildren。
    ///
    /// ===== 可定制面 =====
    ///   Sprites        任意 Image / SpriteRenderer 的 sprite（按 vanilla sprite 名匹配）
    ///   Tints          任意 Image / SpriteRenderer 的 color（同样按 vanilla sprite 名匹配）
    ///   HiddenLayers   任意 Image / SpriteRenderer / 粒子渲染器整层关掉（Renderer.enabled / Behaviour.enabled = false；
    ///                  按 vanilla sprite 名或节点名及其祖先名匹配，大小写不敏感）
    ///   Shaders.*      5 类 Shader Graph 材质的 float / color / texture 属性（按 shader 名分 role）
    ///   Particles      "Glass" ParticleSystemRenderer 的 _BaseMap / _BaseColor / 显示开关
    /// 所有贴图类字段都接受 "none" → 4x4 全透明贴图（去掉该层 / 该效果）。
    ///
    /// 为什么关层用 enabled 而不是 SetActive / "none"：演出 Timeline 与 _objectLibrary 只切 GameObject 激活状态
    /// （dump 里 BackGround 在 spawn 时 active=False，随后被 Timeline 打开），不碰 Renderer.enabled，
    /// 所以 enabled=false 整场都不会被翻回去；而 BackGround 的 Background_0Fix 没有 _MainTex、不采样 sprite，
    /// Sprites:"none" 对它无效，只能关渲染器。
    ///
    /// Glasses_0Fix 的三个额外贴图槽挂在材质 Glasses_Fix_Hiro 上而非 sprite 上（实测 dump）：
    ///   _kirakira      = Hiro_CutIn_StainedGlass_003_kirakira2   细裂纹线      → StainedGlass.CrackTexture
    ///   _kirakira2     = Hiro_CutIn_StainedGlass_003_kirakira1   碎片块高光    → StainedGlass.ShardTexture
    ///   _luminescence  = RefuteCutIn_StainedGlass_luminescence001 玻璃带剪影   → StainedGlass.GlowMaskTexture
    /// 注意槽名与原版文件名的 1/2 是反的，以运行时 dump 为准。
    ///
    /// ===== Index 与 pose =====
    /// Hiro prefab 的 _objectLibrary[] 在不同 Index 下激活不同 pose，映射非字面 1↔1（实测）：
    ///   Index:1 → Hiro_CutIn_002，Index:2 → Hiro_CutIn_003，Index:3 → Hiro_CutIn_001
    /// 激活是累积的（只 SetActive(true) 当前组，不关闭既往组），原版三张图轮廓像素级一致所以看不出来；
    /// mod 只替换一张会露出原版轮廓，建议三张全替换（或用 "none" 去掉不需要的）。
    ///
    /// ===== 诊断 =====
    /// BepInEx config Debug.OpenDebug = true 时，每个 Title→Trial 周期内第一次 mod cut-in spawn
    /// 会把实例上所有层（路径 / sprite 名 / 颜色 / 激活状态 / 材质 / shader）以及每个非默认材质的
    /// 全部 shader 属性当前值打印到日志，作为 Sprites key 与 Shaders 默认值的一手参考。
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

        /// <summary>贴图类字段中表示"换成全透明贴图（去掉该效果）"的关键字。大小写不敏感。</summary>
        public const string TextureNoneKeyword = "none";

        /// <summary>原版 GosubToObjectionCutIn 在 Execute 中写入的脚本变量名。</summary>
        private const string ObjectionCutInSpawnPathVar = "objectionCutInSpawnPath";

        /// <summary>粒子渲染器的 IL2CPP 类型名（UnityEngine.ParticleSystemModule 不在 interop 引用中，按名匹配）。</summary>
        private const string ParticleRendererTypeName = "ParticleSystemRenderer";

        /// <summary>URP 2D 默认 sprite shader；属性 dump 时跳过它以减少噪音。</summary>
        private const string DefaultSpriteShaderName = "Universal Render Pipeline/2D/Sprite-Unlit-Default";

        // ------------------------------------------------------------------
        // Shader roles（按 SpriteRenderer.sharedMaterial.shader.name 分桶）
        // ------------------------------------------------------------------

        private const int RoleBackground       = 0;
        private const int RoleStainedGlass     = 1;
        private const int RoleStainedGlassGlow = 2;
        private const int RoleCharShadow       = 3;
        private const int RoleCharGlow         = 4;
        private const int ShaderRoleCount      = 5;

        /// <summary>role → 配置节名（与 ModItem.ModShaders 属性名一致，用于日志）。</summary>
        private static readonly string[] RoleNames =
        {
            "Background", "StainedGlass", "StainedGlassGlow", "CharacterShadow", "CharacterGlow"
        };

        /// <summary>role → shader 名（跨角色稳定；材质名带 _Hiro / _Ema 后缀所以不按材质名匹配）。</summary>
        private static readonly string[] RoleShaderNames =
        {
            "Shader Graphs/Background_0Fix",
            "Shader Graphs/Glasses_0Fix",
            "Shader Graphs/Iuminescence_dezolve_0Fix",
            "Shader Graphs/Shadow_Fix",
            "Shader Graphs/Iuminescence_Silhouette_0Fix",
        };

        // ------------------------------------------------------------------
        // 数据结构
        // ------------------------------------------------------------------

        /// <summary>已注册的 mod cut-in：条目 + mod 根 + 预烘焙覆盖。</summary>
        private sealed class RegistryEntry
        {
            public ModItem.ModObjectionCutIn Entry;
            public string ModRoot;
            /// <summary>每个 shader role 一个 MPB（null = 该 role 无覆盖）。</summary>
            public MaterialPropertyBlock[] ShaderRoles = new MaterialPropertyBlock[ShaderRoleCount];
            /// <summary>粒子 MPB 覆盖（null = 无覆盖）。关闭粒子走 HiddenLayers。</summary>
            public MaterialPropertyBlock Particles;
            /// <summary>已解析的着色覆盖：vanilla sprite 名 → Color。</summary>
            public readonly Dictionary<string, Color> Tints = new();
            /// <summary>要关掉的层：vanilla sprite 名或节点名（含祖先），大小写不敏感。</summary>
            public readonly HashSet<string> HiddenLayers = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>替换图网格裁切的 alpha 阈值（0–255），来自 entry.MeshAlphaThreshold，缺省 <see cref="DefaultMeshAlphaThreshold"/>。</summary>
            public byte MeshAlphaThreshold = DefaultMeshAlphaThreshold;

            public bool OverridesSprite(string vanillaName)
                => Entry.Sprites != null && Entry.Sprites.ContainsKey(vanillaName);

            /// <summary>该层是否被 HiddenLayers 命中：vanilla sprite 名，或该层节点自身 / 任一祖先的名字。</summary>
            public bool HidesLayer(string vanillaName, string[] nodeNames)
            {
                if (HiddenLayers.Count == 0) return false;
                if (vanillaName != null && HiddenLayers.Contains(vanillaName)) return true;
                if (nodeNames != null)
                    foreach (var n in nodeNames)
                        if (HiddenLayers.Contains(n)) return true;
                return false;
            }
        }

        /// <summary>
        /// 每个 ObjectionCutIn 实例的组件缓存。Naninovel 复用同一实例，prefab 子树稳定，缓存可长期持有。
        /// Vanilla sprite / enabled 状态在首次扫描（此时实例尚未被任何 mod 修改）时记录，用于回滚。
        /// </summary>
        private sealed class InstanceCache
        {
            public readonly List<Image> Images = new();
            public readonly List<Sprite> ImageVanillaSprites = new();
            public readonly List<string> ImageVanillaNames = new();
            public readonly List<Color> ImageVanillaColors = new();
            public readonly List<bool> ImageVanillaEnabled = new();
            /// <summary>每个 Image 层：节点自身及各级祖先（到实例根为止，不含根）的名字，供 HiddenLayers 匹配。</summary>
            public readonly List<string[]> ImageNodeNames = new();

            public readonly List<SpriteRenderer> Sprites = new();
            public readonly List<Sprite> SpriteVanillaSprites = new();
            public readonly List<string> SpriteVanillaNames = new();
            public readonly List<Color> SpriteVanillaColors = new();
            public readonly List<bool> SpriteVanillaEnabled = new();
            public readonly List<string[]> SpriteNodeNames = new();

            /// <summary>该实例上是否有任何层当前处于 mod sprite 状态（用于决定是否需要回滚）。</summary>
            public bool SpritesDirty;
            /// <summary>该实例上是否有任何层当前处于 mod 着色状态。</summary>
            public bool TintsDirty;
            /// <summary>该实例上是否有任何 Image / SpriteRenderer 层当前被 HiddenLayers 关掉。</summary>
            public bool HiddenDirty;

            public readonly List<SpriteRenderer>[] RenderersByRole = new List<SpriteRenderer>[ShaderRoleCount];
            /// <summary>该实例上每个 role 当前是否处于 mod 覆盖状态。</summary>
            public readonly bool[] AppliedRoles = new bool[ShaderRoleCount];

            public readonly List<Renderer> ParticleRenderers = new();
            public readonly List<bool> ParticleVanillaEnabled = new();
            public readonly List<string[]> ParticleNodeNames = new();
            public bool ParticleApplied;

            /// <summary>实例上所有带渲染器的节点及其祖先的名字（大小写不敏感），用于 HiddenLayers 拼写检查。</summary>
            public readonly HashSet<string> NodeNames = new(StringComparer.OrdinalIgnoreCase);

            public InstanceCache()
            {
                for (int i = 0; i < ShaderRoleCount; i++) RenderersByRole[i] = new List<SpriteRenderer>();
            }
        }

        // ------------------------------------------------------------------
        // 状态
        // ------------------------------------------------------------------

        private static readonly Dictionary<string, RegistryEntry> registry = new();

        /// <summary>磁盘贴图缓存：绝对路径 → Texture2D（Sprites 与 Shaders / Particles 贴图共用）。</summary>
        private static readonly Dictionary<string, Texture2D> textureByPath = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>"none" 对应的全透明贴图，惰性创建。</summary>
        private static Texture2D clearTexture = null;

        /// <summary>(modId, vanillaSpriteName) → 已解析的替换贴图（可能是 clearTexture）。</summary>
        private static readonly Dictionary<(string, string), Texture2D> spriteTextures = new();

        /// <summary>(modId, vanillaSpriteName) → Sprite（首次 swap 时基于 vanilla pivot / PPU 创建）。</summary>
        private static readonly Dictionary<(string, string), Sprite> spriteCache = new();

        /// <summary>
        /// Texture2D InstanceID → 从 PNG 字节解出的 alpha 遮罩，用于给替换 sprite 生成贴合轮廓的网格。
        /// 原版 Glasses_0Fix 着色器不读贴图 alpha，玻璃带的形状完全靠网格裁出；Unity 运行时自动生成的
        /// Tight 网格轮廓外有 6–20 px 余量并跨过细小凹陷，余量区会把透明像素的 RGB 原样画出来（灰边）。
        /// 见 SpriteMeshBuilder。
        /// </summary>
        private static readonly Dictionary<int, PngAlphaReader.AlphaMask> alphaMaskByTextureId = new();

        /// <summary>网格裁切用的默认 alpha 阈值（0–255），可由 info.json 的 MeshAlphaThreshold 覆盖。64 ≈ 保留抗锯齿边缘的外侧一半，细刺不易丢失。</summary>
        private const byte DefaultMeshAlphaThreshold = 64;

        /// <summary>ObjectionCutIn GameObject InstanceID → 组件缓存。</summary>
        private static readonly Dictionary<int, InstanceCache> instanceCaches = new();

        /// <summary>上次 SetVariableValue 命中的 mod 条目，供 SetSpawnParameters Postfix 消费。</summary>
        private static RegistryEntry pendingEntry = null;

        /// <summary>SetVariableValue 重入保护。</summary>
        private static bool insideRewrite = false;

        /// <summary>本 Title→Trial 周期是否已打印过诊断 dump。</summary>
        private static bool layersDumped = false;

        /// <summary>已检查过 Sprites / Tints / HiddenLayers key 是否命中图层的条目 ID（每个条目只警告一次）。</summary>
        private static readonly HashSet<string> spriteKeysChecked = new();

        // ------------------------------------------------------------------
        // 生命周期
        // ------------------------------------------------------------------

        public static void Init(Harmony harmony)
        {
            harmony.PatchAll(typeof(CustomVariableManager_SetVariableValue_Patch));
            harmony.PatchAll(typeof(ObjectionCutIn_SetSpawnParameters_Patch));
            CutInLogInfo("ModObjectionCutInLoader patches applied.");
        }

        /// <summary>加载指定 mod 的 cut-in 条目：预加载所有贴图并预烘焙覆盖。</summary>
        public static void LoadModData(string modKey, string modPath, ModItem modItem)
        {
            if (modItem?.Description?.CutIns == null) return;

            int count = 0;
            foreach (var entry in modItem.Description.CutIns)
            {
                if (RegisterEntry(entry, modPath)) count++;
            }

            if (count > 0)
                CutInLogMessage($"Registered {count} mod cut-in(s) for mod: {modKey}");
        }

        /// <summary>清除所有 mod cut-in 数据并释放贴图 / Sprite。</summary>
        public static void ClearModData()
        {
            registry.Clear();
            spriteTextures.Clear();

            foreach (var sp in spriteCache.Values)
                if (sp != null) UnityEngine.Object.Destroy(sp);
            spriteCache.Clear();

            foreach (var tex in textureByPath.Values)
                if (tex != null) UnityEngine.Object.Destroy(tex);
            textureByPath.Clear();
            alphaMaskByTextureId.Clear();

            if (clearTexture != null)
            {
                UnityEngine.Object.Destroy(clearTexture);
                clearTexture = null;
            }

            instanceCaches.Clear();
            spriteKeysChecked.Clear();
            pendingEntry = null;
            insideRewrite = false;
            layersDumped = false;
            CutInLogInfo("CutInLoader data cleared.");
        }

        /// <summary>
        /// 由 ModResourceLoader.Awake（TitleUi.Awake 后）调用，重置 per-Trial 运行时状态。
        /// 数据（registry / 贴图 / Sprite）跨 Trial 持久；旧 ObjectionCutIn 实例已销毁，InstanceID 失效，故清 instanceCaches。
        /// </summary>
        public static void OnTitleAwake()
        {
            instanceCaches.Clear();
            pendingEntry = null;
            insideRewrite = false;
            layersDumped = false;

            if (registry.Count == 0)
                CutInLogDebug("No mod cut-ins configured; hijack patches inactive.");
            else
                CutInLogDebug($"OnTitleAwake: {registry.Count} mod cut-in(s) ready; per-Trial state reset.");
        }

        // ------------------------------------------------------------------
        // 注册 / 预烘焙
        // ------------------------------------------------------------------

        private static bool RegisterEntry(ModItem.ModObjectionCutIn entry, string modRoot)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Id)) return false;
            if (!string.Equals(entry.BaseTemplate, HiroTemplate, StringComparison.OrdinalIgnoreCase))
            {
                CutInLogWarning($"Cut-in '{entry.Id}' uses unsupported BaseTemplate '{entry.BaseTemplate}'; v1 only supports 'Hiro'. Skipping.");
                return false;
            }
            if (string.IsNullOrEmpty(modRoot))
            {
                CutInLogWarning($"Cut-in '{entry.Id}': mod root empty; skipping.");
                return false;
            }

            var reg = new RegistryEntry { Entry = entry, ModRoot = modRoot };
            BuildShaderOverrides(reg);
            reg.Particles = BuildParticleOverride(entry, modRoot);
            BuildTints(reg);
            reg.MeshAlphaThreshold = ResolveMeshAlphaThreshold(entry);
            BuildHiddenLayers(reg);
            PreloadSpriteTextures(entry, modRoot);
            registry[entry.Id] = reg;
            return true;
        }

        /// <summary>整理 entry.HiddenLayers：去空白、去重（大小写不敏感）。是否命中实际图层要到首次 spawn 时才知道。</summary>
        private static void BuildHiddenLayers(RegistryEntry reg)
        {
            var names = reg.Entry.HiddenLayers;
            if (names == null) return;
            foreach (var raw in names)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                reg.HiddenLayers.Add(raw.Trim());
            }
        }

        /// <summary>解析 entry.MeshAlphaThreshold（0–255）；缺省或越界 → 默认值（越界时警告）。</summary>
        private static byte ResolveMeshAlphaThreshold(ModItem.ModObjectionCutIn entry)
        {
            if (entry.MeshAlphaThreshold is not { } value) return DefaultMeshAlphaThreshold;
            if (value < 0 || value > 255)
            {
                CutInLogWarning($"Cut-in '{entry.Id}': MeshAlphaThreshold {value} is out of range 0–255, using {DefaultMeshAlphaThreshold}.");
                return DefaultMeshAlphaThreshold;
            }
            return (byte)value;
        }

        /// <summary>解析 entry.Tints（vanilla sprite 名 → HTML 颜色）。解析失败 → 警告并跳过该层。</summary>
        private static void BuildTints(RegistryEntry reg)
        {
            var tints = reg.Entry.Tints;
            if (tints == null) return;
            foreach (var pair in tints)
            {
                if (string.IsNullOrWhiteSpace(pair.Value)) continue;
                if (!TryParseHtmlColor(pair.Value, out var color))
                {
                    CutInLogWarning($"Cut-in '{reg.Entry.Id}' Tints[\"{pair.Key}\"]: invalid color '{pair.Value}', ignored.");
                    continue;
                }
                reg.Tints[pair.Key] = color;
            }
        }

        /// <summary>预加载 entry.Sprites 的所有贴图（"none" → 全透明）。Sprite.Create 延迟到首次 swap（需要 vanilla pivot / PPU）。</summary>
        private static void PreloadSpriteTextures(ModItem.ModObjectionCutIn entry, string modRoot)
        {
            if (entry.Sprites == null) return;
            foreach (var pair in entry.Sprites)
            {
                var key = (entry.Id, pair.Key);
                if (spriteTextures.ContainsKey(key)) continue;
                var tex = ResolveTexture(modRoot, pair.Value, entry.Id, $"Sprites[\"{pair.Key}\"]");
                if (tex != null) spriteTextures[key] = tex;
            }
        }

        /// <summary>把 entry.Shaders 解析成 5 个 per-role MPB。颜色解析失败 / 字段全空 → 该 role 留 null。</summary>
        private static void BuildShaderOverrides(RegistryEntry reg)
        {
            var entry = reg.Entry;
            string root = reg.ModRoot;
            string id = entry.Id;
            var sh = entry.Shaders;
            if (sh == null) return;

            // Background_0Fix
            if (sh.Background is { } bg)
            {
                MaterialPropertyBlock mpb = null;
                TryAddColor(ref mpb, "_BackgroundA", bg.PrimaryColor,   id, "Background.PrimaryColor");
                TryAddColor(ref mpb, "_BackgroundB", bg.SecondaryColor, id, "Background.SecondaryColor");
                AddFloat   (ref mpb, "_Float",       bg.BlendFactor);
                reg.ShaderRoles[RoleBackground] = mpb;
            }

            // Glasses_0Fix
            if (sh.StainedGlass is { } sg)
            {
                MaterialPropertyBlock mpb = null;
                TryAddColor  (ref mpb, "_EclipseFlame", sg.FlameColor, id, "StainedGlass.FlameColor");
                AddFloat     (ref mpb, "_Fader",        sg.Fader);
                AddFloat     (ref mpb, "_Fader2",       sg.Fader2);
                AddFloat     (ref mpb, "_Speed",        sg.Speed);
                AddFloat     (ref mpb, "_Tick",         sg.Tick);
                AddFloat     (ref mpb, "_EdgeSize",     sg.EdgeSize);
                // 槽名与原版文件名的 1/2 是反的，见类注释
                TryAddTexture(ref mpb, "_kirakira",     sg.CrackTexture,    root, id, "StainedGlass.CrackTexture");
                TryAddTexture(ref mpb, "_kirakira2",    sg.ShardTexture,    root, id, "StainedGlass.ShardTexture");
                TryAddTexture(ref mpb, "_luminescence", sg.GlowMaskTexture, root, id, "StainedGlass.GlowMaskTexture");
                reg.ShaderRoles[RoleStainedGlass] = mpb;
            }

            // Iuminescence_dezolve_0Fix
            if (sh.StainedGlassGlow is { } gg)
            {
                MaterialPropertyBlock mpb = null;
                TryAddColor(ref mpb, "_Color",        gg.Color,      id, "StainedGlassGlow.Color");
                TryAddColor(ref mpb, "_EclipseFlame", gg.FlameColor, id, "StainedGlassGlow.FlameColor");
                AddFloat   (ref mpb, "_Tick",         gg.Tick);
                reg.ShaderRoles[RoleStainedGlassGlow] = mpb;
            }

            // Shadow_Fix
            if (sh.CharacterShadow is { } cs)
            {
                MaterialPropertyBlock mpb = null;
                TryAddColor(ref mpb, "_Color", cs.Color, id, "CharacterShadow.Color");
                reg.ShaderRoles[RoleCharShadow] = mpb;
            }

            // Iuminescence_Silhouette_0Fix
            if (sh.CharacterGlow is { } cg)
            {
                MaterialPropertyBlock mpb = null;
                TryAddColor(ref mpb, "_Color", cg.Color, id, "CharacterGlow.Color");
                AddFloat   (ref mpb, "_Tick",  cg.Tick);
                reg.ShaderRoles[RoleCharGlow] = mpb;
            }
        }

        /// <summary>把 entry.Particles 解析成 MaterialPropertyBlock（全空 → null）。</summary>
        private static MaterialPropertyBlock BuildParticleOverride(ModItem.ModObjectionCutIn entry, string modRoot)
        {
            var cfg = entry.Particles;
            if (cfg == null) return null;

            MaterialPropertyBlock mpb = null;
            TryAddTexture(ref mpb, "_BaseMap",   cfg.Texture, modRoot, entry.Id, "Particles.Texture");
            TryAddColor  (ref mpb, "_BaseColor", cfg.Color,   entry.Id, "Particles.Color");
            return mpb;
        }

        /// <summary>解析并写入颜色。空 = 跳过；非空但解析失败 = 警告并跳过。</summary>
        private static bool TryAddColor(ref MaterialPropertyBlock mpb, string property, string raw, string entryId, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
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

        /// <summary>解析贴图字段并写入 MPB。空 = 跳过；"none" = 全透明；其余 = mod 内相对路径。失败 = 警告并跳过。</summary>
        private static bool TryAddTexture(ref MaterialPropertyBlock mpb, string property, string raw, string modRoot, string entryId, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var tex = ResolveTexture(modRoot, raw, entryId, fieldName);
            if (tex == null) return false;
            mpb ??= new MaterialPropertyBlock();
            mpb.SetTexture(property, tex);
            return true;
        }

        // ------------------------------------------------------------------
        // 贴图解析（所有贴图类字段共用）
        // ------------------------------------------------------------------

        /// <summary>
        /// 贴图字段统一解析：
        ///   - "none"（大小写不敏感）→ 共享的 4x4 全透明贴图
        ///   - 其它 → 相对 mod 根目录的图片文件，按绝对路径缓存（同一文件被多处引用只解码一次）
        /// 返回 null 表示失败（已记录警告 / 错误），调用方应保持原版。
        /// </summary>
        private static Texture2D ResolveTexture(string modRoot, string raw, string entryId, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string value = raw.Trim();

            if (string.Equals(value, TextureNoneKeyword, StringComparison.OrdinalIgnoreCase))
                return GetOrCreateClearTexture();

            string fullPath;
            try { fullPath = Path.GetFullPath(Path.Combine(modRoot, value)); }
            catch (Exception ex)
            {
                CutInLogWarning($"Cut-in '{entryId}' {fieldName}: invalid path '{value}': {ex.Message}");
                return null;
            }

            if (textureByPath.TryGetValue(fullPath, out var cached) && cached != null) return cached;

            if (!File.Exists(fullPath))
            {
                CutInLogWarning($"Cut-in '{entryId}' {fieldName}: file not found: {fullPath}");
                return null;
            }

            try
            {
                var bytes = File.ReadAllBytes(fullPath);
                var tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, bytes))
                {
                    CutInLogError($"Cut-in '{entryId}' {fieldName}: failed to decode image: {fullPath}");
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.name = $"ModCutIn_{Path.GetFileNameWithoutExtension(fullPath)}";
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                textureByPath[fullPath] = tex;
                RegisterAlphaMask(tex, bytes, fullPath, entryId, fieldName);
                return tex;
            }
            catch (Exception ex)
            {
                CutInLogError($"Cut-in '{entryId}' {fieldName}: load error for {fullPath}: {ex}");
                return null;
            }
        }

        /// <summary>从 PNG 原始字节解出 alpha 遮罩并按贴图 InstanceID 缓存；失败只记 debug 日志（回退到 Unity 自动网格）。</summary>
        private static void RegisterAlphaMask(Texture2D tex, byte[] pngBytes, string fullPath, string entryId, string fieldName)
        {
            try
            {
                var mask = PngAlphaReader.TryRead(pngBytes, out var error);
                if (mask == null)
                {
                    CutInLogDebug($"Cut-in '{entryId}' {fieldName}: alpha mask unavailable for {Path.GetFileName(fullPath)} ({error}); Unity tight mesh will be used.");
                    return;
                }
                if (mask.Width != tex.width || mask.Height != tex.height)
                {
                    CutInLogDebug($"Cut-in '{entryId}' {fieldName}: PNG size {mask.Width}x{mask.Height} != texture {tex.width}x{tex.height}; Unity tight mesh will be used.");
                    return;
                }
                alphaMaskByTextureId[tex.GetInstanceID()] = mask;
            }
            catch (Exception ex)
            {
                CutInLogDebug($"Cut-in '{entryId}' {fieldName}: alpha mask read error: {ex.Message}");
            }
        }

        /// <summary>全透明 4x4 贴图（RGBA 全 0）。作为 sprite 时不可见；作为 additive 遮罩时贡献为 0。</summary>
        private static Texture2D GetOrCreateClearTexture()
        {
            if (clearTexture != null) return clearTexture;
            try
            {
                var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                var pixels = new Color32[16];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(0, 0, 0, 0);
                tex.SetPixels32(pixels);
                tex.Apply(false, true);
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.name = "ModCutIn_ClearTexture";
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                clearTexture = tex;
            }
            catch (Exception ex)
            {
                CutInLogError($"Failed to create clear texture: {ex}");
            }
            return clearTexture;
        }

        /// <summary>解析 HTML 颜色（"#RRGGBB" / "#RRGGBBAA"，# 可省略）。</summary>
        private static bool TryParseHtmlColor(string raw, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(raw)) return false;
            string s = raw.Trim();
            if (!s.StartsWith("#")) s = "#" + s;
            return ColorUtility.TryParseHtmlString(s, out color);
        }

        // ------------------------------------------------------------------
        // 劫持：SetVariableValue
        // ------------------------------------------------------------------

        /// <summary>
        /// CustomVariableManager.SetVariableValue 的 Postfix：检测 mod cut-in 标识并改写为 Hiro。
        /// 变量值可能是裸 Kind / "ObjectionCutIn_&lt;Kind&gt;" / "CutIn/ObjectionCutIn_&lt;Kind&gt;" 之一
        /// （实际日志见到 spawn 名形式），用 ExtractKindFromCutInValue 兼容，再用 Replace 保留前缀结构。
        /// </summary>
        internal static void HandleSetVariableValuePostfix(CustomVariableManager mgr, string name, CustomVariableValue value)
        {
            if (insideRewrite) return;
            if (name != ObjectionCutInSpawnPathVar) return;
            if (registry.Count == 0) return;

            try
            {
                string val = value.String;
                string kind = ExtractKindFromCutInValue(val);
                if (string.IsNullOrEmpty(kind) || !registry.TryGetValue(kind, out var reg))
                {
                    // 原生模板（Hiro / Ema / CreatureHiro）或空值：清空残留暂存
                    pendingEntry = null;
                    return;
                }

                pendingEntry = reg;
                string newVal = val.Replace(kind, HiroTemplate);
                insideRewrite = true;
                try
                {
                    mgr.SetVariableValue(name, new CustomVariableValue(newVal));
                    CutInLogDebug($"Hijacked cut-in: '{val}' → '{newVal}'; overrides queued for next SetSpawnParameters.");
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

        /// <summary>从变量值中抽出 Cut-in Kind。兼容裸 Kind / spawn 名 / 完整路径三种形式。</summary>
        private static string ExtractKindFromCutInValue(string val)
        {
            if (string.IsNullOrEmpty(val)) return null;
            const string marker = "ObjectionCutIn_";
            int idx = val.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                string after = val[(idx + marker.Length)..];
                int slash = after.IndexOf('/');
                return slash > 0 ? after[..slash] : after;
            }
            int slash2 = val.IndexOf('/');
            return slash2 > 0 ? val[..slash2] : val;
        }

        // ------------------------------------------------------------------
        // 应用：SetSpawnParameters
        // ------------------------------------------------------------------

        /// <summary>
        /// ObjectionCutIn.SetSpawnParameters Postfix：取/建 InstanceCache，然后把实例收敛到当前条目
        /// （reg == null 表示原版 cut-in → 全部回滚到 vanilla）。
        /// </summary>
        internal static void HandleSetSpawnParametersPostfix(ObjectionCutIn instance)
        {
            var reg = pendingEntry;
            pendingEntry = null;
            if (instance == null || instance.gameObject == null) return;

            try
            {
                var go = instance.gameObject;
                int instId = go.GetInstanceID();
                if (!instanceCaches.TryGetValue(instId, out var cache))
                {
                    cache = BuildInstanceCache(go);
                    instanceCaches[instId] = cache;
                }

                ApplySprites(cache, reg);
                ApplyTints(cache, reg);
                ApplyHiddenLayers(cache, reg);
                ApplyShaderOverrides(cache, reg?.ShaderRoles);
                ApplyParticleOverride(cache, reg);

                if (reg != null && spriteKeysChecked.Add(reg.Entry.Id))
                    WarnUnmatchedLayerKeys(cache, reg);

                if (reg != null && !layersDumped && Plugin.Instance != null && Plugin.Instance.isDebug)
                {
                    layersDumped = true;
                    DumpInstance(go, cache);
                }
            }
            catch (Exception ex)
            {
                CutInLogError($"HandleSetSpawnParametersPostfix failed for '{reg?.Entry?.Id ?? "<vanilla>"}': {ex}");
            }
        }

        /// <summary>一次性扫描实例子树：sprite 层、按 shader 分桶的 SpriteRenderer、粒子渲染器。</summary>
        private static InstanceCache BuildInstanceCache(GameObject root)
        {
            var cache = new InstanceCache();
            try
            {
                var images = root.GetComponentsInChildren<Image>(true);
                if (images != null)
                {
                    foreach (var img in images)
                    {
                        if (img == null) continue;
                        var sp = img.sprite;
                        if (sp == null || string.IsNullOrEmpty(sp.name)) continue;
                        cache.Images.Add(img);
                        cache.ImageVanillaSprites.Add(sp);
                        cache.ImageVanillaNames.Add(sp.name);
                        cache.ImageVanillaColors.Add(img.color);
                        cache.ImageVanillaEnabled.Add(img.enabled);
                        var nodeNames = CollectNodeNames(img.transform, root.transform);
                        cache.ImageNodeNames.Add(nodeNames);
                        cache.NodeNames.UnionWith(nodeNames);
                    }
                }

                var spriteRenderers = root.GetComponentsInChildren<SpriteRenderer>(true);
                if (spriteRenderers != null)
                {
                    foreach (var sr in spriteRenderers)
                    {
                        if (sr == null) continue;

                        var sp = sr.sprite;
                        if (sp != null && !string.IsNullOrEmpty(sp.name))
                        {
                            cache.Sprites.Add(sr);
                            cache.SpriteVanillaSprites.Add(sp);
                            cache.SpriteVanillaNames.Add(sp.name);
                            cache.SpriteVanillaColors.Add(sr.color);
                            cache.SpriteVanillaEnabled.Add(sr.enabled);
                            var nodeNames = CollectNodeNames(sr.transform, root.transform);
                            cache.SpriteNodeNames.Add(nodeNames);
                            cache.NodeNames.UnionWith(nodeNames);
                        }

                        var mat = sr.sharedMaterial;
                        if (mat == null || mat.shader == null) continue;
                        int role = RoleForShaderName(mat.shader.name);
                        if (role >= 0) cache.RenderersByRole[role].Add(sr);
                    }
                }

                var renderers = root.GetComponentsInChildren<Renderer>(true);
                if (renderers != null)
                {
                    foreach (var r in renderers)
                    {
                        if (r == null) continue;
                        if (GetIl2CppTypeName(r) != ParticleRendererTypeName) continue;
                        cache.ParticleRenderers.Add(r);
                        cache.ParticleVanillaEnabled.Add(r.enabled);
                        var nodeNames = CollectNodeNames(r.transform, root.transform);
                        cache.ParticleNodeNames.Add(nodeNames);
                        cache.NodeNames.UnionWith(nodeNames);
                    }
                }
            }
            catch (Exception ex)
            {
                CutInLogError($"BuildInstanceCache failed: {ex}");
            }
            return cache;
        }

        /// <summary>节点自身及其到实例根（不含根）的所有祖先名，由近到远。</summary>
        private static string[] CollectNodeNames(Transform t, Transform root)
        {
            var names = new List<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                if (!string.IsNullOrEmpty(cur.name)) names.Add(cur.name);
                cur = cur.parent;
            }
            return names.ToArray();
        }

        private static int RoleForShaderName(string name)
        {
            for (int i = 0; i < ShaderRoleCount; i++)
                if (RoleShaderNames[i] == name) return i;
            return -1;
        }

        /// <summary>
        /// 把每个 sprite 层收敛到目标状态：条目覆盖了该层 → mod sprite；否则 → vanilla sprite。
        /// 原版 cut-in（reg == null）且实例从未被改过 → 直接跳过。
        /// </summary>
        private static void ApplySprites(InstanceCache cache, RegistryEntry reg)
        {
            if (reg == null && !cache.SpritesDirty) return;

            int swapped = 0, restored = 0;
            bool anyMod = false;
            try
            {
                for (int i = 0; i < cache.Images.Count; i++)
                {
                    var img = cache.Images[i];
                    if (img == null) continue;
                    var target = PickSprite(reg, cache.ImageVanillaNames[i], cache.ImageVanillaSprites[i], out bool isMod);
                    img.sprite = target;
                    if (isMod) { anyMod = true; swapped++; } else restored++;
                }

                for (int i = 0; i < cache.Sprites.Count; i++)
                {
                    var sr = cache.Sprites[i];
                    if (sr == null) continue;
                    var target = PickSprite(reg, cache.SpriteVanillaNames[i], cache.SpriteVanillaSprites[i], out bool isMod);
                    sr.sprite = target;
                    if (isMod) { anyMod = true; swapped++; } else restored++;
                }
            }
            catch (Exception ex)
            {
                CutInLogError($"ApplySprites for '{reg?.Entry?.Id ?? "<vanilla>"}' failed: {ex}");
            }

            cache.SpritesDirty = anyMod;
            if (reg != null)
                CutInLogDebug($"Cut-in '{reg.Entry.Id}': {swapped} sprite layer(s) replaced, {restored} left vanilla.");
            else
                CutInLogDebug($"Vanilla cut-in: restored {restored} sprite layer(s).");
        }

        /// <summary>
        /// Sprites / Tints 里写了但实例上没有任何一层叫这个名字的 key → 一次性警告（多半是拼错了）。
        /// HiddenLayers 同理，但它还可以是节点名（含祖先），且大小写不敏感。
        /// </summary>
        private static void WarnUnmatchedLayerKeys(InstanceCache cache, RegistryEntry reg)
        {
            try
            {
                var present = new HashSet<string>(cache.ImageVanillaNames);
                present.UnionWith(cache.SpriteVanillaNames);

                void Check(string section, IEnumerable<string> keys)
                {
                    if (keys == null) return;
                    var missing = new List<string>();
                    foreach (var key in keys)
                        if (!present.Contains(key)) missing.Add(key);
                    if (missing.Count > 0)
                        CutInLogWarning($"Cut-in '{reg.Entry.Id}': these {section} keys match no layer on the cut-in and are ignored (check spelling / case): {string.Join(", ", missing)}");
                }

                Check("Sprites", reg.Entry.Sprites?.Keys);
                Check("Tints", reg.Entry.Tints?.Keys);

                if (reg.HiddenLayers.Count > 0)
                {
                    var hideTargets = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
                    hideTargets.UnionWith(cache.NodeNames);
                    var missing = new List<string>();
                    foreach (var name in reg.HiddenLayers)
                        if (!hideTargets.Contains(name)) missing.Add(name);
                    if (missing.Count > 0)
                        CutInLogWarning($"Cut-in '{reg.Entry.Id}': these HiddenLayers entries match no layer or node on the cut-in and are ignored (check spelling): {string.Join(", ", missing)}");
                }
            }
            catch (Exception ex)
            {
                CutInLogDebug($"WarnUnmatchedLayerKeys failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 把每个层的着色收敛到目标状态：条目覆盖了该层 → mod 颜色；否则 → 首次扫描记录的 vanilla 颜色。
        /// 原版 cut-in 且实例从未被改过 → 跳过。
        /// </summary>
        private static void ApplyTints(InstanceCache cache, RegistryEntry reg)
        {
            bool hasTints = reg != null && reg.Tints.Count > 0;
            if (!hasTints && !cache.TintsDirty) return;

            int tinted = 0;
            try
            {
                for (int i = 0; i < cache.Images.Count; i++)
                {
                    var img = cache.Images[i];
                    if (img == null) continue;
                    Color target = cache.ImageVanillaColors[i];
                    if (hasTints && reg.Tints.TryGetValue(cache.ImageVanillaNames[i], out var modColor))
                    {
                        target = modColor;
                        tinted++;
                    }
                    img.color = target;
                }

                for (int i = 0; i < cache.Sprites.Count; i++)
                {
                    var sr = cache.Sprites[i];
                    if (sr == null) continue;
                    Color target = cache.SpriteVanillaColors[i];
                    if (hasTints && reg.Tints.TryGetValue(cache.SpriteVanillaNames[i], out var modColor))
                    {
                        target = modColor;
                        tinted++;
                    }
                    sr.color = target;
                }
            }
            catch (Exception ex)
            {
                CutInLogError($"ApplyTints for '{reg?.Entry?.Id ?? "<vanilla>"}' failed: {ex}");
            }

            cache.TintsDirty = tinted > 0;
            CutInLogDebug(tinted > 0
                ? $"Cut-in '{reg.Entry.Id}': tinted {tinted} layer(s)."
                : "Tints: restored vanilla layer colors.");
        }

        /// <summary>
        /// 把每个 Image / SpriteRenderer 层的 enabled 收敛到目标状态：条目把该层列入 HiddenLayers → false；
        /// 否则 → 首次扫描记录的 vanilla 值。粒子渲染器在 ApplyParticleOverride 里一并处理。
        /// 用 enabled 而不是 SetActive：演出 Timeline / _objectLibrary 只切 GameObject 激活状态，不碰 enabled，
        /// 所以关掉的层整场演出都不会被重新打开。原版 cut-in 且实例从未被改过 → 跳过。
        /// </summary>
        private static void ApplyHiddenLayers(InstanceCache cache, RegistryEntry reg)
        {
            bool hasHidden = reg != null && reg.HiddenLayers.Count > 0;
            if (!hasHidden && !cache.HiddenDirty) return;

            int hidden = 0;
            try
            {
                for (int i = 0; i < cache.Images.Count; i++)
                {
                    var img = cache.Images[i];
                    if (img == null) continue;
                    bool hide = hasHidden && reg.HidesLayer(cache.ImageVanillaNames[i], cache.ImageNodeNames[i]);
                    img.enabled = !hide && cache.ImageVanillaEnabled[i];
                    if (hide) hidden++;
                }

                for (int i = 0; i < cache.Sprites.Count; i++)
                {
                    var sr = cache.Sprites[i];
                    if (sr == null) continue;
                    bool hide = hasHidden && reg.HidesLayer(cache.SpriteVanillaNames[i], cache.SpriteNodeNames[i]);
                    sr.enabled = !hide && cache.SpriteVanillaEnabled[i];
                    if (hide) hidden++;
                }
            }
            catch (Exception ex)
            {
                CutInLogError($"ApplyHiddenLayers for '{reg?.Entry?.Id ?? "<vanilla>"}' failed: {ex}");
            }

            cache.HiddenDirty = hidden > 0;
            CutInLogDebug(hidden > 0
                ? $"Cut-in '{reg.Entry.Id}': {hidden} layer(s) switched off via HiddenLayers."
                : "HiddenLayers: restored vanilla layer visibility.");
        }

        private static Sprite PickSprite(RegistryEntry reg, string vanillaName, Sprite vanilla, out bool isMod)
        {
            isMod = false;
            if (reg == null || !reg.OverridesSprite(vanillaName)) return vanilla;
            var mod = GetOrCreateSprite(reg.Entry.Id, vanillaName, vanilla, reg.MeshAlphaThreshold);
            if (mod == null) return vanilla;
            isMod = true;
            return mod;
        }

        /// <summary>从预加载贴图创建 / 取出缓存的 Sprite。复用 vanilla sprite 的 pivot 和 pixelsPerUnit。</summary>
        private static Sprite GetOrCreateSprite(string modId, string vanillaName, Sprite vanillaSprite, byte meshAlphaThreshold)
        {
            var key = (modId, vanillaName);
            if (spriteCache.TryGetValue(key, out var cached)) return cached;

            if (!spriteTextures.TryGetValue(key, out var tex) || tex == null)
            {
                spriteCache[key] = null; // 注册时已警告
                return null;
            }

            try
            {
                var rect = vanillaSprite.rect;
                var pivot = (rect.width > 0 && rect.height > 0)
                    ? vanillaSprite.pivot / new Vector2(rect.width, rect.height)
                    : new Vector2(0.5f, 0.5f);
                float ppu = vanillaSprite.pixelsPerUnit > 0 ? vanillaSprite.pixelsPerUnit : 100f;
                var texRect = new Rect(0, 0, tex.width, tex.height);

                // 优先：FullRect 网格 + 我们自己按 alpha 生成的贴合轮廓网格（见 alphaMaskByTextureId 注释）。
                // 任一步失败 → 回退到 Unity 运行时 Tight 网格（旧行为）。
                Sprite sprite = null;
                if (alphaMaskByTextureId.TryGetValue(tex.GetInstanceID(), out var mask) && mask != null)
                {
                    var mesh = SpriteMeshBuilder.Build(mask.Alpha, mask.Width, mask.Height, meshAlphaThreshold);
                    if (mesh != null && mask.Width == tex.width && mask.Height == tex.height)
                    {
                        try
                        {
                            sprite = Sprite.Create(tex, texRect, pivot, ppu, 0u, SpriteMeshType.FullRect);
                            var verts = new Vector2[mesh.Vertices.Length / 2];
                            for (int i = 0; i < verts.Length; i++)
                                verts[i] = new Vector2(mesh.Vertices[i * 2], mesh.Vertices[i * 2 + 1]);
                            sprite.OverrideGeometry((Il2CppStructArray<Vector2>)verts, (Il2CppStructArray<ushort>)mesh.Triangles);
                            CutInLogInfo($"Cut-in '{modId}': '{vanillaName}' mesh from alpha (threshold {meshAlphaThreshold}): {mesh.RectCount} rect(s), block {mesh.BlockSize}px, {verts.Length} vertices.");
                        }
                        catch (Exception ex)
                        {
                            CutInLogWarning($"Cut-in '{modId}': custom mesh for '{vanillaName}' failed, falling back to Unity tight mesh: {ex.Message}");
                            if (sprite != null) UnityEngine.Object.Destroy(sprite);
                            sprite = null;
                        }
                    }
                }

                if (sprite == null)
                    sprite = Sprite.Create(tex, texRect, pivot, ppu);

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

        /// <summary>按 role 应用 / 回滚 MPB。每个实例独立追踪。</summary>
        private static void ApplyShaderOverrides(InstanceCache cache, MaterialPropertyBlock[] roles)
        {
            for (int role = 0; role < ShaderRoleCount; role++)
            {
                var mpb = roles?[role];
                bool hasOverride = mpb != null;
                if (!hasOverride && !cache.AppliedRoles[role]) continue;

                var renderers = cache.RenderersByRole[role];
                if (renderers.Count == 0)
                {
                    CutInLogDebug($"Shaders.{RoleNames[role]}: no SpriteRenderer with shader '{RoleShaderNames[role]}' on this instance; skipped.");
                    continue;
                }

                int affected = 0;
                try
                {
                    foreach (var sr in renderers)
                    {
                        if (sr == null) continue;
                        sr.SetPropertyBlock(hasOverride ? mpb : null);
                        affected++;
                    }
                }
                catch (Exception ex)
                {
                    CutInLogError($"Shaders.{RoleNames[role]} apply failed: {ex}");
                    continue;
                }

                cache.AppliedRoles[role] = hasOverride;
                CutInLogDebug($"Shaders.{RoleNames[role]}: {(hasOverride ? "applied" : "cleared")} on {affected} renderer(s).");
            }
        }

        /// <summary>
        /// 应用 / 回滚粒子渲染器覆盖（MPB + enabled）。
        /// 关闭粒子只有一个来源：HiddenLayers 命中粒子节点（原版节点名 "Glass"）。
        /// </summary>
        private static void ApplyParticleOverride(InstanceCache cache, RegistryEntry reg)
        {
            var mpb = reg?.Particles;
            bool mayHide = reg != null && reg.HiddenLayers.Count > 0;
            if (mpb == null && !mayHide && !cache.ParticleApplied) return;

            if (cache.ParticleRenderers.Count == 0)
            {
                CutInLogDebug("Particles: no ParticleSystemRenderer on this instance; skipped.");
                return;
            }

            int affected = 0, hidden = 0;
            try
            {
                for (int i = 0; i < cache.ParticleRenderers.Count; i++)
                {
                    var r = cache.ParticleRenderers[i];
                    if (r == null) continue;
                    bool hide = mayHide && reg.HidesLayer(null, cache.ParticleNodeNames[i]);
                    r.SetPropertyBlock(mpb);
                    r.enabled = !hide && cache.ParticleVanillaEnabled[i];
                    if (hide) hidden++;
                    affected++;
                }
            }
            catch (Exception ex)
            {
                CutInLogError($"Particles apply failed: {ex}");
                return;
            }

            cache.ParticleApplied = mpb != null || hidden > 0;
            string state = hidden > 0 ? "hidden" : mpb != null ? "applied" : "restored";
            CutInLogDebug($"Particles: {state} on {affected} renderer(s).");
        }

        // ------------------------------------------------------------------
        // 诊断 dump（仅 isDebug，每个 Title→Trial 周期一次）
        // ------------------------------------------------------------------

        /// <summary>
        /// 打印实例上的所有层与所有非默认材质的 shader 属性当前值。
        /// 由于在 ApplySprites 之后调用，sprite 名会显示 mod sprite（ModCutIn_ 前缀）；vanilla 名以缓存为准一并打印。
        /// </summary>
        private static void DumpInstance(GameObject root, InstanceCache cache)
        {
            try
            {
                CutInLogInfo("First mod cut-in spawn — layers on this ObjectionCutIn instance (vanilla = Sprites key; active = GameObject.activeSelf; enabled = renderer switch, HiddenLayers target; node path segments = HiddenLayers names):");

                var materials = new Dictionary<int, Material>();

                // 每一行独立 try/catch：任何一个 IL2CPP 调用失败（如 unstripping）只丢这一行，不中断整个 dump
                for (int i = 0; i < cache.Images.Count; i++)
                {
                    var img = cache.Images[i];
                    if (img == null) continue;
                    try
                    {
                        string matInfo = "";
                        var mat = img.material;
                        if (mat != null)
                        {
                            matInfo = $" mat={mat.name ?? "?"} shader={(mat.shader != null ? mat.shader.name : "<null>")}";
                            materials.TryAdd(mat.GetInstanceID(), mat);
                        }
                        CutInLogInfo($"  Image           {GetTransformPath(img.transform, root.transform)} -> vanilla={cache.ImageVanillaNames[i]} now={SpriteName(img.sprite)} color={ToHex(img.color)} active={img.gameObject.activeSelf} enabled={img.enabled}{matInfo}");
                    }
                    catch (Exception ex)
                    {
                        CutInLogWarning($"  Image           #{i} (vanilla={cache.ImageVanillaNames[i]}): dump failed: {ex.Message}");
                    }
                }

                for (int i = 0; i < cache.Sprites.Count; i++)
                {
                    var sr = cache.Sprites[i];
                    if (sr == null) continue;
                    try
                    {
                        string matInfo = "";
                        var mat = sr.sharedMaterial; // 不触发实例化
                        if (mat != null)
                        {
                            matInfo = $" mat={mat.name ?? "?"} shader={(mat.shader != null ? mat.shader.name : "<null>")}";
                            materials.TryAdd(mat.GetInstanceID(), mat);
                        }
                        string sorting = "";
                        try { sorting = $" sorting={sr.sortingLayerName}:{sr.sortingOrder}"; } catch { }
                        CutInLogInfo($"  SpriteRenderer  {GetTransformPath(sr.transform, root.transform)} -> vanilla={cache.SpriteVanillaNames[i]} now={SpriteName(sr.sprite)} color={ToHex(sr.color)} active={sr.gameObject.activeSelf} enabled={sr.enabled}{sorting}{matInfo}");
                    }
                    catch (Exception ex)
                    {
                        CutInLogWarning($"  SpriteRenderer  #{i} (vanilla={cache.SpriteVanillaNames[i]}): dump failed: {ex.Message}");
                    }
                }

                // 无 Image / SpriteRenderer 的节点：列出组件类型；若带 Renderer 则附材质信息（如粒子）
                var allTransforms = root.GetComponentsInChildren<Transform>(true);
                if (allTransforms != null)
                {
                    foreach (var t in allTransforms)
                    {
                        if (t == null || t == root.transform) continue;
                        if (t.GetComponent<Image>() != null || t.GetComponent<SpriteRenderer>() != null) continue;

                        var comps = t.GetComponents<Component>();
                        if (comps == null || comps.Length == 0) continue;
                        var names = new List<string>();
                        foreach (var comp in comps)
                        {
                            if (comp == null) continue;
                            string typeName = GetIl2CppTypeName(comp);
                            if (typeName == "Transform" || typeName == "RectTransform") continue;
                            names.Add(typeName);
                        }
                        if (names.Count == 0) continue;

                        string matInfo = "";
                        try
                        {
                            var rd = t.GetComponent<Renderer>();
                            var mat = rd != null ? rd.sharedMaterial : null;
                            if (mat != null)
                            {
                                matInfo = $" enabled={rd.enabled} mat={mat.name ?? "?"} shader={(mat.shader != null ? mat.shader.name : "<null>")}";
                                materials.TryAdd(mat.GetInstanceID(), mat);
                            }
                        }
                        catch { }
                        CutInLogInfo($"  Other           {GetTransformPath(t, root.transform)} -> [{string.Join(", ", names)}] active={t.gameObject.activeSelf}{matInfo}");
                    }
                }

                CutInLogInfo("Materials on this instance (sharedMaterial values = vanilla defaults; mod MPB overrides are not reflected here):");
                foreach (var mat in materials.Values)
                {
                    try
                    {
                        if (mat == null || mat.shader == null) continue;
                        if (mat.shader.name == DefaultSpriteShaderName) continue;
                        CutInLogInfo($"  {mat.name} ({mat.shader.name}): {DescribeMaterialProperties(mat)}");
                    }
                    catch (Exception ex)
                    {
                        CutInLogWarning($"  material dump failed: {ex.Message}");
                    }
                }

            }
            catch (Exception ex)
            {
                CutInLogWarning($"DumpInstance failed: {ex.Message}");
            }
        }

        /// <summary>列出材质 shader 的全部属性及当前值（跳过 unity_ 内部属性）。</summary>
        private static string DescribeMaterialProperties(Material mat)
        {
            var parts = new List<string>();
            try
            {
                var shader = mat.shader;
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    string name = shader.GetPropertyName(i);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("unity_", StringComparison.Ordinal)) continue;
                    var type = shader.GetPropertyType(i);
                    string value;
                    try
                    {
                        switch (type)
                        {
                            case ShaderPropertyType.Color:   value = ToHex(mat.GetColor(name)); break;
                            case ShaderPropertyType.Vector:  value = FormatVector(mat.GetVector(name)); break;
                            case ShaderPropertyType.Float:
                            case ShaderPropertyType.Range:   value = mat.GetFloat(name).ToString("0.###"); break;
                            case ShaderPropertyType.Int:     value = mat.GetInteger(name).ToString(); break;
                            case ShaderPropertyType.Texture:
                                var t = mat.GetTexture(name);
                                value = t != null ? t.name : "<null>";
                                break;
                            default: value = "?"; break;
                        }
                    }
                    catch { value = "<err>"; }
                    parts.Add($"{name}={value} [{type}]");
                }
            }
            catch (Exception ex)
            {
                parts.Add($"<failed: {ex.Message}>");
            }
            return string.Join(", ", parts);
        }

        private static string GetIl2CppTypeName(Component comp)
        {
            try { return comp.GetIl2CppType()?.Name ?? "?"; }
            catch { return "?"; }
        }

        private static string SpriteName(Sprite sp) => sp != null ? sp.name : "<null>";

        /// <summary>
        /// Color → "#RRGGBBAA"。手写而不用 ColorUtility.ToHtmlStringRGBA：
        /// 那是纯托管实现，被游戏的 IL2CPP 构建 strip 掉了，Il2CppInterop unstripping 失败会直接抛异常。
        /// HDR / 越界分量截断到 0..255。
        /// </summary>
        private static string ToHex(Color c)
        {
            static int B(float f) => f <= 0f ? 0 : f >= 1f ? 255 : (int)Math.Round(f * 255.0);
            return $"#{B(c.r):X2}{B(c.g):X2}{B(c.b):X2}{B(c.a):X2}";
        }

        private static string FormatVector(Vector4 v)
            => $"({v.x:0.###}, {v.y:0.###}, {v.z:0.###}, {v.w:0.###})";

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
}
