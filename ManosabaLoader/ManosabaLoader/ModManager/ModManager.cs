using System;
using System.Collections.Generic;
using System.IO;

namespace ManosabaLoader.ModManager
{
    public static class ModManager
    {
        public static Action<string> ModManagerLogMessage;
        public static Action<string> ModManagerLogDebug;
        public static Action<string> ModManagerLogWarning;
        public static Action<string> ModManagerLogError;

        public const string CONFIG_NAME = "info.json";

        static Dictionary<string, ModItem> items = new Dictionary<string, ModItem>();
        public static IReadOnlyDictionary<string, ModItem> Items => items;
        public static void Init(string rootPath)
        {
            foreach(var path in Directory.GetDirectories(rootPath))
            {
                string config_path = Path.Combine(path, CONFIG_NAME);
                ModManagerLogDebug(string.Format("config path:{0}", config_path));
                if (File.Exists(config_path))
                {
                    ModManagerLogMessage(string.Format("load mod config from {0}", path));
                    ModItem item = new ModItem(path, File.ReadAllText(config_path));
                    if (item.Valid)
                    {
                        items[Path.GetFileName(path)] = item;
                    }
                }
            }
        }
    }
}
