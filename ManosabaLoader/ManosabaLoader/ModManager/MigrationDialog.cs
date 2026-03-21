using ManosabaLoader.Utils;
using UnityEngine;

namespace ManosabaLoader.ModManager
{
    /// <summary>
    /// IMGUI 弹窗：在游戏启动后显示 schema 迁移确认对话框。
    /// 每次显示一个待确认的迁移，用户可选择"确认迁移"或"跳过"。
    /// </summary>
    public class MigrationDialog : MonoBehaviour
    {
        private ModItem.PendingMigration current;
        private Rect windowRect;
        private bool initialized;

        private static string L(string zhHans, string ja)
        {
            return LocaleHelper.GetCurrentLocale() == "ja" ? ja : zhHans;
        }

        void OnGUI()
        {
            if (!initialized)
            {
                initialized = true;
                windowRect = new Rect(Screen.width / 2f - 220, Screen.height / 2f - 130, 440, 260);
            }

            var pending = ModItem.PendingMigrations;
            if (pending.Count == 0)
            {
                Destroy(this);
                return;
            }

            if (current == null)
                current = pending[0];

            windowRect = GUI.Window(948201, windowRect,
                (GUI.WindowFunction)DrawWindow,
                L("配置迁移", "設定の移行"));
        }

        void DrawWindow(int id)
        {
            string modName = System.IO.Path.GetFileName(current.ModDir);

            GUI.Label(new Rect(10, 25, 420, 160),
                L(
                    $"Mod 配置文件需要迁移\n\n" +
                    $"Mod: {modName}\n" +
                    $"版本: {current.FromVersion} → {current.ToVersion}\n\n" +
                    "确认后将自动备份原文件并写入迁移后的配置。\n" +
                    "跳过后 Mod 仍可正常加载，但下次启动会再次询问。",
                    $"Mod の設定ファイルを移行する必要があります\n\n" +
                    $"Mod: {modName}\n" +
                    $"バージョン: {current.FromVersion} → {current.ToVersion}\n\n" +
                    "確認すると元ファイルをバックアップし、移行後の設定を書き込みます。\n" +
                    "スキップしても Mod は正常に動作しますが、次回起動時に再度確認されます。"));

            if (GUI.Button(new Rect(70, 210, 130, 30), L("确认迁移", "移行する")))
            {
                ModItem.CommitMigration(current);
                ModItem.PendingMigrations.Remove(current);
                current = null;
            }

            if (GUI.Button(new Rect(240, 210, 130, 30), L("跳过", "スキップ")))
            {
                ModItem.PendingMigrations.Remove(current);
                current = null;
            }

            GUI.DragWindow();
        }
    }
}
