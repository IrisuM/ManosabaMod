using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using BepInEx.Logging;

using GigaCreation.Essentials.Localization;

using HarmonyLib;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

using ManosabaLoader.ModManager;
using ManosabaLoader.Utils;

using TMPro;

using UnityEngine;

using WitchTrials.Models;
using WitchTrials.Views;

namespace ManosabaLoader
{
    /// <summary>
    /// Mod 规则（Rule）和笔记（Note）加载器。
    /// 策略与 ModClueLoader 一致（数据注入 + 状态直设 + UI hook），但纯文本，无纹理。
    /// WitchBookCategory: Rule = 3, Note = 4
    /// </summary>
    public static class ModRuleNoteLoader
    {
        public static Action<string> RuleNoteLogMessage;
        public static Action<string> RuleNoteLogInfo;
        public static Action<string> RuleNoteLogDebug;
        public static Action<string> RuleNoteLogWarning;
        public static Action<string> RuleNoteLogError;
        // ====================================================================
        // Rules 数据
        // ====================================================================
        private static readonly List<ModItem.ModRule> allModRules = new();
        private static readonly HashSet<string> modRuleIds = new();
        /// <summary>与原版内容共用相同 ID 的覆写规则集合（注入时动态确定）</summary>
        private static readonly HashSet<string> modRuleOverrideIds = new();
        private static IntPtr injectedRuleDataPtr = IntPtr.Zero;
        private static IntPtr injectedRuleDataItemMapPtr = IntPtr.Zero;
        private static readonly Dictionary<string, int> pendingModRuleUpdates = new();
        /// <summary>所有已通过 @update 设置过的 mod rule 状态，不会因 DeserializeState 而丢失。</summary>
        private static readonly Dictionary<string, int> allModRuleStates = new();

        // ====================================================================
        // Notes 数据
        // ====================================================================
        private static readonly List<ModItem.ModNote> allModNotes = new();
        private static readonly HashSet<string> modNoteIds = new();
        /// <summary>与原版内容共用相同 ID 的覆写笔记集合（注入时动态确定）</summary>
        private static readonly HashSet<string> modNoteOverrideIds = new();
        private static IntPtr injectedNoteDataPtr = IntPtr.Zero;
        private static IntPtr injectedNoteDataItemMapPtr = IntPtr.Zero;
        private static readonly Dictionary<string, int> pendingModNoteUpdates = new();
        /// <summary>所有已通过 @update 设置过的 mod note 状态，不会因 DeserializeState 而丢失。</summary>
        private static readonly Dictionary<string, int> allModNoteStates = new();

        public static void Init(Harmony harmony)
        {
            // 始终注册 Harmony 补丁（注入数据按需延迟加载）
            harmony.PatchAll(typeof(RuleNoteInjection_Patch));
            harmony.PatchAll(typeof(RulePageRefreshContent_Patch));
            harmony.PatchAll(typeof(RulePageSetupItemButton_Patch));
            harmony.PatchAll(typeof(NotePageRefreshContent_Patch));
            harmony.PatchAll(typeof(NotePageSetupItemButton_Patch));
            RuleNoteLogInfo("ModRuleNoteLoader patches applied.");
        }

        /// <summary>加载指定 mod 的规则/笔记数据。</summary>
        public static void LoadModData(string modKey, ModItem modItem)
        {
            RegisterModRules(modKey, modItem);
            RegisterModNotes(modKey, modItem);
            if (allModRules.Count > 0)
                RuleNoteLogMessage($"Loaded {allModRules.Count} rule entries ({modRuleIds.Count} unique IDs) for mod: {modKey}");
            if (allModNotes.Count > 0)
                RuleNoteLogMessage($"Loaded {allModNotes.Count} note entries ({modNoteIds.Count} unique IDs) for mod: {modKey}");
        }

        /// <summary>清除所有 mod 规则/笔记数据。</summary>
        public static void ClearModData()
        {
            allModRules.Clear();
            modRuleIds.Clear();
            modRuleOverrideIds.Clear();
            injectedRuleDataPtr = IntPtr.Zero;
            injectedRuleDataItemMapPtr = IntPtr.Zero;
            pendingModRuleUpdates.Clear();
            allModRuleStates.Clear();

            allModNotes.Clear();
            modNoteIds.Clear();
            modNoteOverrideIds.Clear();
            injectedNoteDataPtr = IntPtr.Zero;
            injectedNoteDataItemMapPtr = IntPtr.Zero;
            pendingModNoteUpdates.Clear();
            allModNoteStates.Clear();
            RuleNoteLogInfo("RuleNoteLoader data cleared.");
        }

        // ====================================================================
        // 注册
        // ====================================================================

        private static void RegisterModRules(string modPrefix, ModItem modItem)
        {
            if (modItem?.Description?.Rules == null) return;

            var seenIdVersions = new HashSet<(string, int)>();
            foreach (var r in allModRules)
                seenIdVersions.Add((r.Id, r.Version));

            foreach (var group in modItem.Description.Rules)
            {
                if (string.IsNullOrEmpty(group.Id))
                {
                    RuleNoteLogWarning($"[{modPrefix}] Skipping rule group with empty ID.");
                    continue;
                }
                if (group.Items == null || group.Items.Length == 0) continue;

                foreach (var item in group.Items)
                {
                    if (seenIdVersions.Contains((group.Id, item.Version)))
                    {
                        RuleNoteLogDebug($"[{modPrefix}] Skipping duplicate rule: {group.Id} v{item.Version}");
                        continue;
                    }
                    seenIdVersions.Add((group.Id, item.Version));
                    allModRules.Add(new ModItem.ModRule
                    {
                        Id = group.Id,
                        Version = item.Version,
                        Numbering = item.Numbering,
                        Subtitle = item.Subtitle,
                        Description = item.Description
                    });
                    modRuleIds.Add(group.Id);
                    RuleNoteLogDebug($"[{modPrefix}] Registered rule: Id={group.Id}, Version={item.Version}");
                }
            }

            VersionExpander.Expand(allModRules, seenIdVersions, r => (r.Id, r.Version),
                (baseRule, ver) => new ModItem.ModRule
                {
                    Id = baseRule.Id, Version = ver,
                    Numbering = baseRule.Numbering,
                    Subtitle = baseRule.Subtitle,
                    Description = baseRule.Description
                });
        }

        private static void RegisterModNotes(string modPrefix, ModItem modItem)
        {
            if (modItem?.Description?.Notes == null) return;

            var seenIdVersions = new HashSet<(string, int)>();
            foreach (var n in allModNotes)
                seenIdVersions.Add((n.Id, n.Version));

            foreach (var group in modItem.Description.Notes)
            {
                if (string.IsNullOrEmpty(group.Id))
                {
                    RuleNoteLogWarning($"[{modPrefix}] Skipping note group with empty ID.");
                    continue;
                }
                if (group.Items == null || group.Items.Length == 0) continue;

                foreach (var item in group.Items)
                {
                    if (seenIdVersions.Contains((group.Id, item.Version)))
                    {
                        RuleNoteLogDebug($"[{modPrefix}] Skipping duplicate note: {group.Id} v{item.Version}");
                        continue;
                    }
                    seenIdVersions.Add((group.Id, item.Version));
                    allModNotes.Add(new ModItem.ModNote
                    {
                        Id = group.Id,
                        Version = item.Version,
                        Title = item.Title,
                        Description = item.Description
                    });
                    modNoteIds.Add(group.Id);
                    RuleNoteLogDebug($"[{modPrefix}] Registered note: Id={group.Id}, Version={item.Version}");
                }
            }

            VersionExpander.Expand(allModNotes, seenIdVersions, n => (n.Id, n.Version),
                (baseNote, ver) => new ModItem.ModNote
                {
                    Id = baseNote.Id, Version = ver,
                    Title = baseNote.Title,
                    Description = baseNote.Description
                });
        }

        // ====================================================================
        // 查询
        // ====================================================================

        public static bool IsModRule(string id) => modRuleIds.Contains(id);
        public static bool IsModNote(string id) => modNoteIds.Contains(id);
        public static bool HasModRules => allModRules.Count > 0;
        public static bool HasModNotes => allModNotes.Count > 0;

        /// <summary>获取所有已记录的 mod Rule 状态</summary>
        public static Dictionary<string, int> GetAllModRuleStates() => allModRuleStates;
        /// <summary>获取所有已记录的 mod Note 状态</summary>
        public static Dictionary<string, int> GetAllModNoteStates() => allModNoteStates;

        // ====================================================================
        // RuleData 注入
        // ====================================================================

        /// <summary>向 RuleData ScriptableObject 注入 mod 规则数据。</summary>
        public static void TryInjectRuleData()
        {
            if (!HasModRules) return;
            try
            {
                var instances = Resources.FindObjectsOfTypeAll<RuleData>();
                if (instances == null || instances.Length == 0)
                {
                    RuleNoteLogDebug("RuleData not yet loaded.");
                    return;
                }

                var ruleData = instances[0];
                if (ruleData.Pointer == injectedRuleDataPtr) return;

                var itemsList = ruleData._items;
                RuleNoteLogDebug($"Found RuleData 0x{ruleData.Pointer:X}, items: {itemsList.Count}");

                // 扫描现有 _items，确定哪些 mod ID 与原版条目冲突（覆写 ID）
                modRuleOverrideIds.Clear();
                var vanillaIdSet = new HashSet<string>();
                for (int i = 0; i < itemsList.Count; i++)
                {
                    string vid = itemsList[i].Id;
                    if (vid != null) vanillaIdSet.Add(vid);
                }
                foreach (var id in modRuleIds)
                    if (vanillaIdSet.Contains(id))
                        modRuleOverrideIds.Add(id);
                if (modRuleOverrideIds.Count > 0)
                    RuleNoteLogInfo($"Detected {modRuleOverrideIds.Count} vanilla override rule IDs: {string.Join(", ", modRuleOverrideIds)}");

                // 去重检查：仅检查"纯新" mod ID
                bool alreadyHasMod = false;
                for (int i = 0; i < itemsList.Count; i++)
                {
                    string vid = itemsList[i].Id;
                    if (IsModRule(vid) && !modRuleOverrideIds.Contains(vid))
                    {
                        alreadyHasMod = true;
                        break;
                    }
                }

                if (!alreadyHasMod)
                {
                    // 步骤 1：移除覆写 ID 对应的原版条目
                    if (modRuleOverrideIds.Count > 0)
                    {
                        var toRemove = new List<int>();
                        for (int i = 0; i < itemsList.Count; i++)
                            if (modRuleOverrideIds.Contains(itemsList[i].Id))
                                toRemove.Add(i);
                        for (int k = toRemove.Count - 1; k >= 0; k--)
                            itemsList.RemoveAt(toRemove[k]);
                        RuleNoteLogInfo($"Removed {toRemove.Count} vanilla RuleData entries for override IDs.");
                    }

                    // 步骤 2：注入所有 mod 规则（覆写 + 纯新）
                    int count = 0;
                    foreach (var modRule in allModRules)
                    {
                        try
                        {
                            var subtitleArray = modRule.Subtitle.ToIl2CppArray();
                            var descArray = modRule.Description.ToIl2CppArray();
                            var ruleDataItem = new RuleDataItem(modRule.Numbering ?? "", subtitleArray, descArray);
                            var versionedItem = new VersionedItem<RuleDataItem>(
                                modRule.Id, modRule.Version, ruleDataItem);
                            itemsList.Add(versionedItem);
                            count++;
                        }
                        catch (Exception ex)
                        {
                            RuleNoteLogError($"Failed to create VersionedItem for rule '{modRule.Id}': {ex}");
                        }
                    }
                    RuleNoteLogMessage($"Injected {count} mod rules into RuleData (total: {itemsList.Count}).");
                }
                else
                {
                    RuleNoteLogDebug("RuleData already contains mod rules.");
                }

                injectedRuleDataPtr = ruleData.Pointer;
            }
            catch (Exception ex)
            {
                RuleNoteLogError($"TryInjectRuleData failed: {ex}");
            }
        }

        // ====================================================================
        // NoteData 注入
        // ====================================================================

        /// <summary>向 NoteData ScriptableObject 注入 mod 笔记数据。</summary>
        public static void TryInjectNoteData()
        {
            if (!HasModNotes) return;
            try
            {
                var instances = Resources.FindObjectsOfTypeAll<NoteData>();
                if (instances == null || instances.Length == 0)
                {
                    RuleNoteLogDebug("NoteData not yet loaded.");
                    return;
                }

                var noteData = instances[0];
                if (noteData.Pointer == injectedNoteDataPtr) return;

                var itemsList = noteData._items;
                RuleNoteLogDebug($"Found NoteData 0x{noteData.Pointer:X}, items: {itemsList.Count}");

                // 扫描现有 _items，确定哪些 mod ID 与原版条目冲突（覆写 ID）
                modNoteOverrideIds.Clear();
                var vanillaIdSet = new HashSet<string>();
                for (int i = 0; i < itemsList.Count; i++)
                {
                    string vid = itemsList[i].Id;
                    if (vid != null) vanillaIdSet.Add(vid);
                }
                foreach (var id in modNoteIds)
                    if (vanillaIdSet.Contains(id))
                        modNoteOverrideIds.Add(id);
                if (modNoteOverrideIds.Count > 0)
                    RuleNoteLogInfo($"Detected {modNoteOverrideIds.Count} vanilla override note IDs: {string.Join(", ", modNoteOverrideIds)}");

                // 去重检查：仅检查"纯新" mod ID
                bool alreadyHasMod = false;
                for (int i = 0; i < itemsList.Count; i++)
                {
                    string vid = itemsList[i].Id;
                    if (IsModNote(vid) && !modNoteOverrideIds.Contains(vid))
                    {
                        alreadyHasMod = true;
                        break;
                    }
                }

                if (!alreadyHasMod)
                {
                    // 步骤 1：移除覆写 ID 对应的原版条目
                    if (modNoteOverrideIds.Count > 0)
                    {
                        var toRemove = new List<int>();
                        for (int i = 0; i < itemsList.Count; i++)
                            if (modNoteOverrideIds.Contains(itemsList[i].Id))
                                toRemove.Add(i);
                        for (int k = toRemove.Count - 1; k >= 0; k--)
                            itemsList.RemoveAt(toRemove[k]);
                        RuleNoteLogInfo($"Removed {toRemove.Count} vanilla NoteData entries for override IDs.");
                    }

                    // 步骤 2：注入所有 mod 笔记（覆写 + 纯新）
                    int count = 0;
                    foreach (var modNote in allModNotes)
                    {
                        try
                        {
                            var titleArray = modNote.Title.ToIl2CppArray();
                            var descArray = modNote.Description.ToIl2CppArray();
                            var noteDataItem = new NoteDataItem(titleArray, descArray);
                            var versionedItem = new VersionedItem<NoteDataItem>(
                                modNote.Id, modNote.Version, noteDataItem);
                            itemsList.Add(versionedItem);
                            count++;
                        }
                        catch (Exception ex)
                        {
                            RuleNoteLogError($"Failed to create VersionedItem for note '{modNote.Id}': {ex}");
                        }
                    }
                    RuleNoteLogMessage($"Injected {count} mod notes into NoteData (total: {itemsList.Count}).");
                }
                else
                {
                    RuleNoteLogDebug("NoteData already contains mod notes.");
                }

                injectedNoteDataPtr = noteData.Pointer;
            }
            catch (Exception ex)
            {
                RuleNoteLogError($"TryInjectNoteData failed: {ex}");
            }
        }

        // ====================================================================
        // Page 注入（通用辅助方法）
        // ====================================================================

        /// <summary>
        /// 向 _loadedDataItemMap 注入 mod 数据项（通用实现，适用于所有 WitchBookPageBase）。
        /// 使用 Il2CppFieldHelper 直接读取 _id 字段进行去重，避免 Cast 导致的 IL2CPP 类型不匹配。
        /// 返回 true 表示注入成功（或已存在）。
        /// </summary>
        private static bool TryInjectGenericDataItemMap<TPage, TDataItem>(
            TPage page,
            IntPtr mapPtr,
            HashSet<string> modIds,
            HashSet<string> overrideIds,
            Action<Il2CppSystem.Collections.IList> injectAction)
            where TPage : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
        {
            try
            {
                var listObj = new Il2CppSystem.Object(mapPtr);
                var ilist = listObj.Cast<Il2CppSystem.Collections.IList>();
                var icoll = listObj.Cast<Il2CppSystem.Collections.ICollection>();
                int before = icoll.Count;

                // 去重检查：仅检查"纯新"mod ID（非覆写 ID）
                bool alreadyHasMod = false;
                for (int i = 0; i < icoll.Count; i++)
                {
                    var item = ilist[i];
                    if (item != null)
                    {
                        IntPtr idPtr = Il2CppFieldHelper.GetReferenceField(item, "_id");
                        if (idPtr != IntPtr.Zero)
                        {
                            string itemId = IL2CPP.Il2CppStringToManaged(idPtr);
                            if (modIds.Contains(itemId) && (overrideIds == null || !overrideIds.Contains(itemId)))
                            {
                                alreadyHasMod = true;
                                break;
                            }
                        }
                    }
                }

                if (alreadyHasMod)
                {
                    RuleNoteLogDebug($"_loadedDataItemMap for {typeof(TPage).Name} already contains mod items.");
                    return true;
                }

                // 移除覆写 ID 对应的原版条目
                if (overrideIds != null && overrideIds.Count > 0)
                {
                    var toRemoveOverride = new List<int>();
                    for (int i = 0; i < icoll.Count; i++)
                    {
                        var item = ilist[i];
                        if (item != null)
                        {
                            IntPtr idPtr = Il2CppFieldHelper.GetReferenceField(item, "_id");
                            if (idPtr != IntPtr.Zero)
                            {
                                string itemId = IL2CPP.Il2CppStringToManaged(idPtr);
                                if (overrideIds.Contains(itemId))
                                    toRemoveOverride.Add(i);
                            }
                        }
                    }
                    for (int k = toRemoveOverride.Count - 1; k >= 0; k--)
                        ilist.RemoveAt(toRemoveOverride[k]);
                    if (toRemoveOverride.Count > 0)
                        RuleNoteLogInfo($"Removed {toRemoveOverride.Count} vanilla entries from {typeof(TPage).Name} _loadedDataItemMap for override IDs.");
                }

                injectAction(ilist);
                RuleNoteLogInfo($"{typeof(TPage).Name} _loadedDataItemMap: {before} → {icoll.Count}");
                return true;
            }
            catch (Exception ex)
            {
                RuleNoteLogError($"TryInjectGenericDataItemMap<{typeof(TPage).Name}> failed: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 向 page._itemIds 追加 mod ID（通用实现）。
        /// </summary>
        private static void TryAppendModIds<TPage>(TPage page, HashSet<string> modIds, string label)
            where TPage : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
        {
            try
            {
                IntPtr itemIdsPtr = Il2CppFieldHelper.GetReferenceField(page, "_itemIds");
                if (itemIdsPtr == IntPtr.Zero)
                {
                    RuleNoteLogDebug($"_itemIds is null for {label}.");
                    return;
                }

                var oldIds = new Il2CppStringArray(itemIdsPtr);

                // 仅追加"纯新"mod ID（覆写 ID 已存在于原版 _itemIds 中）
                var existingIdSet = new HashSet<string>();
                for (int i = 0; i < oldIds.Length; i++)
                {
                    try
                    {
                        string s = oldIds[i];
                        if (s != null) existingIdSet.Add(s);
                    }
                    catch
                    {
                        // 跳过无效的 IL2CPP 字符串
                    }
                }

                var idsToAppend = new List<string>();
                foreach (var id in modIds)
                    if (!existingIdSet.Contains(id))
                        idsToAppend.Add(id);

                if (idsToAppend.Count == 0)
                {
                    RuleNoteLogDebug($"{label} _itemIds: 无需追加（所有 mod ID 已存在）。");
                    return;
                }

                int newLen = oldIds.Length + idsToAppend.Count;
                var newIds = new Il2CppStringArray(newLen);
                for (int i = 0; i < oldIds.Length; i++)
                {
                    try { newIds[i] = oldIds[i]; }
                    catch { newIds[i] = string.Empty; }
                }
                for (int i = 0; i < idsToAppend.Count; i++)
                    newIds[oldIds.Length + i] = idsToAppend[i];

                Il2CppFieldHelper.SetReferenceField(page, "_itemIds", newIds.Pointer);
                RuleNoteLogInfo($"{label} _itemIds: {oldIds.Length} → {newLen} (+{idsToAppend.Count} 纯新 ID)");
            }
            catch (Exception ex)
            {
                RuleNoteLogError($"TryAppendModIds({label}) failed: {ex}");
            }
        }

        /// <summary>
        /// 直接设置状态（通用实现）。
        /// 从 page._state 获取 VersionedState 并调用 SetVersion。
        /// </summary>
        private static void DirectSetState<TPage>(
            string id, int version,
            Dictionary<string, int> pendingUpdates,
            string label)
            where TPage : UnityEngine.Object
        {
            try
            {
                var pages = Resources.FindObjectsOfTypeAll<TPage>();
                if (pages == null || pages.Length == 0)
                {
                    pendingUpdates[id] = version;
                    RuleNoteLogDebug($"{label} not found, queued pending: {id} v{version}");
                    return;
                }

                IntPtr statePtr = Il2CppFieldHelper.GetReferenceField(pages[0], "_state");
                if (statePtr == IntPtr.Zero)
                {
                    pendingUpdates[id] = version;
                    RuleNoteLogWarning($"{label}._state is null, queued pending: {id} v{version}");
                    return;
                }

                var state = new VersionedState(statePtr);
                state.SetVersion(id, version);
                RuleNoteLogDebug($"Directly set {label} state: {id} v{version}");
            }
            catch (Exception ex)
            {
                pendingUpdates[id] = version;
                RuleNoteLogError($"DirectSetState<{label}> failed for {id}: {ex}");
            }
        }

        /// <summary>应用待处理的状态更新（通用实现）。</summary>
        private static void ApplyPendingUpdates<TPage>(
            Dictionary<string, int> pendingUpdates,
            string label)
            where TPage : UnityEngine.Object
        {
            if (pendingUpdates.Count == 0) return;

            try
            {
                var pages = Resources.FindObjectsOfTypeAll<TPage>();
                if (pages == null || pages.Length == 0)
                {
                    RuleNoteLogWarning($"ApplyPendingUpdates<{label}>: page still not found, {pendingUpdates.Count} remain.");
                    return;
                }

                IntPtr statePtr = Il2CppFieldHelper.GetReferenceField(pages[0], "_state");
                if (statePtr == IntPtr.Zero)
                {
                    RuleNoteLogWarning($"ApplyPendingUpdates<{label}>: _state is null.");
                    return;
                }

                var state = new VersionedState(statePtr);
                foreach (var kvp in pendingUpdates)
                {
                    state.SetVersion(kvp.Key, kvp.Value);
                    RuleNoteLogDebug($"Applied pending {label} state: {kvp.Key} v{kvp.Value}");
                }

                pendingUpdates.Clear();
            }
            catch (Exception ex)
            {
                RuleNoteLogError($"ApplyPendingUpdates<{label}> failed: {ex}");
            }
        }

        // ====================================================================
        // RulePage 注入
        // ====================================================================

        public static void TryInjectRulePageStateAndRebuild()
        {
            if (!HasModRules) return;
            try
            {
                var pages = Resources.FindObjectsOfTypeAll<RulePage>();
                if (pages == null || pages.Length == 0)
                {
                    RuleNoteLogDebug("RulePage not found yet.");
                    return;
                }

                var rulePage = pages[0];
                IntPtr mapPtr = Il2CppFieldHelper.GetReferenceField(rulePage, "_loadedDataItemMap");
                if (mapPtr == IntPtr.Zero)
                {
                    RuleNoteLogDebug("RulePage._loadedDataItemMap is null.");
                    return;
                }

                if (mapPtr == injectedRuleDataItemMapPtr) return;

                bool injected = TryInjectGenericDataItemMap<RulePage, RuleDataItem>(
                    rulePage, mapPtr, modRuleIds, modRuleOverrideIds,
                    ilist =>
                    {
                        foreach (var modRule in allModRules)
                        {
                            try
                            {
                                var subtitleArr = modRule.Subtitle.ToIl2CppArray();
                                var descArr = modRule.Description.ToIl2CppArray();
                                var dataItem = new RuleDataItem(modRule.Numbering ?? "", subtitleArr, descArr);
                                var vi = new VersionedItem<RuleDataItem>(modRule.Id, modRule.Version, dataItem);
                                ilist.Add(vi.Cast<Il2CppSystem.Object>());
                                RuleNoteLogDebug($"Injected rule into _loadedDataItemMap: {modRule.Id}");
                            }
                            catch (Exception ex)
                            {
                                RuleNoteLogError($"Failed to inject rule '{modRule.Id}': {ex}");
                            }
                        }
                    });

                TryAppendModIds(rulePage, modRuleIds, "RulePage");

                if (injected)
                {
                    injectedRuleDataItemMapPtr = mapPtr;
                    RuleNoteLogInfo("RulePage injection complete.");
                }
            }
            catch (Exception ex)
            {
                RuleNoteLogError($"TryInjectRulePageStateAndRebuild failed: {ex}");
            }
        }

        public static void DirectSetModRuleState(string id, int version)
        {
            allModRuleStates[id] = version;
            DirectSetState<RulePage>(id, version, pendingModRuleUpdates, "RulePage");
        }

        public static void ApplyPendingModRuleUpdates()
            => ApplyPendingUpdates<RulePage>(pendingModRuleUpdates, "RulePage");

        /// <summary>
        /// 强制重新注入 + 重新应用所有 mod rule 状态。
        /// 重置 injectedRuleDataItemMapPtr 以在下次 TryInjectRulePageStateAndRebuild 时强制重新注入。
        /// 然后将 allModRuleStates 中的所有记录重新设置到 _state。
        /// </summary>
        public static void EnsureAllModRuleStatesAndForceReinject()
        {
            // 始终重置注入缓存——_loadedDataItemMap 可能已被重建
            injectedRuleDataItemMapPtr = IntPtr.Zero;

            if (allModRuleStates.Count == 0) return;
            try
            {

                // 重新应用所有已记录的 mod rule 状态
                var pages = Resources.FindObjectsOfTypeAll<RulePage>();
                if (pages == null || pages.Length == 0) return;

                IntPtr statePtr = Il2CppFieldHelper.GetReferenceField(pages[0], "_state");
                if (statePtr == IntPtr.Zero) return;

                var state = new VersionedState(statePtr);

                // 读取 _state._list 中已有的版本号（可能已被 DeserializeState 从存档正确恢复）
                var currentVersions = new Dictionary<string, int>();
                IntPtr listPtr = Il2CppFieldHelper.GetReferenceField(statePtr, "_list");
                if (listPtr != IntPtr.Zero)
                {
                    var list = new Il2CppSystem.Collections.Generic.List<IdVersionPair>(listPtr);
                    for (int i = 0; i < list.Count; i++)
                    {
                        var pair = list[i];
                        if (pair != null && pair.Id != null)
                            currentVersions[pair.Id] = pair.Version;
                    }
                }

                // 仅在 _state 中不存在对应条目时才注入；若存档中已有，以存档版本为准
                var syncBack = new Dictionary<string, int>();
                foreach (var kvp in allModRuleStates)
                {
                    if (currentVersions.TryGetValue(kvp.Key, out int existingVer))
                    {
                        if (existingVer != kvp.Value)
                            syncBack[kvp.Key] = existingVer;
                    }
                    else
                    {
                        state.SetVersion(kvp.Key, kvp.Value);
                    }
                }
                foreach (var kvp in syncBack)
                    allModRuleStates[kvp.Key] = kvp.Value;

                RuleNoteLogInfo($"EnsureAllModRuleStates: synced {allModRuleStates.Count} entries.");
            }
            catch (Exception ex)
            {
                RuleNoteLogError($"EnsureAllModRuleStatesAndForceReinject: {ex}");
            }
        }

        // ====================================================================
        // NotePage 注入
        // ====================================================================

        public static void TryInjectNotePageStateAndRebuild()
        {
            if (!HasModNotes) return;
            try
            {
                var pages = Resources.FindObjectsOfTypeAll<NotePage>();
                if (pages == null || pages.Length == 0)
                {
                    RuleNoteLogDebug("NotePage not found yet.");
                    return;
                }

                var notePage = pages[0];
                IntPtr mapPtr = Il2CppFieldHelper.GetReferenceField(notePage, "_loadedDataItemMap");
                if (mapPtr == IntPtr.Zero)
                {
                    RuleNoteLogDebug("NotePage._loadedDataItemMap is null.");
                    return;
                }

                if (mapPtr == injectedNoteDataItemMapPtr) return;

                bool injected = TryInjectGenericDataItemMap<NotePage, NoteDataItem>(
                    notePage, mapPtr, modNoteIds, modNoteOverrideIds,
                    ilist =>
                    {
                        foreach (var modNote in allModNotes)
                        {
                            try
                            {
                                var titleArr = modNote.Title.ToIl2CppArray();
                                var descArr = modNote.Description.ToIl2CppArray();
                                var dataItem = new NoteDataItem(titleArr, descArr);
                                var vi = new VersionedItem<NoteDataItem>(modNote.Id, modNote.Version, dataItem);
                                ilist.Add(vi.Cast<Il2CppSystem.Object>());
                                RuleNoteLogDebug($"Injected note into _loadedDataItemMap: {modNote.Id}");
                            }
                            catch (Exception ex)
                            {
                                RuleNoteLogError($"Failed to inject note '{modNote.Id}': {ex}");
                            }
                        }
                    });

                TryAppendModIds(notePage, modNoteIds, "NotePage");

                if (injected)
                {
                    injectedNoteDataItemMapPtr = mapPtr;
                    RuleNoteLogInfo("NotePage injection complete.");
                }
            }
            catch (Exception ex)
            {
                RuleNoteLogError($"TryInjectNotePageStateAndRebuild failed: {ex}");
            }
        }

        public static void DirectSetModNoteState(string id, int version)
        {
            allModNoteStates[id] = version;
            DirectSetState<NotePage>(id, version, pendingModNoteUpdates, "NotePage");
        }

        public static void ApplyPendingModNoteUpdates()
            => ApplyPendingUpdates<NotePage>(pendingModNoteUpdates, "NotePage");

        /// <summary>
        /// 强制重新注入 + 重新应用所有 mod note 状态。
        /// </summary>
        public static void EnsureAllModNoteStatesAndForceReinject()
        {
            // 始终重置注入缓存——_loadedDataItemMap 可能已被重建
            injectedNoteDataItemMapPtr = IntPtr.Zero;

            if (allModNoteStates.Count == 0) return;
            try
            {

                var pages = Resources.FindObjectsOfTypeAll<NotePage>();
                if (pages == null || pages.Length == 0) return;

                IntPtr statePtr = Il2CppFieldHelper.GetReferenceField(pages[0], "_state");
                if (statePtr == IntPtr.Zero) return;

                var state = new VersionedState(statePtr);

                // 读取 _state._list 中已有的版本号（可能已被 DeserializeState 从存档正确恢复）
                var currentVersions = new Dictionary<string, int>();
                IntPtr listPtr = Il2CppFieldHelper.GetReferenceField(statePtr, "_list");
                if (listPtr != IntPtr.Zero)
                {
                    var list = new Il2CppSystem.Collections.Generic.List<IdVersionPair>(listPtr);
                    for (int i = 0; i < list.Count; i++)
                    {
                        var pair = list[i];
                        if (pair != null && pair.Id != null)
                            currentVersions[pair.Id] = pair.Version;
                    }
                }

                // 仅在 _state 中不存在对应条目时才注入；若存档中已有，以存档版本为准
                var syncBack = new Dictionary<string, int>();
                foreach (var kvp in allModNoteStates)
                {
                    if (currentVersions.TryGetValue(kvp.Key, out int existingVer))
                    {
                        if (existingVer != kvp.Value)
                            syncBack[kvp.Key] = existingVer;
                    }
                    else
                    {
                        state.SetVersion(kvp.Key, kvp.Value);
                    }
                }
                foreach (var kvp in syncBack)
                    allModNoteStates[kvp.Key] = kvp.Value;

                RuleNoteLogInfo($"EnsureAllModNoteStates: synced {allModNoteStates.Count} entries.");
            }
            catch (Exception ex)
            {
                RuleNoteLogError($"EnsureAllModNoteStatesAndForceReinject: {ex}");
            }
        }

        // ====================================================================
        // 本地化文本
        // ====================================================================

        public static (string numbering, string subtitle, string description) GetModRuleLocalizedText(string id, int version)
        {
            foreach (var r in allModRules)
            {
                if (r.Id == id && r.Version == version)
                    return (r.Numbering ?? "", r.Subtitle?.Resolve(""), r.Description?.Resolve(""));
            }
            foreach (var r in allModRules)
            {
                if (r.Id == id)
                    return (r.Numbering ?? "", r.Subtitle?.Resolve(""), r.Description?.Resolve(""));
            }
            return (id, "", "");
        }

        public static (string title, string description) GetModNoteLocalizedText(string id, int version)
        {
            foreach (var n in allModNotes)
            {
                if (n.Id == id && n.Version == version)
                    return (n.Title?.Resolve(id), n.Description?.Resolve(""));
            }
            foreach (var n in allModNotes)
            {
                if (n.Id == id)
                    return (n.Title?.Resolve(id), n.Description?.Resolve(""));
            }
            return (id, "");
        }
    }

    // ========================================================================
    // 数据注入 + 状态预设 Patch（Rule / Note）
    // ========================================================================

    /// <summary>
    /// 在 WitchBookScreen.UpdateVersion 和 BeginToPresent 中处理 Rule 和 Note 的注入。
    /// 与 ClueDataInjection_Patch 协作，处理 WitchBookCategory.Rule (3) 和 Note (4)。
    /// </summary>
    [HarmonyPatch]
    static class RuleNoteInjection_Patch
    {
        [HarmonyPatch(typeof(WitchBookScreen), nameof(WitchBookScreen.UpdateVersion))]
        [HarmonyPrefix]
        static void WitchBookScreen_UpdateVersion_Prefix(WitchBookCategory category, string id, int version)
        {
            try
            {
                // 确保 mod 数据已加载（@update 可能在 WitchBook 打开前执行）
                ModResourceLoader.EnsureModDataLoaded();

                if (category == WitchBookCategory.Rule && ModRuleNoteLoader.HasModRules)
                {
                    ModRuleNoteLoader.TryInjectRuleData();
                    if (ModRuleNoteLoader.IsModRule(id))
                    {
                        ModRuleNoteLoader.DirectSetModRuleState(id, version);
                        ModRuleNoteLoader.RuleNoteLogDebug($"UpdateVersion intercepted mod rule: {id} v{version}");
                    }
                    ModRuleNoteLoader.TryInjectRulePageStateAndRebuild();
                }
                else if (category == WitchBookCategory.Note && ModRuleNoteLoader.HasModNotes)
                {
                    ModRuleNoteLoader.TryInjectNoteData();
                    if (ModRuleNoteLoader.IsModNote(id))
                    {
                        ModRuleNoteLoader.DirectSetModNoteState(id, version);
                        ModRuleNoteLoader.RuleNoteLogDebug($"UpdateVersion intercepted mod note: {id} v{version}");
                    }
                    ModRuleNoteLoader.TryInjectNotePageStateAndRebuild();
                }
            }
            catch (Exception ex)
            {
                ModRuleNoteLoader.RuleNoteLogError($"RuleNoteInjection UpdateVersion prefix: {ex}");
            }
        }

        [HarmonyPatch(typeof(WitchBookScreen), nameof(WitchBookScreen.BeginToPresent))]
        [HarmonyPrefix]
        static void WitchBookScreen_BeginToPresent_Prefix()
        {
            try
            {
                // 确保当前选中 mod 的数据已加载
                ModResourceLoader.EnsureModDataLoaded();

                if (ModRuleNoteLoader.HasModRules)
                {
                    ModRuleNoteLoader.TryInjectRuleData();
                    ModRuleNoteLoader.TryInjectRulePageStateAndRebuild();
                    ModRuleNoteLoader.ApplyPendingModRuleUpdates();
                }

                if (ModRuleNoteLoader.HasModNotes)
                {
                    ModRuleNoteLoader.TryInjectNoteData();
                    ModRuleNoteLoader.TryInjectNotePageStateAndRebuild();
                    ModRuleNoteLoader.ApplyPendingModNoteUpdates();
                }
            }
            catch (Exception ex)
            {
                ModRuleNoteLoader.RuleNoteLogError($"RuleNoteInjection BeginToPresent prefix: {ex}");
            }
        }

        /// <summary>
        /// InitializePages 前缀 — 与 ClueDataInjection_Patch 同理。
        /// 在 InitializeItems 运行前强制重新注入 mod 数据和状态。
        /// </summary>
        [HarmonyPatch(typeof(WitchBookScreen), nameof(WitchBookScreen.InitializePages))]
        [HarmonyPrefix]
        static void WitchBookScreen_InitializePages_Prefix()
        {
            try
            {
                if (ModRuleNoteLoader.HasModRules)
                {
                    ModRuleNoteLoader.TryInjectRuleData();
                    ModRuleNoteLoader.EnsureAllModRuleStatesAndForceReinject();
                    ModRuleNoteLoader.TryInjectRulePageStateAndRebuild();
                }

                if (ModRuleNoteLoader.HasModNotes)
                {
                    ModRuleNoteLoader.TryInjectNoteData();
                    ModRuleNoteLoader.EnsureAllModNoteStatesAndForceReinject();
                    ModRuleNoteLoader.TryInjectNotePageStateAndRebuild();
                }
            }
            catch (Exception ex)
            {
                ModRuleNoteLoader.RuleNoteLogError($"RuleNoteInjection InitializePages prefix: {ex}");
            }
        }
    }

    // ========================================================================
    // RulePage.RefreshPageContent Hook
    // ========================================================================

    /// <summary>
    /// Hook RulePage.RefreshPageContent(VersionedItem&lt;RuleDataItem&gt; map)
    /// 
    /// 原版方法通过 _localizedTextData 和 _numberings 查找本地化文字。
    /// 对 mod 规则：直接设置 _titleNumLabel / _subtitleLabel / _descriptionLabel。
    /// </summary>
    [HarmonyPatch]
    static class RulePageRefreshContent_Patch
    {
        [HarmonyPatch(typeof(RulePage), nameof(RulePage.RefreshPageContent))]
        [HarmonyPrefix]
        static bool Prefix(RulePage __instance, VersionedItem<RuleDataItem> map)
        {
            try
            {
                string id = map?.Id;
                if (id == null || !ModRuleNoteLoader.IsModRule(id))
                    return true;

                int version = Il2CppFieldHelper.GetIntField(map, "_version", 1);

                var (numbering, subtitle, description) = ModRuleNoteLoader.GetModRuleLocalizedText(id, version);

                // _titleNumLabel at 0xC0
                IntPtr titleNumPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_titleNumLabel");
                if (titleNumPtr != IntPtr.Zero)
                {
                    var label = new TMP_Text(titleNumPtr);
                    label.text = numbering;
                }

                // _subtitleLabel at 0xC8
                IntPtr subtitlePtr = Il2CppFieldHelper.GetReferenceField(__instance, "_subtitleLabel");
                if (subtitlePtr != IntPtr.Zero)
                {
                    var label = new TMP_Text(subtitlePtr);
                    label.text = subtitle;
                }

                // _descriptionLabel at 0xD0
                IntPtr descPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_descriptionLabel");
                if (descPtr != IntPtr.Zero)
                {
                    var label = new TMP_Text(descPtr);
                    label.text = description;
                }

                ModRuleNoteLoader.RuleNoteLogDebug($"RefreshPageContent handled for mod rule: {id}");
                return false;
            }
            catch (Exception ex)
            {
                ModRuleNoteLoader.RuleNoteLogError($"RulePageRefreshContent error: {ex}");
                return true;
            }
        }
    }

    // ========================================================================
    // RulePage.SetupItemButton Hook
    // ========================================================================

    /// <summary>
    /// Hook RulePage.SetupItemButton 处理 mod 规则的按钮显示。
    /// 使用 WitchBookItemButton.SetupWithText 设置编号+副标题文字。
    /// </summary>
    [HarmonyPatch]
    static class RulePageSetupItemButton_Patch
    {
        [HarmonyPatch(typeof(RulePage), nameof(RulePage.SetupItemButton))]
        [HarmonyPrefix]
        static bool Prefix(RulePage __instance, WitchBookItemButton button, VersionedItem<RuleDataItem> map)
        {
            try
            {
                string id = map?.Id;
                if (id == null || !ModRuleNoteLoader.IsModRule(id))
                    return true;

                int version = Il2CppFieldHelper.GetIntField(map, "_version", 1);

                var (_, subtitle, _) = ModRuleNoteLoader.GetModRuleLocalizedText(id, version);
                string buttonText = subtitle;
                button.SetupWithText(buttonText, 0);

                ModRuleNoteLoader.RuleNoteLogDebug($"SetupItemButton handled for mod rule: {id}");
                return false;
            }
            catch (Exception ex)
            {
                ModRuleNoteLoader.RuleNoteLogError($"RulePageSetupItemButton error: {ex}");
                return true;
            }
        }
    }

    // ========================================================================
    // NotePage.RefreshPageContent Hook
    // ========================================================================

    /// <summary>
    /// Hook NotePage.RefreshPageContent(VersionedItem&lt;NoteDataItem&gt; map)
    /// 
    /// 对 mod 笔记：直接设置 _titleLabel / _descriptionLabel。
    /// </summary>
    [HarmonyPatch]
    static class NotePageRefreshContent_Patch
    {
        [HarmonyPatch(typeof(NotePage), nameof(NotePage.RefreshPageContent))]
        [HarmonyPrefix]
        static bool Prefix(NotePage __instance, VersionedItem<NoteDataItem> map)
        {
            try
            {
                string id = map?.Id;
                if (id == null || !ModRuleNoteLoader.IsModNote(id))
                    return true;

                int version = Il2CppFieldHelper.GetIntField(map, "_version", 1);

                var (title, description) = ModRuleNoteLoader.GetModNoteLocalizedText(id, version);

                // _titleLabel at 0xB8
                IntPtr titlePtr = Il2CppFieldHelper.GetReferenceField(__instance, "_titleLabel");
                if (titlePtr != IntPtr.Zero)
                {
                    var label = new TMP_Text(titlePtr);
                    label.text = title;
                }

                // _descriptionLabel at 0xC0
                IntPtr descPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_descriptionLabel");
                if (descPtr != IntPtr.Zero)
                {
                    var label = new TMP_Text(descPtr);
                    label.text = description;
                }

                ModRuleNoteLoader.RuleNoteLogDebug($"RefreshPageContent handled for mod note: {id}");
                return false;
            }
            catch (Exception ex)
            {
                ModRuleNoteLoader.RuleNoteLogError($"NotePageRefreshContent error: {ex}");
                return true;
            }
        }
    }

    // ========================================================================
    // NotePage.SetupItemButton Hook
    // ========================================================================

    /// <summary>
    /// Hook NotePage.SetupItemButton 处理 mod 笔记的按钮显示。
    /// 使用 WitchBookItemButton.SetupWithText 设置标题。
    /// </summary>
    [HarmonyPatch]
    static class NotePageSetupItemButton_Patch
    {
        [HarmonyPatch(typeof(NotePage), nameof(NotePage.SetupItemButton))]
        [HarmonyPrefix]
        static bool Prefix(NotePage __instance, WitchBookItemButton button, VersionedItem<NoteDataItem> map)
        {
            try
            {
                string id = map?.Id;
                if (id == null || !ModRuleNoteLoader.IsModNote(id))
                    return true;

                int version = Il2CppFieldHelper.GetIntField(map, "_version", 1);

                var (title, _) = ModRuleNoteLoader.GetModNoteLocalizedText(id, version);
                button.SetupWithText(title, 0);

                ModRuleNoteLoader.RuleNoteLogDebug($"SetupItemButton handled for mod note: {id}");
                return false;
            }
            catch (Exception ex)
            {
                ModRuleNoteLoader.RuleNoteLogError($"NotePageSetupItemButton error: {ex}");
                return true;
            }
        }
    }
}
