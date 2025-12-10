using System;
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
        public static Action<string> ScriptLoaderLogDebug;
        public static Action<string> ScriptLoaderLogWarning;
        public static Action<string> ScriptLoaderLogError;

        private static ProvisionSource modProvisionSource = null;
        private static ProvisionSource modTextProvisionSource = null;
        public const string modScriptPrefix = "TaffyModLoader";
        const string modMenuScript = "TaffyStart";
        private static string modScriptEnter = modMenuScript;
        private static string modScriptEnterLabel = null;

        public static NamedString ModScriptEnter => new NamedString(modScriptEnter, modScriptEnterLabel);

        public static void Init(Harmony instance, string enter, string label, bool directMode)
        {
            instance.PatchAll(typeof(TitleUi_Patch));

            if (directMode)
            {
                modScriptEnter = enter;
                modScriptEnterLabel = label;
            }
        }

        public static void Awake()
        {
            foreach (var service in Engine.services)
            {
                ScriptLoaderLogDebug(string.Format("Find Engine:{0}",Il2CppType.TypeFromPointer(service.ObjectClass).FullName));
            }

            //添加Mod框架私有加载器
            var localResourceProvider = new LocalResourceProvider("");
            modProvisionSource = new ProvisionSource(localResourceProvider.Cast<IResourceProvider>(), Path.Combine(modScriptPrefix, "Scripts").Replace("\\", "/"));
            modTextProvisionSource = new ProvisionSource(localResourceProvider.Cast<IResourceProvider>(), Path.Combine(modScriptPrefix, "Text").Replace("\\", "/"));

            var rootPath = Plugin.Instance.ModRootPath;
            foreach (var item in ModManager.ModManager.Items)
            {
                AddModLoader(rootPath, item.Key, "Scripts");
                foreach(var character in item.Value.Description.Characters)
                {
                    AddCharacterModLoader(item.Key, character);
                }
            }
            
            if (ScriptWorkingManager.IsEnabled)
            {
                var root = Path.GetDirectoryName(ScriptWorkingManager.WorkspacePath);
                var prefix = Path.GetFileName(ScriptWorkingManager.WorkspacePath);
                AddModLoader(ScriptWorkingManager.WorkspacePath, "", "Scripts");
                foreach (var character in ScriptWorkingManager.ModInfo.Description.Characters)
                {
                    AddCharacterModLoader("", character);
                }
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
        //添加 Mod角色加载器
        public static void AddCharacterModLoader(string prefix, ModItem.ModCharacter character)
        {
            {
                //角色加载器
                var service = Engine.GetServiceOrErr<CharacterManager>();
                if (!service.Configuration.ActorMetadataMap.ContainsId(character.ActorId))
                {
                    var character_meta = new CharacterMetadata();
                    character_meta.Implementation = typeof(SpriteCharacter).AssemblyQualifiedName;
                    var providerTypes = new Il2CppSystem.Collections.Generic.List<string>();
                    providerTypes.Add(prefix.Replace("\\", "/"));
                    character_meta.Loader = new() { PathPrefix = Path.Combine(prefix, "Characters").Replace("\\", "/"), ProviderTypes = providerTypes };
                    character_meta.Pivot = new(.5f, .695f);
                    character_meta.DisplayName = '\u200B' + character.DisplayName;
                    service.Configuration.ActorMetadataMap.AddRecord(character.ActorId, character_meta);
                    ScriptLoaderLogDebug(string.Format("{0} Add Character:{1}", service.GetIl2CppType().FullName, character.ActorId));
                }
            }
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
            ModResourceLoader.HookStartGame(__instance);
        }
    }
}
