using System;
using System.Collections.Generic;

using HarmonyLib;

using Il2CppInterop.Runtime;

using WitchTrials.Models;
using WitchTrials.Views;

namespace ManosabaLoader
{
    /// <summary>
    /// 统一的魔女图鉴修复补丁。
    ///
    /// ===== 核心问题 =====
    ///
    /// 当 @update 脚本命令执行时，如果玩家从未打开过魔女图鉴 UI，
    /// Mod 的证物/规则/笔记/角色简介无法正常注入。
    ///
    /// ===== 修复策略 =====
    ///
    /// 1. Hook WitchBookUi.UpdateVersion（void 方法，安全）——
    ///    UpdateWitchBook.Execute → IWitchBookUi.UpdateVersion → WitchBookScreen.UpdateVersion
    ///    在 WitchBookUi.UpdateVersion 前缀中捕获所有 mod 状态更新。
    ///
    ///    ⚠ 不能直接 Hook UpdateWitchBook.Execute：该方法返回 UniTask（虚方法），
    ///    在 IL2CPP 下 Harmony patch 会破坏整个 Command.Execute 虚表，
    ///    导致所有 Naninovel 命令执行崩溃（MethodAccessException）。
    ///
    /// 2. Postfix WitchBookScreen.GetActiveEvidences —— 安全网。
    ///    当游戏查询证据列表时（如审判中提供证据），确保 mod 项目出现在结果中。
    /// </summary>
    public static class ModWitchBookPatch
    {
        public static Action<string> WitchBookLogMessage;
        public static Action<string> WitchBookLogInfo;
        public static Action<string> WitchBookLogDebug;
        public static Action<string> WitchBookLogWarning;
        public static Action<string> WitchBookLogError;
        public static void Init(Harmony harmony)
        {
            harmony.PatchAll(typeof(WitchBookUiUpdateVersion_Patch));
            harmony.PatchAll(typeof(GetActiveEvidences_Patch));
            WitchBookLogInfo("ModWitchBookPatch applied.");
        }
    }

    // ========================================================================
    // WitchBookUi.UpdateVersion 拦截（安全的 void 方法）
    // ========================================================================

    /// <summary>
    /// Hook WitchBookUi.UpdateVersion —— @update 命令调用链中的安全拦截点。
    ///
    /// 调用链：
    ///   UpdateWitchBook.Execute() → IWitchBookUi.UpdateVersion(category, id, version)
    ///     → WitchBookScreen.UpdateVersion(category, id, version)
    ///
    /// WitchBookUi.UpdateVersion 是 void 方法，可安全使用 Harmony patch。
    /// 此前缀记录 mod 状态并触发数据注入。
    /// </summary>
    [HarmonyPatch]
    static class WitchBookUiUpdateVersion_Patch
    {
        [HarmonyPatch(typeof(WitchBookUi), nameof(WitchBookUi.UpdateVersion))]
        [HarmonyPrefix]
        static void WitchBookUi_UpdateVersion_Prefix(WitchBookCategory category, string id, int version)
        {
            try
            {
                if (string.IsNullOrEmpty(id))
                    return;

                // 确保 mod 数据已加载（@update 可能在 WitchBook 打开前执行）
                ModResourceLoader.EnsureModDataLoaded();

                ModWitchBookPatch.WitchBookLogDebug(
                    $"@update intercepted: category={category}, id={id}, version={version}");

                switch (category)
                {
                    case WitchBookCategory.Clue:
                        HandleClueUpdate(id, version);
                        break;
                    case WitchBookCategory.Profile:
                        HandleProfileUpdate(id, version);
                        break;
                    case WitchBookCategory.Rule:
                        HandleRuleUpdate(id, version);
                        break;
                    case WitchBookCategory.Note:
                        HandleNoteUpdate(id, version);
                        break;
                }
            }
            catch (Exception ex)
            {
                ModWitchBookPatch.WitchBookLogError($"WitchBookUi.UpdateVersion prefix: {ex}");
            }
        }

        private static void HandleClueUpdate(string id, int version)
        {
            if (!ModClueLoader.IsModClue(id)) return;
            ModClueLoader.TryInjectClueData();
            ModClueLoader.DirectSetModClueState(id, version);
            ModClueLoader.TryInjectCluePageStateAndRebuild();
        }

        private static void HandleProfileUpdate(string id, int version)
        {
            if (!ModProfileLoader.IsModProfile(id)) return;
            ModProfileLoader.TryInjectProfileData();
            ModProfileLoader.TryInjectCharacterData();
            ModProfileLoader.DirectSetModProfileState(id, version);
            ModProfileLoader.TryInjectProfilePageStateAndRebuild();
            ModProfileLoader.EnsureTexturesRegistered();
        }

        private static void HandleRuleUpdate(string id, int version)
        {
            if (!ModRuleNoteLoader.IsModRule(id)) return;
            ModRuleNoteLoader.TryInjectRuleData();
            ModRuleNoteLoader.DirectSetModRuleState(id, version);
            ModRuleNoteLoader.TryInjectRulePageStateAndRebuild();
        }

        private static void HandleNoteUpdate(string id, int version)
        {
            if (!ModRuleNoteLoader.IsModNote(id)) return;
            ModRuleNoteLoader.TryInjectNoteData();
            ModRuleNoteLoader.DirectSetModNoteState(id, version);
            ModRuleNoteLoader.TryInjectNotePageStateAndRebuild();
        }
    }

    // ========================================================================
    // GetActiveEvidences Postfix（安全网）
    // ========================================================================

    /// <summary>
    /// 对 WitchBookScreen.GetActiveEvidences 添加 Postfix。
    ///
    /// 如果 _state.ActiveItems 已包含 mod 项目（通过 DirectSet*State 设置），
    /// 但 _displayableItemMaps 尚未构建（WitchBook 未打开过，InitializeItems 未运行），
    /// 原版 GetActiveEvidences 可能不包含 mod 项目。
    ///
    /// 此 Postfix 检查返回值是否缺失已知的 mod 项目，如有缺失则补充。
    /// </summary>
    [HarmonyPatch]
    static class GetActiveEvidences_Patch
    {
        [HarmonyPatch(typeof(WitchBookScreen), nameof(WitchBookScreen.GetActiveEvidences))]
        [HarmonyPostfix]
        static void WitchBookScreen_GetActiveEvidences_Postfix(
            WitchBookCategory category,
            ref Il2CppSystem.Collections.Generic.IReadOnlyCollection<IdVersionPair> __result)
        {
            try
            {
                Dictionary<string, int> modStates = null;

                switch (category)
                {
                    case WitchBookCategory.Clue:
                        modStates = ModClueLoader.GetAllModStates();
                        break;
                    case WitchBookCategory.Profile:
                        modStates = ModProfileLoader.GetAllModStates();
                        break;
                    case WitchBookCategory.Rule:
                        modStates = ModRuleNoteLoader.GetAllModRuleStates();
                        break;
                    case WitchBookCategory.Note:
                        modStates = ModRuleNoteLoader.GetAllModNoteStates();
                        break;
                }

                if (modStates == null || modStates.Count == 0)
                    return;

                // 收集原始结果中已有的 ID
                var existingIds = new HashSet<string>();
                if (__result != null)
                {
                    // 将 IReadOnlyCollection 转为可枚举
                    var enumerable = __result.Cast<Il2CppSystem.Collections.Generic.IEnumerable<IdVersionPair>>();
                    var enumerator = enumerable.GetEnumerator();
                    var moveNext = enumerator.Cast<Il2CppSystem.Collections.IEnumerator>();
                    while (moveNext.MoveNext())
                    {
                        var pair = enumerator.Current;
                        if (pair != null)
                            existingIds.Add(pair.Id);
                    }
                }

                // 检查是否有缺失的 mod 项目
                var missing = new List<KeyValuePair<string, int>>();
                foreach (var kvp in modStates)
                {
                    if (kvp.Value > 0 && !existingIds.Contains(kvp.Key))
                    {
                        missing.Add(kvp);
                    }
                }

                if (missing.Count == 0)
                    return;

                // 构建新的结果列表
                var newList = new Il2CppSystem.Collections.Generic.List<IdVersionPair>();

                // 添加原始结果
                if (__result != null)
                {
                    var enumerable = __result.Cast<Il2CppSystem.Collections.Generic.IEnumerable<IdVersionPair>>();
                    var enumerator = enumerable.GetEnumerator();
                    var moveNext2 = enumerator.Cast<Il2CppSystem.Collections.IEnumerator>();
                    while (moveNext2.MoveNext())
                    {
                        newList.Add(enumerator.Current);
                    }
                }

                // 添加缺失的 mod 项目
                foreach (var kvp in missing)
                {
                    newList.Add(new IdVersionPair(kvp.Key, kvp.Value));
                    ModWitchBookPatch.WitchBookLogDebug(
                        $"GetActiveEvidences补充 mod 项: category={category}, id={kvp.Key}, v={kvp.Value}");
                }

                __result = newList.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<IdVersionPair>>();
            }
            catch (Exception ex)
            {
                ModWitchBookPatch.WitchBookLogError($"GetActiveEvidences postfix: {ex}");
            }
        }
    }
}
