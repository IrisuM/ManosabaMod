using System;
using System.Collections.Generic;
using System.IO;

using GigaCreation.NaninovelExtender.Audio;
using GigaCreation.NaninovelExtender.Common;
using GigaCreation.NaninovelExtender.ExtendedActors;

using HarmonyLib;

using Il2CppInterop.Runtime;

using ManosabaLoader.ModManager;

using Naninovel;

using UnityEngine;

using WitchTrials.Models;
using WitchTrials.Views;

namespace ManosabaLoader
{
    public static class ModResourceLoader
    {
        public static Action<string> ScriptLoaderLogMessage;
        public static Action<string> ScriptLoaderLogInfo;
        public static Action<string> ScriptLoaderLogDebug;
        public static Action<string> ScriptLoaderLogWarning;
        public static Action<string> ScriptLoaderLogError;

        /// <summary>已注入的 mod ProvisionSource（按加载器类型分组），语言切换时 InitializeProvisionSources 会清除这些注入，需要重新注入。</summary>
        private static readonly List<ProvisionSource> modInjectedTextSources = new();
        private static readonly List<ProvisionSource> modInjectedAudioSources = new();
        private static readonly List<ProvisionSource> modInjectedVoiceSources = new();

        private static ProvisionSource modProvisionSource = null;
        private static ProvisionSource modTextProvisionSource = null;
        public const string modScriptPrefix = "TaffyModLoader";
        const string modMenuScript = "TaffyStart";
        private static string modScriptEnter = modMenuScript;
        private static string modScriptEnterLabel = null;

        /// <summary>当前已加载注入数据的 mod key。null 表示未加载任何 mod（原版/标题）。</summary>
        private static string _currentLoadedModKey = null;

        /// <summary>Naninovel 变量名，用于标识当前选择的 mod。</summary>
        public const string ModKeyVariable = "modKey";

        /// <summary>原版游戏标识（不加载任何 mod 数据）。</summary>
        public const string VanillaModKey = "__vanilla__";

        /// <summary>脚本工作区标识。</summary>
        public const string WorkspaceModKey = "__workspace__";

        /// <summary>已注册的简单角色映射（ID → 原始数据），用于本地化更新。</summary>
        private static readonly Dictionary<string, ModItem.ModSimpleCharacter> simpleCharacterMap = new();

        /// <summary>已注册的富角色映射（ID → 原始数据），用于本地化更新。</summary>
        private static readonly Dictionary<string, ModItem.ModCharacter> richCharacterMap = new();

        /// <summary>该 ID 是否为 Mod 注册的简单角色。</summary>
        public static bool IsModSimpleCharacter(string id) => simpleCharacterMap.ContainsKey(id);

        /// <summary>获取简单角色当前语言的 DisplayName（含 \u200B 前缀）。</summary>
        public static string GetSimpleCharacterDisplayName(string id)
        {
            if (!simpleCharacterMap.TryGetValue(id, out var sc)) return null;
            return '\u200B' + ResolveDisplayName(sc.Id, sc.DisplayName, sc.Color, sc.FamilyName, sc.Name);
        }

        public static NamedString ModScriptEnter => new NamedString(modScriptEnter, modScriptEnterLabel);

        /// <summary>
        /// 从 Naninovel 变量中读取当前选择的 mod key。
        /// 返回 null 表示未选择 mod 或选择了原版。
        /// </summary>
        public static string GetCurrentModKey()
        {
            try
            {
                if (!Engine.Initialized) return null;
                var varManager = Engine.GetService<ICustomVariableManager>();
                if (varManager == null) return null;
                var modKey = varManager.GetVariableValue(ModKeyVariable).String;
                if (string.IsNullOrEmpty(modKey) || modKey == VanillaModKey) return null;
                return modKey;
            }
            catch { return null; }
        }

        /// <summary>
        /// 确保已为当前 mod 加载了注入数据。
        /// 在注入 hook 中调用此方法以实现延迟加载。
        /// </summary>
        public static void EnsureModDataLoaded()
        {
            string wantedKey = GetCurrentModKey();
            if (wantedKey == _currentLoadedModKey) return;

            // mod 发生变化，清理旧数据并加载新数据
            ClearAllModData();
            if (wantedKey != null)
            {
                LoadSelectedModData(wantedKey);
            }
            _currentLoadedModKey = wantedKey;
        }

        /// <summary>清理所有 mod 注入数据，释放内存。</summary>
        public static void ClearAllModData()
        {
            ModClueLoader.ClearModData();
            ModRuleNoteLoader.ClearModData();
            ModProfileLoader.ClearModData();
            ModMovieLoader.ClearModData();
            _currentLoadedModKey = null;
            ScriptLoaderLogInfo("All mod injection data cleared.");
        }

        /// <summary>加载指定 mod 的注入数据。</summary>
        private static void LoadSelectedModData(string modKey)
        {
            ModItem modItem;
            string modPath;

            if (modKey == WorkspaceModKey && ScriptWorkingManager.IsEnabled && ScriptWorkingManager.ModInfo != null)
            {
                modItem = ScriptWorkingManager.ModInfo;
                modPath = ScriptWorkingManager.WorkspacePath;
            }
            else if (ModManager.ModManager.Items.TryGetValue(modKey, out modItem))
            {
                modPath = Path.Combine(Plugin.Instance.ModRootPath, modKey);
            }
            else
            {
                ScriptLoaderLogWarning($"Mod key '{modKey}' not found, no data loaded.");
                return;
            }

            ModClueLoader.LoadModData(modKey, modPath, modItem);
            ModRuleNoteLoader.LoadModData(modKey, modItem);
            ModProfileLoader.LoadModData(modKey, modPath, modItem);
            ModMovieLoader.LoadModData(modKey, modPath);
            ScriptLoaderLogMessage($"Loaded injection data for mod: {modKey}");
        }

        public static void Init(Harmony instance, string enter, string label, bool directMode)
        {
            instance.PatchAll(typeof(TitleUi_Patch));

            if (directMode)
            {
                modScriptEnter = enter;
                modScriptEnterLabel = label;
            }

            // 初始化 Mod 线索加载器
            ModClueLoader.Init(instance);

            // 初始化 Mod 规则/笔记加载器
            ModRuleNoteLoader.Init(instance);

            // 初始化存档画面章节名显示
            ModChapterDisplay.Init(instance);

            // 初始化 Mod 角色简介加载器
            ModProfileLoader.Init(instance);

            // 初始化统一 WitchBook 补丁（@update 命令拦截 + GetActiveEvidences 安全网）
            ModWitchBookPatch.Init(instance);

            // 初始化 Mod 视频加载器（@movie 命令支持）
            ModMovieLoader.Init(instance);

            // 语言切换时更新角色 DisplayName
            Utils.LocaleHelper.OnLocaleChanged += RefreshCharacterDisplayNames;
        }

        /// <summary>语言切换后重新注入已被 InitializeProvisionSources 清除的 mod ProvisionSource。</summary>
        internal static void ReInjectModProvisionSources()
        {
            if (modInjectedTextSources.Count == 0 && modInjectedAudioSources.Count == 0 && modInjectedVoiceSources.Count == 0)
                return;
            int count = 0;
            try
            {
                if (modInjectedTextSources.Count > 0)
                {
                    var service = Engine.GetServiceOrErr<TextManager>();
                    var ps = service.textLoader.Cast<ResourceLoader<TextAsset>>().ProvisionSources;
                    foreach (var s in modInjectedTextSources)
                        ps.System_Collections_IList_Insert(0, s);
                    count += modInjectedTextSources.Count;
                }
                if (modInjectedAudioSources.Count > 0)
                {
                    var service = Engine.GetServiceOrErr<AudioManagerExtended>();
                    var ps = service.audioLoader.Cast<ResourceLoader<AudioClip>>().ProvisionSources;
                    foreach (var s in modInjectedAudioSources)
                        ps.System_Collections_IList_Insert(0, s);
                    count += modInjectedAudioSources.Count;
                }
                if (modInjectedVoiceSources.Count > 0)
                {
                    var service = Engine.GetServiceOrErr<AudioManagerExtended>();
                    var ps = service.voiceLoader.Cast<ResourceLoader<AudioClip>>().ProvisionSources;
                    foreach (var s in modInjectedVoiceSources)
                        ps.System_Collections_IList_Insert(0, s);
                    count += modInjectedVoiceSources.Count;
                }
            }
            catch (Exception ex)
            {
                ScriptLoaderLogWarning($"Failed to re-inject mod provision sources: {ex.Message}");
            }
            if (count > 0)
                ScriptLoaderLogDebug($"Re-injected {count} mod provision source(s) after locale/provision source rebuild.");
        }

        /// <summary>语言切换时刷新所有 mod 角色的 CharacterMetadata.DisplayName。</summary>
        private static void RefreshCharacterDisplayNames()
        {
            if (simpleCharacterMap.Count == 0 && richCharacterMap.Count == 0) return;
            try
            {
                var service = Engine.GetServiceOrErr<CharacterManager>();
                int count = 0;
                foreach (var kvp in simpleCharacterMap)
                {
                    if (service.Configuration.ActorMetadataMap.ContainsId(kvp.Key))
                    {
                        var sc = kvp.Value;
                        var meta = service.Configuration.ActorMetadataMap[kvp.Key];
                        meta.DisplayName = '\u200B' + ResolveDisplayName(sc.Id, sc.DisplayName, sc.Color, sc.FamilyName, sc.Name);
                        count++;
                    }
                }
                foreach (var kvp in richCharacterMap)
                {
                    if (service.Configuration.ActorMetadataMap.ContainsId(kvp.Key))
                    {
                        var rc = kvp.Value;
                        var meta = service.Configuration.ActorMetadataMap[kvp.Key];
                        meta.DisplayName = '\u200B' + ResolveDisplayName(rc.Id, rc.DisplayName, rc.Color, rc.FamilyName, rc.Name);
                        count++;
                    }
                }
                ScriptLoaderLogDebug($"Refreshed {count} character display names for locale change.");
            }
            catch (Exception ex)
            {
                ScriptLoaderLogWarning($"Failed to refresh character display names: {ex.Message}");
            }
        }

        /// <summary>
        /// 统一的 DisplayName 解析逻辑（SimpleCharacter 和 Character 共用）。
        ///
        /// 优先级：
        /// 1. 若 DisplayName 已设置 → 直接使用
        /// 2. Color 未设置时默认白色
        /// 3. 若 FamilyName + Name 均已设置 → 自动生成富文本
        /// 4. 仅部分名字或均未设置 → 使用可用名字或回退到 Id，按姓氏格式处理
        /// </summary>
        private static string ResolveDisplayName(string id, ModItem.LocalizedString displayName, string color, ModItem.LocalizedString familyName, ModItem.LocalizedString name)
        {
            // 优先级 1：显式 DisplayName
            string explicitName = displayName?.Resolve();
            if (!string.IsNullOrEmpty(explicitName))
                return explicitName;

            // 优先级 2：确定颜色（默认白色，不带 # 前缀）
            string colorHex = "FFFFFF";
            if (!string.IsNullOrEmpty(color))
            {
                colorHex = color;
                if (colorHex.StartsWith("#"))
                    colorHex = colorHex.Substring(1);
            }

            string family = familyName?.Resolve();
            string given = name?.Resolve();

            // 优先级 3：姓名均有 → 自动生成全名富文本
            if (!string.IsNullOrEmpty(family) && !string.IsNullOrEmpty(given))
                return AuthorTaggedTextGenerator.BuildDisplayName(family, given, colorHex);

            // 优先级 4：回退到可用名字或 Id，按姓氏格式处理
            string fallback = !string.IsNullOrEmpty(family) ? family
                : !string.IsNullOrEmpty(given) ? given
                : id;
            return AuthorTaggedTextGenerator.BuildSimpleDisplayName(fallback, colorHex);
        }

        public static void Awake()
        {
            foreach (var service in Engine.services)
            {
                ScriptLoaderLogDebug(string.Format("Find Engine:{0}",Il2CppType.TypeFromPointer(service.ObjectClass).FullName));
            }

            // 创建每帧语言检测组件，确保语言切换后 OnLocaleChanged 立即触发
            try
            {
                Plugin.Instance.AddComponent<LocaleWatcherComponent>();
                ScriptLoaderLogDebug("LocaleWatcherComponent created for per-frame locale detection.");
            }
            catch (Exception ex)
            {
                ScriptLoaderLogWarning($"Could not create LocaleWatcherComponent: {ex.Message}");
            }

            //添加Mod框架私有加载器
            var localResourceProvider = new LocalResourceProvider("");
            modProvisionSource = new ProvisionSource(localResourceProvider.Cast<IResourceProvider>(), Path.Combine(modScriptPrefix, "Scripts").Replace("\\", "/"));
            modTextProvisionSource = new ProvisionSource(localResourceProvider.Cast<IResourceProvider>(), Path.Combine(modScriptPrefix, "Text").Replace("\\", "/"));

            var rootPath = Plugin.Instance.ModRootPath;
            foreach (var item in ModManager.ModManager.Items)
            {
                AddModLoader(rootPath, item.Key, "Scripts");
                foreach (var sc in item.Value.Description.SimpleCharacters)
                    AddSimpleCharacter(item.Key, sc);
                foreach (var c in item.Value.Description.Characters)
                    AddRichCharacter(item.Key, c);
            }
            
            if (ScriptWorkingManager.IsEnabled)
            {
                var root = Path.GetDirectoryName(ScriptWorkingManager.WorkspacePath);
                var prefix = Path.GetFileName(ScriptWorkingManager.WorkspacePath);
                AddModLoader(ScriptWorkingManager.WorkspacePath, "", "Scripts");
                foreach (var sc in ScriptWorkingManager.ModInfo.Description.SimpleCharacters)
                    AddSimpleCharacter("", sc);
                foreach (var c in ScriptWorkingManager.ModInfo.Description.Characters)
                    AddRichCharacter("", c);
            }
        }
        public static void AddModStartMenu()
        {
            {
                var service = Engine.GetServiceOrErr<WitchTrialsScriptPlayer>();
                if (service.scripts.ScriptLoader.GetLoaded(modMenuScript) != null)
                {
                    return;
                }
            }

            //创建开始菜单
            string TaffyStart =
"""

@ProcessInput false
@trialMode false
@HideUI AutoToggle,WitchBookButtonUI AllowToggle:false time:0
@ShowUI ControlPanel time:0
@back SubId:"Overlay" SolidColor tint:"#000000" time:0 Lazy:false

@back 50_3 pos:50,50 Id:"Stills" Scale:{g_backgroundDefaultScale} time:0 Lazy:false
@back SubId:"Overlay" Transparent time:0.5 Lazy:false

""";
            string choice_list = "\n";
            const int perChoiceCount = 4;
            const string perChoiceLabel = "ChoiceList_";
            int choice_index = 0;

            int choice_page = 0;
            choice_list += "# " + perChoiceLabel + choice_page + "\n";

            //原始剧本
            var version = Engine.GetServiceOrErr<StateManagerExtended>().GlobalState.GetState<VersioningManager.VersioningState>().EditedVersion;
            choice_list += "@choice \"原版游戏剧情\" Lock:false play:true show:true" + "\n";
            choice_list += "    @set \"nextScenario=\\\"Act01_Chapter01/Act01_Chapter01_Adv01\\\"\"" + "\n";
            choice_list += "    @set \"modKey=\\\"" + VanillaModKey + "\\\"\"" + "\n";
            choice_list += "    @set \"modName=\\\"原版游戏剧情\\\"\"" + "\n";
            choice_list += "    @set \"modDescription=\\\"原汁原味的游戏内容。\\\"\"" + "\n";
            choice_list += "    @set \"modAuthor=\\\"Acacia, Re,AER\\\"\"" + "\n";
            choice_list += "    @set \"modVersion=\\\"" + version.Major + "." + version.Minor + "." + version.Patch + "\\\"\"" + "\n";
            choice_list += "    @goto .GoToModScript" + "\n";
            choice_index++;

            if (ScriptWorkingManager.IsEnabled && ScriptWorkingManager.ModInfo != null)
            {
                //脚本工作坊模式
                var modItem = ScriptWorkingManager.ModInfo;
                choice_list += 
$"""
@choice "工作区：{modItem.Description.Name}" Lock:false play:true show:true
    @set "modKey=\"{WorkspaceModKey}\""
    @set "modName=\"{modItem.Description.Name}\""
    @set "modDescription=\"{modItem.Description.Description}\""
    @set "modAuthor=\"{modItem.Description.Author}\""
    @set "modVersion=\"{modItem.Description.Version}\""
    @set "nextScenario=\"{modItem.Description.Enter}\""
    @goto .GoToModScript

""";
                choice_index++;
            }

            foreach (var item in ModManager.ModManager.Items)
            {
                //超出单页上限，分页
                if(choice_index>= perChoiceCount)
                {
                    if (choice_page > 0)
                    {
                        //上一页
                        choice_list += "@choice \"上一页\" Lock:false play:true show:true" + "\n";
                        choice_list += "    @goto ." + perChoiceLabel + (choice_page - 1) + "\n";
                    }

                    //下一页
                    choice_list += "@choice \"下一页\" Lock:false play:true show:true" + "\n";
                    choice_list += "    @goto ." + perChoiceLabel + (choice_page + 1) + "\n";

                    choice_list += "@Stop" + "\n";

                    choice_page++;
                    choice_list += "# " + perChoiceLabel + choice_page + "\n";

                    choice_index = 0;
                }

                choice_list += "@choice \"" + item.Value.Description.Name + "\" Lock:false play:true show:true" + "\n";
                choice_list += "    @set \"modKey=\\\"" + item.Key + "\\\"\"" + "\n";
                choice_list += "    @set \"modName=\\\"" + item.Value.Description.Name + "\\\"\"" + "\n";
                choice_list += "    @set \"modDescription=\\\"" + item.Value.Description.Description + "\\\"\"" + "\n";
                choice_list += "    @set \"modAuthor=\\\"" + item.Value.Description.Author + "\\\"\"" + "\n";
                choice_list += "    @set \"modVersion=\\\"" + item.Value.Description.Version + "\\\"\"" + "\n";
                choice_list += "    @set \"nextScenario=\\\"" + item.Value.Description.Enter + "\\\"\"" + "\n";
                choice_list += "    @goto .GoToModScript" + "\n";
                choice_index++;
            }

            //添加结尾
            //上一页
            choice_list += "@choice \"上一页\" Lock:false play:true show:true" + "\n";
            choice_list += "    @goto ." + perChoiceLabel + (choice_page - 1) + "\n";

            choice_list += "@Stop" + "\n";

            TaffyStart += choice_list + "\n";

            TaffyStart +=
"""
# GoToModScript
@ProcessInput true set:Continue.true,Pause.true,Skip.true,ToggleSkip.true,SkipMovie.true,AutoPlay.true,ToggleUI.{allowToggleUI},ShowBacklog.true,Rollback.{allowRollback}
@ClearBacklog
@print "Mod名称：{modName}" author:{modAuthor} speed:1 waitInput:true Wait:true
@print "Mod说明：{modDescription}" author:{modAuthor} speed:1 waitInput:true Wait:true
@print "Mod版本：{modVersion}" author:{modAuthor} speed:1 waitInput:true Wait:true
@ClearBacklog
@hide Stills Lazy:false
@back SubId:"Overlay" SolidColor tint:"#000000" time:0.5 Lazy:false
@Wait "0.5"
@goto {nextScenario}

# ReturnToTitle
@ClearBacklog
@ReturnToTitle time:1.2 delay:0.6 Wait:true
""";

            {
                string path = Path.Combine(modScriptPrefix, "Scripts", modMenuScript).Replace("\\", "/");
                var service = Engine.GetServiceOrErr<WitchTrialsScriptPlayer>();
                Resource<Script> resource = new Resource<Script>(path, Script.FromText(modMenuScript, TaffyStart));
                ResourceLoader<Script>.LoadedResource loadedResource = new ResourceLoader<Script>.LoadedResource(resource, modProvisionSource);
                loadedResource.AddHolder(modProvisionSource);
                service.scripts.ScriptLoader.Cast<ResourceLoader<Script>>().AddLoadedResource(loadedResource);
            }

            {
                string path = Path.Combine(modScriptPrefix, "Text/Scripts", modMenuScript).Replace("\\", "/");
                var service = Engine.GetServiceOrErr<TextManager>();
                Resource<TextAsset> resource = new Resource<TextAsset>(path, new TextAsset());
                ResourceLoader<TextAsset>.LoadedResource loadedResource = new ResourceLoader<TextAsset>.LoadedResource(resource, modTextProvisionSource);
                loadedResource.AddHolder(modProvisionSource);
                service.textLoader.Cast<ResourceLoader<TextAsset>>().AddLoadedResource(loadedResource);
            }
        }
        /// <summary>注册 ModSimpleCharacter → 仅 Naninovel CharacterMetadata。</summary>
        public static void AddSimpleCharacter(string prefix, ModItem.ModSimpleCharacter character)
        {
            var service = Engine.GetServiceOrErr<CharacterManager>();
            if (service.Configuration.ActorMetadataMap.ContainsId(character.Id)) return;

            var meta = CreateBaseCharacterMeta(prefix);
            meta.DisplayName = '\u200B' + ResolveDisplayName(character.Id, character.DisplayName, character.Color, character.FamilyName, character.Name);
            ApplyCharacterColor(meta, character.Color, character.Id);

            service.Configuration.ActorMetadataMap.AddRecord(character.Id, meta);
            simpleCharacterMap[character.Id] = character;
            ScriptLoaderLogDebug($"{service.GetIl2CppType().FullName} Add SimpleCharacter:{character.Id}");
        }

        /// <summary>注册 ModCharacter → Naninovel CharacterMetadata + CharacterData + AuthorData。</summary>
        public static void AddRichCharacter(string prefix, ModItem.ModCharacter character)
        {
            var service = Engine.GetServiceOrErr<CharacterManager>();
            if (service.Configuration.ActorMetadataMap.ContainsId(character.Id)) return;

            var meta = CreateBaseCharacterMeta(prefix);
            meta.DisplayName = '\u200B' + ResolveDisplayName(character.Id, character.DisplayName, character.Color, character.FamilyName, character.Name);
            ApplyCharacterColor(meta, character.Color, character.Id);

            service.Configuration.ActorMetadataMap.AddRecord(character.Id, meta);
            richCharacterMap[character.Id] = character;
            ScriptLoaderLogDebug($"{service.GetIl2CppType().FullName} Add Character:{character.Id}");
        }

        /// <summary>将角色 Color 应用到 CharacterMetadata（UseCharacterColor / NameColor / MessageColor）。</summary>
        private static void ApplyCharacterColor(CharacterMetadata meta, string color, string characterId)
        {
            string colorStr = !string.IsNullOrEmpty(color) ? color : "#FFFFFF";
            if (!colorStr.StartsWith("#")) colorStr = "#" + colorStr;

            if (UnityEngine.ColorUtility.TryParseHtmlString(colorStr, out var parsedColor))
            {
                meta.UseCharacterColor = true;
                meta.NameColor = parsedColor;
                meta.MessageColor = UnityEngine.Color.white;
            }
            else
            {
                ScriptLoaderLogWarning($"Invalid color format '{color}' for character '{characterId}'");
            }
        }

        private static CharacterMetadata CreateBaseCharacterMeta(string prefix)
        {
            var meta = new CharacterMetadata();
            meta.Implementation = typeof(SpriteCharacter).AssemblyQualifiedName;
            var providerTypes = new Il2CppSystem.Collections.Generic.List<string>();
            providerTypes.Add(prefix.Replace("\\", "/"));
            meta.Loader = new() { PathPrefix = Path.Combine(prefix, "Characters").Replace("\\", "/"), ProviderTypes = providerTypes };
            meta.Pivot = new(.5f, .695f);
            return meta;
        }
        
        //添加 Mod加载器
        public static void AddModLoader(string root, string prefix, string scenarioDirName)
        {

            {
                //默认资源加载器
                var service = Engine.GetServiceOrErr<ResourceProviderManager>();
                var localResourceProvider = new LocalResourceProvider(root);
                localResourceProvider.AddConverter(new NaniToScriptAssetConverter().Cast<IRawConverter<Script>>());
                localResourceProvider.AddConverter(new TxtToTextAssetConverter().Cast<IRawConverter<TextAsset>>());
                localResourceProvider.AddConverter(new WavToAudioClipConverter().Cast<IRawConverter<AudioClip>>());
                localResourceProvider.AddConverter(new JpgOrPngToTextureConverter().Cast<IRawConverter<Texture2D>>());
                service.providersMap.Add(prefix.Replace("\\", "/"), localResourceProvider.Cast<IResourceProvider>());
                ScriptLoaderLogDebug(string.Format("{0} Path:{1}", service.GetIl2CppType().FullName, ProvisionSource.BuildFullPath(localResourceProvider.RootPath, prefix)));
            }

            {
                //剧本加载器
                var service = Engine.GetServiceOrErr<WitchTrialsScriptPlayer>();
                var ProvisionSources = service.scripts.ScriptLoader.Cast<ResourceLoader<Script>>().ProvisionSources;
                var localResourceProvider = new LocalResourceProvider(root);
                localResourceProvider.AddConverter(new NaniToScriptAssetConverter().Cast<IRawConverter<Script>>());
                var provisionSource = new ProvisionSource(localResourceProvider.Cast<IResourceProvider>(), Path.Combine(prefix, scenarioDirName).Replace("\\", "/"));
                ProvisionSources.System_Collections_IList_Insert(0, provisionSource);
                ScriptLoaderLogDebug(string.Format("{0} Path:{1}", service.GetIl2CppType().FullName, ProvisionSource.BuildFullPath(localResourceProvider.RootPath, provisionSource.PathPrefix)));
            }

            {
                //本地化加载器
                var service = Engine.GetServiceOrErr<TextManager>();
                var ProvisionSources = service.textLoader.Cast<ResourceLoader<TextAsset>>().ProvisionSources;
                var localResourceProvider = new LocalResourceProvider(root);
                localResourceProvider.AddConverter(new TxtToTextAssetConverter().Cast<IRawConverter<TextAsset>>());
                var provisionSource = new ProvisionSource(localResourceProvider.Cast<IResourceProvider>(), Path.Combine(prefix, "Text").Replace("\\", "/"));
                ProvisionSources.System_Collections_IList_Insert(0, provisionSource);
                modInjectedTextSources.Add(provisionSource);
                ScriptLoaderLogDebug(string.Format("{0} Path:{1}", service.GetIl2CppType().FullName, ProvisionSource.BuildFullPath(localResourceProvider.RootPath, provisionSource.PathPrefix)));
            }

            {
                //音频加载器
                var service = Engine.GetServiceOrErr<AudioManagerExtended>();
                var ProvisionSources = service.audioLoader.Cast<ResourceLoader<AudioClip>>().ProvisionSources;
                var localResourceProvider = new LocalResourceProvider(root);
                localResourceProvider.AddConverter(new WavToAudioClipConverter().Cast<IRawConverter<AudioClip>>());
                var provisionSource = new ProvisionSource(localResourceProvider.Cast<IResourceProvider>(), Path.Combine(prefix, "Audio").Replace("\\", "/"));
                ProvisionSources.System_Collections_IList_Insert(0, provisionSource);
                modInjectedAudioSources.Add(provisionSource);
                ScriptLoaderLogDebug(string.Format("{0} Path:{1}", service.GetIl2CppType().FullName, ProvisionSource.BuildFullPath(localResourceProvider.RootPath, provisionSource.PathPrefix)));
            }

            {
                //角色声音加载器
                var service = Engine.GetServiceOrErr<AudioManagerExtended>();
                var ProvisionSources = service.voiceLoader.Cast<ResourceLoader<AudioClip>>().ProvisionSources;
                var localResourceProvider = new LocalResourceProvider(root);
                localResourceProvider.AddConverter(new WavToAudioClipConverter().Cast<IRawConverter<AudioClip>>());
                var provisionSource = new ProvisionSource(localResourceProvider.Cast<IResourceProvider>(), Path.Combine(prefix, "Voice").Replace("\\", "/"));
                ProvisionSources.System_Collections_IList_Insert(0, provisionSource);
                modInjectedVoiceSources.Add(provisionSource);
                ScriptLoaderLogDebug(string.Format("{0} Path:{1}", service.GetIl2CppType().FullName, ProvisionSource.BuildFullPath(localResourceProvider.RootPath, provisionSource.PathPrefix)));
            }

            {
                //背景加载器
                string[] backIds = { "MainBackground", "Stills", "Tricks" };
                var service = Engine.GetServiceOrErr<BackgroundManagerExtended>();
                foreach (var backId in backIds)
                {
                    var MainBackground = service.GetAppearanceLoader(backId);
                    var ProvisionSources = MainBackground.Cast<ResourceLoader<Texture2D>>().ProvisionSources;
                    var localResourceProvider = new LocalResourceProvider(root);
                    localResourceProvider.AddConverter(new JpgOrPngToTextureConverter().Cast<IRawConverter<Texture2D>>());
                    var provisionSource = new ProvisionSource(localResourceProvider.Cast<IResourceProvider>(), Path.Combine(prefix, "Backgrounds", backId).Replace("\\", "/"));
                    ProvisionSources.System_Collections_IList_Insert(0, provisionSource);
                    ScriptLoaderLogDebug(string.Format("{0} Path:{1}", service.GetIl2CppType().FullName, ProvisionSource.BuildFullPath(localResourceProvider.RootPath, provisionSource.PathPrefix)));
                }
            }
        }

        //修改 开始游戏 按钮的目标地址
        public static void HookStartGame(TitleUi title) 
        {
            bool is_StartGame = false;
            foreach (var line in title.NaniScriptPlayer.PlayedScript.lines)
            {
                //游戏 System/System_Title.nani 标签 StartGame
                if (line.GetIl2CppType().IsEquivalentTo(Il2CppType.From(typeof(LabelScriptLine))))
                {
                    if ("StartGame" == line.Cast<LabelScriptLine>().LabelText)
                    {
                        is_StartGame = true;
                    }
                }

                //修改标签 StartGame 下面goto指令的目标
                if (is_StartGame && line.GetIl2CppType().IsEquivalentTo(Il2CppType.From(typeof(CommandScriptLine))))
                {
                    var command = line.Cast<CommandScriptLine>().command;
                    if (command.GetIl2CppType().IsEquivalentTo(Il2CppType.From(typeof(GotoModified))))
                    {
                        var gotoModified = command.Cast<GotoModified>();
                        gotoModified.Path.SetValue(ModScriptEnter);
                        AddModStartMenu();
                        break;
                    }
                }
            }
        }
    }

    // Hook 时机点
    [HarmonyPatch]
    class TitleUi_Patch
    {
        [HarmonyPatch(typeof(TitleUi), nameof(TitleUi.Awake))]
        [HarmonyPostfix]
        static void TitleUi_Awake_Patch()
        {
            ModResourceLoader.Awake();
        }

        [HarmonyPatch(typeof(TitleUi), nameof(TitleUi.Activate))]
        [HarmonyPostfix]
        static void TitleUi_Activate_Patch(ref TitleUi __instance)
        {
            // 回到标题画面时清理 mod 注入数据
            ModResourceLoader.ClearAllModData();
            ModResourceLoader.HookStartGame(__instance);
        }
    }
}
