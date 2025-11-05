using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

using BepInEx.Logging;

using ManosabaLoader.ModManager;

using UnityEngine;

using Logger = BepInEx.Logging.Logger;

namespace ManosabaLoader;

public static class ScriptWorkingManager
{
    private static ManualLogSource logger;
    private static string configJsonPath;
    private static string workspacePath;
    
    public static string WorkspacePath => workspacePath ??= Path.TrimEndingDirectorySeparator(Path.IsPathFullyQualified(Plugin.Instance.WorkspacePathConfig.Value) ?
        Plugin.Instance.WorkspacePathConfig.Value :
        Path.Combine(Path.GetDirectoryName(Application.dataPath)!, Plugin.Instance.WorkspacePathConfig.Value));
    public static string ConfigJsonPath => configJsonPath ??= Path.Combine(WorkspacePath, ModManager.ModManager.CONFIG_NAME);
    public static bool IsEnabled { get; private set; }
    public static ModItem ModInfo { get; private set; }
    
    public static void Init()
    {
        logger = Logger.CreateLogSource($"{MyPluginInfo.PLUGIN_NAME}.{typeof(ScriptWorkingManager)}");
        ModBridgeTools.RestartServer();
        
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

        IsEnabled = true;
    }
}