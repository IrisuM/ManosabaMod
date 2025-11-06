using System;
using System.IO;
using System.Threading.Tasks;

using BepInEx.Logging;

using Il2CppInterop.Runtime;

using ManosabaLoader.Marshaling;
using ManosabaLoader.Utils;

using Naninovel;
using Naninovel.Bridging;
using Naninovel.UI;

using UnityEngine;

using WitchTrials.Views;

using Logger = BepInEx.Logging.Logger;
using ILS = Il2CppSystem;
using PlaybackSpot = Naninovel.Bridging.PlaybackSpot;

namespace ManosabaLoader.BridgingProtocolAdapt;

public class BridgingService
{
    private readonly ManualLogSource logger;
    private readonly string rootPath;
    private readonly ISerializer serde;
    private IOFiles files;
    private Server server;
    private readonly string bridgingDir;
    private readonly string metadataFile;
    private readonly string beaconFile;
    private bool isRunning = false;

    private ILS.Action il2cppAttachServiceListenersAction;
    private ILS.Action il2cppNotifyPlaybackStoppedAction;
    private ILS.Action<bool> il2cppNotifySkipStatusChangedAction;
    private ILS.Action il2cppNotifyPlayerNotReadyAction;
    private ILS.Action<Command> il2cppNotifyPlayedCommandAction;

    public BridgingService(string rootPath, ISerializer serde)
    {
        logger = Logger.CreateLogSource($"{MyPluginInfo.PLUGIN_NAME}.{nameof(BridgingService)}");
        this.rootPath = rootPath;
        this.serde = serde;

        logger.LogInfo($"Scripting root path: {rootPath}");
        var dataDir = Path.Combine(rootPath, "NaninovelData");
        var transientDir = Path.Combine(dataDir, ".nani", "Transient");
        bridgingDir = Path.Combine(transientDir, "Bridging");
        metadataFile = Path.Combine(transientDir, "Metadata.json");
        beaconFile = Path.Combine(dataDir, ".naninovel.unity.data");
        
        il2cppAttachServiceListenersAction = new Action(AttachServiceListeners);
        il2cppNotifyPlaybackStoppedAction = new Action(NotifyPlaybackStopped);
        il2cppNotifySkipStatusChangedAction = new Action<bool>(NotifySkipStatusChanged);
        il2cppNotifyPlayerNotReadyAction = new Action(NotifyPlayerNotReady);
        il2cppNotifyPlayedCommandAction = new Action<Command>(NotifyPlayedCommand);
    }

    public void SetupWorkingDirectory()
    {
        var scriptsDir = Path.Combine(rootPath, "Scripts");
        
        Directory.CreateDirectory(bridgingDir);
        Directory.CreateDirectory(scriptsDir);
        Directory.CreateDirectory(Path.Combine(rootPath, "Text"));
        
        SafeCreateFile(Path.Combine(scriptsDir, "editor.beacon.nani"));
        SafeCreateFile(beaconFile);
        CreateMetadata();
        return;

        static void SafeCreateFile(string path)
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);
            if (!File.Exists(path))
                File.Create(path!).Dispose();
        }
    }
    
    public void CreateMetadata()
    {
        var meta = ModMetadataGenerator.GenerateProjectMetadata();
        var json = serde.Serialize(meta);
        
        Directory.CreateDirectory(Path.GetDirectoryName(metadataFile)!);
        File.WriteAllText(metadataFile, json, System.Text.Encoding.UTF8);
        logger.LogInfo($"Bridging metadata created at: {metadataFile}");
    }

    public void RestartServer()
    {
        StopServer();
        StartServer();
    }

    public void StartServer()
    {
        if (isRunning)
            return;
        
        files?.Dispose();
        files = new IOFiles(bridgingDir);
        server = new Server(files.Cast<IFiles>(), serde);
        server.Start(new ServerInfo {
            Name = "SherryAppleJuice",
            Version = EngineVersion.LoadFromResources().BuildVersionTag()
        });
        server.OnGotoRequested += HandleGotoRequest;
        server.OnPlayRequested += HandlePlayRequest;
        server.OnStopRequested += HandleStopRequest;
        server.OnSkipRequested += HandleSkipRequest;
        Engine.OnInitializationFinished += il2cppAttachServiceListenersAction;
        Engine.OnDestroyed += il2cppNotifyPlaybackStoppedAction;

        Application.quitting += il2cppNotifyPlayerNotReadyAction;
        server?.NotifyPlayerStatusChanged(true);
    }

    private void AttachServiceListeners ()
    {
        if (Engine.Behaviour.TryCast<RuntimeBehaviour>() == null) return;
        Engine.GetServiceOrErr<IScriptPlayer>().add_OnCommandExecutionStart(il2cppNotifyPlayedCommandAction);
        Engine.GetServiceOrErr<IScriptPlayer>().add_OnSkip(il2cppNotifySkipStatusChangedAction);
    }
    
    public void StopServer()
    {
        if (!isRunning)
            return;
        
        Engine.OnInitializationFinished -= il2cppAttachServiceListenersAction;
        Engine.OnDestroyed -= il2cppNotifyPlaybackStoppedAction;
        if (server != null)
        {
            server.OnGotoRequested -= HandleGotoRequest;
            server.OnPlayRequested -= HandlePlayRequest;
            server.OnStopRequested -= HandleStopRequest;
            server.OnSkipRequested -= HandleSkipRequest;
        }
        server = null;
        files?.Dispose();

        isRunning = false;
    }

    private static void HandleGotoRequest(PlaybackSpotStruct spot)
    {
        var scriptPath = spot.scriptPath;
        var lineIdx = spot.lineIndex;
        var refAction = new RefWrapper<ILS.Action>(null);
        
        refAction.Value = DelegateSupport.ConvertDelegate<ILS.Action>(Goto);
        
        if (Engine.Initialized) Goto();
        else Engine.OnInitializationFinished += refAction.Value;
        return;


        void Goto ()
        {
            Engine.OnInitializationFinished -= refAction.Value;
            var player = Engine.GetServiceOrErr<IScriptPlayer>();
            if (player.PlayedScript && player.PlayedScript.Path == scriptPath)
                player.Rewind(lineIdx).Forget();
            else
                Engine.GetServiceOrErr<IStateManager>().ResetState()
                    .ContinueWith(new Action(() => UniTask.DelayFrame(1)))
                    .ContinueWith(new Action(() => player.LoadAndPlay(spot.scriptPath)))
                    .ContinueWith(new Action(() => Engine.GetServiceOrErr<IUIManager>().GetUI<ITitleUI>()?.Cast<TitleUi>().Hide()))
                    .ContinueWith(new Action(() => player.Rewind(spot.lineIndex))).Forget();
        }
    }

    private async void HandlePlayRequest ()
    {
        await Task.Delay(233);
        if (Engine.Initialized)
           server.NotifyPlayerStatusChanged(true); 
    }

    private async void HandleStopRequest ()
    {
        await Task.Delay(233);
        if (Engine.Initialized)
            server.NotifyPlayerStatusChanged(false);
    }

    private static void HandleSkipRequest (bool enable)
    {
        if (Engine.Initialized)
            Engine.GetService<IScriptPlayer>()?.SetSkipEnabled(enable);
    }

    private void NotifyPlayedCommand (Command command)
    {
        server?.NotifyPlaybackStatusChanged(new PlaybackStatus {
            Playing = true,
            PlayedSpot = new PlaybackSpot {
                ScriptPath = command.PlaybackSpot.ScriptPath,
                LineIndex = command.PlaybackSpot.LineIndex,
                InlineIndex = command.PlaybackSpot.InlineIndex
            }
        });
    }
    
    private void NotifyPlaybackStopped()
    {
        server?.NotifyPlaybackStatusChanged(new PlaybackStatus { Playing = false });
        server?.NotifySkipStatusChanged(false);
    }

    private void NotifySkipStatusChanged (bool skipEnabled)
    {
        server?.NotifySkipStatusChanged(skipEnabled);
    }

    private void NotifyPlayerNotReady ()
    {
        server?.NotifyPlayerStatusChanged(false);
    }
}