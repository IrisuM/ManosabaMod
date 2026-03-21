using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using HarmonyLib;

using Il2CppInterop.Runtime;

using ManosabaLoader.Utils;

using Naninovel;

namespace ManosabaLoader
{
    /// <summary>
    /// Mod 视频加载器。
    ///
    /// ===== 策略 =====
    ///
    /// Naninovel 的 MoviePlayer 支持两种视频加载模式：
    ///   1. VideoClip 模式（默认）：通过 ResourceLoader 加载 VideoClip 资源。
    ///   2. URL 流式模式（UrlStreaming）：通过 VideoPlayer.url 直接指定文件路径。
    ///
    /// 对于 Mod 视频，我们利用已有的 URL 流式模式：
    ///   - 在 get_UrlStreaming 后缀中对 mod 视频强制返回 true。
    ///   - 重写 BuildStreamUrl 返回 Mod 视频的本地绝对路径。
    ///   - 这样原版的异步播放逻辑（渐变、跳过输入、阻断交互等）全部保留。
    ///
    /// ⚠ 不能 Hook MoviePlayer.Play / HoldResources / LoadMovieClip：
    ///   这些方法返回 UniTask / UniTask&lt;T&gt;，在 IL2CPP 下
    ///   Harmony patch 会破坏异步虚表，导致 MethodAccessException 崩溃。
    ///
    /// ===== 预加载阶段拦截 =====
    ///
    /// 脚本加载时的调用链：
    ///   ScriptPlaylist.LoadResources
    ///     → PlayMovie.PreloadResources()        [UniTask，不能 patch]
    ///       → PlayMovie.Player (get_Player)     [IMoviePlayer，安全！]
    ///       → MoviePlayer.HoldResources(name)   [UniTask，不能 patch]
    ///         → get_UrlStreaming                 [bool，安全！]
    ///         → 若 false：加载 VideoClip → 对 mod 视频会失败
    ///
    /// 解决方案：Patch PlayMovie.get_Player（安全，返回 IMoviePlayer），
    /// 在后缀中读取 PlayMovie.MovieName 并设置 pendingPreloadMovieName。
    /// 随后 HoldResources 同步调用 get_UrlStreaming 时，
    /// UrlStreaming_Postfix 识别 pending 标志并返回 true，跳过 VideoClip 加载。
    ///
    /// ===== 使用方式 =====
    ///
    /// 在 Naninovel 脚本中使用 @movie 命令即可：
    ///   @movie MyModMovie
    ///
    /// 对应 Mod 目录结构：
    ///   ModRoot/ModName/Movie/MyModMovie.mp4
    ///
    /// 支持格式：.mp4, .webm, .ogv
    ///
    /// ===== IL2CPP 字段（通过 Il2CppFieldHelper 动态解析） =====
    /// MoviePlayer.playedMovieName  (string)
    /// PlayMovie.MovieName          (StringParameter → Nullable&lt;string&gt;)
    /// Nullable&lt;string&gt;.value       (string)
    /// </summary>
    public static class ModMovieLoader
    {
        public static Action<string> MovieLogMessage;
        public static Action<string> MovieLogInfo;
        public static Action<string> MovieLogDebug;
        public static Action<string> MovieLogWarning;
        public static Action<string> MovieLogError;
        /// <summary>movieName → 绝对文件路径</summary>
        private static readonly Dictionary<string, string> modMovies = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 在 PlayMovie.get_Player 后缀中设置，用于在 HoldResources → get_UrlStreaming
        /// 调用链中识别当前正在预加载的 mod 视频名称。
        /// playedMovieName 仅在 Play 时才被设置，而 HoldResources 阶段尚未设置，
        /// 因此需要此字段来桥接预加载阶段的 mod 视频识别。
        /// </summary>
        private static string pendingPreloadMovieName;

        public static bool HasModMovies => modMovies.Count > 0;

        public static void Init(Harmony harmony)
        {
            // 始终注册 Harmony 补丁（影片数据按需延迟加载）
            harmony.PatchAll(typeof(MoviePlayerPatch));
            PatchPlayMovieGetPlayer(harmony);

            MovieLogInfo("ModMovieLoader patches applied.");
        }

        /// <summary>加载指定 mod 的影片数据。</summary>
        public static void LoadModData(string modKey, string modPath)
        {
            ScanMovieDirectory(modPath);
            if (modMovies.Count > 0)
                MovieLogMessage($"Loaded {modMovies.Count} movie(s) for mod: {modKey}");
        }

        /// <summary>清除所有 mod 影片数据。</summary>
        public static void ClearModData()
        {
            modMovies.Clear();
            pendingPreloadMovieName = null;
            MovieLogInfo("MovieLoader data cleared.");
        }

        private static void ScanMovieDirectory(string baseDir)
        {
            var movieDir = Path.Combine(baseDir, "Movie");
            if (!Directory.Exists(movieDir)) return;

            foreach (var file in Directory.GetFiles(movieDir))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".mp4" or ".webm" or ".ogv")
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (modMovies.TryGetValue(name, out var existing))
                    {
                        MovieLogWarning($"Duplicate mod movie '{name}': {existing} vs {file}");
                        continue;
                    }
                    modMovies[name] = file.Replace("\\", "/");
                    MovieLogDebug($"Registered mod movie: {name} -> {file}");
                }
            }
        }

        public static bool IsModMovie(string movieName)
        {
            return !string.IsNullOrEmpty(movieName) && modMovies.ContainsKey(movieName);
        }

        /// <summary>
        /// 手动 Patch PlayMovie.get_Player（protected virtual IMoviePlayer）。
        ///
        /// PlayMovie 位于 Naninovel.Commands 命名空间，使用 AccessTools 在运行时查找类型，
        /// 避免编译时依赖问题。
        ///
        /// 调用链：PreloadResources() → Player (get_Player) → HoldResources(movieName)
        ///   → get_UrlStreaming
        ///
        /// 我们在 get_Player 后缀中通过 Il2CppFieldHelper 动态读取 PlayMovie.MovieName，
        /// 提取字符串后设置 pendingPreloadMovieName。
        /// 随后 HoldResources 同步调用 get_UrlStreaming 时，
        /// UrlStreaming_Postfix 可以识别出当前是 mod 视频并返回 true，从而跳过 VideoClip 加载。
        /// </summary>
        private static void PatchPlayMovieGetPlayer(Harmony harmony)
        {
            try
            {
                var playMovieType = AccessTools.TypeByName("Naninovel.Commands.PlayMovie");
                if (playMovieType == null)
                {
                    MovieLogWarning("PlayMovie type not found, preload bypass unavailable.");
                    return;
                }

                var getPlayerMethod = AccessTools.PropertyGetter(playMovieType, "Player");
                if (getPlayerMethod == null)
                {
                    MovieLogWarning("PlayMovie.get_Player not found, preload bypass unavailable.");
                    return;
                }

                var postfix = new HarmonyMethod(typeof(ModMovieLoader),
                    nameof(GetPlayer_Postfix));

                harmony.Patch(getPlayerMethod, postfix: postfix);
                MovieLogDebug("Patched PlayMovie.get_Player for preload bypass.");
            }
            catch (Exception ex)
            {
                MovieLogError($"Failed to patch PlayMovie.get_Player: {ex}");
            }
        }

        /// <summary>
        /// PlayMovie.get_Player 后缀：读取当前 PlayMovie 命令的 MovieName，
        /// 如果是 mod 视频则设置 pendingPreloadMovieName。
        /// </summary>
        private static void GetPlayer_Postfix(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase __instance)
        {
            pendingPreloadMovieName = null; // 清除旧状态
            try
            {
                // 脚本预加载阶段可能在 WitchBook 打开之前触发，
                // 确保 mod 数据（包括 modMovies）已加载
                ModResourceLoader.EnsureModDataLoaded();

                // 动态读取 PlayMovie.MovieName (StringParameter)
                IntPtr paramPtr = Il2CppFieldHelper.GetReferenceField(__instance, "MovieName");
                if (paramPtr == IntPtr.Zero) return;

                // 动态读取 Nullable<string>.value
                IntPtr valuePtr = Il2CppFieldHelper.GetReferenceField(paramPtr, "value");
                if (valuePtr == IntPtr.Zero) return;

                string movieName = IL2CPP.Il2CppStringToManaged(valuePtr);
                if (IsModMovie(movieName))
                {
                    pendingPreloadMovieName = movieName;
                    MovieLogDebug($"PlayMovie.get_Player: pending mod movie '{movieName}'");
                }
            }
            catch (Exception ex)
            {
                MovieLogError($"GetPlayer_Postfix error: {ex}");
            }
        }

        // ====================================================================
        // Harmony Patches
        // ====================================================================

        [HarmonyPatch]
        static class MoviePlayerPatch
        {
            /// <summary>
            /// get_UrlStreaming 后缀：对 mod 视频强制返回 true。
            ///
            /// 原版 get_UrlStreaming 检查 streamExtension 是否非空。
            /// 
            /// 两阶段检查：
            ///   1. 预加载阶段（HoldResources 调用）：检查 pendingPreloadMovieName，
            ///      此时 playedMovieName 尚未设置，但 get_Player 后缀已记录了待预加载的 mod 视频名。
            ///   2. 播放阶段（Play 调用）：检查 playedMovieName，
            ///      此时 Play 方法已设置了正在播放的视频名。
            /// </summary>
            [HarmonyPatch(typeof(MoviePlayer), "get_UrlStreaming")]
            [HarmonyPostfix]
            static void UrlStreaming_Postfix(MoviePlayer __instance, ref bool __result)
            {
                try
                {
                    if (__result) return; // 已经是 URL 流式模式

                    // 阶段 1：预加载（HoldResources → get_UrlStreaming）
                    var pending = pendingPreloadMovieName;
                    if (pending != null)
                    {
                        pendingPreloadMovieName = null; // 消费标志，避免影响后续调用
                        if (IsModMovie(pending))
                        {
                            __result = true;
                            MovieLogDebug($"UrlStreaming preload override: {pending} → true");
                            return;
                        }
                    }

                    // 阶段 2：播放（Play → get_UrlStreaming）
                    IntPtr namePtr = Il2CppFieldHelper.GetReferenceField(__instance, "playedMovieName");
                    if (namePtr == IntPtr.Zero) return;

                    string playedName = IL2CPP.Il2CppStringToManaged(namePtr);
                    if (IsModMovie(playedName))
                    {
                        __result = true;
                        MovieLogDebug($"UrlStreaming play override: {playedName} → true");
                    }
                }
                catch (Exception ex)
                {
                    MovieLogError($"UrlStreaming_Postfix error: {ex}");
                }
            }

            /// <summary>
            /// BuildStreamUrl 后缀：对 mod 视频返回本地绝对路径。
            ///
            /// 原版实现返回 "{streamingAssetsPath}/{PathPrefix}/{movieName}{streamExtension}"，
            /// 我们替换为 mod 视频的绝对路径。
            ///
            /// Unity VideoPlayer 的 URL 模式接受普通绝对路径（无需 file:// 前缀）。
            /// </summary>
            [HarmonyPatch(typeof(MoviePlayer), nameof(MoviePlayer.BuildStreamUrl))]
            [HarmonyPostfix]
            static void BuildStreamUrl_Postfix(string movieName, ref string __result)
            {
                try
                {
                    if (modMovies.TryGetValue(movieName, out var path))
                    {
                        __result = path;
                        MovieLogDebug($"BuildStreamUrl: returning mod path '{path}' for '{movieName}'");
                    }
                }
                catch (Exception ex)
                {
                    MovieLogError($"BuildStreamUrl_Postfix error: {ex}");
                }
            }

            /// <summary>
            /// Stop 前缀：清理 mod 视频状态。
            /// </summary>
            [HarmonyPatch(typeof(MoviePlayer), nameof(MoviePlayer.Stop))]
            [HarmonyPrefix]
            static void Stop_Prefix(MoviePlayer __instance)
            {
                try
                {
                    IntPtr namePtr = Il2CppFieldHelper.GetReferenceField(__instance, "playedMovieName");
                    if (namePtr == IntPtr.Zero) return;

                    string playedName = IL2CPP.Il2CppStringToManaged(namePtr);
                    if (IsModMovie(playedName))
                    {
                        MovieLogDebug($"Stop: mod movie '{playedName}' stopping.");
                    }
                }
                catch (Exception ex)
                {
                    MovieLogError($"Stop_Prefix error: {ex}");
                }
            }

            /// <summary>
            /// ReleaseResources 前缀：对 mod 视频跳过资源释放。
            /// </summary>
            [HarmonyPatch(typeof(MoviePlayer), nameof(MoviePlayer.ReleaseResources))]
            [HarmonyPrefix]
            static bool ReleaseResources_Prefix(string movieName)
            {
                if (modMovies.ContainsKey(movieName))
                {
                    MovieLogDebug($"ReleaseResources: skipping for mod movie '{movieName}'");
                    return false;
                }
                return true;
            }
        }

    }
}
