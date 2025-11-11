using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

using BepInEx.Logging;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;

using ManosabaLoader.BridgingProtocolAdapt;
using ManosabaLoader.ModManager;
using ManosabaLoader.Utils;

using Naninovel;

using UnityEngine;

using Logger = BepInEx.Logging.Logger;

namespace ManosabaLoader;

public static class ScriptWorkingManager
{
    private static ManualLogSource logger;
    private static ManualLogSource hotReloadLogger;
    private static BridgingService bridgingService;
    private static string configJsonPath;
    private static string workspacePath;
    private static FileSystemWatcher scriptFileWatcher;
    private static IScriptPlayer scriptPlayer;
    private static IScriptManager scriptManager;

    public static string WorkspacePath => workspacePath ??= Path.TrimEndingDirectorySeparator(Path.IsPathFullyQualified(Plugin.Instance.WorkspacePathConfig.Value) ? Plugin.Instance.WorkspacePathConfig.Value : Path.Combine(Path.GetDirectoryName(Application.dataPath)!, Plugin.Instance.WorkspacePathConfig.Value));
    public static string ConfigJsonPath => configJsonPath ??= Path.Combine(WorkspacePath, ModManager.ModManager.CONFIG_NAME);
    public static bool IsEnabled { get; private set; }
    public static ModItem ModInfo { get; private set; }

    public static void Init()
    {
        logger = Logger.CreateLogSource($"{MyPluginInfo.PLUGIN_NAME}.{typeof(ScriptWorkingManager)}");
        hotReloadLogger = Logger.CreateLogSource($"{MyPluginInfo.PLUGIN_NAME}.{typeof(ScriptWorkingManager)}.HotReload");

        if (!Directory.Exists(WorkspacePath))
            Directory.CreateDirectory(WorkspacePath);

        if (!File.Exists(ConfigJsonPath))
        {
            logger.LogWarning($"Config file not found at {ConfigJsonPath}, creating default config at {ConfigJsonPath}.");
            logger.LogWarning("Please close the game and edit the config file before launching again.");
            var defaultModDesc = new ModItem.ModDescription();
            File.WriteAllText(ConfigJsonPath, JsonSerializer.Serialize(defaultModDesc));
            return;
        }

        ModInfo = new ModItem(ConfigJsonPath, File.ReadAllText(ConfigJsonPath));
        logger.LogInfo($"Loaded mod config from {ConfigJsonPath}. Mod name: {ModInfo.Description.Name}, Entry: {ModInfo.Description.Enter}");

        bridgingService = new BridgingService(WorkspacePath, ModJsonSerializer.Shared.Cast<ISerializer>());

        bridgingService.SetupWorkingDirectory();
        bridgingService.RestartServer();

        Engine.OnInitializationFinished += (Il2CppSystem.Action)SetupScriptWatcher;

        IsEnabled = true;
    }

    private static void SetupScriptWatcher()
    {
        scriptPlayer = Engine.GetServiceOrErr<IScriptPlayer>();
        scriptManager = Engine.GetServiceOrErr<IScriptManager>();
        scriptPlayer.add_OnPlay((Il2CppSystem.Action<Script>)OnScriptPlayerPlay);
    }

    private static void OnScriptPlayerPlay(Script script)
    {
        var scriptLoader = scriptManager.ScriptLoader.Cast<ResourceLoader<Script>>();
        var scriptLoadedRes = scriptLoader.GetLoadedResourceOrNull(script.Path);
        if (!scriptLoadedRes.Valid)
        {
            hotReloadLogger.LogWarning($"Couldn't find loaded script resource for script at path: {script.Path}");
            DisposeWatcher();
            return;
        }

        if (scriptLoadedRes.ProvisionSource.Provider.TryCast<LocalResourceProvider>() is not { } provider)
        {
            hotReloadLogger.LogDebug($"A non-local resource provider detected for script at path: {script.Path}.");
            DisposeWatcher();
            return;
        }

        if (string.IsNullOrEmpty(provider.RootPath) || new DirectoryInfo(WorkspacePath).FullName != new DirectoryInfo(provider.RootPath).FullName)
        {
            hotReloadLogger.LogDebug($"The script at path: {script.Path} is not provided from the workspace path: {WorkspacePath}.");
            DisposeWatcher();
            return;
        }

        var scriptFilePath = Path.Combine(provider.RootPath, scriptLoadedRes.ProvisionSource.BuildFullPath(script.Path) + ".nani");
        WatchAnotherFile(scriptFilePath);
    }

    private static void WatchAnotherFile(string path)
    {
        if (scriptFileWatcher != null && Path.Combine(scriptFileWatcher.Path ?? "", scriptFileWatcher.Filter ?? "") == path && scriptFileWatcher.EnableRaisingEvents)
            return;

        if (scriptFileWatcher == null)
        {
            scriptFileWatcher = new FileSystemWatcher
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };
            scriptFileWatcher.Changed += OnFileChanged;
        }

        scriptFileWatcher.Path = Path.GetDirectoryName(path)!;
        scriptFileWatcher.Filter = Path.GetFileName(path);
        scriptFileWatcher.EnableRaisingEvents = true;
        hotReloadLogger.LogDebug($"Started watching script file at path: {path}");
    }

    private static void DisposeWatcher()
    {
        if (scriptFileWatcher == null)
            return;

        hotReloadLogger.LogInfo("Disposing existing script file watcher.");
        scriptFileWatcher.Changed -= OnFileChanged;
        scriptFileWatcher.Dispose();
        scriptFileWatcher = null;
    }

    private static void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        hotReloadLogger.LogDebug($"Detected script file {e.ChangeType} at path: {e.FullPath}");
        if (scriptPlayer == null || scriptManager == null || e.ChangeType != WatcherChangeTypes.Changed)
            return;

        string scriptContent;
        try
        {
            scriptContent = File.ReadAllText(e.FullPath);
        }
        catch (IOException ioException)
        {
            hotReloadLogger.LogError($"Failed to read changed script file at path: {e.FullPath}. Exception: {ioException}");
            return;
        }
        
        UniTask.Run(new Action(() => { }))
            .ContinueWith(new Action(() =>
            {
                var script = FromText(scriptPlayer.PlayedScript.Path, scriptContent, e.FullPath);
                var oldScript = scriptPlayer.PlayedScript;
                oldScript.textMap = script.textMap;
                oldScript.playlist = script.playlist;
                oldScript.lines = script.lines;
                hotReloadLogger.LogDebug($"Hot-reloaded script at path: {e.FullPath} into currently playing script.");
            }))
            .Forget();
    }

    public static unsafe Script FromText(string path, string text, string file = null)
    {
        var numPtr = stackalloc IntPtr[3];
        numPtr[0] = IL2CPP.ManagedStringToIl2Cpp(path);
        numPtr[1] = IL2CPP.ManagedStringToIl2Cpp(text);
        numPtr[2] = IL2CPP.ManagedStringToIl2Cpp(file);
        var exc = IntPtr.Zero;
        var methodPtr = (IntPtr)typeof(Script).GetField("NativeMethodInfoPtr_FromText_Public_Static_Script_String_String_String_0", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var num = IL2CPP.il2cpp_runtime_invoke(methodPtr, IntPtr.Zero, (void**)numPtr, ref exc);
        Il2CppInterop.Runtime.Il2CppException.RaiseExceptionIfNecessary(exc);
        var ptr = num;
        return ptr == IntPtr.Zero ? null : Il2CppObjectPool.Get<Script>(ptr);
    }
}