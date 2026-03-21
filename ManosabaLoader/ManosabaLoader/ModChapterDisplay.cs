using System;
using System.Collections.Generic;

using GigaCreation.NaninovelExtender.Ui;

using HarmonyLib;

using Il2CppInterop.Runtime;

using ManosabaLoader.ModManager;
using ManosabaLoader.Utils;

using Naninovel;

using TMPro;

namespace ManosabaLoader
{
    /// <summary>
    /// 存档画面自定义章节名显示。
    /// 
    /// ===== 原理 =====
    /// 
    /// GameStateSlotExtended.SetNonEmptyState 从 GameStateMap 读取 PlaybackSpot，
    /// 调用虚方法 BuildSubTitleText(PlaybackSpot) 生成存档画面的副标题文字。
    /// 原版 BuildSubTitleText 只是返回 PlaybackSpot.ToString()（即脚本路径+行号）。
    /// 
    /// 本模块 hook BuildSubTitleText，检查 PlaybackSpot.ScriptPath 是否匹配已注册
    /// 的 mod 自定义章节名。如果匹配，返回自定义文字；否则回退到原版行为。
    /// 
    /// ===== PlaybackSpot 内存布局 =====
    /// PlaybackSpot 是值类型：
    ///   scriptPath: IntPtr (0x0)   — Il2Cpp string 指针
    ///   lineIndex:  int    (0x8)
    ///   inlineIndex: int   (0xC)
    /// 
    /// 在 Harmony 中 PlaybackSpot 作为参数传递时，使用 Naninovel.PlaybackSpot 类型
    /// 即可。但因为这是 IL2CPP 值类型，在前缀方法中可能作为 boxed 对象传入。
    /// 为安全起见，我们使用 __instance + 读取 GameStateMap 的方式来拿到 PlaybackSpot。
    /// 
    /// 实际上，BuildSubTitleText 是在 SetNonEmptyState 中调用的：
    ///   playbackSpot = *(PlaybackSpot*)(state + 0x30)
    ///   text = BuildSubTitleText(playbackSpot)
    ///   SetSubTitleText(text)
    /// 
    /// 所以我们直接 hook SetNonEmptyState 的 Postfix，在原版设置完后覆写即可。
    /// 但更简洁的做法是 hook BuildSubTitleText 本身。
    /// 
    /// 由于 BuildSubTitleText 的参数是值类型 PlaybackSpot，在 IL2CPP Harmony 中
    /// 可能无法直接作为参数接收。因此我们选择 hook SetNonEmptyState 并在 Postfix
    /// 中直接覆写 _subTitleLabel 的文字。
    /// </summary>
    public static class ModChapterDisplay
    {
        public static Action<string> ChapterLogMessage;
        public static Action<string> ChapterLogInfo;
        public static Action<string> ChapterLogDebug;
        public static Action<string> ChapterLogWarning;
        public static Action<string> ChapterLogError;
        /// <summary>脚本路径 → 自定义章节名</summary>
        private static readonly Dictionary<string, string> chapterNameMap = new();

        public static void Init(Harmony harmony)
        {
            // 注册所有 mod 的 ChapterNames
            foreach (var item in ModManager.ModManager.Items)
            {
                RegisterChapterNames(item.Key, item.Value);
            }

            if (ScriptWorkingManager.IsEnabled && ScriptWorkingManager.ModInfo != null)
            {
                RegisterChapterNames("__workspace__", ScriptWorkingManager.ModInfo);
            }

            if (chapterNameMap.Count == 0)
            {
                ChapterLogDebug("No custom chapter names registered, skipping patches.");
                return;
            }

            ChapterLogMessage($"Registered {chapterNameMap.Count} custom chapter name entries.");
            harmony.PatchAll(typeof(SaveSlotSubTitle_Patch));
            ChapterLogInfo("ModChapterDisplay patches applied.");
        }

        private static void RegisterChapterNames(string modPrefix, ModItem modItem)
        {
            if (modItem?.Description?.ChapterNames == null) return;

            foreach (var kvp in modItem.Description.ChapterNames)
            {
                if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrEmpty(kvp.Value))
                {
                    ChapterLogWarning($"[{modPrefix}] Skipping empty chapter name entry.");
                    continue;
                }

                if (chapterNameMap.ContainsKey(kvp.Key))
                {
                    ChapterLogDebug($"[{modPrefix}] Overriding chapter name for: {kvp.Key}");
                }

                chapterNameMap[kvp.Key] = kvp.Value;
                ChapterLogDebug($"[{modPrefix}] Chapter: {kvp.Key} → {kvp.Value}");
            }
        }

        /// <summary>
        /// 查找指定脚本路径的自定义章节名。
        /// </summary>
        public static bool TryGetChapterName(string scriptPath, out string chapterName)
            => chapterNameMap.TryGetValue(scriptPath, out chapterName);

        // ====================================================================
        // Harmony Patch
        // ====================================================================

        /// <summary>
        /// Hook GameStateSlotExtended.SetNonEmptyState(int slotNumber, GameStateMap state)
        /// 
        /// 在原版方法执行后（已设置 _subTitleLabel），检查 PlaybackSpot.ScriptPath
        /// 是否有自定义章节名。如果有，覆写 _subTitleLabel 的文字。
        /// 
        /// GameStateMap 布局：
        ///   +0x20: DateTime (SaveDateTime)
        ///   playbackSpot: PlaybackSpot (值类型)
        ///     scriptPath: string
        /// </summary>
        [HarmonyPatch]
        static class SaveSlotSubTitle_Patch
        {
            /// <summary>
            /// Finalizer: 捕获 WitchTrialsGameStateSlot.SetNonEmptyState 中的异常。
            /// 
            /// 当 PlaybackSpot 来自 mod 脚本时，原版 TryGetChapterKind 无法解析
            /// 非标准脚本路径（如 "MaxMixAlex_ManosabaModEnhance/Main"），导致
            /// NullReferenceException。此 Finalizer 压制异常，使 Postfix 仍然能
            /// 执行自定义章节名覆写，并防止保存操作崩溃。
            /// </summary>
            [HarmonyPatch(typeof(GameStateSlotExtended), nameof(GameStateSlotExtended.SetNonEmptyState))]
            [HarmonyFinalizer]
            static Exception Finalizer(Exception __exception, int slotNumber)
            {
                if (__exception != null)
                {
                    ChapterLogWarning($"SetNonEmptyState exception suppressed for slot {slotNumber}: {__exception.Message}");
                }
                return null; // 始终压制异常，允许 Postfix 继续运行
            }

            [HarmonyPatch(typeof(GameStateSlotExtended), nameof(GameStateSlotExtended.SetNonEmptyState))]
            [HarmonyPostfix]
            static void Postfix(GameStateSlotExtended __instance, int slotNumber, GameStateMap state)
            {
                try
                {
                    if (state == null || chapterNameMap.Count == 0) return;

                    // 动态解析 GameStateMap.playbackSpot.scriptPath
                    IntPtr scriptPathPtr = Il2CppFieldHelper.GetNestedReferenceField(
                        state, "playbackSpot", "scriptPath");
                    if (scriptPathPtr == IntPtr.Zero) return;

                    string scriptPath = IL2CPP.Il2CppStringToManaged(scriptPathPtr);
                    if (string.IsNullOrEmpty(scriptPath)) return;

                    if (TryGetChapterName(scriptPath, out string chapterName))
                    {
                        // 通过 Il2CppFieldHelper 读取 _subTitleLabel 并覆写文字
                        IntPtr labelPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_subTitleLabel");
                        if (labelPtr != IntPtr.Zero)
                        {
                            var label = new TMP_Text(labelPtr);
                            label.richText = true;  // false in vanilla, allow here for modding
                            label.text = chapterName;
                        }
                        ChapterLogDebug($"Slot {slotNumber}: {scriptPath} → {chapterName}");
                    }
                }
                catch (Exception ex)
                {
                    ChapterLogError($"SaveSlotSubTitle Postfix: {ex}");
                }
            }
        }
    }
}
