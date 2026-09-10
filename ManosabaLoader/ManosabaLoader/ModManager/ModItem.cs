using ManosabaLoader.Utils;
using GigaCreation.Essentials.Localization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ManosabaLoader.ModManager
{
    public class ModItem
    {
        /// <summary>
        /// 只注入 Naninovel，用于仅需要在对话框中显示名字的简单角色。
        /// </summary>
        public class ModSimpleCharacter
        {
            /// <summary>Naninovel actor ID，即剧本中使用的角色标识符。</summary>
            public string Id { get; set; } = "Taffy";

            /// <summary>角色显示名，支持本地化。若设置则为最高优先级，直接使用。</summary>
            public LocalizedString DisplayName { get; set; }

            /// <summary>
            /// 角色主题色（HTML 格式，如 "#C8AACC" 或 "C8AACC"）。
            /// 未设置时默认为白色。
            /// </summary>
            public string Color { get; set; } = "";

            /// <summary>角色名（名前 / given name）。与 FamilyName 配合自动生成 DisplayName。</summary>
            public LocalizedString Name { get; set; }

            /// <summary>角色姓（姓氏 / family name）。与 Name 配合自动生成 DisplayName。</summary>
            public LocalizedString FamilyName { get; set; }
        }

        /// <summary>
        /// 注入 Naninovel、CharacterData 和 AuthorData。
        /// AuthorText 始终根据 Name / FamilyName 自动生成。
        /// </summary>
        public class ModCharacter
        {
            /// <summary>Naninovel actor ID，同时也用作 CharacterData / AuthorData 的 ID。</summary>
            public string Id { get; set; } = "Taffy";

            /// <summary>角色显示名，支持本地化。若设置则为最高优先级，直接使用。</summary>
            public LocalizedString DisplayName { get; set; }

            /// <summary>
            /// 角色主题色（HTML 格式，如 "#C8AACC" 或 "C8AACC"）。
            /// 未设置时默认为白色。用于 DisplayName 富文本标记和 AuthorText 颜色标记。
            /// </summary>
            public string Color { get; set; } = "";

            /// <summary>角色名（名前 / given name）。与 FamilyName 配合自动生成 DisplayName。</summary>
            public LocalizedString Name { get; set; }

            /// <summary>角色姓（姓氏 / family name）。与 Name 配合自动生成 DisplayName。</summary>
            public LocalizedString FamilyName { get; set; }

            /// <summary>年龄文字。</summary>
            public string Age { get; set; } = "";
            /// <summary>身高文字。</summary>
            public string Height { get; set; } = "";
            /// <summary>体重文字。</summary>
            public string Weight { get; set; } = "";
        }

        /// <summary>
        /// 通用版本化分组容器，将同一 Id 的多个版本聚合在一起。
        /// 模仿原版游戏的 VersionedItem&lt;T&gt; 概念，避免同 Id 的多个 Version 扁平展开。
        /// </summary>
        public class ModVersionedGroup<T>
        {
            public string Id { get; set; } = "";
            public T[] Items { get; set; } = [];
        }

        /// <summary>
        /// 角色简介条目（对应 ProfileData / VersionedItem&lt;ProfileDataItem&gt;）。
        /// </summary>
        public class ModProfile
        {
            public int Version { get; set; } = 1;
            public LocalizedString Description { get; set; } = new();
        }

        /// <summary>线索版本条目，用于 ModVersionedGroup 内部。</summary>
        public class ModClueItem
        {
            public int Version { get; set; } = 1;
            public LocalizedString Name { get; set; } = new();
            public LocalizedString Description { get; set; } = new();
        }

        /// <summary>规定版本条目，用于 ModVersionedGroup 内部。</summary>
        public class ModRuleItem
        {
            public int Version { get; set; } = 1;
            /// <summary>编号文字，如 "I", "II", "III" 等</summary>
            public string Numbering { get; set; } = "";
            public LocalizedString Subtitle { get; set; } = new();
            public LocalizedString Description { get; set; } = new();
        }

        /// <summary>记录版本条目，用于 ModVersionedGroup 内部。</summary>
        public class ModNoteItem
        {
            public int Version { get; set; } = 1;
            public LocalizedString Title { get; set; } = new();
            public LocalizedString Description { get; set; } = new();
        }

        /// <summary>
        /// 自定义 JSON 转换器：同时支持纯字符串（向后兼容，视为 zh-Hans）和标准对象格式。
        /// </summary>
        public class LocalizedStringConverter : JsonConverter<LocalizedString>
        {
            public override LocalizedString Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String)
                    return new LocalizedString { ZhHans = reader.GetString() };

                if (reader.TokenType == JsonTokenType.Null)
                    return null;

                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    var ls = new LocalizedString();
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndObject) break;
                        if (reader.TokenType == JsonTokenType.PropertyName)
                        {
                            string prop = reader.GetString();
                            reader.Read();
                            switch (prop)
                            {
                                case "ja": ls.Ja = reader.GetString(); break;
                                case "zh-Hans": ls.ZhHans = reader.GetString(); break;
                            }
                        }
                    }
                    return ls;
                }

                throw new JsonException($"Unexpected token {reader.TokenType} for LocalizedString");
            }

            public override void Write(Utf8JsonWriter writer, LocalizedString value, JsonSerializerOptions options)
            {
                if (value == null) { writer.WriteNullValue(); return; }
                writer.WriteStartObject();
                if (value.Ja != null) writer.WriteString("ja", value.Ja);
                if (value.ZhHans != null) writer.WriteString("zh-Hans", value.ZhHans);
                writer.WriteEndObject();
            }
        }

        /// <summary>
        /// Dictionary&lt;string, LocalizedString&gt; 的自定义转换器：
        /// 值同时支持纯字符串（向后兼容）和 LocalizedString 对象。
        /// </summary>
        public class LocalizedStringDictionaryConverter : JsonConverter<Dictionary<string, LocalizedString>>
        {
            public override Dictionary<string, LocalizedString> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                var dict = new Dictionary<string, LocalizedString>();
                if (reader.TokenType != JsonTokenType.StartObject)
                    throw new JsonException("Expected StartObject for dictionary");

                var lsConverter = new LocalizedStringConverter();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) break;
                    string key = reader.GetString();
                    reader.Read();
                    dict[key] = lsConverter.Read(ref reader, typeof(LocalizedString), options);
                }
                return dict;
            }

            public override void Write(Utf8JsonWriter writer, Dictionary<string, LocalizedString> value, JsonSerializerOptions options)
            {
                writer.WriteStartObject();
                var lsConverter = new LocalizedStringConverter();
                foreach (var kvp in value)
                {
                    writer.WritePropertyName(kvp.Key);
                    lsConverter.Write(writer, kvp.Value, options);
                }
                writer.WriteEndObject();
            }
        }

        [JsonConverter(typeof(LocalizedStringConverter))]
        public class LocalizedString
        {
            [JsonPropertyName("ja")]
            public string Ja { get; set; }

            //[JsonPropertyName("en-US")]
            //public string EnUs { get; set; }

            [JsonPropertyName("zh-Hans")]
            public string ZhHans { get; set; }

            //[JsonPropertyName("zh-Hant")]
            //public string ZhHant { get; set; }

            /// <summary>
            /// 根据当前游戏语言返回最佳本地化文本。
            /// 优先返回当前语言的文本，若为空则回退到其它可用语言。
            /// </summary>
            public string Resolve()
            {
                string locale = LocaleHelper.GetCurrentLocale();
                return locale switch
                {
                    "ja" => Ja ?? ZhHans,
                    "zh-Hans" => ZhHans ?? Ja,
                    _ => ZhHans ?? Ja
                };
            }

            /// <summary>
            /// 按优先级返回最佳本地化文本，若全部为空则返回 <paramref name="fallback"/>。
            /// </summary>
            public string Resolve(string fallback)
            {
                var v = Resolve();
                return string.IsNullOrEmpty(v) ? fallback : v;
            }

            public Il2CppReferenceArray<LocalizedText> ToIl2CppArray()
            {
                var tempList = new System.Collections.Generic.List<LocalizedText>();

                if (!string.IsNullOrEmpty(Ja))
                    tempList.Add(new LocalizedText(LocaleKind.Ja, Ja));

                if (!string.IsNullOrEmpty(ZhHans))
                    tempList.Add(new LocalizedText(LocaleKind.ZhHans, ZhHans));

                if (tempList.Count == 0)
                    tempList.Add(new LocalizedText(LocaleKind.ZhHans, ""));

                var il2cppArray = new Il2CppReferenceArray<LocalizedText>(tempList.Count);
                for (int i = 0; i < tempList.Count; i++)
                {
                    il2cppArray[i] = tempList[i];
                }

                return il2cppArray;
            }
        }

        /// <summary>线索扁平记录（供加载器内部使用，由 ModVersionedGroup 展开生成）。</summary>
        public class ModClue
        {
            public string Id { get; set; } = "";
            public int Version { get; set; } = 1;
            public LocalizedString Name { get; set; } = new();
            public LocalizedString Description { get; set; } = new();
        }

        /// <summary>规定扁平记录（供加载器内部使用，由 ModVersionedGroup 展开生成）。</summary>
        public class ModRule
        {
            public string Id { get; set; } = "";
            public int Version { get; set; } = 1;
            /// <summary>编号文字，如 "I", "II", "III" 等</summary>
            public string Numbering { get; set; } = "";
            public LocalizedString Subtitle { get; set; } = new();
            public LocalizedString Description { get; set; } = new();
        }

        /// <summary>记录扁平记录（供加载器内部使用，由 ModVersionedGroup 展开生成）。</summary>
        public class ModNote
        {
            public string Id { get; set; } = "";
            public int Version { get; set; } = 1;
            public LocalizedString Title { get; set; } = new();
            public LocalizedString Description { get; set; } = new();
        }

        /// <summary>
        /// Mod 自定义 @choice handler。
        /// 在剧本中通过 <c>@choice "..." handler:"&lt;Id&gt;"</c> 使用。
        /// 克隆原版 Trial / TrialHiro 面板，仅替换其中的人物立绘 Sprite。
        /// </summary>
        public class ModChoiceHandler
        {
            /// <summary>handler ID，剧本中作为 <c>handler:"..."</c> 的值使用。</summary>
            public string Id { get; set; } = "";
            /// <summary>克隆来源面板：<c>"Trial"</c>（默认）或 <c>"TrialHiro"</c>。</summary>
            public string BasePanel { get; set; } = "Trial";
            /// <summary>立绘图片路径，相对于 mod 目录，如 <c>"ChoiceHandlers/anan.png"</c>。</summary>
            public string Portrait { get; set; } = "";
        }

        /// <summary>
        /// Cut-in prefab 内各 Shader Graph shader 的参数覆盖。按视觉元素分组：
        /// 每个子配置对应 prefab 中一类 SpriteRenderer（按 shader 名匹配），
        /// 通过 <see cref="UnityEngine.MaterialPropertyBlock"/> 实现 per-property 覆盖。
        /// 任何字段 null/空 = 保持 sharedMaterial 角色默认值（Hiro/Ema 等专属配色）。
        ///
        /// JSON 示例（所有字段都是可选的，按需声明）：
        /// <code>
        /// "Shaders": {
        ///   "Background":       { "PrimaryColor": "#66CCFF", "SecondaryColor": "#001133", "BlendFactor": 0.5 },
        ///   "StainedGlass":     { "FlameColor": "#33FF99", "Fader": 0.7, "Speed": 4.0,
        ///                         "CrackTexture": "none", "ShardTexture": "Cutins/my_shards.png" },
        ///   "StainedGlassGlow": { "Color": "#AAEEFF" },
        ///   "CharacterShadow":  { "Color": "#88AACC" },
        ///   "CharacterGlow":    { "Color": "#CCEEFF", "Tick": 0.4 }
        /// }
        /// </code>
        /// 贴图类字段（*Texture）统一接受 mod 内相对路径或 <c>"none"</c>（全透明贴图）。
        /// 每个字段对应的 shader 属性、原版贴图与默认值见 docs/cutin.*.md。
        /// </summary>
        public class ModShaders
        {
            /// <summary>
            /// 全屏噪点 / 渐变背景层（BackGround GameObject）。
            /// Shader: <c>Shader Graphs/Background_0Fix</c>。
            /// </summary>
            public BackgroundParams Background { get; set; }

            /// <summary>
            /// 破碎彩色玻璃碎片（RefuteCutIn_StainedGlass_001/002/003）。
            /// Shader: <c>Shader Graphs/Glasses_0Fix</c>。
            /// </summary>
            public StainedGlassParams StainedGlass { get; set; }

            /// <summary>
            /// 彩色玻璃发光叠加层（RefuteCutIn_StainedGlass_luminescence）。
            /// Shader: <c>Shader Graphs/Iuminescence_dezolve_0Fix</c>。
            /// </summary>
            public StainedGlassGlowParams StainedGlassGlow { get; set; }

            /// <summary>
            /// 角色侧 / 背阴影叠加（RefuteCutIn_Hiro_Shadow）。
            /// Shader: <c>Shader Graphs/Shadow_Fix</c>。
            /// </summary>
            public CharacterShadowParams CharacterShadow { get; set; }

            /// <summary>
            /// 角色发光叠加（RefuteCutIn_Hiro_luminescence）。
            /// Shader: <c>Shader Graphs/Iuminescence_Silhouette_0Fix</c>。
            /// </summary>
            public CharacterGlowParams CharacterGlow { get; set; }

            /// <summary>Background_0Fix shader 参数。</summary>
            public class BackgroundParams
            {
                /// <summary>主色调，覆盖 <c>_BackgroundA</c>（角色专属，Hiro 实测 #FF3C45）。HTML 格式。</summary>
                public string PrimaryColor { get; set; }
                /// <summary>副色调，覆盖 <c>_BackgroundB</c>（Hiro 实测 #000000）。参与渐变 / 混合。HTML 格式。</summary>
                public string SecondaryColor { get; set; }
                /// <summary>混合 / 噪点因子，覆盖 <c>_Float</c>（默认 0.3）。</summary>
                public float? BlendFactor { get; set; }
            }

            /// <summary>Glasses_0Fix shader 参数。</summary>
            public class StainedGlassParams
            {
                /// <summary>火焰色调，覆盖 <c>_EclipseFlame</c>（Hiro 实测 #FF000B）。HTML 格式。</summary>
                public string FlameColor { get; set; }
                /// <summary>渐变因子 1，覆盖 <c>_Fader</c>（范围 0–1；由演出动画驱动，静止值 1，覆盖会定住动画）。</summary>
                public float? Fader { get; set; }
                /// <summary>渐变因子 2，覆盖 <c>_Fader2</c>（范围 0–1；由演出动画驱动，静止值 1，覆盖会定住动画）。</summary>
                public float? Fader2 { get; set; }
                /// <summary>动画速度，覆盖 <c>_Speed</c>（默认 3）。</summary>
                public float? Speed { get; set; }
                /// <summary>动画 phase，覆盖 <c>_Tick</c>（默认 0.119，范围 0–1）。</summary>
                public float? Tick { get; set; }
                /// <summary>边缘宽度，覆盖 <c>_EdgeSize</c>（Hiro 实测 0.02）。</summary>
                public float? EdgeSize { get; set; }

                /// <summary>
                /// 覆盖玻璃裂纹遮罩贴图，shader 槽 <c>_kirakira</c>
                /// （原版材质里装的是 <c>Hiro_CutIn_StainedGlass_003_kirakira2</c>：黑底白色细裂纹线，2048×883）。
                /// 这些贴图挂在材质 Glasses_Fix_Hiro 上而不是 Sprite 上，所以无法通过 <c>Sprites</c> 替换。
                /// 取值：mod 内相对图片路径（与玻璃 sprite 同尺寸的黑底白色遮罩），
                /// 或 <c>"none"</c> 表示换成全透明贴图以去掉该效果。空 = 保持原版。
                /// </summary>
                public string CrackTexture { get; set; }
                /// <summary>
                /// 覆盖玻璃碎片高光遮罩贴图，shader 槽 <c>_kirakira2</c>
                /// （原版材质里装的是 <c>Hiro_CutIn_StainedGlass_003_kirakira1</c>：黑底白色碎片块高光，2048×883）。
                /// 取值同 <see cref="CrackTexture"/>。
                /// </summary>
                public string ShardTexture { get; set; }
                /// <summary>
                /// 覆盖玻璃发光区域遮罩贴图，shader 槽 <c>_luminescence</c>
                /// （原版 <c>RefuteCutIn_StainedGlass_luminescence001</c>：黑底白色玻璃带剪影，2048×883）。
                /// 取值同 <see cref="CrackTexture"/>。
                /// </summary>
                public string GlowMaskTexture { get; set;}
            }

            /// <summary>Iuminescence_dezolve_0Fix shader 参数。</summary>
            public class StainedGlassGlowParams
            {
                /// <summary>主色，覆盖 <c>_Color</c>（Hiro 实测 #FDA5A4；另与该层 SpriteRenderer tint #FFDEFF 相乘）。HTML 格式。</summary>
                public string Color { get; set; }
                /// <summary>火焰色，覆盖 <c>_EclipseFlame</c>（Hiro 实测 #FF000B）。HTML 格式。</summary>
                public string FlameColor { get; set; }
                /// <summary>溶解 phase，覆盖 <c>_Tick</c>（范围 0–1；由演出动画驱动，静止值 0.952，覆盖会定住动画）。</summary>
                public float? Tick { get; set; }
            }

            /// <summary>Shadow_Fix shader 参数。</summary>
            public class CharacterShadowParams
            {
                /// <summary>阴影主色，覆盖 <c>_Color</c>（Hiro 实测 #737D99 灰蓝）。HTML 格式。</summary>
                public string Color { get; set; }
            }

            /// <summary>Iuminescence_Silhouette_0Fix shader 参数。</summary>
            public class CharacterGlowParams
            {
                /// <summary>发光主色，覆盖 <c>_Color</c>（Hiro 实测 #FDA5A4；另与该层 SpriteRenderer tint #FFDEFF 相乘）。HTML 格式。</summary>
                public string Color { get; set; }
                /// <summary>动画 phase，覆盖 <c>_Tick</c>（默认 0.119，范围 0–1）。</summary>
                public float? Tick { get; set; }
            }
        }

        /// <summary>
        /// Mod 自定义 cut-in（异议演出）。
        /// 在剧本中通过 <c>@gosubCutIn "&lt;Id&gt;" index:N</c> 使用。
        /// 克隆原版 ObjectionCutIn_Hiro prefab，按名称替换其中的 Sprite，
        /// 复用原版动画时间轴和 per-index 可见性切换。
        /// </summary>
        public class ModObjectionCutIn
        {
            /// <summary>CutIn ID，对应 <c>@gosubCutIn "Id"</c> 的值。所有 mod 之间必须唯一。</summary>
            public string Id { get; set; } = "";
            /// <summary>克隆来源模板。v1 仅支持 <c>"Hiro"</c>。</summary>
            public string BaseTemplate { get; set; } = "Hiro";
            /// <summary>
            /// 原版 Sprite 名 → mod 内相对图片路径的替换映射。
            /// 例如 <c>{ "Hiro_CutIn_001": "Cutins/MyChar/main_001.png", ... }</c>。
            /// 值为 <c>"none"</c> 时该层换成全透明贴图（即隐藏该层）。
            /// 未列出的 Sprite 保持原版不变。完整 key 列表见 docs/cutin.*.md。
            /// </summary>
            public Dictionary<string, string> Sprites { get; set; } = new();

            /// <summary>
            /// 原版 Sprite 名 → 该层着色（<c>SpriteRenderer.color</c>，HTML 颜色）的覆盖映射，键与 <see cref="Sprites"/> 相同。
            /// 着色与该层的图片、材质颜色相乘；原版除 <c>Hiro_CutIn_StainedGlass_luminescence001</c>（#FFDEFF）外基本都是白色。
            /// 若某层的着色由演出动画驱动（如 <c>White</c> 的透明度），动画写入的通道会盖掉覆盖值。
            /// 未列出的层保持原版着色。
            /// </summary>
            public Dictionary<string, string> Tints { get; set; } = new();

            /// <summary>
            /// 要整层关掉的图层名列表，例如 <c>["BackGround", "Glass2"]</c>。
            /// 每一项匹配原版 Sprite 名（与 <see cref="Sprites"/> 的键相同）或 prefab 节点名
            /// （<c>BackGround</c> / <c>Hiro</c> / <c>CutIN</c> / <c>Glow</c> / <c>Glass2</c> / <c>Glass</c> 等，
            /// 节点名同时关掉其下所有层）。大小写不敏感。
            /// 通过 <c>Renderer.enabled = false</c>（Image 为 <c>Behaviour.enabled</c>）实现：演出动画只切 GameObject 激活状态，
            /// 不碰 enabled，所以关掉的层整场演出都不会再出现。比 <c>Sprites:"none"</c> 更省，
            /// 也是唯一能关掉 <c>BackGround</c>（颜色由 shader 生成、不采样 sprite）的方式。
            /// 未列出的层保持原版；切回原版 cut-in 时自动恢复。
            /// </summary>
            public string[] HiddenLayers { get; set; } = [];

            /// <summary>
            /// 替换图网格裁切用的 alpha 阈值（0–255，默认 64）：alpha ≥ 阈值的像素算作图片的一部分。
            /// 玻璃 shader 不读贴图 alpha，玻璃带的形状完全由网格裁出（见 ModObjectionCutInLoader / SpriteMeshBuilder）；
            /// 调低可保留更淡的边缘和细刺，调高则裁得更紧。对该 cut-in 的所有替换图生效。null = 默认。
            /// </summary>
            public int? MeshAlphaThreshold { get; set; }

            /// <summary>
            /// 覆盖 cut-in prefab 内各 Shader Graph shader 的参数。按视觉元素分组。
            /// 任何字段 null/空 = 保持该项的角色默认；整个对象 null = 完全保持原版。
            /// </summary>
            public ModShaders Shaders { get; set; }

            /// <summary>
            /// 覆盖 cut-in 内的闪光粒子（prefab 节点 "Glass"，ParticleSystem，材质 Kirakira）。
            /// 整个对象 null = 保持原版。
            /// </summary>
            public ModCutInParticles Particles { get; set; }
        }

        /// <summary>
        /// Cut-in 闪光粒子（ParticleSystemRenderer，shader <c>Universal Render Pipeline/Particles/Unlit</c>）的覆盖。
        /// 通过 <see cref="UnityEngine.MaterialPropertyBlock"/> 实现，不改动粒子系统本身的发射参数。
        /// 要整个关掉粒子，在 <see cref="ModObjectionCutIn.HiddenLayers"/> 里写 <c>"Glass"</c>。
        /// </summary>
        public class ModCutInParticles
        {
            /// <summary>
            /// 粒子贴图，覆盖 <c>_BaseMap</c>（原版 <c>kirakira</c>：128×128 白色十字星光，透明背景）。
            /// 取值：mod 内相对图片路径，或 <c>"none"</c>（全透明，粒子不可见）。空 = 保持原版。
            /// </summary>
            public string Texture { get; set; }
            /// <summary>粒子颜色乘数，覆盖 <c>_BaseColor</c>（默认白）。HTML 格式。会与粒子系统自身的颜色相乘。</summary>
            public string Color { get; set; }
        }

        public class ModDescription
        {
            const string DefaultAuthor = "佚名";
            const string DefaultDescription = "无内容。";

            /// <summary>info.json 的 Schema 版本号。当前目标版本为 2.2。</summary>
            [JsonPropertyName("$schemaVersion")]
            public string SchemaVersion { get; set; }

            public LocalizedString Name { get; set; } = new() { ZhHans = "" };
            public LocalizedString Description { get; set; } = new() { ZhHans = DefaultDescription };
            public LocalizedString Author { get; set; } = new() { ZhHans = DefaultAuthor };
            public string Version { get; set; } = "1.0.0";
            public string Enter { get; set; } = "";
            /// <summary>只注入 Naninovel 的简单角色。</summary>
            public ModSimpleCharacter[] SimpleCharacters { get; set; } = [];
            /// <summary>
            /// 注入 Naninovel + CharacterData + AuthorData 的角色。
            /// 不包含简介数据，简介通过 <see cref="Profiles"/> 单独管理。
            /// </summary>
            public ModCharacter[] Characters { get; set; } = [];
            public ModVersionedGroup<ModClueItem>[] Clues { get; set; } = [];
            /// <summary>
            /// 角色简介分组列表，与 Characters 解耦。
            /// 每个条目通过 Id 引用角色（可以是 Characters 中定义的 mod 角色，
            /// 也可以是原版游戏角色 ID），从而允许覆写或追加原版角色的简介词条。
            /// </summary>
            public ModVersionedGroup<ModProfile>[] Profiles { get; set; } = [];
            public ModVersionedGroup<ModRuleItem>[] Rules { get; set; } = [];
            public ModVersionedGroup<ModNoteItem>[] Notes { get; set; } = [];
            /// <summary>
            /// Mod 自定义 @choice handler。每个条目克隆原版 Trial 面板并替换立绘，
            /// 在剧本中可通过 <c>handler:"&lt;Id&gt;"</c> 使用。
            /// </summary>
            public ModChoiceHandler[] ChoiceHandlers { get; set; } = [];

            /// <summary>
            /// Mod 自定义 cut-in。每个条目克隆原版 ObjectionCutIn_Hiro prefab 并按名称替换 sprite，
            /// 在剧本中可通过 <c>@gosubCutIn "&lt;Id&gt;" index:N</c> 使用。
            /// </summary>
            public ModObjectionCutIn[] CutIns { get; set; } = [];

            /// <summary>
            /// 自定义章节名映射：脚本路径 → 存档画面显示的章节名（支持本地化）。
            ///
            /// 键为 Naninovel 脚本路径（与 PlaybackSpot.ScriptPath 对应），
            /// 值为要在存档画面中显示的自定义文字，支持 LocalizedString 或纯字符串（向后兼容）。
            ///
            /// 示例：
            /// <code>
            /// "ChapterNames": {
            ///     "mymod_1_1_Adv": { "zh-Hans": "第一幕 第一章", "ja": "第一幕 第一章" },
            ///     "mymod_1_2_Trial": "第一幕 第二章 审判"
            /// }
            /// </code>
            /// </summary>
            [JsonConverter(typeof(LocalizedStringDictionaryConverter))]
            public Dictionary<string, LocalizedString> ChapterNames { get; set; } = new();
        }
        class ModItemException : Exception
        {
            public ModItemException(string ex) : base(ex) { }
        }

        /// <summary>当前 info.json schema 版本。</summary>
        public const string CurrentSchemaVersion = "2.2";

        private static readonly JsonSerializerOptions MigrationWriteOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        bool valid = false;
        ModDescription description = null;
        
        public bool Valid
        {
            get => valid;
            internal set => valid = value;
        }

        public ModDescription Description
        {
            get => description;
            internal set => description = value;
        }

        internal ModItem()
        {

        }

        public ModItem(string path, string config)
        {
            try
            {
                config = MigrateIfNeeded(path, config);
                description = JsonSerializer.Deserialize<ModDescription>(config);
                if (description == null || string.IsNullOrEmpty(description.Name?.Resolve()) || string.IsNullOrEmpty(description.Enter))
                {
                    throw new ModItemException("config format error.");
                }
                valid = true;
            }
            catch (Exception ex)
            {
                ModManager.ModManagerLogError(string.Format("Load {0} failed!", path));
                ModManager.ModManagerLogError(ex.ToString());
            }
        }

        // ================================================================
        // Schema 迁移
        // ================================================================

        /// <summary>待确认的迁移信息。</summary>
        internal class PendingMigration
        {
            public string ModDir;
            public string FromVersion;
            public string ToVersion;
            public string MigratedJson;
        }

        /// <summary>所有待用户确认的迁移列表。</summary>
        internal static readonly List<PendingMigration> PendingMigrations = new();

        /// <summary>迁移步骤定义。</summary>
        private static readonly (string From, string To, Action<JsonObject> Migrate)[] MigrationSteps =
        [
            ("1.0", "2.0", Migrate_1_0_To_2_0),
            ("2.0", "2.1", Migrate_2_0_To_2_1),
            ("2.1", "2.2", Migrate_2_1_To_2_2),
        ];

        /// <summary>
        /// 检测并在内存中执行 schema 迁移，但不写回文件。
        /// 文件写回需用户通过弹窗确认后调用 <see cref="CommitMigration"/>。
        /// </summary>
        private static string MigrateIfNeeded(string path, string config)
        {
            JsonNode root;
            try { root = JsonNode.Parse(config); }
            catch { return config; }

            var obj = root as JsonObject;
            if (obj == null) return config;

            string schemaVersion = obj["$schemaVersion"]?.GetValue<string>()
                ?? obj["SchemaVersion"]?.GetValue<string>();
            if (schemaVersion == CurrentSchemaVersion)
                return config;

            string detectedVersion = DetectSchemaVersion(obj, schemaVersion);
            if (detectedVersion == CurrentSchemaVersion)
                return config;

            // 在内存中顺序执行迁移
            RunMigrations(obj, detectedVersion);

            string migrated = obj.ToJsonString(MigrationWriteOptions);

            // 记录待确认迁移（文件写回由弹窗确认后触发）
            string modDir = System.IO.Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path);
            PendingMigrations.Add(new PendingMigration
            {
                ModDir = modDir,
                FromVersion = detectedVersion,
                ToVersion = CurrentSchemaVersion,
                MigratedJson = migrated,
            });

            ModManager.ModManagerLogMessage($"Detected {ModManager.CONFIG_NAME} schema {detectedVersion}, needs migration to {CurrentSchemaVersion}. Awaiting confirmation.");

            return migrated;
        }

        /// <summary>
        /// 从指定版本开始，顺序执行后续所有迁移步骤。
        /// </summary>
        private static void RunMigrations(JsonObject obj, string fromVersion)
        {
            bool started = false;
            foreach (var (from, to, migrate) in MigrationSteps)
            {
                if (!started && from == fromVersion)
                    started = true;
                if (started)
                    migrate(obj);
            }
        }

        /// <summary>
        /// 将待确认的迁移写回文件（备份 + 保存）。
        /// </summary>
        internal static void CommitMigration(PendingMigration pm)
        {
            string configPath = System.IO.Path.Combine(pm.ModDir, ModManager.CONFIG_NAME);

            // 备份原始文件
            string backupName = $"info.{pm.FromVersion}.json";
            string backupPath = System.IO.Path.Combine(pm.ModDir, backupName);
            try
            {
                System.IO.File.Copy(configPath, backupPath, overwrite: true);
                ModManager.ModManagerLogMessage($"Backed up {ModManager.CONFIG_NAME} → {backupName}");
            }
            catch (Exception ex)
            {
                ModManager.ModManagerLogWarning($"Failed to backup {ModManager.CONFIG_NAME}: {ex.Message}");
            }

            // 写回迁移后的文件
            try
            {
                System.IO.File.WriteAllText(configPath, pm.MigratedJson);
                ModManager.ModManagerLogMessage($"Migrated {ModManager.CONFIG_NAME} from {pm.FromVersion} → {pm.ToVersion}");
            }
            catch (Exception ex)
            {
                ModManager.ModManagerLogWarning($"Failed to write migrated {ModManager.CONFIG_NAME}: {ex.Message}");
            }
        }

        /// <summary>
        /// 检测 info.json 的 schema 版本。
        /// </summary>
        private static string DetectSchemaVersion(JsonObject obj, string explicitVersion)
        {
            if (!string.IsNullOrEmpty(explicitVersion))
                return explicitVersion;

            var characters = obj["Characters"]?.AsArray();
            if (characters != null && characters.Count > 0)
            {
                var first = characters[0]?.AsObject();
                if (first != null && first.ContainsKey("ActorId"))
                    return "1.0";
            }

            return "2.0";
        }

        /// <summary>
        /// 1.0 → 2.0 迁移：将旧 Characters（ActorId/DisplayName）转为 SimpleCharacters。
        /// </summary>
        private static void Migrate_1_0_To_2_0(JsonObject obj)
        {
            var characters = obj["Characters"]?.AsArray();
            if (characters == null || characters.Count == 0) return;

            var simpleChars = obj["SimpleCharacters"]?.AsArray() ?? new JsonArray();

            for (int i = characters.Count - 1; i >= 0; i--)
            {
                var entry = characters[i]?.AsObject();
                if (entry == null) continue;
                if (!entry.ContainsKey("ActorId")) continue;

                string actorId = entry["ActorId"]?.GetValue<string>() ?? "";
                string displayName = entry["DisplayName"]?.GetValue<string>() ?? "";

                var newEntry = new JsonObject
                {
                    ["Id"] = actorId,
                    ["DisplayName"] = new JsonObject
                    {
                        ["zh-Hans"] = displayName
                    }
                };

                simpleChars.Add(newEntry);
                characters.RemoveAt(i);
            }

            obj["SimpleCharacters"] = simpleChars;
            if (characters.Count == 0)
                obj["Characters"] = new JsonArray();

            ModManager.ModManagerLogMessage($"Migration 1.0→2.0: converted {simpleChars.Count} old Characters to SimpleCharacters.");
        }

        /// <summary>
        /// 2.0 → 2.1 迁移：添加 $schemaVersion 字段。
        /// </summary>
        private static void Migrate_2_0_To_2_1(JsonObject obj)
        {
            obj["$schemaVersion"] = "2.1";
            ModManager.ModManagerLogMessage("Migration 2.0→2.1: added $schemaVersion field.");
        }

        /// <summary>
        /// 2.1 → 2.2 迁移：将 Name/Description/Author 从字符串转为 LocalizedString 对象，
        /// 将 ChapterNames 的值从字符串转为 LocalizedString 对象。
        /// </summary>
        private static void Migrate_2_1_To_2_2(JsonObject obj)
        {
            foreach (var field in new[] { "Name", "Description", "Author" })
            {
                var node = obj[field];
                if (node is JsonValue val && val.TryGetValue(out string strVal))
                {
                    obj[field] = new JsonObject { ["zh-Hans"] = strVal };
                }
            }

            if (obj["ChapterNames"] is JsonObject chapters)
            {
                foreach (var key in chapters.Select(kvp => kvp.Key).ToList())
                {
                    var val = chapters[key];
                    if (val is JsonValue jv && jv.TryGetValue(out string s))
                    {
                        chapters[key] = new JsonObject { ["zh-Hans"] = s };
                    }
                }
            }

            obj["$schemaVersion"] = "2.2";
            ModManager.ModManagerLogMessage("Migration 2.1→2.2: converted Name/Description/Author/ChapterNames to LocalizedString.");
        }
    }
}
