using Cpp2IL.Core.Extensions;
using GigaCreation.NaninovelExtender.Common;
using GigaCreation.NaninovelExtender.ExtendedActors;
using HarmonyLib;
using Il2CppInterop.Runtime;
using manosaba_mod;
using Naninovel;
using StableNameDotNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using WitchTrials.Models;
using WitchTrials.Views;

namespace ManosabaLoader
{
    public static class ModDebugTools
    {
        public static Action<string> ModDebugToolsLogMessage;
        public static Action<string> ModDebugToolsLogDebug;
        public static Action<string> ModDebugToolsLogWarning;
        public static Action<string> ModDebugToolsLogError;
        public static string StackTraceToString()
        {
            StringBuilder sb = new StringBuilder(256);
            var frames = new System.Diagnostics.StackTrace().GetFrames();
            for (int i = 1; i < frames.Length; i++) /* Ignore current StackTraceToString method...*/
            {
                var currFrame = frames[i];
                var method = currFrame.GetMethod();
                sb.AppendLine(string.Format("{0}:{1}",
                    method.ReflectedType != null ? method.ReflectedType.Name : string.Empty,
                    method.Name));
            }
            return sb.ToString();
        }

        public static void ReleaseAllScript()
        {
            var service = Engine.GetServiceOrErr<WitchTrialsScriptPlayer>();
            foreach(var script in service.scripts.ScriptLoader.GetAllLoaded().Cast<Il2CppSystem.Collections.Generic.List<Resource<Script>>>())
            {
                if (script.Path.Contains(ModResourceLoader.modScriptPrefix))
                {
                    continue;
                }
                if(!service.PlayedScript.Equals(script.Object)) 
                {
                    UnityEngine.Object.Destroy(script.Object);
                }
            }
        }

        public static void ShowConsole()
        {
            ConsoleGUI.Show();
        }

        public static void Init()
        {
            var instance = new Harmony(MyPluginInfo.PLUGIN_NAME);
            instance.PatchAll(typeof(UnityLogger_Patch));
            instance.PatchAll(typeof(ResourceProvider_Patch));
        }

        public static void DumpCharacter()
        {
            int BKDRHash(string str)
            {
                int seed = 131; // 31 131 1313 13131 131313 etc..   
                int hash = 0;

                for (int i = 0; i < str.Length; i++)
                {
                    hash = hash * seed + str[i];
                }

                return (hash & 0x7FFFFFFF);
            }
            void WriteTex2D(RenderTexture render, string path)
            {
                Texture2D out_texture = new Texture2D(render.width, render.height, TextureFormat.RGB24, false);
                RenderTexture.active = render;
                out_texture.ReadPixels(new Rect(0, 0, render.width, render.height), 0, 0);
                out_texture.Apply();
                byte[] bytes = out_texture.EncodeToJPG();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
            }
            Dictionary<string, string> MergeCompositions(Dictionary<string, string> compositions)
            {
                const string selectLiteral = ">";
                const string enableLiteral = "+";
                const string disableLiteral = "-";
                const string splitLiteral = ",";
                Func<string, string[]> get_groups = (x) => { return x.Split(splitLiteral).Select(l => l.Substring(0, new int[] { l.IndexOf(selectLiteral), l.IndexOf(enableLiteral), l.IndexOf(disableLiteral) }.Max() + 1)).ToArray(); };
                Dictionary<string, string> result = new Dictionary<string, string>();
                while (compositions.Count > 0)
                {
                    List<string> new_pos_name = new List<string>();
                    List<string> new_pos = new List<string>();
                    foreach (var pair in compositions)
                    {
                        var old_pos = get_groups(pair.Value);
                        if (new_pos.Intersect(old_pos).Count() > 0)
                        {
                            continue;
                        }
                        new_pos_name.Add(pair.Key);
                        new_pos.AddRange(old_pos);
                    }
                    string new_pos_name_value = string.Join(splitLiteral, new_pos_name.ToArray());
                    string new_pos_value = string.Join(splitLiteral, new_pos_name.Select(l => compositions[l]).ToArray());
                    result[new_pos_name_value] = new_pos_value;
                    new_pos_name.All(l => compositions.Remove(l));
                }

                return result;
            }
            ModDebugToolsLogMessage("DumpCharacter");
            var characterManager = Engine.GetServiceOrErr<CharacterManager>();
            foreach(var character_pair in characterManager.ManagedActors)
            {
                ModDebugToolsLogMessage(character_pair.Key);
                var character = character_pair.Value;
                string file_list = "";
                if (Il2CppType.TypeFromPointer(character.ObjectClass).IsEquivalentTo(Il2CppType.From(typeof(LayeredCharacterExtended))))
                {
                    Dictionary<string, string> compositions = new Dictionary<string, string>();
                    LayeredCharacterExtended layeredCharacter = character.Cast<LayeredCharacterExtended>();
                    // 遍历预设姿势
                    foreach (var pose in layeredCharacter.Behaviour.compositionMap)
                    {
                        string pos_name = pose.Key;
                        string pos = pose.Composition;
                        compositions[pos_name] = pos;
                    }

                    compositions = MergeCompositions(compositions);

                    foreach (var pair in compositions)
                    {
                        string pos_name = pair.Key;
                        string pos = pair.Value;
                        layeredCharacter.SetAppearance(""); //还原
                        layeredCharacter.SetAppearance(pos_name);
                        string file_tag = Path.Combine(pos_name + "(" + pos.Replace("/", "##").Replace(">", "@@") + ")");
                        int file_tag_hash = BKDRHash(file_tag);
                        string path = Path.Combine(".", "dump_character", character_pair.Key, file_tag_hash.ToString() + ".jpg");
                        WriteTex2D(layeredCharacter.appearanceTexture, path);
                        file_list = file_list + pos_name + ":" + pos + ":" + file_tag_hash + "\n";
                    }
                }
                File.WriteAllText(Path.Combine(".", "dump_character", character_pair.Key, "info.txt"), file_list);
            }
        }
    }

    [HarmonyPatch]
    class UnityLogger_Patch
    {
        [HarmonyPatch(typeof(UnityLogger), nameof(UnityLogger.Log))]
        [HarmonyPrefix]
        static bool UnityLogger_Log_Patch(string message)
        {
            ModDebugTools.ModDebugToolsLogMessage(message);
            return false;
        }

        [HarmonyPatch(typeof(UnityLogger), nameof(UnityLogger.Warn))]
        [HarmonyPrefix]
        static bool UnityLogger_Warn_Patch(string message)
        {
            ModDebugTools.ModDebugToolsLogWarning(message);
            return false;
        }

        [HarmonyPatch(typeof(UnityLogger), nameof(UnityLogger.Err))]
        [HarmonyPrefix]
        static bool UnityLogger_Err_Patch(string message)
        {
            ModDebugTools.ModDebugToolsLogError(message);
            return false;
        }
    }

    // 打印资源调用信息
    [HarmonyPatch]
    class ResourceProvider_Patch
    {
        [HarmonyTargetMethod]
        static MethodBase TargetMethod()
        {
            return typeof(ResourceProvider).GetMethod("ResourceLoaded", 0, new Type[] { typeof(string) });
        }
        public static void Postfix(string path)
        {
            ModDebugTools.ModDebugToolsLogDebug(string.Format("ResourceProvider.ResourceLoaded: {0}", path));
        }
    }
}
