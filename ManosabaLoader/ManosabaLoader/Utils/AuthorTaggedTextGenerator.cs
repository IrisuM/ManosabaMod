using System.Text;

namespace ManosabaLoader
{
    /// <summary>
    /// 生成与原版游戏 AuthorData 模板格式一致的 TMP 富文本标记。
    ///
    /// 原版角色走 _authorData 路径（模板中硬编码了字号）；
    /// mod 角色若走 BuildTaggedFullName 则使用 AuthorTextBuilder.Settings 中的字号值
    /// (132/72/120/72)，与原版模板值 (136/73/118/75) 不一致，导致渲染偏小。
    ///
    /// 本工具类直接生成与原版模板结构完全一致的标记文本，
    /// 确保 mod 角色在 TextPrinter、BacklogUI、WitchBook 中的字号与原版一致。
    ///
    /// 原版 zh-Hans 模板结构（以二阶堂希罗为例）：
    ///   <color=#RRGGBB><size=136>二</size></color>
    ///   <space=4><voffset=-2><size=73>阶堂</size></voffset>
    ///   <space=4><size=118>希</size>
    ///   <space=4><voffset=-2><size=75>罗</size></voffset>
    /// </summary>
    internal static class AuthorTaggedTextGenerator
    {
        // 字号值来自原版 AuthorData 模板，与所有原版角色保持一致
        private const int FamilyInitialSize = 136;
        private const int FamilyBodySize = 73;
        private const int GivenInitialSize = 118;
        private const int GivenBodySize = 75;
        private const int BodyVOffset = -2;
        private const int PartSpacing = 4;

        /// <summary>
        /// 为全名（姓 + 名）生成带颜色和字号标记的富文本。
        /// </summary>
        /// <param name="familyName">姓氏文本。</param>
        /// <param name="givenName">名前文本。</param>
        /// <param name="colorHex">不带 # 前缀的 HTML 颜色值，如 "C8AACC"。传 null 则不添加颜色标签。</param>
        public static string BuildFullName(string familyName, string givenName, string colorHex)
        {
            var sb = new StringBuilder(128);

            bool hasFamily = !string.IsNullOrEmpty(familyName);
            bool hasGiven = !string.IsNullOrEmpty(givenName);

            if (hasFamily && hasGiven)
            {
                AppendNamePart(sb, familyName, FamilyInitialSize, FamilyBodySize, colorHex);
                AppendNamePart(sb, givenName, GivenInitialSize, GivenBodySize, null);
            }
            else if (hasFamily)
            {
                AppendNamePart(sb, familyName, FamilyInitialSize, FamilyBodySize, colorHex);
            }
            else if (hasGiven)
            {
                AppendNamePart(sb, givenName, GivenInitialSize, GivenBodySize, colorHex);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 为简单名字（无姓/名拆分）生成带标记的富文本。
        /// </summary>
        public static string BuildSimpleName(string name, string colorHex)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            var sb = new StringBuilder(64);
            AppendNamePart(sb, name, GivenInitialSize, GivenBodySize, colorHex);
            return sb.ToString();
        }

        private static void AppendNamePart(StringBuilder sb, string name, int initialSize, int bodySize, string colorHex)
        {
            if (string.IsNullOrEmpty(name)) return;

            string initial = name.Substring(0, 1);
            string body = name.Length > 1 ? name.Substring(1) : null;

            // 名称各部分之间用 <space=4> 分隔
            if (sb.Length > 0)
                sb.Append("<space=").Append(PartSpacing).Append('>');

            // 首字（initial）：家族名首字带颜色标记
            if (colorHex != null)
            {
                sb.Append("<color=#").Append(colorHex).Append('>');
                sb.Append("<size=").Append(initialSize).Append('>').Append(initial).Append("</size>");
                sb.Append("</color>");
            }
            else
            {
                sb.Append("<size=").Append(initialSize).Append('>').Append(initial).Append("</size>");
            }

            // 余字（body）：带 voffset 偏移
            if (!string.IsNullOrEmpty(body))
            {
                sb.Append("<space=").Append(PartSpacing).Append('>');
                sb.Append("<voffset=").Append(BodyVOffset).Append('>');
                sb.Append("<size=").Append(bodySize).Append('>').Append(body).Append("</size>");
                sb.Append("</voffset>");
            }
        }

        // ================================================================
        // DisplayName 格式（用于 CharacterMetadata.DisplayName）
        // ================================================================

        // DisplayName 字号值（与 Author 标签略有不同）
        private const int DisplayFamilyInitialSize = 136;
        private const int DisplayFamilyBodySize = 73;
        private const int DisplayGivenInitialSize = 114;
        private const int DisplayGivenBodySize = 73;

        /// <summary>
        /// 为全名（姓 + 名）生成 DisplayName 格式的富文本。
        ///
        /// 格式（以二阶堂希罗 + 颜色 FF0000 为例）：
        ///   <color=#FF0000&gt;<size=136&gt;二</size&gt;</color&gt;<size=73&gt;阶堂</size&gt;
        ///   <space=4&gt;<voffset=-2&gt;<size=114&gt;希</size&gt;<size=73&gt;罗</size&gt;</voffset&gt;
        /// </summary>
        public static string BuildDisplayName(string familyName, string givenName, string colorHex)
        {
            var sb = new StringBuilder(128);

            bool hasFamily = !string.IsNullOrEmpty(familyName);
            bool hasGiven = !string.IsNullOrEmpty(givenName);

            if (hasFamily)
                AppendDisplayFamilyName(sb, familyName, colorHex);

            if (hasGiven)
            {
                if (hasFamily)
                    sb.Append("<space=").Append(PartSpacing).Append('>');
                AppendDisplayGivenName(sb, givenName);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 为简单名字（无姓/名拆分）生成 DisplayName 格式的富文本。
        /// </summary>
        public static string BuildSimpleDisplayName(string name, string colorHex)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            var sb = new StringBuilder(64);
            // 单名使用 Given 的字号，首字带颜色
            AppendDisplayFamilyName(sb, name, colorHex);
            return sb.ToString();
        }

        /// <summary>
        /// 姓氏部分：首字带颜色 + 大号，余字正常号。
        /// <color=#HEX><size=136>首</size></color><size=73>余字</size>
        /// </summary>
        private static void AppendDisplayFamilyName(StringBuilder sb, string name, string colorHex)
        {
            string initial = name.Substring(0, 1);
            string body = name.Length > 1 ? name.Substring(1) : null;

            // 首字：带颜色标记
            if (colorHex != null)
            {
                sb.Append("<color=#").Append(colorHex).Append('>');
                sb.Append("<size=").Append(DisplayFamilyInitialSize).Append('>').Append(initial).Append("</size>");
                sb.Append("</color>");
            }
            else
            {
                sb.Append("<size=").Append(DisplayFamilyInitialSize).Append('>').Append(initial).Append("</size>");
            }

            // 余字
            if (!string.IsNullOrEmpty(body))
            {
                sb.Append("<size=").Append(DisplayFamilyBodySize).Append('>').Append(body).Append("</size>");
            }
        }

        /// <summary>
        /// 名前部分：整体 voffset，首字大号 + 余字小号。
        /// <voffset=-2><size=114>首</size><size=73>余字</size></voffset>
        /// </summary>
        private static void AppendDisplayGivenName(StringBuilder sb, string name)
        {
            string initial = name.Substring(0, 1);
            string body = name.Length > 1 ? name.Substring(1) : null;

            sb.Append("<voffset=").Append(BodyVOffset).Append('>');
            sb.Append("<size=").Append(DisplayGivenInitialSize).Append('>').Append(initial).Append("</size>");

            if (!string.IsNullOrEmpty(body))
            {
                sb.Append("<size=").Append(DisplayGivenBodySize).Append('>').Append(body).Append("</size>");
            }

            sb.Append("</voffset>");
        }
    }
}
