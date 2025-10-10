using Cpp2IL.Core.Extensions;
using GigaCreation.NaninovelExtender.Common;
using GigaCreation.NaninovelExtender.ExtendedActors;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using manosaba_mod;
using Naninovel;
using StableNameDotNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Bindings;
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
            foreach (var script in service.scripts.ScriptLoader.GetAllLoaded().Cast<Il2CppSystem.Collections.Generic.List<Resource<Script>>>())
            {
                if (script.Path.Contains(ModResourceLoader.modScriptPrefix))
                {
                    continue;
                }
                if (!service.PlayedScript.Equals(script.Object))
                {
                    ModDebugToolsLogDebug(string.Format("Release script:{0}", script.Path));
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
        static void WriteTex2D_jpg(RenderTexture render, string path)
        {
            Texture2D out_texture = new Texture2D(render.width, render.height, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = render;
            out_texture.ReadPixels(new Rect(0, 0, render.width, render.height), 0, 0);
            out_texture.Apply();
            RenderTexture.active = previous;
            byte[] bytes = out_texture.EncodeToJPG();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
            UnityEngine.Object.Destroy(out_texture);
        }
        //莫名奇妙的内存泄漏，不知道是Bepinex的bug还是Unity的bug，总之就是内存泄漏
        static Texture2D out_texture = new Texture2D(16, 16, TextureFormat.RGBA32, false);
        static void WriteTex2D(RenderTexture render, string path)
        {
            out_texture.Resize(render.width, render.height);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = render;
            out_texture.ReadPixels(new Rect(0, 0, render.width, render.height), 0, 0);
            out_texture.Apply();
            RenderTexture.active = previous;
            WriteTex2D(out_texture, path);
        }
        static void WriteTex2D(Texture2D texture, string path)
        {
            BlittableArrayWrapper blittableArrayWrapper;
            Il2CppStructArray<byte> il2CppStructArray = new Il2CppStructArray<byte>(0);
            ImageConversion.EncodeToPNG_Injected(UnityEngine.Object.MarshalledUnityObject.Marshal(texture), out blittableArrayWrapper);

            unsafe void Unmarshal(ref BlittableArrayWrapper blittableArrayWrapper, ref Il2CppStructArray<byte> array)
            {
                System.IntPtr* ptr = stackalloc System.IntPtr[1];
                System.IntPtr intPtr = IL2CPP.Il2CppObjectBaseToPtr(array);
                *ptr = (nint)(&intPtr);
                Unsafe.SkipInit(out System.IntPtr exc);
                var Pointer = typeof(BlittableArrayWrapper).GetNestedType("MethodInfoStoreGeneric_Unmarshal_Internal_Void_byref_Il2CppArrayBase_1_T_0`1", BindingFlags.NonPublic).MakeGenericType(typeof(byte)).GetField("Pointer", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                System.IntPtr intPtr2 = IL2CPP.il2cpp_runtime_invoke((System.IntPtr)Pointer, (nint)Unsafe.AsPointer(ref blittableArrayWrapper), (void**)ptr, ref exc);
                Il2CppException.RaiseExceptionIfNecessary(exc);
                System.IntPtr intPtr3 = intPtr;
                array = ((intPtr3 == (System.IntPtr)0) ? null : Il2CppInterop.Runtime.Runtime.Il2CppObjectPool.Get<Il2CppStructArray<byte>>(intPtr3));
            }

            Unmarshal(ref blittableArrayWrapper, ref il2CppStructArray);
            byte[] bytes = il2CppStructArray;

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
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
            foreach (var character_pair in characterManager.ManagedActors)
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
                        string path = Path.Combine(".", "dump_character", character_pair.Key, file_tag_hash.ToString() + ".png");
                        WriteTex2D(layeredCharacter.appearanceTexture, path);
                        file_list = file_list + pos_name + ":" + pos + ":" + file_tag_hash + "\n";
                    }
                }
                string info_path = Path.Combine(".", "dump_character", character_pair.Key, "info.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(info_path));
                File.WriteAllText(info_path, file_list);
            }
        }
        public static void DumpCharacterLayer()
        {            ModDebugToolsLogMessage("DumpCharacterLayer");
            var characterManager = Engine.GetServiceOrErr<CharacterManager>();
            foreach (var character_pair in characterManager.ManagedActors)
            {
                ModDebugToolsLogMessage(character_pair.Key);
                var character = character_pair.Value;

                string file_list = "";
                if (Il2CppType.TypeFromPointer(character.ObjectClass).IsEquivalentTo(Il2CppType.From(typeof(LayeredCharacterExtended))))
                {
                    LayeredCharacter layeredCharacter = character.Cast<LayeredCharacter>();
                    // 遍历图层
                    foreach (var layer in layeredCharacter.Behaviour.Drawer.layers)
                    {
                        if (!Il2CppType.TypeFromPointer(layer.ObjectClass).IsEquivalentTo(Il2CppType.From(typeof(LayeredCameraLayer))))
                        {
                            continue;
                        }
                        layer.Enabled = false;
                    }
                    foreach (var layer in layeredCharacter.Behaviour.Drawer.layers)
                    {
                        if (!Il2CppType.TypeFromPointer(layer.ObjectClass).IsEquivalentTo(Il2CppType.From(typeof(LayeredCameraLayer))))
                        {
                            continue;
                        }
                        LayeredCameraLayer layeredCameraLayer = layer.Cast<LayeredCameraLayer>();
                        SpriteRenderer render;
                        if (!layeredCameraLayer.go.TryGetComponent<SpriteRenderer>(out render))
                        {
                            continue;
                        }
                        if (layeredCameraLayer.Name == "")
                        {
                            continue;
                        }
                        file_list += layeredCameraLayer.Group + ":" + layeredCameraLayer.Name + ":";
                        file_list += render.sortingOrder;
                        file_list += "\n";

                        layer.Enabled = true;
                        var renderTexture = layeredCharacter.Behaviour.Render(layeredCharacter.ActorMeta.PixelsPerUnit);
                        WriteTex2D(renderTexture, Path.Combine(".", "dump_character_layer", character_pair.Key, layeredCameraLayer.Group, layeredCameraLayer.Name + ".png"));
                        RenderTexture.ReleaseTemporary(renderTexture);
                        layer.Enabled = false;
                    }
                }
                string info_path = Path.Combine(".", "dump_character_layer", character_pair.Key, "info.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(info_path));
                File.WriteAllText(info_path, file_list);
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
}