using ManosabaLoader.Utils;
using GigaCreation.Essentials.Localization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System;
using System.Collections.Generic;
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

            /// <summary>角色显示名，支持本地化。</summary>
            public LocalizedString DisplayName { get; set; }
        }

        /// <summary>
        /// 注入 Naninovel、CharacterData 和 AuthorData。
        /// AuthorText 始终根据 Name / FamilyName 自动生成。
        /// </summary>
        public class ModCharacter
        {
            /// <summary>Naninovel actor ID，同时也用作 CharacterData / AuthorData 的 ID。</summary>
            public string Id { get; set; } = "Taffy";

            /// <summary>
            /// 角色主题色（HTML 格式，如 "#C8AACC" 或 "C8AACC"）。
            /// 用于 CharacterMetadata.NameColor 和 AuthorText 颜色标记。
            /// </summary>
            public string Color { get; set; } = "";

            /// <summary>角色名（名前 / given name）。</summary>
            public LocalizedString Name { get; set; }

            /// <summary>角色姓（姓氏 / family name）。</summary>
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

        public class ModDescription
        {
            const string DefaultAuthor = "佚名";
            const string DefaultDescription = "无内容。";

            /// <summary>info.json 的 Schema 版本号。当前目标版本为 2.1。</summary>
            [JsonPropertyName("$schemaVersion")]
            public string SchemaVersion { get; set; }

            public string Name { get; set; } = "";
            public string Description { get; set; } = DefaultDescription;
            public string Author { get; set; } = DefaultAuthor;
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
            /// 自定义章节名映射：脚本路径 → 存档画面显示的章节名。
            /// 
            /// 键为 Naninovel 脚本路径（与 PlaybackSpot.ScriptPath 对应），
            /// 值为要在存档画面中显示的自定义文字。
            /// 
            /// 示例：
            /// <code>
            /// "ChapterNames": {
            ///     "mymod_1_1_Adv": "第一幕 第一章",
            ///     "mymod_1_2_Trial": "第一幕 第二章 审判",
            ///     "mymod_ending": "终章"
            /// }
            /// </code>
            /// </summary>
            public Dictionary<string, string> ChapterNames { get; set; } = new();
        }
        class ModItemException : Exception
        {
            public ModItemException(string ex) : base(ex) { }
        }

        /// <summary>当前 info.json schema 版本。</summary>
        public const string CurrentSchemaVersion = "2.1";

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
                if (description == null || string.IsNullOrEmpty(description.Name) || string.IsNullOrEmpty(description.Enter))
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
    }
}
