using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using GigaCreation.Essentials.AddressablesUtils;
using GigaCreation.Essentials.Localization;

using HarmonyLib;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

using ManosabaLoader.ModManager;
using ManosabaLoader.Utils;

using Naninovel;

using TMPro;

using UnityEngine;
using UnityEngine.UI;

using WitchTrials.Models;
using WitchTrials.Views;

namespace ManosabaLoader
{
    /// <summary>
    /// Mod 线索（Clue/证物）加载器。
    ///
    /// 策略：向 ClueData._items / CluePage._loadedDataItemMap 注入 mod 数据，
    /// 直接设置 _state.SetVersion 绕过 _itemIds 检查，通过 Addressables 缓存注册纹理，
    /// Hook UI 显示方法绕过 _localizedTextData 查找。
    ///
    /// IL2CPP 约束：仅 hook 同步、非泛型方法；绝不 patch 返回 UniTask 的异步方法。
    /// </summary>
    public static class ModClueLoader
    {
        public static Action<string> ClueLogMessage;
        public static Action<string> ClueLogInfo;
        public static Action<string> ClueLogDebug;
        public static Action<string> ClueLogWarning;
        public static Action<string> ClueLogError;
        /// <summary>所有 mod 线索数据</summary>
        private static readonly List<ModItem.ModClue> allModClues = new();

        /// <summary>mod 线索 ID 集合</summary>
        private static readonly HashSet<string> modClueIds = new();

        /// <summary>与原版内容共用相同 ID 的覆写线索集合（注入时动态确定）</summary>
        private static readonly HashSet<string> modClueOverrideIds = new();

        /// <summary>mod 线索的图片本地路径：clue ID → file path</summary>
        private static readonly Dictionary<string, string> modClueTexturePathMap = new();

        /// <summary>已加载的 mod 线索纹理缓存</summary>
        private static readonly Dictionary<string, Texture2D> modClueTextureCache = new();

        /// <summary>Addressables 纹理地址 → mod clue ID 反查映射</summary>
        private static readonly Dictionary<string, string> modClueAddressToIdMap = new();

        /// <summary>已注入 mod 数据的 ClueData 实例指针（用于检测游戏重新开始）</summary>
        private static IntPtr injectedClueDataPtr = IntPtr.Zero;

        /// <summary>已注入数据的 CluePage._loadedDataItemMap 实例指针</summary>
        private static IntPtr injectedDataItemMapPtr = IntPtr.Zero;

        /// <summary>
        /// 待应用的 mod 线索状态更新。
        /// 当 @update 在 CluePage 尚未初始化时触发，状态更新暂存于此。
        /// 在 BeginToPresent（WitchBook 打开时）应用。
        /// </summary>
        private static readonly Dictionary<string, int> pendingModClueUpdates = new();

        /// <summary>
        /// 所有已收到的 mod 线索状态更新（持久记录）。
        /// 关键作用：即使 _state 被 DeserializeState 覆盖（如存档加载/回溯），
        /// 也能在 InitializePages 时重新应用所有 mod 状态。
        /// </summary>
        private static readonly Dictionary<string, int> allModClueStates = new();

        public static void Init(Harmony harmony)
        {
            // 始终注册 Harmony 补丁（注入数据按需延迟加载）
            harmony.PatchAll(typeof(ClueDataInjection_Patch));
            harmony.PatchAll(typeof(RefreshPageContent_Patch));
            harmony.PatchAll(typeof(SetupItemButton_Patch));
            harmony.PatchAll(typeof(ThumbnailSetup_Patch));
            harmony.PatchAll(typeof(SpawnableClueSpawn_Patch));
            ClueLogInfo("ModClueLoader patches applied.");
        }

        /// <summary>加载指定 mod 的线索数据。</summary>
        public static void LoadModData(string modKey, string modPath, ModItem modItem)
        {
            RegisterModClues(modKey, modItem);
            RegisterClueTextures(modPath, modItem);
            if (allModClues.Count > 0)
            {
                BuildAddressMaps();
                ClueLogMessage($"Loaded {allModClues.Count} clue entries ({modClueIds.Count} unique IDs) for mod: {modKey}");
            }
        }

        /// <summary>清除所有 mod 线索数据，释放纹理缓存。</summary>
        public static void ClearModData()
        {
            allModClues.Clear();
            modClueIds.Clear();
            modClueOverrideIds.Clear();
            modClueTexturePathMap.Clear();
            ModTextureHelper.DestroyAndClearCache(modClueTextureCache);
            modClueAddressToIdMap.Clear();
            pendingModClueUpdates.Clear();
            allModClueStates.Clear();
            injectedClueDataPtr = IntPtr.Zero;
            injectedDataItemMapPtr = IntPtr.Zero;
            ClueLogInfo("ClueLoader data cleared.");
        }

        private static void RegisterModClues(string modPrefix, ModItem modItem)
        {
            if (modItem?.Description?.Clues == null) return;

            // 允许同一 ID 的不同版本；仅跳过 (id, version) 完全重复的条目
            var seenIdVersions = new HashSet<(string, int)>();
            foreach (var mc in allModClues)
                seenIdVersions.Add((mc.Id, mc.Version));

            foreach (var group in modItem.Description.Clues)
            {
                if (string.IsNullOrEmpty(group.Id))
                {
                    ClueLogWarning($"[{modPrefix}] Skipping clue group with empty ID.");
                    continue;
                }
                if (group.Items == null || group.Items.Length == 0) continue;

                foreach (var item in group.Items)
                {
                    if (seenIdVersions.Contains((group.Id, item.Version)))
                    {
                        ClueLogDebug($"[{modPrefix}] Skipping duplicate clue: {group.Id} v{item.Version}");
                        continue;
                    }

                    seenIdVersions.Add((group.Id, item.Version));
                    allModClues.Add(new ModItem.ModClue
                    {
                        Id = group.Id,
                        Version = item.Version,
                        Name = item.Name,
                        Description = item.Description
                    });
                    modClueIds.Add(group.Id);
                    ClueLogDebug($"[{modPrefix}] Registered clue: Id={group.Id}, Version={item.Version}");
                }
            }

            // 版本补齐：为每个唯一 ID 填充缺失的版本以支持 @update 升级
            VersionExpander.Expand(allModClues, seenIdVersions,
                mc => (mc.Id, mc.Version),
                (baseClue, ver) => new ModItem.ModClue
                {
                    Id = baseClue.Id,
                    Version = ver,
                    Name = baseClue.Name,
                    Description = baseClue.Description
                });
        }

        private static void RegisterClueTextures(string modPath, ModItem modItem)
        {
            if (modItem?.Description?.Clues == null) return;

            var clueTexDir = Path.Combine(modPath, "WitchBook", "Clues");

            foreach (var group in modItem.Description.Clues)
            {
                if (string.IsNullOrEmpty(group.Id)) continue;
                if (modClueTexturePathMap.ContainsKey(group.Id)) continue;

                string[] extensions = [".png", ".jpg", ".jpeg"];
                foreach (var ext in extensions)
                {
                    var texPath = Path.Combine(clueTexDir, group.Id + ext);
                    if (File.Exists(texPath))
                    {
                        modClueTexturePathMap[group.Id] = texPath;
                        ClueLogDebug($"Registered clue texture: {group.Id} -> {texPath}");
                        break;
                    }
                }

                if (!modClueTexturePathMap.ContainsKey(group.Id))
                {
                    ClueLogWarning($"No texture found for mod clue '{group.Id}' in {clueTexDir}");
                }
            }
        }

        /// <summary>判断线索 ID 是否为 mod 线索</summary>
        public static bool IsModClue(string id) => modClueIds.Contains(id);

        /// <summary>是否有已加载的 mod 线索数据</summary>
        public static bool HasModClues => allModClues.Count > 0;

        /// <summary>从 Addressables 纹理地址反查 mod clue ID（若不是 mod 则返回 null）</summary>
        public static string TryGetModClueIdFromAddress(string address)
        {
            if (address != null && modClueAddressToIdMap.TryGetValue(address, out var id))
                return id;
            return null;
        }

        /// <summary>
        /// 构建 Addressables 纹理地址，镜像 WitchBookDataHelper.BuildClueTextureAddress 的逻辑：
        /// 1. 按 '-' 分割 ID
        /// 2. 每段不足 3 字符则用 '0' 左填充
        /// 3. 用 '_' 连接，前缀 "General/WitchBook/Clue"
        /// 示例: "MaxMixAlex_CluesTest_1-1" → "General/WitchBook/Clue_MaxMixAlex_CluesTest_1_001"
        /// </summary>
        public static string BuildClueTextureAddress(string clueId)
        {
            var parts = clueId.Split('-');
            var sb = new StringBuilder("General/WitchBook/Clue");
            foreach (var part in parts)
            {
                sb.Append('_');
                sb.Append(part.Length < 3 ? part.PadLeft(3, '0') : part);
            }
            return sb.ToString();
        }

        /// <summary>构建地址映射表（clue ID ↔ texture address）</summary>
        private static void BuildAddressMaps()
        {
            modClueAddressToIdMap.Clear();
            foreach (var clueId in modClueIds)
            {
                string address = BuildClueTextureAddress(clueId);
                modClueAddressToIdMap[address] = clueId;
                ClueLogDebug($"Address map: {clueId} → {address}");
            }
        }

        // ====================================================================
        // 数据注入
        // ====================================================================

        /// <summary>
        /// 向已加载的 ClueData ScriptableObject 注入 mod 线索。
        /// 注意：_loadedDataItemMap 由 LoadDataAsync 的 LINQ 另行创建，不共享此引用。
        /// 此注入主要供 @spawn ClueItem 和 AddressableAssets 使用。
        /// 
        /// 幂等机制：通过实例指针追踪 + 内容检查，避免重复注入。
        /// 支持游戏重新开始时自动检测新实例并重新注入。
        /// </summary>
        public static void TryInjectClueData()
        {
            try
            {
                var instances = Resources.FindObjectsOfTypeAll<ClueData>();
                if (instances == null || instances.Length == 0)
                {
                    ClueLogDebug("ClueData not yet loaded, will retry on next opportunity.");
                    return;
                }

                var clueData = instances[0];

                // 同一实例指针 → 已注入，跳过
                if (clueData.Pointer == injectedClueDataPtr) return;

                var itemsList = clueData._items;
                ClueLogDebug($"Found ClueData instance 0x{clueData.Pointer:X}, items count: {itemsList.Count}");

                // 扫描现有 _items，确定哪些 mod ID 与原版条目冲突（覆写 ID）
                modClueOverrideIds.Clear();
                var vanillaIdSet = new HashSet<string>();
                for (int i = 0; i < itemsList.Count; i++)
                {
                    string vid = itemsList[i].Id;
                    if (vid != null) vanillaIdSet.Add(vid);
                }
                foreach (var id in modClueIds)
                    if (vanillaIdSet.Contains(id))
                        modClueOverrideIds.Add(id);
                if (modClueOverrideIds.Count > 0)
                    ClueLogInfo($"Detected {modClueOverrideIds.Count} vanilla override clue IDs: {string.Join(", ", modClueOverrideIds)}");

                // 检测是否已包含 mod 数据（仅检查"纯新" mod ID，避免被原版覆写 ID 误判）
                bool alreadyHasMod = false;
                for (int i = 0; i < itemsList.Count; i++)
                {
                    string vid = itemsList[i].Id;
                    if (IsModClue(vid) && !modClueOverrideIds.Contains(vid))
                    {
                        alreadyHasMod = true;
                        break;
                    }
                }

                if (!alreadyHasMod)
                {
                    // 步骤 1：移除覆写 ID 对应的原版条目（倒序移除，保持索引有效）
                    if (modClueOverrideIds.Count > 0)
                    {
                        var toRemove = new List<int>();
                        for (int i = 0; i < itemsList.Count; i++)
                            if (modClueOverrideIds.Contains(itemsList[i].Id))
                                toRemove.Add(i);
                        for (int k = toRemove.Count - 1; k >= 0; k--)
                            itemsList.RemoveAt(toRemove[k]);
                        ClueLogInfo($"Removed {toRemove.Count} vanilla ClueData entries for override IDs.");
                    }

                    // 步骤 2：注入所有 mod 线索（覆写 + 纯新）
                    int count = 0;
                    foreach (var modClue in allModClues)
                    {
                        try
                        {
                            var nameArray = modClue.Name.ToIl2CppArray();
                            var descArray = modClue.Description.ToIl2CppArray();
                            var clueDataItem = new ClueDataItem(nameArray, descArray);
                            var versionedItem = new VersionedItem<ClueDataItem>(
                                modClue.Id, modClue.Version, clueDataItem);
                            itemsList.Add(versionedItem);
                            count++;
                        }
                        catch (Exception ex)
                        {
                            ClueLogError($"Failed to create VersionedItem for clue '{modClue.Id}': {ex}");
                        }
                    }
                    ClueLogMessage($"Injected {count} mod clues into ClueData (total: {itemsList.Count}).");
                }
                else
                {
                    ClueLogDebug("ClueData already contains mod clues, skipping injection.");
                }

                injectedClueDataPtr = clueData.Pointer;
#if DEBUG
                DumpClueDataToFile(clueData);
#endif
            }
            catch (Exception ex)
            {
                ClueLogError($"TryInjectClueData failed: {ex}");
            }
        }

        // ====================================================================
        // CluePage 数据注入 + _itemIds 追加
        // ====================================================================

        /// <summary>
        /// 向 CluePage 注入 mod 线索数据：_loadedDataItemMap + _itemIds + 纹理注册。
        /// 通过实例指针追踪实现幂等。
        /// </summary>
        public static void TryInjectCluePageStateAndRebuild()
        {
            try
            {
                var pages = Resources.FindObjectsOfTypeAll<CluePage>();
                if (pages == null || pages.Length == 0)
                {
                    ClueLogDebug("CluePage not found yet, will retry.");
                    return;
                }

                var cluePage = pages[0];

                // 检查 _loadedDataItemMap 是否为同一实例
                IntPtr mapPtr = Il2CppFieldHelper.GetReferenceField(cluePage, "_loadedDataItemMap");
                if (mapPtr == IntPtr.Zero)
                {
                    ClueLogDebug("_loadedDataItemMap is null, will retry.");
                    return;
                }

                if (mapPtr == injectedDataItemMapPtr) return; // 同一列表，已注入

                // ---- 步骤 1: 向 _loadedDataItemMap 注入 mod VersionedItem ----
                bool injected = TryInjectIntoLoadedDataItemMap(cluePage, mapPtr);

                // ---- 步骤 2: 向 _itemIds 追加 mod 线索 ID ----
                TryAppendModClueIds(cluePage);

                // ---- 步骤 3: 确保 Addressables 纹理已注册 ----
                EnsureTexturesRegistered();

                // 仅在注入成功时标记指针，否则下次进入时重试
                if (injected)
                {
                    injectedDataItemMapPtr = mapPtr;
                    ClueLogInfo("CluePage _loadedDataItemMap + _itemIds injected.");
                }
                else
                {
                    ClueLogWarning("_loadedDataItemMap injection failed, will retry next time.");
                }

                // ---- 验证 ----
#if DEBUG
                DumpCluePageState(cluePage);
#endif
            }
            catch (Exception ex)
            {
                ClueLogError($"TryInjectCluePageStateAndRebuild failed: {ex}");
            }
        }

        /// <summary>
        /// 向 CluePage._loadedDataItemMap (IList&lt;VersionedItem&lt;ClueDataItem&gt;&gt;) 中注入 mod 线索。
        /// 通过非泛型 IList 接口的 Add 方法添加。
        /// 这样当 InitializeItems 自然运行时，会将 mod 线索纳入 _displayableItemMaps。
        /// 返回 true 表示注入成功（或已存在），false 表示注入失败需要重试。
        /// </summary>
        private static bool TryInjectIntoLoadedDataItemMap(CluePage cluePage, IntPtr mapPtr)
        {
            try
            {
                // 通过非泛型 IList 接口操作，避免 IL2CPP 泛型实例化问题
                var listObj = new Il2CppSystem.Object(mapPtr);
                var ilist = listObj.Cast<Il2CppSystem.Collections.IList>();
                var icoll = listObj.Cast<Il2CppSystem.Collections.ICollection>();
                int before = icoll.Count;

                // 去重检查：仅检查"纯新"mod ID（非覆写 ID），避免被原版同名条目误判
                // 注意：不可使用 Cast<VersionedItem<ClueDataItem>>()，因为 IL2CPP 运行时
                // 类为 VersionedItem<__Il2CppFullySharedGenericType>，Cast 会抛出类型不匹配异常。
                // 改用 Il2CppFieldHelper 直接读取 _id 字段。
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
                            if (IsModClue(itemId) && !modClueOverrideIds.Contains(itemId))
                            {
                                alreadyHasMod = true;
                                break;
                            }
                        }
                    }
                }

                if (alreadyHasMod)
                {
                    ClueLogDebug("_loadedDataItemMap already contains mod clues, skipping injection.");
                    return true;
                }

                // 移除覆写 ID 对应的原版条目（倒序移除，保持索引有效）
                if (modClueOverrideIds.Count > 0)
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
                                if (modClueOverrideIds.Contains(itemId))
                                    toRemoveOverride.Add(i);
                            }
                        }
                    }
                    for (int k = toRemoveOverride.Count - 1; k >= 0; k--)
                        ilist.RemoveAt(toRemoveOverride[k]);
                    if (toRemoveOverride.Count > 0)
                        ClueLogInfo($"Removed {toRemoveOverride.Count} vanilla entries from _loadedDataItemMap for override clue IDs.");
                }

                foreach (var modClue in allModClues)
                {
                    try
                    {
                        var nameArray = modClue.Name.ToIl2CppArray();
                        var descArray = modClue.Description.ToIl2CppArray();
                        var clueDataItem = new ClueDataItem(nameArray, descArray);
                        var versionedItem = new VersionedItem<ClueDataItem>(
                            modClue.Id, modClue.Version, clueDataItem);

                        ilist.Add(versionedItem.Cast<Il2CppSystem.Object>());
                        ClueLogDebug($"Injected into _loadedDataItemMap: {modClue.Id}");
                    }
                    catch (Exception ex)
                    {
                        ClueLogError($"Failed to inject '{modClue.Id}' into _loadedDataItemMap: {ex}");
                    }
                }

                ClueLogInfo($"_loadedDataItemMap: {before} → {icoll.Count}");
                return true;
            }
            catch (Exception ex)
            {
                ClueLogError($"TryInjectIntoLoadedDataItemMap failed: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 向 CluePage._itemIds (string[]) 追加 mod 线索 ID。
        /// _itemIds 由 LoadDataAsync/InitializeAsync 创建，包含所有已知 item ID。
        /// vanilla UpdateVersion 检查 _itemIds.Contains(id) 后才调用 _state.SetVersion。
        /// 追加 mod ID 后，@update WitchBook.Clue modClueId version 就能被原版代码正确处理。
        /// </summary>
        private static void TryAppendModClueIds(CluePage cluePage)
        {
            try
            {
                IntPtr itemIdsPtr = Il2CppFieldHelper.GetReferenceField(cluePage, "_itemIds");
                if (itemIdsPtr == IntPtr.Zero)
                {
                    ClueLogDebug("_itemIds is null, cannot append mod clue IDs.");
                    return;
                }

                var oldIds = new Il2CppStringArray(itemIdsPtr);

                // 仅追加"纯新"mod ID（覆写 ID 已存在于原版 _itemIds 中，无需重复）
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
                        // 跳过无效的 IL2CPP 字符串（指针损坏或未初始化）
                    }
                }

                var idsToAppend = new List<string>();
                foreach (var id in modClueIds)
                    if (!existingIdSet.Contains(id))
                        idsToAppend.Add(id);

                if (idsToAppend.Count == 0)
                {
                    ClueLogDebug("_itemIds: 无需追加（所有 mod ID 已存在于 _itemIds 中）。");
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

                Il2CppFieldHelper.SetReferenceField(cluePage, "_itemIds", newIds.Pointer);
                ClueLogInfo($"_itemIds: {oldIds.Length} → {newLen} (+{idsToAppend.Count} 纯新 ID)");
            }
            catch (Exception ex)
            {
                ClueLogError($"TryAppendModClueIds failed: {ex}");
            }
        }

        // ====================================================================
        // 直接状态设置（绕过 _itemIds.Contains 检查）
        // ====================================================================

        /// <summary>
        /// 直接在 CluePage._state (VersionedState) 上设置 mod 线索版本。
        /// 解决 Bug：@update 在 WitchBook 未打开时触发，此时 _itemIds 尚未加载，
        /// vanilla UpdateVersion 中 _itemIds.Contains(id) 返回 false，无法设置状态。
        /// 此方法直接访问 _state 字段（readonly，构造函数中创建，始终可用），
        /// 调用 VersionedState.SetVersion(id, version)。
        /// 若 CluePage 尚不存在，将更新暂存到 pendingModClueUpdates。
        /// </summary>
        public static void DirectSetModClueState(string id, int version)
        {
            // 始终记录到持久状态表，保证 InitializePages 时可重新应用
            allModClueStates[id] = version;

            try
            {
                var pages = Resources.FindObjectsOfTypeAll<CluePage>();
                if (pages == null || pages.Length == 0)
                {
                    pendingModClueUpdates[id] = version;
                    ClueLogDebug($"CluePage not found, queued pending update: {id} v{version}");
                    return;
                }

                IntPtr statePtr = Il2CppFieldHelper.GetReferenceField(pages[0], "_state");
                if (statePtr == IntPtr.Zero)
                {
                    pendingModClueUpdates[id] = version;
                    ClueLogWarning($"CluePage._state is null, queued pending update: {id} v{version}");
                    return;
                }

                var state = new VersionedState(statePtr);
                state.SetVersion(id, version);
                ClueLogDebug($"Directly set mod clue state: {id} v{version}");
            }
            catch (Exception ex)
            {
                pendingModClueUpdates[id] = version;
                ClueLogError($"DirectSetModClueState failed for {id}: {ex}");
            }
        }

        /// <summary>
        /// 应用所有待处理的 mod 线索状态更新。
        /// 在 WitchBook 打开（BeginToPresent）时调用，此时 CluePage 一定存在。
        /// </summary>
        public static void ApplyPendingModClueUpdates()
        {
            if (pendingModClueUpdates.Count == 0) return;

            try
            {
                var pages = Resources.FindObjectsOfTypeAll<CluePage>();
                if (pages == null || pages.Length == 0)
                {
                    ClueLogWarning($"ApplyPendingModClueUpdates: CluePage still not found, {pendingModClueUpdates.Count} updates remain pending.");
                    return;
                }

                IntPtr statePtr = Il2CppFieldHelper.GetReferenceField(pages[0], "_state");
                if (statePtr == IntPtr.Zero)
                {
                    ClueLogWarning("ApplyPendingModClueUpdates: _state is null.");
                    return;
                }

                var state = new VersionedState(statePtr);
                foreach (var kvp in pendingModClueUpdates)
                {
                    state.SetVersion(kvp.Key, kvp.Value);
                    ClueLogDebug($"Applied pending mod clue state: {kvp.Key} v{kvp.Value}");
                }

                pendingModClueUpdates.Clear();
            }
            catch (Exception ex)
            {
                ClueLogError($"ApplyPendingModClueUpdates failed: {ex}");
            }
        }

        /// <summary>
        /// 确保所有已记录的 mod 线索状态都存在于 _state 中。
        /// 在 InitializePages 前调用，防止 DeserializeState 或其他机制覆盖 mod 状态。
        /// 同时重置 injectedDataItemMapPtr 以强制重新注入 _loadedDataItemMap。
        /// </summary>
        public static void EnsureAllModClueStatesAndForceReinject()
        {
            // 始终重置注入缓存——即使没有 mod 状态需要应用，
            // _loadedDataItemMap 可能已被 InitializeAsync/LoadDataAsync 重建，需要重新注入 mod 条目
            injectedDataItemMapPtr = IntPtr.Zero;

            if (allModClueStates.Count == 0 && pendingModClueUpdates.Count == 0) return;

            try
            {

                var pages = Resources.FindObjectsOfTypeAll<CluePage>();
                if (pages == null || pages.Length == 0)
                {
                    ClueLogDebug("EnsureAllModClueStates: CluePage not found.");
                    return;
                }

                IntPtr statePtr = Il2CppFieldHelper.GetReferenceField(pages[0], "_state");
                if (statePtr == IntPtr.Zero)
                {
                    ClueLogDebug("EnsureAllModClueStates: _state is null.");
                    return;
                }

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
                foreach (var kvp in allModClueStates)
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
                    allModClueStates[kvp.Key] = kvp.Value;

                // pendingModClueUpdates 是当前脚本刚发出的 @update，始终应用
                foreach (var kvp in pendingModClueUpdates)
                {
                    state.SetVersion(kvp.Key, kvp.Value);
                    allModClueStates[kvp.Key] = kvp.Value;
                }
                pendingModClueUpdates.Clear();

                ClueLogInfo($"EnsureAllModClueStates: synced {allModClueStates.Count} mod clue states.");
            }
            catch (Exception ex)
            {
                ClueLogError($"EnsureAllModClueStates failed: {ex}");
            }
        }

        /// <summary>获取 mod 线索数据（供 Harmony hook 使用）</summary>
        public static ModItem.ModClue GetModClueData(string id)
        {
            foreach (var mc in allModClues)
            {
                if (mc.Id == id) return mc;
            }
            return null;
        }

        /// <summary>获取所有已记录的 mod 线索状态（供 GetActiveEvidences postfix 使用）</summary>
        public static Dictionary<string, int> GetAllModStates() => allModClueStates;

        /// <summary>获取 mod 线索指定版本的本地化文本。优先精确匹配版本，回退到任意版本。</summary>
        public static (string name, string description) GetModClueLocalizedText(string clueId, int version)
        {
            // 精确匹配 (id, version)
            foreach (var mc in allModClues)
            {
                if (mc.Id == clueId && mc.Version == version)
                    return (mc.Name?.Resolve(clueId), mc.Description?.Resolve(""));
            }
            // 回退：任意版本
            foreach (var mc in allModClues)
            {
                if (mc.Id == clueId)
                    return (mc.Name?.Resolve(clueId), mc.Description?.Resolve(""));
            }
            return (clueId, "");
        }

        // ====================================================================
        // Addressables 纹理缓存注册
        // ====================================================================

        /// <summary>确保 mod 纹理已注册到 AddressablesManager._loadedAssets。</summary>
        public static void EnsureTexturesRegistered()
        {
            ModTextureHelper.EnsureRegisteredInAddressables<CluePage>(ptr =>
                ModTextureHelper.RegisterTexturesInManager(
                    ptr, modClueIds, BuildClueTextureAddress, LoadModClueTexture, null, "Clue"));
        }

        // ====================================================================
        // 纹理加载
        // ====================================================================

        /// <summary>从本地文件加载 mod 线索纹理（带缓存）</summary>
        public static Texture2D LoadModClueTexture(string clueId)
            => ModTextureHelper.LoadTexture(clueId, "ModClue_", modClueTexturePathMap, modClueTextureCache);

        // ====================================================================
        // 验证日志
        // ====================================================================

        /// <summary>将 ClueData 完整内容写入日志文件</summary>
        private static void DumpClueDataToFile(ClueData clueData)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"=== ClueData Dump ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
                sb.AppendLine($"Total items: {clueData._items.Count}");
                sb.AppendLine();

                for (int i = 0; i < clueData._items.Count; i++)
                {
                    var vi = clueData._items[i];
                    string id = vi.Id ?? "(null)";
                    bool isMod = IsModClue(id);
                    sb.AppendLine($"  [{i}] Id={id}, IsMod={isMod}");
                }

                string dumpPath = Path.Combine(
                    Path.GetDirectoryName(Application.dataPath)!,
                    "ManosabaMod_ClueData_Dump.txt");
                File.WriteAllText(dumpPath, sb.ToString());
                ClueLogInfo($"ClueData dump written to: {dumpPath}");
            }
            catch (Exception ex)
            {
                ClueLogWarning($"Failed to dump ClueData: {ex.Message}");
            }
        }

        /// <summary>输出 CluePage 显示状态到日志</summary>
        private static void DumpCluePageState(CluePage cluePage)
        {
            try
            {
                // 读取 _itemIds
                IntPtr itemIdsPtr = Il2CppFieldHelper.GetReferenceField(cluePage, "_itemIds");
                if (itemIdsPtr != IntPtr.Zero)
                {
                    var arr = new Il2CppStringArray(itemIdsPtr);
                    ClueLogDebug($"CluePage _itemIds count: {arr.Length}");
                    for (int i = 0; i < arr.Length && i < 30; i++)
                    {
                        string id = arr[i] ?? "(null)";
                        bool isMod = IsModClue(id);
                        ClueLogDebug($"  _itemIds[{i}] = {id}, IsMod={isMod}");
                    }
                    if (arr.Length > 30)
                        ClueLogDebug($"  ... ({arr.Length - 30} more)");
                }
                else
                {
                    ClueLogWarning("CluePage _itemIds is null.");
                }
            }
            catch (Exception ex)
            {
                ClueLogWarning($"DumpCluePageState error: {ex.Message}");
            }
        }
    }

    // ========================================================================
    // 数据注入 + 状态预设 Patch
    // ========================================================================

    /// <summary>
    /// 注入时机：
    /// UpdateVersion 前缀：对 mod 线索直接设置 _state，对原版放行。
    /// BeginToPresent 前缀：注入 ClueData + CluePage + 纹理 + 暂存状态。
    /// InitializePages 前缀：强制重新注入所有 mod 数据和状态。
    /// </summary>
    [HarmonyPatch]
    static class ClueDataInjection_Patch
    {
        [HarmonyPatch(typeof(WitchBookScreen), nameof(WitchBookScreen.UpdateVersion))]
        [HarmonyPrefix]
        static void WitchBookScreen_UpdateVersion_Prefix(WitchBookCategory category, string id, int version)
        {
            try
            {
                // 确保 mod 数据已加载（@update 可能在 WitchBook 打开前执行）
                ModResourceLoader.EnsureModDataLoaded();

                // 始终尝试注入 ClueData（供 LoadDataAsync 使用）
                ModClueLoader.TryInjectClueData();

                // 对 mod 线索：直接设置状态，绕过 _itemIds.Contains 检查
                if (ModClueLoader.IsModClue(id))
                {
                    ModClueLoader.DirectSetModClueState(id, version);
                    ModClueLoader.ClueLogDebug($"UpdateVersion intercepted mod clue: {id} v{version}");
                }

                // 尝试 CluePage 注入（如果数据已加载）
                ModClueLoader.TryInjectCluePageStateAndRebuild();
            }
            catch (Exception ex)
            {
                ModClueLoader.ClueLogError($"UpdateVersion prefix: {ex}");
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

                // WitchBook 打开时，确保数据注入
                ModClueLoader.TryInjectClueData();
                ModClueLoader.TryInjectCluePageStateAndRebuild();

                // 应用之前 @update 时暂存的状态更新
                ModClueLoader.ApplyPendingModClueUpdates();
            }
            catch (Exception ex)
            {
                ModClueLoader.ClueLogError($"BeginToPresent prefix: {ex}");
            }
        }

        /// <summary>
        /// InitializePages 前缀 — 最关键的注入时机。
        /// 
        /// InitializePages 在 BeginToPresent 末尾调用，遍历所有 page
        /// 并调用 page.InitializeItems()。InitializeItems 从 _loadedDataItemMap
        /// + _state 构建 _displayableItemMaps 用于 UI 显示。
        /// 
        /// 此前缀确保在 InitializeItems 运行前：
        /// 1. mod 数据存在于 _loadedDataItemMap（强制重新注入）
        /// 2. mod 状态存在于 _state（重新应用所有已记录状态）
        /// 3. mod ID 存在于 _itemIds
        /// 
        /// 通过重置 injectedDataItemMapPtr 缓存实现强制重新注入。
        /// 这解决了多种边缘情况：
        /// - DeserializeState 覆盖了 _state
        /// - _loadedDataItemMap 被 LoadDataAsync 重新创建
        /// - WitchBook 在 @update 之前从未打开过
        /// </summary>
        [HarmonyPatch(typeof(WitchBookScreen), nameof(WitchBookScreen.InitializePages))]
        [HarmonyPrefix]
        static void WitchBookScreen_InitializePages_Prefix()
        {
            try
            {
                ModClueLoader.TryInjectClueData();
                ModClueLoader.EnsureAllModClueStatesAndForceReinject();
                ModClueLoader.TryInjectCluePageStateAndRebuild();
                ModClueLoader.EnsureTexturesRegistered();
            }
            catch (Exception ex)
            {
                ModClueLoader.ClueLogError($"InitializePages prefix (clue): {ex}");
            }
        }

    }

    // ========================================================================
    // 缩略图加载 Patch
    // ========================================================================

    /// <summary>
    /// Hook WitchBookItemThumbnail.Setup(string address)
    /// 
    /// 通过 modClueAddressToIdMap 反查纹理地址是否对应 mod 线索。
    /// 若纹理已在 Addressables 缓存中（由 TryRegisterTexturesInAddressables 注册），
    /// 则让原版 Setup 正常运行。仅在缓存未命中时手动加载本地纹理作为回退。
    /// </summary>
    [HarmonyPatch]
    static class ThumbnailSetup_Patch
    {
        [HarmonyPatch(typeof(WitchBookItemThumbnail), nameof(WitchBookItemThumbnail.Setup))]
        [HarmonyPrefix]
        static bool Prefix(WitchBookItemThumbnail __instance, string address)
        {
            try
            {
                // 通过地址→ID反查映射检测 mod 线索
                string clueId = ModClueLoader.TryGetModClueIdFromAddress(address);
                if (clueId == null)
                    return true; // 非 mod 线索，走原版

                var tex = ModClueLoader.LoadModClueTexture(clueId);
                if (tex == null)
                {
                    ModClueLoader.ClueLogWarning(
                        $"No texture for mod clue '{clueId}', original Setup may fail.");
                    return true;
                }

                // 直接设置纹理，跳过异步 Addressables 加载
                __instance._rawImage.texture = tex;
                __instance._canvasGroup.alpha = 1f;

                ModClueLoader.ClueLogDebug($"Thumbnail set for mod clue: {clueId} (addr={address})");
                return false; // 跳过原版 Setup
            }
            catch (Exception ex)
            {
                ModClueLoader.ClueLogError($"ThumbnailSetup prefix error: {ex}");
                return true;
            }
        }
    }

    // ========================================================================
    // RefreshPageContent Hook — 处理 mod 线索的详情页显示
    // ========================================================================

    /// <summary>
    /// Hook CluePage.RefreshPageContent(VersionedItem&lt;ClueDataItem&gt; map)
    /// 
    /// 原版方法通过 _localizedTextData[map.IdVersionPair] 获取本地化文字。
    /// 由于 IdVersionPair 未重写 GetHashCode()（使用引用相等），
    /// 注入的 mod VersionedItem 的 IdVersionPair 不在字典中 → KeyNotFoundException。
    /// 
    /// 对 mod 线索：直接从 ModClue 数据设置 _subjectLabel / _descriptionLabel / _thumbnail。
    /// 对原版线索：放行原方法。
    /// </summary>
    [HarmonyPatch]
    static class RefreshPageContent_Patch
    {
        [HarmonyPatch(typeof(CluePage), nameof(CluePage.RefreshPageContent))]
        [HarmonyPrefix]
        static bool Prefix(CluePage __instance, VersionedItem<ClueDataItem> map)
        {
            try
            {
                string id = map?.Id;
                if (id == null || !ModClueLoader.IsModClue(id))
                    return true; // 原版线索，放行

                // 从 VersionedItem 的 _version 字段读取实际版本（由 InitializeItems 从 _state 匹配得到）
                int version = Il2CppFieldHelper.GetIntField(map, "_version", 1);

                var (name, description) = ModClueLoader.GetModClueLocalizedText(id, version);

                // 设置标题标签
                IntPtr subjectPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_subjectLabel");
                if (subjectPtr != IntPtr.Zero)
                {
                    var subjectLabel = new WitchBookItemSubjectLabel(subjectPtr);
                    subjectLabel.SetText(name);
                }

                // 设置描述文本
                IntPtr descPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_descriptionLabel");
                if (descPtr != IntPtr.Zero)
                {
                    var descLabel = new TMP_Text(descPtr);
                    descLabel.text = description;
                }

                // 设置缩略图
                IntPtr thumbPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_thumbnail");
                if (thumbPtr != IntPtr.Zero)
                {
                    var thumbnail = new WitchBookItemThumbnail(thumbPtr);
                    string address = ModClueLoader.BuildClueTextureAddress(id);
                    thumbnail.Setup(address);
                }

                ModClueLoader.ClueLogDebug($"RefreshPageContent handled for mod clue: {id}");
                return false; // 跳过原版（避免 _localizedTextData KeyNotFoundException）
            }
            catch (Exception ex)
            {
                ModClueLoader.ClueLogError($"RefreshPageContent_Patch error: {ex}");
                return true; // fallback 到原版
            }
        }
    }

    // ========================================================================
    // SetupItemButton Hook — 处理 mod 线索按钮的文字设置
    // ========================================================================

    /// <summary>
    /// Hook CluePage.SetupItemButton(WitchBookItemButton button, VersionedItem&lt;ClueDataItem&gt; map)
    /// 
    /// CluePage 的 SetupItemButton 可能通过 WitchBookDataHelper.BuildClueTextureAddress
    /// 生成地址后调用 button.SetupWithAddressableTexture。由于我们已预注册纹理到
    /// Addressables 缓存，原版方法理论上可行。此 hook 作为防御措施以防原版访问
    /// _localizedTextData 导致 KeyNotFoundException。
    /// </summary>
    [HarmonyPatch]
    static class SetupItemButton_Patch
    {
        [HarmonyPatch(typeof(CluePage), nameof(CluePage.SetupItemButton))]
        [HarmonyPrefix]
        static bool Prefix(CluePage __instance, WitchBookItemButton button, VersionedItem<ClueDataItem> map)
        {
            try
            {
                string id = map?.Id;
                if (id == null || !ModClueLoader.IsModClue(id))
                    return true; // 原版线索，放行

                // 使用 Addressables 纹理地址设置按钮（纹理已预注册到缓存）
                string address = ModClueLoader.BuildClueTextureAddress(id);
                button.SetupWithAddressableTexture(address);

                ModClueLoader.ClueLogDebug($"SetupItemButton handled for mod clue: {id} (addr={address})");
                return false; // 跳过原版
            }
            catch (Exception ex)
            {
                ModClueLoader.ClueLogError($"SetupItemButton_Patch error: {ex}");
                return true; // fallback 到原版
            }
        }
    }

    // ========================================================================
    // SpawnableClue Hook — spawn 前确保 mod 纹理已注册
    // ========================================================================

    /// <summary>
    /// Hook SpawnableClue.SetSpawnParameters (IReadOnlyList&lt;string&gt;, bool)
    /// 
    /// 修复 Bug 3：如果在打开魔女图鉴之前执行 @spawn ClueItem，
    /// SpawnableClue.AwaitSpawn 会从 AddressablesManager 加载纹理，而此时
    /// mod 纹理尚未注册到 _loadedAssets 缓存中。
    /// 
    /// SpawnableClue 通过 ServiceLocator.Get&lt;IAddressablesManager&gt;() 获取全局
    /// AddressablesManager 实例（与 CluePage._addressableAssetLoader 为同一对象）。
    /// 在 SetSpawnParameters 的 Postfix 中检测 mod 线索 ID，并通过
    /// EnsureTexturesRegistered 确保纹理已预注册。
    /// 
    /// 时序：SetSpawnParameters → [本 Postfix] → AwaitSpawn → GetOrLoadAddressableAsset
    /// 由于 Postfix 在 AwaitSpawn 之前同步执行，纹理注册一定先于纹理加载。
    /// </summary>
    [HarmonyPatch]
    static class SpawnableClueSpawn_Patch
    {
        [HarmonyPatch(typeof(SpawnableClue), nameof(SpawnableClue.SetSpawnParameters))]
        [HarmonyPostfix]
        static void Postfix(SpawnableClue __instance)
        {
            try
            {
                // 读取 _clueId 字段（由 SetSpawnParameters 从 parameters[0] 设置）
                IntPtr strPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_clueId");
                if (strPtr == IntPtr.Zero) return;

                string clueId = IL2CPP.Il2CppStringToManaged(strPtr);
                if (string.IsNullOrEmpty(clueId) || !ModClueLoader.IsModClue(clueId)) return;

                ModClueLoader.ClueLogDebug($"SpawnableClue detected mod clue: {clueId}, ensuring textures registered.");
                ModClueLoader.EnsureTexturesRegistered();
            }
            catch (Exception ex)
            {
                ModClueLoader.ClueLogError($"SpawnableClueSpawn_Patch error: {ex}");
            }
        }
    }
}