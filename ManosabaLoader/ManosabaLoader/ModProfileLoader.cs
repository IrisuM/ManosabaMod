using System;
using System.Collections.Generic;
using System.IO;

using GigaCreation.Essentials.Localization;

using HarmonyLib;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

using ManosabaLoader.ModManager;
using ManosabaLoader.Utils;

using Naninovel;
using Naninovel.UI;

using System.Text;

using TMPro;

using UnityEngine;

using WitchTrials.Models;
using WitchTrials.Views;

namespace ManosabaLoader
{
    /// <summary>
    /// Mod 角色简介（Profile）加载器。
    /// 策略与 ModClueLoader 一致（数据注入 + 状态直设 + UI hook + 纹理注册）。
    /// WitchBookCategory: Profile = 1
    /// </summary>
    public static class ModProfileLoader
    {
        public static Action<string> ProfileLogMessage;
        public static Action<string> ProfileLogInfo;
        public static Action<string> ProfileLogDebug;
        public static Action<string> ProfileLogWarning;
        public static Action<string> ProfileLogError;
        // ====================================================================
        // 角色简介数据
        // ====================================================================

        /// <summary>注册的 mod 角色简介列表。每个 ModVersionedGroup（有 Id + Items）展开为每个版本一条。</summary>
        private static readonly List<ProfileEntry> allModProfiles = new();
        private static readonly HashSet<string> modProfileIds = new();
        /// <summary>与原版内容共用相同 ID 的覆写简介集合（注入时动态确定）</summary>
        private static readonly HashSet<string> modProfileOverrideIds = new();
        /// <summary>mod 新建角色映射（来自 Characters[]），用于自定义显示渲染。</summary>
        private static readonly Dictionary<string, ModItem.ModCharacter> modCharacterMap = new();

        // 结果缓存（BuildModAuthorLabel / GetModCharacterDisplayName 对同一角色反复调用）
        private static readonly Dictionary<string, string> authorLabelCache = new();
        private static readonly Dictionary<string, string> displayNameCache = new();

        // 纹理
        private static readonly Dictionary<string, string> modProfileTexturePathMap = new();
        private static readonly Dictionary<string, Texture2D> modProfileTextureCache = new();
        private static readonly Dictionary<string, string> modProfileAddressToIdMap = new();

        // 注入状态追踪
        private static IntPtr injectedProfileDataPtr = IntPtr.Zero;
        private static IntPtr injectedCharacterDataPtr = IntPtr.Zero;
        private static IntPtr injectedAuthorDataPtr = IntPtr.Zero;
        private static IntPtr injectedProfileDataItemMapPtr = IntPtr.Zero;
        private static readonly Dictionary<string, int> pendingModProfileUpdates = new();
        /// <summary>所有已通过 @update 设置过的 mod profile 状态。</summary>
        internal static readonly Dictionary<string, int> allModProfileStates = new();

        /// <summary>
        /// 展开后的单条简介数据（一个角色可能有多个版本）。
        /// 用于注入 ProfileData._items。
        /// </summary>
        private class ProfileEntry
        {
            public string CharacterId;
            public int Version;
            public ModItem.LocalizedString Description;
        }

        public static void Init(Harmony harmony)
        {
            // 始终注册 Harmony 补丁（注入数据按需延迟加载）
            harmony.PatchAll(typeof(ProfileInjection_Patch));
            harmony.PatchAll(typeof(ProfilePageRefreshContent_Patch));
            harmony.PatchAll(typeof(ProfilePageSetupItemButton_Patch));
            harmony.PatchAll(typeof(LogAuthorFormat_Patch));
            harmony.PatchAll(typeof(TextPrinterAuthorFormat_Patch));

            // 语言切换时清理依赖 locale 的缓存
            Utils.LocaleHelper.OnLocaleChanged += () =>
            {
                authorLabelCache.Clear();
                displayNameCache.Clear();
            };

            ProfileLogInfo("ModProfileLoader patches applied.");
        }

        /// <summary>加载指定 mod 的角色简介数据。</summary>
        public static void LoadModData(string modKey, string modPath, ModItem modItem)
        {
            RegisterModCharacters(modKey, modItem);
            RegisterModProfiles(modKey, modPath, modItem);
            if (allModProfiles.Count > 0)
                ProfileLogMessage($"Loaded {allModProfiles.Count} profile entries ({modProfileIds.Count} unique IDs) for mod: {modKey}");
        }

        /// <summary>清除所有 mod 角色简介数据，释放纹理缓存。</summary>
        public static void ClearModData()
        {
            allModProfiles.Clear();
            modProfileIds.Clear();
            modProfileOverrideIds.Clear();
            modCharacterMap.Clear();
            authorLabelCache.Clear();
            displayNameCache.Clear();
            modProfileTexturePathMap.Clear();
            ModTextureHelper.DestroyAndClearCache(modProfileTextureCache);
            modProfileAddressToIdMap.Clear();
            injectedProfileDataPtr = IntPtr.Zero;
            injectedCharacterDataPtr = IntPtr.Zero;
            injectedAuthorDataPtr = IntPtr.Zero;
            injectedProfileDataItemMapPtr = IntPtr.Zero;
            pendingModProfileUpdates.Clear();
            allModProfileStates.Clear();
            ProfileLogInfo("ProfileLoader data cleared.");
        }

        // ====================================================================
        // 注册
        // ====================================================================

        /// <summary>构建 modCharacterMap（仅 mod 新建角色，来自 Characters[]）。</summary>
        private static void RegisterModCharacters(string modPrefix, ModItem modItem)
        {
            if (modItem?.Description?.Characters == null) return;

            foreach (var character in modItem.Description.Characters)
            {
                if (string.IsNullOrEmpty(character.Id)) continue;
                if (modCharacterMap.ContainsKey(character.Id))
                {
                    ProfileLogDebug($"[{modPrefix}] Skipping duplicate character ID in modCharacterMap: {character.Id}");
                    continue;
                }
                modCharacterMap[character.Id] = character;
            }
        }

        /// <summary>注册 mod 简介数据（来自解耦后的 Profiles[]）。</summary>
        private static void RegisterModProfiles(string modPrefix, string modPath, ModItem modItem)
        {
            if (modItem?.Description?.Profiles == null) return;

            foreach (var group in modItem.Description.Profiles)
            {
                if (string.IsNullOrEmpty(group.Id)) continue;
                if (group.Items == null || group.Items.Length == 0) continue;

                if (modProfileIds.Contains(group.Id))
                {
                    ProfileLogDebug($"[{modPrefix}] Skipping duplicate profile group ID: {group.Id}");
                    continue;
                }

                modProfileIds.Add(group.Id);

                var seenVersions = new HashSet<int>();
                int maxVersion = 0;
                ModItem.ModProfile highestProfile = null;

                foreach (var profile in group.Items)
                {
                    if (seenVersions.Contains(profile.Version)) continue;
                    seenVersions.Add(profile.Version);
                    allModProfiles.Add(new ProfileEntry
                    {
                        CharacterId = group.Id,
                        Version = profile.Version,
                        Description = profile.Description
                    });
                    if (profile.Version > maxVersion)
                    {
                        maxVersion = profile.Version;
                        highestProfile = profile;
                    }
                }

                // 版本补齐：填充到 maxVersion + 4，确保 @update 升级不丢失
                if (highestProfile != null)
                {
                    const int VersionPadding = 4;
                    int paddedMax = maxVersion + VersionPadding;
                    for (int v = 1; v <= paddedMax; v++)
                    {
                        if (seenVersions.Contains(v)) continue;
                        allModProfiles.Add(new ProfileEntry
                        {
                            CharacterId = group.Id,
                            Version = v,
                            Description = highestProfile.Description
                        });
                    }
                }

                // 注册纹理
                RegisterProfileTexture(modPrefix, modPath, group.Id);

                ProfileLogDebug($"[{modPrefix}] Registered profile group: Id={group.Id}, Versions={group.Items.Length}");
            }
        }

        private static void RegisterProfileTexture(string modPrefix, string modPath, string characterId)
        {
            var texDir = Path.Combine(modPath, "WitchBook", "Profiles");
            string[] extensions = [".png", ".jpg", ".jpeg"];
            foreach (var ext in extensions)
            {
                var texPath = Path.Combine(texDir, characterId + ext);
                if (File.Exists(texPath))
                {
                    modProfileTexturePathMap[characterId] = texPath;
                    ProfileLogDebug($"[{modPrefix}] Registered profile texture: {characterId} -> {texPath}");
                    return;
                }
            }

            ProfileLogWarning($"[{modPrefix}] No texture found for profile '{characterId}'");
        }

        // ====================================================================
        // 查询
        // ====================================================================

        /// <summary>该 ID 是否为 mod 管理的 profile（无论是新角色还是覆写原版）。</summary>
        public static bool IsModProfile(string id) => modProfileIds.Contains(id);

        /// <summary>该 ID 是否为 mod 新建的角色（有自定义显示数据）。
        /// 覆写原版角色的 profile 不在此列——它们使用原版渲染路径。</summary>
        public static bool IsModCharacter(string id) => modCharacterMap.ContainsKey(id);

        public static bool HasModProfiles => allModProfiles.Count > 0;

        /// <summary>获取所有已记录的 mod profile 状态（供 GetActiveEvidences postfix 使用）</summary>
        public static Dictionary<string, int> GetAllModStates() => allModProfileStates;

        /// <summary>
        /// 构建 mod profile 的 Addressables 纹理地址。
        /// 使用与 WitchBookDataHelper.BuildProfileTextureAddress 相同的格式。
        /// </summary>
        public static string BuildProfileTextureAddress(string characterId)
        {
            return WitchBookDataHelper.BuildProfileTextureAddress(characterId);
        }

        // ====================================================================
        // ProfileData 注入
        // ====================================================================

        /// <summary>向 ProfileData ScriptableObject 注入 mod 简介数据。</summary>
        public static void TryInjectProfileData()
        {
            if (!HasModProfiles) return;
            try
            {
                var instances = Resources.FindObjectsOfTypeAll<ProfileData>();
                if (instances == null || instances.Length == 0)
                {
                    ProfileLogDebug("ProfileData not yet loaded.");
                    return;
                }

                var profileData = instances[0];
                if (profileData.Pointer == injectedProfileDataPtr) return;

                var itemsList = profileData._items;
                ProfileLogDebug($"Found ProfileData 0x{profileData.Pointer:X}, items: {itemsList.Count}");

                // 扫描现有 _items，确定哪些 mod ID 与原版条目冲突（覆写 ID）
                modProfileOverrideIds.Clear();
                var vanillaIdSet = new HashSet<string>();
                for (int i = 0; i < itemsList.Count; i++)
                {
                    string vid = itemsList[i].Id;
                    if (vid != null) vanillaIdSet.Add(vid);
                }
                foreach (var id in modProfileIds)
                    if (vanillaIdSet.Contains(id))
                        modProfileOverrideIds.Add(id);
                if (modProfileOverrideIds.Count > 0)
                    ProfileLogInfo($"Detected {modProfileOverrideIds.Count} vanilla override profile IDs: {string.Join(", ", modProfileOverrideIds)}");

                // 去重检查：仅检查"纯新" mod ID
                bool alreadyHasMod = false;
                for (int i = 0; i < itemsList.Count; i++)
                {
                    string vid = itemsList[i].Id;
                    if (IsModProfile(vid) && !modProfileOverrideIds.Contains(vid))
                    {
                        alreadyHasMod = true;
                        break;
                    }
                }

                if (!alreadyHasMod)
                {
                    // 步骤 1：移除覆写 ID 对应的原版条目
                    if (modProfileOverrideIds.Count > 0)
                    {
                        var toRemove = new List<int>();
                        for (int i = 0; i < itemsList.Count; i++)
                            if (modProfileOverrideIds.Contains(itemsList[i].Id))
                                toRemove.Add(i);
                        for (int k = toRemove.Count - 1; k >= 0; k--)
                            itemsList.RemoveAt(toRemove[k]);
                        ProfileLogInfo($"Removed {toRemove.Count} vanilla ProfileData entries for override IDs.");
                    }

                    // 步骤 2：注入所有 mod 简介（覆写 + 纯新）
                    int count = 0;
                    foreach (var entry in allModProfiles)
                    {
                        try
                        {
                            var descArray = entry.Description?.ToIl2CppArray()
                                ?? new Il2CppReferenceArray<LocalizedText>(0);
                            var profileDataItem = new ProfileDataItem(descArray);
                            var versionedItem = new VersionedItem<ProfileDataItem>(
                                entry.CharacterId, entry.Version, profileDataItem);
                            itemsList.Add(versionedItem);
                            count++;
                        }
                        catch (Exception ex)
                        {
                            ProfileLogError($"Failed to create VersionedItem for profile '{entry.CharacterId}' v{entry.Version}: {ex}");
                        }
                    }
                    ProfileLogMessage($"Injected {count} mod profiles into ProfileData (total: {itemsList.Count}).");
                }
                else
                {
                    ProfileLogDebug("ProfileData already contains mod profiles.");
                }

                injectedProfileDataPtr = profileData.Pointer;
            }
            catch (Exception ex)
            {
                ProfileLogError($"TryInjectProfileData failed: {ex}");
            }
        }

        // ====================================================================
        // CharacterData 注入
        // ====================================================================

        /// <summary>向 CharacterData ScriptableObject 注入 mod 角色基本数据（仅 mod 新建角色）。</summary>
        public static void TryInjectCharacterData()
        {
            if (modCharacterMap.Count == 0) return;
            try
            {
                var instances = Resources.FindObjectsOfTypeAll<CharacterData>();
                if (instances == null || instances.Length == 0)
                {
                    ProfileLogDebug("CharacterData not yet loaded.");
                    return;
                }

                var charData = instances[0];
                if (charData.Pointer == injectedCharacterDataPtr) return;

                var itemsList = charData._items;
                ProfileLogDebug($"Found CharacterData 0x{charData.Pointer:X}, items: {itemsList.Count}");

                bool alreadyHasMod = false;
                for (int i = 0; i < itemsList.Count; i++)
                {
                    if (IsModCharacter(itemsList[i].Id))
                    {
                        alreadyHasMod = true;
                        break;
                    }
                }

                if (!alreadyHasMod)
                {
                    int count = 0;
                    foreach (var kvp in modCharacterMap)
                    {
                        try
                        {
                            var mc = kvp.Value;
                            var nameArray = mc.Name?.ToIl2CppArray()
                                ?? new Il2CppReferenceArray<LocalizedText>(0);
                            var familyNameArray = mc.FamilyName?.ToIl2CppArray()
                                ?? new Il2CppReferenceArray<LocalizedText>(0);

                            var charDataItem = new CharacterDataItem(
                                mc.Id,
                                nameArray,
                                familyNameArray,
                                mc.Age ?? "",
                                mc.Height ?? "",
                                mc.Weight ?? ""
                            );
                            itemsList.Add(charDataItem);
                            count++;
                        }
                        catch (Exception ex)
                        {
                            ProfileLogError($"Failed to create CharacterDataItem for '{kvp.Key}': {ex}");
                        }
                    }
                    ProfileLogMessage($"Injected {count} mod characters into CharacterData (total: {itemsList.Count}).");
                }
                else
                {
                    ProfileLogDebug("CharacterData already contains mod characters.");
                }

                injectedCharacterDataPtr = charData.Pointer;
            }
            catch (Exception ex)
            {
                ProfileLogError($"TryInjectCharacterData failed: {ex}");
            }
        }

        // ====================================================================
        // AuthorData 注入
        // ====================================================================

        /// <summary>
        /// 向 AuthorData ScriptableObject 注入 mod 角色的发言人名数据。
        ///
        /// AuthorText 始终根据 Name / FamilyName 自动生成，使用 %COLOR% 占位符。
        /// TryBuildAuthorText 在运行时通过 _authorData 字典找到模板后完成 %COLOR% 替换。
        ///
        /// ⚠ 时序限制：此注入修改 ScriptableObject._items，必须在 AuthorTextBuilder.LoadDataAsync
        ///   构建 _authorData 字典之前执行才有效。在 InitializePages 前缀中调用可满足此条件。
        /// </summary>
        /// <summary>向 AuthorData ScriptableObject 注入 mod 角色作者数据（仅 mod 新建角色）。</summary>
        public static void TryInjectAuthorData()
        {
            if (modCharacterMap.Count == 0) return;

            try
            {
                var instances = Resources.FindObjectsOfTypeAll<AuthorData>();
                if (instances == null || instances.Length == 0)
                {
                    ProfileLogDebug("AuthorData not yet loaded.");
                    return;
                }

                var authorData = instances[0];
                if (authorData.Pointer == injectedAuthorDataPtr) return;

                var itemsList = authorData.Items;
                var itemsCollection = itemsList.Cast<Il2CppSystem.Collections.Generic.ICollection<AuthorDataItem>>();
                int itemsCount = itemsCollection.Count;
                ProfileLogDebug($"Found AuthorData 0x{authorData.Pointer:X}, items: {itemsCount}");

                // 检查是否已包含 mod 角色数据
                bool alreadyHasMod = false;
                for (int i = 0; i < itemsCount; i++)
                {
                    if (IsModCharacter(itemsList[i].Id))
                    {
                        alreadyHasMod = true;
                        break;
                    }
                }

                if (!alreadyHasMod)
                {
                    IntPtr listPtr = Il2CppFieldHelper.GetReferenceField(authorData, "_items");
                    if (listPtr == IntPtr.Zero)
                    {
                        ProfileLogWarning("AuthorData._items is null.");
                        return;
                    }

                    var ilist = new Il2CppSystem.Object(listPtr).Cast<Il2CppSystem.Collections.IList>();
                    int count = 0;

                    foreach (var kvp in modCharacterMap)
                    {
                        try
                        {
                            var mc = kvp.Value;
                            var taggedTextArray = BuildAutoAuthorTextArray(mc);
                            var item = new AuthorDataItem(mc.Id, taggedTextArray);
                            ilist.Add(item.Cast<Il2CppSystem.Object>());
                            count++;
                            ProfileLogDebug($"Injected auto-generated AuthorDataItem: {mc.Id}");
                        }
                        catch (Exception ex)
                        {
                            ProfileLogError($"Failed to create AuthorDataItem for '{kvp.Key}': {ex}");
                        }
                    }

                    ProfileLogMessage($"Injected {count} auto-generated AuthorData entries.");
                }
                else
                {
                    ProfileLogDebug("AuthorData already contains mod entries.");
                }

                injectedAuthorDataPtr = authorData.Pointer;
            }
            catch (Exception ex)
            {
                ProfileLogError($"TryInjectAuthorData failed: {ex}");
            }
        }

        /// <summary>
        /// 根据 ModCharacter 的 Name / FamilyName 自动生成各语言的 AuthorText 模板。
        /// 使用 %COLOR% 占位符（TryBuildAuthorText 会在运行时替换为实际颜色值）。
        /// </summary>
        private static Il2CppReferenceArray<LocalizedText> BuildAutoAuthorTextArray(ModItem.ModCharacter mc)
        {
            var list = new System.Collections.Generic.List<LocalizedText>();

            // 为每个有数据的语言生成模板
            string jaFamily = mc.FamilyName?.Ja;
            string jaGiven = mc.Name?.Ja;
            if (!string.IsNullOrEmpty(jaFamily) || !string.IsNullOrEmpty(jaGiven))
            {
                string template = AuthorTaggedTextGenerator.BuildFullName(
                    jaFamily, jaGiven, "%COLOR%");
                list.Add(new LocalizedText(LocaleKind.Ja, template));
            }

            string zhFamily = mc.FamilyName?.ZhHans;
            string zhGiven = mc.Name?.ZhHans;
            if (!string.IsNullOrEmpty(zhFamily) || !string.IsNullOrEmpty(zhGiven))
            {
                string template = AuthorTaggedTextGenerator.BuildFullName(
                    zhFamily, zhGiven, "%COLOR%");
                list.Add(new LocalizedText(LocaleKind.ZhHans, template));
            }

            // 回退：至少有一个条目
            if (list.Count == 0)
            {
                string fallback = AuthorTaggedTextGenerator.BuildSimpleName(
                    mc.Id, "%COLOR%");
                list.Add(new LocalizedText(LocaleKind.ZhHans, fallback));
            }

            var arr = new Il2CppReferenceArray<LocalizedText>(list.Count);
            for (int i = 0; i < list.Count; i++)
                arr[i] = list[i];
            return arr;
        }

        // ====================================================================
        // ProfilePage 注入
        // ====================================================================

        public static void TryInjectProfilePageStateAndRebuild()
        {
            if (!HasModProfiles) return;
            try
            {
                var pages = Resources.FindObjectsOfTypeAll<ProfilePage>();
                if (pages == null || pages.Length == 0)
                {
                    ProfileLogDebug("ProfilePage not found yet.");
                    return;
                }

                var profilePage = pages[0];
                IntPtr mapPtr = Il2CppFieldHelper.GetReferenceField(profilePage, "_loadedDataItemMap");
                if (mapPtr == IntPtr.Zero)
                {
                    ProfileLogDebug("ProfilePage._loadedDataItemMap is null.");
                    return;
                }

                if (mapPtr == injectedProfileDataItemMapPtr) return;

                // 去重检查 + 注入 _loadedDataItemMap
                try
                {
                    var listObj = new Il2CppSystem.Object(mapPtr);
                    var ilist = listObj.Cast<Il2CppSystem.Collections.IList>();
                    var icoll = listObj.Cast<Il2CppSystem.Collections.ICollection>();
                    int before = icoll.Count;

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
                                if (modProfileIds.Contains(itemId) && !modProfileOverrideIds.Contains(itemId))
                                {
                                    alreadyHasMod = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (!alreadyHasMod)
                    {
                        // 移除覆写 ID 对应的原版条目
                        if (modProfileOverrideIds.Count > 0)
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
                                        if (modProfileOverrideIds.Contains(itemId))
                                            toRemoveOverride.Add(i);
                                    }
                                }
                            }
                            for (int k = toRemoveOverride.Count - 1; k >= 0; k--)
                                ilist.RemoveAt(toRemoveOverride[k]);
                            if (toRemoveOverride.Count > 0)
                                ProfileLogInfo($"Removed {toRemoveOverride.Count} vanilla entries from ProfilePage _loadedDataItemMap for override IDs.");
                        }

                        foreach (var entry in allModProfiles)
                        {
                            try
                            {
                                var descArray = entry.Description?.ToIl2CppArray()
                                    ?? new Il2CppReferenceArray<LocalizedText>(0);
                                var dataItem = new ProfileDataItem(descArray);
                                var vi = new VersionedItem<ProfileDataItem>(
                                    entry.CharacterId, entry.Version, dataItem);
                                ilist.Add(vi.Cast<Il2CppSystem.Object>());
                                ProfileLogDebug($"Injected profile into _loadedDataItemMap: {entry.CharacterId} v{entry.Version}");
                            }
                            catch (Exception ex)
                            {
                                ProfileLogError($"Failed to inject profile '{entry.CharacterId}': {ex}");
                            }
                        }
                        ProfileLogInfo($"ProfilePage _loadedDataItemMap: {before} → {icoll.Count}");
                    }
                    else
                    {
                        ProfileLogDebug("ProfilePage._loadedDataItemMap already contains mod profiles.");
                    }
                }
                catch (Exception ex)
                {
                    ProfileLogError($"Failed to inject into ProfilePage._loadedDataItemMap: {ex}");
                }

                // 追加 _itemIds
                TryAppendModProfileIds(profilePage);

                injectedProfileDataItemMapPtr = mapPtr;
                ProfileLogInfo("ProfilePage injection complete.");
            }
            catch (Exception ex)
            {
                ProfileLogError($"TryInjectProfilePageStateAndRebuild failed: {ex}");
            }
        }

        private static void TryAppendModProfileIds(ProfilePage page)
        {
            try
            {
                IntPtr itemIdsPtr = Il2CppFieldHelper.GetReferenceField(page, "_itemIds");
                if (itemIdsPtr == IntPtr.Zero)
                {
                    ProfileLogDebug("ProfilePage._itemIds is null.");
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
                foreach (var id in modProfileIds)
                    if (!existingIdSet.Contains(id))
                        idsToAppend.Add(id);

                if (idsToAppend.Count == 0)
                {
                    ProfileLogDebug("ProfilePage _itemIds: 无需追加（所有 mod ID 已存在）。");
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
                ProfileLogInfo($"ProfilePage _itemIds: {oldIds.Length} → {newLen} (+{idsToAppend.Count} 纯新 ID)");
            }
            catch (Exception ex)
            {
                ProfileLogError($"TryAppendModProfileIds failed: {ex}");
            }
        }

        // ====================================================================
        // 状态管理
        // ====================================================================

        public static void DirectSetModProfileState(string id, int version)
        {
            allModProfileStates[id] = version;
            try
            {
                var pages = Resources.FindObjectsOfTypeAll<ProfilePage>();
                if (pages == null || pages.Length == 0)
                {
                    pendingModProfileUpdates[id] = version;
                    ProfileLogDebug($"ProfilePage not found, queued pending: {id} v{version}");
                    return;
                }

                IntPtr statePtr = Il2CppFieldHelper.GetReferenceField(pages[0], "_state");
                if (statePtr == IntPtr.Zero)
                {
                    pendingModProfileUpdates[id] = version;
                    ProfileLogWarning($"ProfilePage._state is null, queued pending: {id} v{version}");
                    return;
                }

                var state = new VersionedState(statePtr);
                state.SetVersion(id, version);
                ProfileLogDebug($"Directly set ProfilePage state: {id} v{version}");
            }
            catch (Exception ex)
            {
                pendingModProfileUpdates[id] = version;
                ProfileLogError($"DirectSetModProfileState failed for {id}: {ex}");
            }
        }

        public static void ApplyPendingModProfileUpdates()
        {
            if (pendingModProfileUpdates.Count == 0) return;
            try
            {
                var pages = Resources.FindObjectsOfTypeAll<ProfilePage>();
                if (pages == null || pages.Length == 0)
                {
                    ProfileLogWarning($"ApplyPendingProfileUpdates: page still not found, {pendingModProfileUpdates.Count} remain.");
                    return;
                }

                IntPtr statePtr = Il2CppFieldHelper.GetReferenceField(pages[0], "_state");
                if (statePtr == IntPtr.Zero)
                {
                    ProfileLogWarning("ApplyPendingProfileUpdates: _state is null.");
                    return;
                }

                var state = new VersionedState(statePtr);
                foreach (var kvp in pendingModProfileUpdates)
                {
                    state.SetVersion(kvp.Key, kvp.Value);
                    ProfileLogDebug($"Applied pending profile state: {kvp.Key} v{kvp.Value}");
                }
                pendingModProfileUpdates.Clear();
            }
            catch (Exception ex)
            {
                ProfileLogError($"ApplyPendingProfileUpdates failed: {ex}");
            }
        }

        /// <summary>
        /// 强制重新注入 + 重新应用所有 mod profile 状态。
        /// </summary>
        public static void EnsureAllModProfileStatesAndForceReinject()
        {
            // 始终重置注入缓存——_loadedDataItemMap 可能已被重建
            injectedProfileDataItemMapPtr = IntPtr.Zero;

            if (allModProfileStates.Count == 0) return;
            try
            {

                var pages = Resources.FindObjectsOfTypeAll<ProfilePage>();
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
                foreach (var kvp in allModProfileStates)
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
                    allModProfileStates[kvp.Key] = kvp.Value;

                ProfileLogInfo($"EnsureAllModProfileStates: synced {allModProfileStates.Count} entries.");
            }
            catch (Exception ex)
            {
                ProfileLogError($"EnsureAllModProfileStatesAndForceReinject: {ex}");
            }
        }

        // ====================================================================
        // 纹理管理
        // ====================================================================

        /// <summary>确保 mod profile 纹理已注册到 Addressables 缓存。</summary>
        public static void EnsureTexturesRegistered()
        {
            ModTextureHelper.EnsureRegisteredInAddressables<ProfilePage>(ptr =>
                ModTextureHelper.RegisterTexturesInManager(
                    ptr, modProfileIds, BuildProfileTextureAddress,
                    LoadModProfileTexture,
                    (id, addr) => modProfileAddressToIdMap[addr] = id,
                    "Profile"));
        }

        public static Texture2D LoadModProfileTexture(string characterId)
            => ModTextureHelper.LoadTexture(characterId, "ModProfile_", modProfileTexturePathMap, modProfileTextureCache);

        // ====================================================================
        // 本地化文本
        // ====================================================================

        /// <summary>获取 mod 角色的显示名（FamilyName + Name → 简单拼接）。</summary>
        public static string GetModCharacterDisplayName(string id)
        {
            if (displayNameCache.TryGetValue(id, out var cached))
                return cached;

            if (!modCharacterMap.TryGetValue(id, out var character))
                return id;

            string family = character.FamilyName?.Resolve();
            string given = character.Name?.Resolve();
            string result = !string.IsNullOrEmpty(family) && !string.IsNullOrEmpty(given)
                ? family + given
                : !string.IsNullOrEmpty(given) ? given
                : !string.IsNullOrEmpty(family) ? family
                : id;

            displayNameCache[id] = result;
            return result;
        }

        /// <summary>
        /// 为 mod 角色构建带 TMP 富文本标记的作者名标签。
        ///
        /// 使用 AuthorTaggedTextGenerator 直接生成标记文本，字号和布局与原版
        /// AuthorData 模板一致（136/73/118/75 + voffset + space），不再依赖
        /// AuthorTextBuilder 实例。
        ///
        /// 格式：
        ///   - 有 FamilyName + Name → BuildFullName（姓首字大号带色 + 名首字次大号）
        ///   - 仅有单名 → BuildSimpleName
        /// </summary>
        public static string BuildModAuthorLabel(string id)
        {
            if (authorLabelCache.TryGetValue(id, out var cached))
                return cached;

            if (!modCharacterMap.TryGetValue(id, out var mc))
                return id;

            // 确定颜色（不带 # 前缀）
            string colorHex = null;
            if (!string.IsNullOrEmpty(mc.Color))
            {
                colorHex = mc.Color;
                if (colorHex.StartsWith("#"))
                    colorHex = colorHex.Substring(1);
            }

            string familyName = mc.FamilyName?.Resolve();
            string givenName = mc.Name?.Resolve();
            string result;

            if (!string.IsNullOrEmpty(familyName) && !string.IsNullOrEmpty(givenName))
            {
                result = AuthorTaggedTextGenerator.BuildFullName(familyName, givenName, colorHex);
            }
            else
            {
                string name = !string.IsNullOrEmpty(givenName) ? givenName
                    : !string.IsNullOrEmpty(familyName) ? familyName
                    : id;
                result = AuthorTaggedTextGenerator.BuildSimpleName(name, colorHex);
            }

            authorLabelCache[id] = result;
            return result;
        }

        /// <summary>获取 mod 角色的指定版本简介描述。</summary>
        public static string GetModProfileDescription(string id, int version)
        {
            foreach (var entry in allModProfiles)
            {
                if (entry.CharacterId == id && entry.Version == version)
                    return entry.Description?.Resolve("");
            }
            // 没有精确匹配版本，返回第一个
            foreach (var entry in allModProfiles)
            {
                if (entry.CharacterId == id)
                    return entry.Description?.Resolve("");
            }
            return "";
        }

        /// <summary>根据 Addressables 纹理地址反查 mod character ID</summary>
        public static string TryGetModProfileIdFromAddress(string address)
        {
            if (address != null && modProfileAddressToIdMap.TryGetValue(address, out var id))
                return id;
            return null;
        }
    }

    // ========================================================================
    // 数据注入 + 状态预设 Patch（Profile）
    // ========================================================================

    /// <summary>
    /// 在 WitchBookScreen.UpdateVersion 和 BeginToPresent / InitializePages 中
    /// 处理 Profile 的注入。
    /// </summary>
    [HarmonyPatch]
    static class ProfileInjection_Patch
    {
        [HarmonyPatch(typeof(WitchBookScreen), nameof(WitchBookScreen.UpdateVersion))]
        [HarmonyPrefix]
        static void WitchBookScreen_UpdateVersion_Prefix(WitchBookCategory category, string id, int version)
        {
            try
            {
                // 确保 mod 数据已加载（@update 可能在 WitchBook 打开前执行）
                ModResourceLoader.EnsureModDataLoaded();

                if (category != WitchBookCategory.Profile || !ModProfileLoader.HasModProfiles) return;

                ModProfileLoader.TryInjectProfileData();
                ModProfileLoader.TryInjectCharacterData();
                ModProfileLoader.TryInjectAuthorData();

                if (ModProfileLoader.IsModProfile(id))
                {
                    ModProfileLoader.DirectSetModProfileState(id, version);
                    ModProfileLoader.ProfileLogDebug($"UpdateVersion intercepted mod profile: {id} v{version}");
                }

                ModProfileLoader.TryInjectProfilePageStateAndRebuild();
                ModProfileLoader.EnsureTexturesRegistered();
            }
            catch (Exception ex)
            {
                ModProfileLoader.ProfileLogError($"ProfileInjection UpdateVersion prefix: {ex}");
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

                if (!ModProfileLoader.HasModProfiles) return;

                ModProfileLoader.TryInjectProfileData();
                ModProfileLoader.TryInjectCharacterData();
                ModProfileLoader.TryInjectAuthorData();
                ModProfileLoader.TryInjectProfilePageStateAndRebuild();
                ModProfileLoader.ApplyPendingModProfileUpdates();
                ModProfileLoader.EnsureTexturesRegistered();
            }
            catch (Exception ex)
            {
                ModProfileLoader.ProfileLogError($"ProfileInjection BeginToPresent prefix: {ex}");
            }
        }

        [HarmonyPatch(typeof(WitchBookScreen), nameof(WitchBookScreen.InitializePages))]
        [HarmonyPrefix]
        static void WitchBookScreen_InitializePages_Prefix()
        {
            try
            {
                if (!ModProfileLoader.HasModProfiles) return;

                ModProfileLoader.TryInjectProfileData();
                ModProfileLoader.TryInjectCharacterData();
                ModProfileLoader.TryInjectAuthorData();
                ModProfileLoader.EnsureAllModProfileStatesAndForceReinject();
                ModProfileLoader.TryInjectProfilePageStateAndRebuild();
                ModProfileLoader.EnsureTexturesRegistered();
            }
            catch (Exception ex)
            {
                ModProfileLoader.ProfileLogError($"ProfileInjection InitializePages prefix: {ex}");
            }
        }
    }

    // ========================================================================
    // ProfilePage.RefreshPageContent Hook
    // ========================================================================

    /// <summary>
    /// Hook ProfilePage.RefreshPageContent(VersionedItem&lt;ProfileDataItem&gt; map)
    /// 
    /// 原版方法通过 NaniCharacterManager 和 _localizedTextData 获取角色名和描述。
    /// 对 mod 角色：直接设置 _authorLabel / _descriptionLabel 和 _thumbnail。
    /// 
    /// ProfilePage 字段偏移：
    ///   _authorLabel: 0xB8  (TMP_Text)
    ///   _descriptionLabel: 0xC0  (TMP_Text)
    ///   _thumbnail: 0xC8  (WitchBookItemThumbnail)
    /// </summary>
    [HarmonyPatch]
    static class ProfilePageRefreshContent_Patch
    {
        [HarmonyPatch(typeof(ProfilePage), nameof(ProfilePage.RefreshPageContent))]
        [HarmonyPrefix]
        static bool Prefix(ProfilePage __instance, VersionedItem<ProfileDataItem> map)
        {
            try
            {
                string id = map?.Id;
                // IsModProfile 覆盖"全新 mod 角色"和"覆写原版角色"两种情况
                if (id == null || !ModProfileLoader.IsModProfile(id))
                    return true;

                // VersionedItem 的 _version 字段保存了 InitializeItems 从 _state 匹配得到的实际版本
                int version = Il2CppFieldHelper.GetIntField(map, "_version", 1);

                // ---- 设置角色名（_authorLabel at 0xB8）----
                if (ModProfileLoader.IsModCharacter(id))
                {
                    // 全新 mod 角色：modCharacterMap 中有数据，使用自定义格式化
                    IntPtr authorPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_authorLabel");
                    if (authorPtr != IntPtr.Zero)
                        new TMP_Text(authorPtr).text = ModProfileLoader.BuildModAuthorLabel(id);
                }
                else
                {
                    // 覆写原版角色简介：角色名由原版 _authorTextBuilder 生成（已加载 vanilla 角色数据）
                    IntPtr builderPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_authorTextBuilder");
                    if (builderPtr != IntPtr.Zero)
                    {
                        try
                        {
                            // TryBuildAuthorText 依赖 _authorData（InitializeAsync 异步初始化）；
                            // 若该字段未完成加载则会 FailFast，因此改为直接从 AuthorData ScriptableObject
                            // 读取预构建模板文本（含 %COLOR% 占位符），再用 CharacterMetadata.NameColor 替换。

                            // 1. 从 AuthorData 读取该角色的模板文本（含 %COLOR% 占位符）
                            string authorTaggedText = null;
                            var authorDataInstances = Resources.FindObjectsOfTypeAll<AuthorData>();
                            if (authorDataInstances != null && authorDataInstances.Length > 0)
                            {
                                var authorDataSO = authorDataInstances[0];
                                var authorItems = authorDataSO.Items; // IList<AuthorDataItem>
                                // IList<T> 无 .Count，需转型为 ICollection<T>
                                int authorCount = authorItems
                                    .Cast<Il2CppSystem.Collections.Generic.ICollection<AuthorDataItem>>()
                                    .Count;
                                for (int ai = 0; ai < authorCount && authorTaggedText == null; ai++)
                                {
                                    var item = authorItems[ai];
                                    if (item.Id == id)
                                    {
                                        // _taggedText 是 LocalizedText[]，通过 Il2CppReferenceArray 访问
                                        IntPtr taggedPtr = Il2CppFieldHelper.GetReferenceField(item, "_taggedText");
                                        if (taggedPtr != IntPtr.Zero)
                                        {
                                            var taggedArr = new Il2CppReferenceArray<LocalizedText>(taggedPtr);
                                            var currentLocale = LocaleHelper.GetCurrentLocaleKind();
                                            string fallbackText = null;
                                            for (int ti = 0; ti < taggedArr.Length; ti++)
                                            {
                                                var lt = taggedArr[ti];
                                                if (lt.Locale == currentLocale)
                                                { authorTaggedText = lt.Text; break; }
                                                if (fallbackText == null) fallbackText = lt.Text;
                                            }
                                            if (authorTaggedText == null) authorTaggedText = fallbackText;
                                        }
                                    }
                                }
                            }

                            // 2. 获取角色 NameColor（CharacterMetadata.UseCharacterColor + NameColor）
                            // TMP rich text 要求 <color=#RRGGBB> 格式，%COLOR% 需替换为 "#RRGGBB"
                            string colorHex = "#FFFFFF";
                            try
                            {
                                if (Engine.Initialized)
                                {
                                    var charConfig = Engine.GetConfiguration<CharactersConfiguration>();
                                    if (charConfig != null)
                                    {
                                        var meta = charConfig.GetMetadataOrDefault(id);
                                        if (meta != null && meta.UseCharacterColor)
                                            colorHex = "#" + ColorUtility.ToHtmlStringRGB(meta.NameColor);
                                    }
                                }
                            }
                            catch (Exception exColor)
                            {
                                ModProfileLoader.ProfileLogDebug($"GetCharacterColor failed for override '{id}': {exColor.Message}");
                            }

                            // 3. 替换 %COLOR% 占位符并写入标签
                            string finalAuthorText = !string.IsNullOrEmpty(authorTaggedText)
                                ? authorTaggedText.Replace("%COLOR%", colorHex)
                                : id; // 最终回退：至少显示 ID
                            IntPtr authorPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_authorLabel");
                            if (authorPtr != IntPtr.Zero)
                                new TMP_Text(authorPtr).text = finalAuthorText;
                        }
                        catch (Exception exAuthor)
                        {
                            ModProfileLoader.ProfileLogWarning($"BuildAuthorLabel failed for override profile '{id}': {exAuthor.Message}");
                        }
                    }
                }

                // ---- 设置描述（_descriptionLabel at 0xC0）----
                IntPtr descPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_descriptionLabel");
                if (descPtr != IntPtr.Zero)
                    new TMP_Text(descPtr).text = ModProfileLoader.GetModProfileDescription(id, version);

                // ---- 设置缩略图（_thumbnail at 0xC8）----
                IntPtr thumbnailPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_thumbnail");
                if (thumbnailPtr != IntPtr.Zero)
                    new WitchBookItemThumbnail(thumbnailPtr).Setup(ModProfileLoader.BuildProfileTextureAddress(id));

                ModProfileLoader.ProfileLogDebug($"RefreshPageContent handled for mod profile: {id} v{version} isChar={ModProfileLoader.IsModCharacter(id)}");
                return false; // 跳过原版（原版会因 _localizedTextData 引用相等问题崩溃）
            }
            catch (Exception ex)
            {
                ModProfileLoader.ProfileLogError($"ProfilePageRefreshContent error: {ex}");
                return true; // 出错时回退到原版
            }
        }
    }

    // ========================================================================
    // ProfilePage.SetupItemButton Hook
    // ========================================================================

    /// <summary>
    /// Hook ProfilePage.SetupItemButton 处理 mod 角色按钮显示。
    /// 
    /// 原版通过 WitchBookItemButton.SetupWithAddressableTexture 设置缩略图按钮。
    /// 对 mod 角色：使用相同的方式设置（纹理已预注册到 Addressables 缓存）。
    /// </summary>
    [HarmonyPatch]
    static class ProfilePageSetupItemButton_Patch
    {
        [HarmonyPatch(typeof(ProfilePage), nameof(ProfilePage.SetupItemButton))]
        [HarmonyPrefix]
        static bool Prefix(ProfilePage __instance, WitchBookItemButton button, VersionedItem<ProfileDataItem> map)
        {
            try
            {
                string id = map?.Id;
                // IsModProfile 同时覆盖新建 mod 角色和覆写原版角色两种情况
                if (id == null || !ModProfileLoader.IsModProfile(id))
                    return true;

                string address = ModProfileLoader.BuildProfileTextureAddress(id);
                button.SetupWithAddressableTexture(address);

                ModProfileLoader.ProfileLogDebug($"SetupItemButton handled for mod profile: {id}");
                return false;
            }
            catch (Exception ex)
            {
                ModProfileLoader.ProfileLogError($"ProfilePageSetupItemButton error: {ex}");
                return true;
            }
        }
    }

    // ========================================================================
    // Log / TextPrinter 作者名格式化 Patch
    // ========================================================================

    /// <summary>
    /// WitchTrialsLogMessageUi.ModifyAuthorPanel Postfix。
    ///
    /// 消息历史/回放日志的 AuthorTextBuilder 实例独立于魔女图鉴，
    /// 其 _nameData / _authorData 字典在 WitchTrialsLogUi.Initialize 时构建，
    /// 此时 mod 数据尚未注入到 ScriptableObject，因此 TryBuildAuthorText 对
    /// mod 角色返回 false，导致 _authorLabel 保留无格式的 DisplayName。
    ///
    /// 此 Postfix 在原版 ModifyAuthorPanel 执行完毕后为 mod 角色重新构建
    /// 带字号和颜色标记的文本。
    /// </summary>
    [HarmonyPatch]
    static class LogAuthorFormat_Patch
    {
        [HarmonyPatch(typeof(WitchTrialsLogMessageUi), nameof(WitchTrialsLogMessageUi.ModifyAuthorPanel))]
        [HarmonyPostfix]
        static void Postfix(WitchTrialsLogMessageUi __instance)
        {
            try
            {
                // 读取 _authorId（WitchTrialsLogMessageUi 私有字段 0xE0）
                IntPtr authorIdPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_authorId");
                if (authorIdPtr == IntPtr.Zero) return;
                string authorId = new Il2CppSystem.String(authorIdPtr);
                if (string.IsNullOrEmpty(authorId)) return;

                // 读取 _authorLabel（TMP_Text 字段 0xD8）
                IntPtr labelPtr = Il2CppFieldHelper.GetReferenceField(__instance, "_authorLabel");
                if (labelPtr == IntPtr.Zero) return;

                var label = new TMP_Text(labelPtr);

                if (ModProfileLoader.IsModCharacter(authorId))
                {
                    label.text = ModProfileLoader.BuildModAuthorLabel(authorId);
                }
                else if (ModResourceLoader.IsModSimpleCharacter(authorId))
                {
                    // 简单角色：使用当前语言的 DisplayName（含富文本标签）
                    string displayName = ModResourceLoader.GetSimpleCharacterDisplayName(authorId);
                    if (!string.IsNullOrEmpty(displayName))
                        label.text = displayName;
                }
            }
            catch (Exception ex)
            {
                ModProfileLoader.ProfileLogError($"LogAuthorFormat postfix error: {ex}");
            }
        }
    }

    /// <summary>
    /// WitchTrialsTextPrinterPanel.SetMessageAuthor Postfix。
    ///
    /// 与 LogAuthorFormat 同理，对话框的 AuthorTextBuilder 也可能
    /// 不包含 mod 角色数据。此 Postfix 在原版执行后补充格式化文本。
    ///
    /// WitchTrialsTextPrinterPanel 字段：
    ///   _authorTextBuilder: 0x228
    /// 基类 AuthorNamePanel.Text 用于显示角色名。
    /// </summary>
    [HarmonyPatch]
    static class TextPrinterAuthorFormat_Patch
    {
        [HarmonyPatch(typeof(WitchTrialsTextPrinterPanel), nameof(WitchTrialsTextPrinterPanel.SetMessageAuthor))]
        [HarmonyPostfix]
        static void Postfix(WitchTrialsTextPrinterPanel __instance, MessageAuthor author)
        {
            try
            {
                string authorId = author.Id;
                if (string.IsNullOrEmpty(authorId)) return;

                string formatted = null;
                if (ModProfileLoader.IsModCharacter(authorId))
                {
                    formatted = ModProfileLoader.BuildModAuthorLabel(authorId);
                }
                else if (ModResourceLoader.IsModSimpleCharacter(authorId))
                {
                    formatted = ModResourceLoader.GetSimpleCharacterDisplayName(authorId);
                }

                if (formatted == null) return;

                // 读取 authorNamePanel（基类 RevealableTextPrinterPanel 的私有字段 0x158）
                IntPtr panelPtr = Il2CppFieldHelper.GetReferenceField(__instance, "authorNamePanel");
                if (panelPtr != IntPtr.Zero)
                {
                    var namePanel = new AuthorNameTMProPanel(panelPtr);
                    namePanel.Text = formatted;
                }
            }
            catch (Exception ex)
            {
                ModProfileLoader.ProfileLogError($"TextPrinterAuthorFormat postfix error: {ex}");
            }
        }
    }
}
