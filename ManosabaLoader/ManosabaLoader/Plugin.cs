using System.IO;

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

using Il2CppInterop.Runtime.Injection;

using Naninovel;

using System.Linq;

using HarmonyLib;

using Il2CppInterop.Runtime;

using ManosabaLoader.Patches;
using ManosabaLoader.Utils;

using UnityEngine;
using UnityEngine.InputSystem;
using WitchTrials.Models;

using Logger = BepInEx.Logging.Logger;
using NaniILogger = Naninovel.ILogger;

namespace ManosabaLoader
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BasePlugin
    {
        const string Taffy_Icon =
"""

                      关注永雏塔菲喵！关注永雏塔菲谢谢喵！
                     https://space.bilibili.com/1265680561
=============================================================================
                                     .::-::.                                    
                        --:==        :####*        ---.                         
                     .:++***+=       =###+.       :++++*-.                      
                   .=+++**+++*-      =###=        *+++**++*.                    
                  =+++**####+*+:::=:.:++#=-:*===-==*#%%+=*++:                   
                .**==*+####+***++===*:++#++*==*+++*=+#%%#*=**+-                 
               -***##%####+*=*=**++++=+++*==++***==:=+#%%%#+*=*-                
              :+*+%%%%%##+*====+**:-:*+*=:---:*=++=::=+#%%%###=*=               
             :+*+%%%%%%#+**=====*+**+*=::----***+*::::=*#%%####***              
             +*+##%%%%+*****====:====:-------:====:::===*+%%####*+:             
            :**+##%%#+******===:----:=****=:----:::::===*=*#%###+*+.            
            :**++++++********==---:+########+=---:::===****++++++**-            
            .****===*+*******=--=+############+=---:==*****+*=*****.            
             -=::==*********=--*################+:--==****+***=:::-             
              =:::==**+***=:-:+###################=--:=***+**==:::              
              .:::==**++*:-:*#####################++:-:=*+***=::=.              
              .-:::=***:-:*++#####################++++:-:***==:::               
              -.-::::--:*++++###################+##++++*:-:===::-.              
             :-..--.-=+++###+####%##########%%%##+#++++++*=-------              
            -=...-:*##+#####+####%%#%%%#######%##+#++####+++*:---:              
            *...=+++########+####%%#%%###########+#++########++:--:             
           :-..-+++++######++####################++++######+++++---.            
          .*...=+++++++####++####################+++*+####++++#+:---            
          ::..-++++++++####*+####################+++*+####+++###*---.           
          *...=++++++++###+*+##++################++#*+#####+++##+:---           
         -=..-++++++++++##+*+##++#################+#**+####+++###*--:.          
         :-..=+++++++++###***+#++################++#**+#####++###+:---          
         =..-++++++++++##+**++#++################++#+*+#####+++###*--:          
        .=..=++++++++++##+*+#+#+++###############++#+**+#####++###+--:          
        ::..*++++++++++#++*+*+++++###############+*#+**+#####+++###=-:-         
        *-.-+++++++++++++**+#++++++#############+++#+***+####+++###*--=         
        +-.=+++++++++++++*#%%++++++#############++++##+*+####+++###+--*         
       -+..*++++++++++++*+#%%#++++++############+++#%%#*+++##+++####:-+.        
       =+..+++#++++++++++#%%%#+++++++##########++++#%%#+++++#++++###:-+:        
       ++.-++##+++++++++#%%%%%++++++++#########++*+#%%%#*+++#++++###=-+=        
      -++--++##+++++++++#%%%%%#++++++++########+++##%%%%+++++++++###=-##        
      =++--++##++*++++*#%%%%%%%+++++++++######++++#%%%%%#*+++++++###=:##-       
      +++::++###+*+++*+%%%%%%%%#++++++++++##++++++#%%%%%%#*++++++###*=+#=       
     -+++=:++###+*++++#%%########+++++++++++++++#######%%#+++++++###**+#+       
     *++++:++###+*+++#%%#=::-:*###++++++++++++++#*:-:==#%%#*+++++###++++#-      
    -+++++:++##++*++###=..-.-..-+#+*+++++++++++#:..-.--.=###*++++###++++#*      
   .++++++=++##++*++%+-..-::::===##+*+++++++++#*==::::-..-##+++++###+++++#-     
  -+++++++*++##++*+##-.-**----:=##%####+++++++#+*:----**-.-##**++###+++++#+-    
:++++++++++++##++*#%=.:++.----:#%#%%%%%##++++##+.-----+%#:.=#+*+####+#+++++#*-  
*+++*: =+++++##++*##-:##:.-:#=:#+#%%%%%%%######:.-=+=-#%#+--##*+####+## :+++++: 
       *+++++##++*#*.*##.-*+##=-.+%%%%%%%%%%%%#.-=+##*=-##*.+#*+###++##   ....  
      .++++++##++*#=-##*.:+####=.*%%%%%%%%%%%%+.:#####:.+##-+++####++##-        
      -+++++++#++*#+=##*.=+###++:*%%%%%%%%%%%%+:++###+=.+##=+++####++##:        
      *+++++++#++++%#%%+=++#+#++*+%%%%%%%%%%%%#+#+#+#++-+%%##*+###+++##=        
      +++++++++##++%%%%%%#+####+*#%%%%%%%%%%%%%%#####++:#%%%#*+###+###+*        
      +++++++++##+*#%%%%%#######+%%%%%%%%%%%%%%####%##+*%%%%#*+##++#+#+#        
     .++++++++++#+*#%%%%%+#%%%%##%##%%%%#%%%#%%####%%#+%%%%%#*+##+######.       
     -++++++++++##*#%%%%%############%%%%%%%###########%%%%%++##++######-       
     -+++++++++++#++%%###############%%%%%%%###############%++##+#######-       
     -#+++++++++++++#################%%%%%%%################+##+########-       
     .#++++++++++++++#################%%%%%%%##############+++++###+####.       
      #+++++++++++++++###############++***+#%%############++++++###+###+        
      ++++++++++++++++++############%+*****#%%%##########+++++++###+###*        
      =++++++++++++++++###########%%%#++++#%%%%%#########+++++++###+###:        
      .+#+++++++++++++++########%%%%%%####%%%%%%%#######++++++++###+##+         
      .+++++++++++++++++++#####%%%%%%%%%%%%%%%%%%%%###++++++++++###++++         
      :++++++++++++++++++++**+##%%%%%%%%%%%%%%%%##++++++++++++++##++++#-        
      *++++++++++++++++++++*==**+#####%%%%%###++***+++++++++++++##+++##=        
      +++++++++++++++++++++**=:+++++++####++++++==*++++++++++++###+++##*        
      +++++++++++++++++++++*= :+++++++++++++++++*-**+++++++++++##+++##+*        
      ++++++*+++++++++++++**:-+++++++######+++++#::=+++++++++++##+++##+*        
      *+++++**+++++++++++*:-.-++++++++####++++###*.-:*+++++++++##++++++*        
      -*++++*+++++++++++*----:###++++*###+++++++##----*++++++++#+++++++*        
       =*+++++++++++++++=.---=##+*:*+++##+++=:*#%%=--.:+++++++###+++##++        
       -+***++++++++++*:-.--.*+*=:=:+++##++*-*:**#*-----*+++++###++==#:-        
      -+*::=*++++++++=-......::=::+:=++++++:=+=:*::.-----=++++.:*    .          
      .   .......................  ..      .. ..............                    
=============================================================================
                                塔不灭！塔不灭！

""";
        internal static new ManualLogSource Log;
        private static Plugin _instance;
        private string modRootPath;
        
        public static ManualLogSource LogIns => Log;
        public static Plugin Instance => _instance;

        private ConfigEntry<string> modRootPathConfig;
        private ConfigEntry<string> configScriptEnter;
        private ConfigEntry<string> configScriptEnterLabel;
        private ConfigEntry<bool> openDebug;
        private ConfigEntry<bool> isDirectMode;
        private ConfigEntry<bool> enableScriptingMode;
        private ConfigEntry<string> workspacePath;
        
        public bool isDebug => openDebug != null && openDebug.Value == true;

        public bool EnableScriptingMode => enableScriptingMode.Value;

        public string ModRootPath => modRootPath ??= Path.TrimEndingDirectorySeparator(Path.IsPathFullyQualified(modRootPathConfig.Value)
            ? modRootPathConfig.Value
            : Path.Combine(Path.GetDirectoryName(Application.dataPath)!, modRootPathConfig.Value));

        public ConfigEntry<string> WorkspacePathConfig => workspacePath;

        public override void Load()
        {
            // Plugin startup logic
            if (_instance != null && _instance != this)
            {
                base.Log.LogError("Multiple instances of Plugin detected; It may cause unexpected behavior.");
            }
            _instance = this;
            Log = base.Log;

            ClassInjector.RegisterTypeInIl2Cpp<NaninovelLoggerWrapper>(new RegisterTypeOptions { Interfaces = new Il2CppInterfaceCollection([typeof(NaniILogger)]) });
            var loggerWrapper = new NaninovelLoggerWrapper(Logger.CreateLogSource("Naninovel Log"));
            Engine.UseLogger(loggerWrapper.Cast<NaniILogger>());
            loggerWrapper.Cast<NaniILogger>().Log("test log from Naninovel logger");


            modRootPathConfig = Config.Bind("General",
                                "ModRootPath",
                                "ManosabaMod",
                                "Mod剧本目录");
            openDebug = Config.Bind("Debug",
                                "OpenDebug",
                                false,
                                "是否开启调试功能");
            isDirectMode = Config.Bind("Debug",
                                "IsDirectMode",
                                false,
                                "是否直接跳到起始点");
            configScriptEnter = Config.Bind("Debug",
                                            "ScriptEnter",
                                            "TaffyStart",
                                            "开始游戏时的起始点剧本");
            configScriptEnterLabel = Config.Bind("Debug",
                                            "ScriptEnterLabel",
                                            "",
                                            "开始游戏时的起始点标签");
            enableScriptingMode = Config.Bind("Scripting", "EnableScriptingMode", false, "是否启用剧本编辑模式（创作者使用）");
            workspacePath = Config.Bind<string>("Scripting", "WorkspacePath", null, "剧本工作区路径（创作者使用）");
            
            var classTypePtr = Il2CppClassPointerStore.GetNativeClassPointer(typeof(Il2CppSystem.Action<PlaybackSpot>));
            var il2CppDelegateType = Il2CppSystem.Type.internal_from_handle(IL2CPP.il2cpp_class_get_type(classTypePtr));
            var nativeDelegateInvokeMethod = il2CppDelegateType.GetMethod("Invoke");
            var paramTypes = nativeDelegateInvokeMethod.GetParameters();
            Log.LogInfo(paramTypes.Aggregate("", (acc, p) => acc + p.ParameterType.FullName + ", "));
            
            var harmony = new Harmony(MyPluginInfo.PLUGIN_NAME);

            //初始化调试器
            ModDebugTools.ModDebugToolsLogMessage += msg => { Log.LogMessage(string.Format("[ModDebugTools]\t{0}", msg)); };
            ModDebugTools.ModDebugToolsLogDebug += msg => { Log.LogDebug(string.Format("[ModDebugTools]\t{0}", msg)); };
            ModDebugTools.ModDebugToolsLogWarning += msg => { Log.LogWarning(string.Format("[ModDebugTools]\t{0}", msg)); };
            ModDebugTools.ModDebugToolsLogError += msg => { Log.LogError(string.Format("[ModDebugTools]\t{0}", msg)); };
            ModDebugTools.Init();
            //初始化桥接器
            ClassInjector.RegisterTypeInIl2Cpp<ModJsonSerializer>(new RegisterTypeOptions() { Interfaces = new[] { typeof(ISerializer) } });
            // ModBridgeTools.RestartServer();
            ModMetadataGenerator.ModMetadataGeneratorLogMessage += msg => { Log.LogMessage(string.Format("[ModMetadataGenerator]\t{0}", msg)); };
            ModMetadataGenerator.ModMetadataGeneratorLogDebug += msg => { Log.LogDebug(string.Format("[ModMetadataGenerator]\t{0}", msg)); };
            ModMetadataGenerator.ModMetadataGeneratorLogWarning += msg => { Log.LogWarning(string.Format("[ModMetadataGenerator]\t{0}", msg)); };
            ModMetadataGenerator.ModMetadataGeneratorLogError += msg => { Log.LogError(string.Format("[ModMetadataGenerator]\t{0}", msg)); };
            
            //初始化Mod管理器
            ModManager.ModManager.ModManagerLogMessage += msg => { Log.LogMessage(string.Format("[ModManager]\t{0}", msg)); };
            ModManager.ModManager.ModManagerLogDebug += msg => { Log.LogDebug(string.Format("[ModManager]\t{0}", msg)); };
            ModManager.ModManager.ModManagerLogWarning += msg => { Log.LogWarning(string.Format("[ModManager]\t{0}", msg)); };
            ModManager.ModManager.ModManagerLogError += msg => { Log.LogError(string.Format("[ModManager]\t{0}", msg)); };
            ModManager.ModManager.Init(ModRootPath);

            //初始化加载器
            ModResourceLoader.ScriptLoaderLogMessage += msg => { Log.LogMessage(string.Format("[ScriptLoader]\t{0}", msg)); };
            ModResourceLoader.ScriptLoaderLogDebug += msg => { Log.LogDebug(string.Format("[ScriptLoader]\t{0}", msg)); };
            ModResourceLoader.ScriptLoaderLogWarning += msg => { Log.LogWarning(string.Format("[ScriptLoader]\t{0}", msg)); };
            ModResourceLoader.ScriptLoaderLogError += msg => { Log.LogError(string.Format("[ScriptLoader]\t{0}", msg)); };
            ModResourceLoader.Init(harmony, configScriptEnter.Value, configScriptEnterLabel.Value == "" ? null : configScriptEnterLabel.Value, isDirectMode.Value);
            
            harmony.PatchAll(typeof(BridgingProtocolVersionPatch));
            
            //调试用组件
            if (isDebug)
            {
                ModDebugComponent component = AddComponent<ModDebugComponent>();
            }

            // 启用剧本编辑模式
            if (EnableScriptingMode)
            {
                ScriptWorkingManager.Init();
            }

            Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

            Log.LogInfo("Author: 雪莉苹果汁");
            Log.LogInfo("测试版本，缺文档，缺UI，有问题请加群 970841791");

            Log.LogInfo(Taffy_Icon);
        }

        public override bool Unload()
        {
            base.Log.LogError("Plugin unloading is not supported. It may cause unexpected behavior.");
            return false;
        }
    }

    public class ModDebugComponent : MonoBehaviour
    {
        public bool isDebug = false;
        
        object Get_Services()
        {
            return Engine.services;
        }

        object Get_WitchTrialsScriptPlayer()
        {
            return Engine.GetServiceOrErr<WitchTrialsScriptPlayer>();
        }

        void DumpCharacter()
        {
            ModDebugTools.DumpCharacter();
        }

        void DumpCharacterLayer()
        {
            ModDebugTools.DumpCharacterLayer();
        }
        void OpenServer()
        {
            ModBridgeTools.RestartServer();
        }

        void Update()
        {
            if (Keyboard.current.ctrlKey.isPressed && Keyboard.current.rKey.wasReleasedThisFrame)
            {
                ModDebugTools.ReleaseAllScript();
            }

            if (Keyboard.current.ctrlKey.isPressed && Keyboard.current.tKey.wasReleasedThisFrame)
            {
                ModDebugTools.ShowConsole();
            }
        }

        void OnGUI()
        {
            //GUI.Box(new Rect(0, 0, 300, 100), "Debug Menu");
        }
    }
}
