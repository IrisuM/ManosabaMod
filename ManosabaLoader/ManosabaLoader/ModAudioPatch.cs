using System;
using System.Linq;
using System.Reflection;

using HarmonyLib;

using Il2CppInterop.Runtime.InteropTypes.Arrays;

using ManosabaLoader.Utils;

using Naninovel;

using UnityEngine;

namespace ManosabaLoader
{
    /// <summary>
    /// 音频格式补丁：Harmony Patch WavToAudioClipConverter。
    ///
    /// ===== 目的 =====
    ///
    /// Naninovel 内置的 WavToAudioClipConverter 仅支持 PCM16 44100Hz 立体声 WAV。
    /// 本补丁通过 Harmony 替换其方法实现：
    ///   1. 支持任意采样率/声道/位深度的 WAV 文件
    ///   2. 支持 OGG Vorbis 格式（通过 NVorbis）
    ///
    /// ===== 策略 =====
    ///
    /// 全部使用手动 Patch（非 attribute），因为 IL2CPP interop 的方法参数类型
    /// 与 managed 类型不一致（如 byte[] → Il2CppStructArray&lt;byte&gt;），
    /// attribute-based patch 无法通过类型匹配找到目标方法。
    ///
    /// Patch get_Representations → 追加 .ogg 支持
    /// Patch ConvertBlocking → 使用 ModAudioDecoder 完整解码
    /// Patch Convert (async) → 尝试 patch，若失败则回退
    /// </summary>
    public static class ModAudioPatch
    {
        public static Action<string> AudioLogMessage;
        public static Action<string> AudioLogInfo;
        public static Action<string> AudioLogDebug;
        public static Action<string> AudioLogWarning;
        public static Action<string> AudioLogError;

        /// <summary>缓存的 Representations 数组（含 .ogg），避免重复分配。</summary>
        private static Il2CppReferenceArray<RawDataRepresentation> _cachedRepresentations;

        public static void Init(Harmony harmony)
        {
            var converterType = typeof(WavToAudioClipConverter);

            // 列出所有方法供调试
            foreach (var m in converterType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var paramStr = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                AudioLogDebug?.Invoke($"WavToAudioClipConverter method: {m.ReturnType.Name} {m.Name}({paramStr})");
            }

            // Patch 1: get_Representations — Harmony patch（Prefix/Postfix）均会导致 IL2CPP 崩溃
            // 改为 patch 构造函数，通过 Il2CppFieldHelper 直接写入 backing field
            PatchRepresentationsViaConstructor(harmony, converterType);

            // Patch 2: ConvertBlocking — 找带 2 个参数且返回 AudioClip 的重载
            var convertBlockingMethod = converterType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == nameof(WavToAudioClipConverter.ConvertBlocking)
                                     && m.ReturnType == typeof(AudioClip)
                                     && m.GetParameters().Length == 2);
            if (convertBlockingMethod != null)
            {
                PatchMethodDirect(harmony, convertBlockingMethod,
                    nameof(ConvertBlocking_Prefix), null, "ConvertBlocking(typed)");
            }
            else
            {
                AudioLogWarning?.Invoke("ConvertBlocking(typed) not found — trying fallback by parameter count.");
                // Fallback: 找任意 ConvertBlocking 带 2 个参数的
                var fallback = converterType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == nameof(WavToAudioClipConverter.ConvertBlocking) && m.GetParameters().Length == 2)
                    .ToArray();
                foreach (var fb in fallback)
                {
                    PatchMethodDirect(harmony, fb, nameof(ConvertBlocking_Prefix), null,
                        $"ConvertBlocking({fb.ReturnType.Name})");
                }
            }

            // Patch 3: Convert (async) — 尝试 patch，可能因 UniTask 返回类型而失败
            TryPatchConvertAsync(harmony, converterType);
        }

        private static void PatchMethod(Harmony harmony, Type targetType, string methodName,
            string prefixName, string postfixName, string label)
        {
            try
            {
                var method = AccessTools.Method(targetType, methodName);
                if (method == null)
                {
                    // 尝试 property getter
                    method = AccessTools.PropertyGetter(targetType, methodName.Replace("get_", ""));
                }
                if (method == null)
                {
                    AudioLogWarning?.Invoke($"{label}: method not found on {targetType.Name}.");
                    return;
                }
                PatchMethodDirect(harmony, method, prefixName, postfixName, label);
            }
            catch (Exception ex)
            {
                AudioLogError?.Invoke($"Failed to patch {label}: {ex}");
            }
        }

        private static void PatchMethodDirect(Harmony harmony, MethodBase method,
            string prefixName, string postfixName, string label)
        {
            try
            {
                var prefix = prefixName != null ? new HarmonyMethod(typeof(ModAudioPatch), prefixName) : null;
                var postfix = postfixName != null ? new HarmonyMethod(typeof(ModAudioPatch), postfixName) : null;
                harmony.Patch(method, prefix: prefix, postfix: postfix);
                AudioLogInfo?.Invoke($"Patched {label}.");
            }
            catch (Exception ex)
            {
                AudioLogError?.Invoke($"Failed to patch {label}: {ex}");
            }
        }

        private static void PatchRepresentationsViaConstructor(Harmony harmony, Type converterType)
        {
            try
            {
                var ctor = AccessTools.Constructor(converterType);
                if (ctor == null)
                {
                    AudioLogWarning?.Invoke("WavToAudioClipConverter constructor not found.");
                    return;
                }
                var postfix = new HarmonyMethod(typeof(ModAudioPatch), nameof(Constructor_Postfix));
                harmony.Patch(ctor, postfix: postfix);
                AudioLogInfo?.Invoke("Patched WavToAudioClipConverter constructor for Representations injection.");
            }
            catch (Exception ex)
            {
                AudioLogError?.Invoke($"Failed to patch constructor: {ex}");
            }
        }

        private static void TryPatchConvertAsync(Harmony harmony, Type converterType)
        {
            try
            {
                // 找返回类型含 "UniTask" 且参数数为 2 的 Convert 方法（非 object 版本）
                var convertMethods = converterType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Convert" && m.GetParameters().Length == 2)
                    .ToArray();

                foreach (var cm in convertMethods)
                {
                    var retName = cm.ReturnType.Name;
                    var paramTypes = cm.GetParameters().Select(p => p.ParameterType.Name).ToArray();
                    AudioLogDebug?.Invoke($"Found Convert overload: {retName} Convert({string.Join(", ", paramTypes)})");

                    // 跳过 object 版本，只 patch typed 版本
                    if (paramTypes[0] == "Object" || paramTypes[0] == "Il2CppObjectBase")
                        continue;

                    var prefix = new HarmonyMethod(typeof(ModAudioPatch), nameof(ConvertAsync_Prefix));
                    harmony.Patch(cm, prefix: prefix);
                    AudioLogInfo?.Invoke($"Patched Convert (async typed): {retName} Convert({string.Join(", ", paramTypes)})");
                    return;
                }

                AudioLogWarning?.Invoke("No suitable async Convert method found to patch.");
            }
            catch (Exception ex)
            {
                AudioLogWarning?.Invoke($"Failed to patch async Convert: {ex.Message}");
                AudioLogWarning?.Invoke("Async audio conversion will use original converter — only ConvertBlocking is patched.");
            }
        }

        /// <summary>
        /// 共享的音频解码逻辑：从原始字节解码并创建 AudioClip。
        /// 参数使用 object 类型以兼容 IL2CPP interop 的数组类型。
        /// </summary>
        private static AudioClip DecodeAndCreateClip(object rawData, string name)
        {
            // IL2CPP interop 中 byte[] 可能是 Il2CppStructArray<byte>，需要转换
            byte[] data;
            if (rawData is byte[] managed)
            {
                data = managed;
            }
            else
            {
                // Il2CppStructArray<byte> → byte[]
                var il2cppArray = (Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>)rawData;
                data = il2cppArray.ToArray();
            }

            var audio = ModAudioDecoder.Decode(data);
            AudioLogDebug?.Invoke($"Decoded audio: format={ModAudioDecoder.DetectFormat(data)}, " +
                          $"channels={audio.Channels}, sampleRate={audio.SampleRate}, " +
                          $"samples={audio.SampleCount}, name={name}");

            var clip = AudioClip.Create(name, audio.SampleCount, audio.Channels, audio.SampleRate, false);
            clip.SetData(audio.Samples, 0);
            return clip;
        }

        // ====================================================================
        // Harmony Prefix/Postfix 方法
        // 参数使用 object 类型以兼容 IL2CPP interop 的实际参数类型
        // ====================================================================

        static void Constructor_Postfix(WavToAudioClipConverter __instance)
        {
            try
            {
                if (_cachedRepresentations == null)
                {
                    _cachedRepresentations = new Il2CppReferenceArray<RawDataRepresentation>(2);
                    _cachedRepresentations[0] = new RawDataRepresentation(".wav", "audio/wav");
                    _cachedRepresentations[1] = new RawDataRepresentation(".ogg", "audio/ogg");
                    AudioLogDebug?.Invoke("Created extended Representations: .wav, .ogg");
                }

                Il2CppFieldHelper.SetReferenceField(__instance, "<Representations>k__BackingField", _cachedRepresentations.Pointer);
                AudioLogDebug?.Invoke("Set Representations via Il2CppFieldHelper on WavToAudioClipConverter instance.");
            }
            catch (Exception ex)
            {
                AudioLogError?.Invoke($"Constructor_Postfix failed: {ex}");
            }
        }

        static bool ConvertBlocking_Prefix(object obj, string path, ref AudioClip __result)
        {
            try
            {
                __result = DecodeAndCreateClip(obj, path);
                return false; // 跳过原始实现
            }
            catch (Exception ex)
            {
                AudioLogError?.Invoke($"ConvertBlocking failed for '{path}': {ex}");
                return true; // 回退到原始实现
            }
        }

        static bool ConvertAsync_Prefix(object obj, string path, ref UniTask<AudioClip> __result)
        {
            try
            {
                var clip = DecodeAndCreateClip(obj, path);
                __result = new UniTask<AudioClip>(clip);
                return false; // 跳过原始异步实现
            }
            catch (Exception ex)
            {
                AudioLogError?.Invoke($"Convert (async) failed for '{path}': {ex}");
                return true; // 回退到原始异步实现
            }
        }
    }
}
