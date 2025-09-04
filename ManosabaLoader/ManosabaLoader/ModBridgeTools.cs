using GigaCreation.Essentials.SaveLoad;
using Il2CppInterop.Runtime.Injection;
using Naninovel;
using Naninovel.Bridging;
using Naninovel.UI;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine.InputSystem.Utilities;
using WitchTrials.Views;

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
        private static string bridgingDir;
        private static string metadataFile;
        private static Server server;
        private static IOFiles files;
        private static string scriptPath = "Act01_Chapter01/Act01_Chapter01_Adv01";
        private static int lineIdx = 0;
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
        /*private static Il2CppSystem.Action<Naninovel.Bridging.PlaybackSpot> HandleGotoRequest_Action = new Action<Naninovel.Bridging.PlaybackSpot>((spot) =>
        {
            HandleGotoRequest(spot);
        });*/
        private static Il2CppSystem.Action OnEngineInit_Action = new Action(() =>
        {
            OnEngineInit();
        });

        public static void RestartServer()
        {
            Console.WriteLine("RestartServer");
            ResolvePaths();
            StopServer();
            Console.WriteLine("StopServer");
            // UpdateMetadata
            StartServer();
            Console.WriteLine("StartServer");
        }

        private static void ResolvePaths()
        {
            var cfg = Configuration.GetOrDefault<EngineConfiguration>();
            var root = "E:/SteamLibrary/steamapps/common/manosaba_game/ManosabaMod";
            bridgingDir = $"{root}/.nani/Bridging";
            metadataFile = $"{root}/.nani/Metadata.json";
            if (!Directory.Exists(bridgingDir)) Directory.CreateDirectory(bridgingDir);
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
            //server.OnGotoRequested += HandleGotoRequest_Action;
            Engine.OnInitializationFinished += AttachServiceListeners_Action;
            Engine.OnDestroyed += NotifyPlaybackStopped_Action;
        }

        private static void StopServer()
        {
            Engine.OnInitializationFinished -= AttachServiceListeners_Action;
            Engine.OnDestroyed -= NotifyPlaybackStopped_Action;
            //if (server != null) server.OnGotoRequested -= HandleGotoRequest_Action;
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

        private static void HandleGotoRequest(Naninovel.Bridging.PlaybackSpot spot)
        {
            scriptPath = spot.ScriptPath;
            lineIdx = spot.LineIndex;

            if (Engine.Initialized) OnEngineInit();
            else Engine.OnInitializationFinished += OnEngineInit_Action;
        }
        private static void OnEngineInit()
        {
            Engine.OnInitializationFinished -= OnEngineInit_Action;
            var player = Engine.GetServiceOrErr<IScriptPlayer>();
            if (player.PlayedScript && player.PlayedScript.Path == scriptPath)
                player.Rewind(lineIdx).Forget();
            else
                Engine.GetServiceOrErr<IStateManager>().ResetState()
                    .ContinueWith(new Action(() => player.LoadAndPlay(scriptPath)))
                    .ContinueWith(new Action(() => Engine.GetServiceOrErr<IUIManager>().GetUI<ITitleUI>()?.Cast<TitleUi>().Hide()))
                    .ContinueWith(new Action(() => player.Rewind(lineIdx))).Forget();
        }
    }
}
