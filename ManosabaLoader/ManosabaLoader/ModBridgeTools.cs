using Il2CppInterop.Runtime.Injection;
using Naninovel;
using Naninovel.Bridging;
using Naninovel.UI;
using Newtonsoft.Json.Serialization;
using System;
using System.IO;

using BepInEx.Logging;

using ManosabaLoader.Utils;

using Naninovel.Metadata;

using WitchTrials.Views;

using Command = Naninovel.Command;
using Logger = BepInEx.Logging.Logger;

namespace ManosabaLoader
{
    public class ModJsonSerializer : Il2CppSystem.Object
    {
        public ModJsonSerializer(IntPtr pointer) : base(pointer) { }
        public ModJsonSerializer() : base(ClassInjector.DerivedConstructorPointer<ModJsonSerializer>()) => ClassInjector.DerivedConstructorBody(this);

        private static readonly Newtonsoft.Json.JsonSerializerSettings settings = new()
        {
            ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
            {
                NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy()
            }.Cast<IContractResolver>(),
            Formatting = Newtonsoft.Json.Formatting.None,
            NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
            MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Ignore,
            DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Include
        };

        public string Serialize(Il2CppSystem.Object poco) => Newtonsoft.Json.JsonConvert.SerializeObject(poco, Newtonsoft.Json.Formatting.None, settings);
        public string Serialize(Il2CppSystem.Object poco, Il2CppSystem.Type type) => Newtonsoft.Json.JsonConvert.SerializeObject(poco, type, Newtonsoft.Json.Formatting.None, settings);
        public Il2CppSystem.Object Deserialize(string serialized, Il2CppSystem.Type type) => Newtonsoft.Json.JsonConvert.DeserializeObject(serialized, type, settings);
    }
    
    public static class ModBridgeTools
    {
        private static ManualLogSource logger = Logger.CreateLogSource($"{MyPluginInfo.PLUGIN_NAME}.{typeof(ModBridgeTools)}");
        private static string beaconFile;
        private static string bridgingDir;
        private static string metadataFile;
        private static string scenarioDir;
        private static Server server;
        private static IOFiles files;
        private static Il2CppSystem.Action<Command> NotifyPlayedCommand_Action = new Action<Command>(command =>
        {
            NotifyPlayedCommand(command);
        });
        private static Il2CppSystem.Action AttachServiceListeners_Action = new Action(() =>
        {
            AttachServiceListeners();
        });
        private static Il2CppSystem.Action NotifyPlaybackStopped_Action = new Action(() =>
        {
            NotifyPlaybackStopped();
        });

        // private static Il2CppSystem.Action<Naninovel.Bridging.PlaybackSpot> HandleGotoRequest_Action = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Naninovel.Bridging.PlaybackSpot>>(HandleGotoRequest);
        private static Il2CppSystem.Action<Naninovel.Bridging.PlaybackSpot> HandleGotoRequest_Action =
            Il2CppEx.ConvertDelegateDangerous<Il2CppSystem.Action<Naninovel.Bridging.PlaybackSpot>>(HandleGotoRequest);
        private static Il2CppSystem.Action OnEngineInit_Action;

        public static string MetadataFile
        {
            get => metadataFile;
            set => metadataFile = value;
        }

        public static void RestartServer()
        {
            logger.LogInfo("RestartServer");
            ResolvePaths();
            StopServer();
            logger.LogInfo("StopServer");
            UpdateMeta();
            StartServer();
            logger.LogInfo("StartServer");
            
        }

        private static void ResolvePaths()
        {
            var root = ScriptWorkingManager.WorkspacePath;
            logger.LogInfo($"Scripting root path: {root}");
            var dataDir = Path.Combine(root, "NaninovelData");
            var transientDir = Path.Combine(dataDir, ".nani", "Transient");
            scenarioDir = Path.Combine(root, "Scripts");
            bridgingDir = Path.Combine(transientDir, "Bridging");
            metadataFile = Path.Combine(transientDir, "Metadata.json");
            beaconFile = Path.Combine(dataDir, ".naninovel.unity.data");
            if (!Directory.Exists(bridgingDir)) Directory.CreateDirectory(bridgingDir);
            if (!Directory.Exists(scenarioDir)) Directory.CreateDirectory(scenarioDir);
        }
        
        private static Project UpdateMeta()
        {
            var project = ModMetadataGenerator.GenerateProjectMetadata();
            var serializer = new ModJsonSerializer();
            var json = serializer.Serialize(project);
            var metadataDir = Path.GetDirectoryName(MetadataFile);
            if (metadataDir != null && !Directory.Exists(metadataDir))
                Directory.CreateDirectory(metadataDir);
            File.WriteAllText(MetadataFile, json);
            Plugin.LogIns.LogInfo("Dumped mod metadata to " + MetadataFile);
            return project;
        }

        private static void StartServer()
        {
            files?.Dispose();
            files = new IOFiles(bridgingDir);
            server = new(files.Cast<IFiles>(), (new ModJsonSerializer()).Cast<ISerializer>());
            server.Start(new()
            {
                Name = $"SherryAppleJuice",
                Version = EngineVersion.LoadFromResources().BuildVersionTag()
            });
            server.OnGotoRequested += HandleGotoRequest_Action;
            Engine.OnInitializationFinished += AttachServiceListeners_Action;
            Engine.OnDestroyed += NotifyPlaybackStopped_Action;

            if (!File.Exists(beaconFile))
                File.Create(beaconFile).Dispose();
        }

        private static void StopServer()
        {
            Engine.OnInitializationFinished -= AttachServiceListeners_Action;
            Engine.OnDestroyed -= NotifyPlaybackStopped_Action;
            if (server != null) server.OnGotoRequested -= HandleGotoRequest_Action;
            server = null;
            files?.Dispose();
        }

        private static void AttachServiceListeners()
        {
            Engine.GetServiceOrErr<ScriptPlayer>().OnCommandExecutionStart += NotifyPlayedCommand_Action;
        }

        private static void NotifyPlayedCommand(Command command)
        {
            server?.NotifyPlaybackStatusChanged(new()
            {
                Playing = true,
                PlayedSpot = new()
                {
                    ScriptPath = command.PlaybackSpot.ScriptPath,
                    LineIndex = command.PlaybackSpot.LineIndex,
                    InlineIndex = command.PlaybackSpot.InlineIndex
                }
            });
        }

        private static void NotifyPlaybackStopped()
        {
            server?.NotifyPlaybackStatusChanged(new() { Playing = false });
        }

        private static void HandleGotoRequest(PlaybackSpotIl2CppStruct nativeSpot)
        {
            var spot = (PlaybackSpotStruct)nativeSpot;
            logger.LogInfo($"HandleGotoRequest: {spot.scriptPath}, {spot.lineIndex}, {spot.inlineIndex}");
            
            OnEngineInit_Action ??= (Il2CppSystem.Action)OnEngineInit;
            
            if (Engine.Initialized) OnEngineInit();
            else Engine.OnInitializationFinished += OnEngineInit_Action;
            return;
            
            void OnEngineInit()
            {
                Engine.OnInitializationFinished -= OnEngineInit_Action;
                var player = Engine.GetServiceOrErr<IScriptPlayer>();
                if (player.PlayedScript && player.PlayedScript.Path == spot.scriptPath)
                    player.Rewind(spot.lineIndex).Forget();
                else
                    Engine.GetServiceOrErr<IStateManager>().ResetState()
                        .ContinueWith(new Action(() => player.LoadAndPlay(spot.scriptPath)))
                        .ContinueWith(new Action(() => Engine.GetServiceOrErr<IUIManager>().GetUI<ITitleUI>()?.Cast<TitleUi>().Hide()))
                        .ContinueWith(new Action(() => player.Rewind(spot.lineIndex))).Forget();
            }
        }
    }
}
