using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace TOAHEX
{
    /// <summary>
    /// 词典引擎：所有界面文本统一从 Dictory/ 下的语言文件读取。
    ///
    /// 文件格式（UTF-8，键=中文原文，值=该语言译文）：
    ///   # 注释
    ///   [分组]                  ← 仅用于阅读分组，解析时忽略
    ///   中文原文=译文
    ///   \n \t \\ 为转义；行首行尾空白不参与键匹配，值保留原文。
    ///
    /// 查找顺序：当前语言 → 中文（zh-CN.txt 可做术语覆盖）→ 代码内中文常量。
    /// 因此任何漏翻都会“退化显示中文”，并由 Stats/校验脚本报告出来，不会静默显示空白。
    /// </summary>
    public static class Lang
    {
        public const string FolderName = "Dictory";

        private static readonly Dictionary<string, string> _cn = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _jp = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _en = new Dictionary<string, string>(StringComparer.Ordinal);

        // 控件原始文本（首次 ApplyTree 时捕获），语言切换时据此重新翻译
        private static readonly Dictionary<Control, string> _origin = new Dictionary<Control, string>();
        private static readonly Dictionary<ToolStripItem, string> _itemOrigin = new Dictionary<ToolStripItem, string>();
        private static readonly Dictionary<Control, string[]> _comboOrigin = new Dictionary<Control, string[]>();
        private static readonly Dictionary<DataGridView, string[]> _gridOrigin = new Dictionary<DataGridView, string[]>();

        public static string DictoryPath { get; private set; }
        public static bool Loaded { get; private set; }
        public static string LastError { get; private set; }

        static Lang()
        {
            Load();
        }

        // ---------------------------------------------------------------- 路径

        /// <summary>定位 Dictory 目录：exe 同级 → 上溯 3 层（bin/Debug）→ 环境变量。</summary>
        private static string ResolveFolder()
        {
            var env = Environment.GetEnvironmentVariable("TOAHEX_DICTORY");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
                return Path.GetFullPath(env);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string dir = Path.Combine(baseDir, FolderName);
            if (Directory.Exists(dir)) return Path.GetFullPath(dir);

            var up = new DirectoryInfo(baseDir);
            for (int i = 0; i < 4 && up != null; i++)
            {
                up = up.Parent;
                if (up == null) break;
                dir = Path.Combine(up.FullName, FolderName);
                if (Directory.Exists(dir)) return Path.GetFullPath(dir);
            }
            return Path.Combine(baseDir, FolderName);
        }

        // ---------------------------------------------------------------- 加载

        public static void Load()
        {
            DictoryPath = ResolveFolder();
            _cn.Clear(); _jp.Clear(); _en.Clear();
            LastError = null;
            Loaded = false;

            if (!Directory.Exists(DictoryPath))
            {
                LastError = "找不到词典目录：" + DictoryPath;
                return;
            }

            LoadInto(Path.Combine(DictoryPath, "zh-CN.txt"), _cn);
            LoadInto(Path.Combine(DictoryPath, "ja-JP.txt"), _jp);
            LoadInto(Path.Combine(DictoryPath, "en-US.txt"), _en);
            Loaded = true;
        }

        public static void Reload()
        {
            Load();
            LoadPreference();
            _origin.Clear();
            _itemOrigin.Clear();
            _comboOrigin.Clear();
            _gridOrigin.Clear();
            LanguageConfig.RaiseChanged();
        }

        /// <summary>读取 Dictory/language.txt 记住上次选择的界面语言。</summary>
        public static void LoadPreference()
        {
            try
            {
                string p = Path.Combine(DictoryPath, "language.txt");
                if (!File.Exists(p)) return;
                string code = File.ReadAllText(p, Encoding.UTF8).Trim().ToLowerInvariant();
                if (code.StartsWith("ja")) LanguageConfig.Current = Language.JP;
                else if (code.StartsWith("en")) LanguageConfig.Current = Language.EN;
                else LanguageConfig.Current = Language.CN;
            }
            catch { }
        }

        public static void SavePreference()
        {
            try
            {
                if (!Directory.Exists(DictoryPath)) return;
                string code = LanguageConfig.Current == Language.JP ? "ja-JP"
                            : LanguageConfig.Current == Language.EN ? "en-US" : "zh-CN";
                File.WriteAllText(Path.Combine(DictoryPath, "language.txt"), code, new UTF8Encoding(false));
            }
            catch { }
        }

        private static void LoadInto(string path, Dictionary<string, string> target)
        {
            if (!File.Exists(path)) return;
            try
            {
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    string head = line.TrimStart();
                    if (head.StartsWith("#") || head.StartsWith(";")) continue;
                    if (head.StartsWith("[") && head.EndsWith("]")) continue;

                    int eq = IndexOfSeparator(line);
                    if (eq <= 0) continue;
                    string key = Unescape(line.Substring(0, eq).Trim());
                    string val = Unescape(line.Substring(eq + 1));
                    if (key.Length == 0) continue;
                    target[key] = val;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        /// <summary>找第一个未转义的分隔符 '='（键内可写 \=）。</summary>
        private static int IndexOfSeparator(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '\\') { i++; continue; }
                if (line[i] == '=') return i;
            }
            return -1;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '\\': sb.Append('\\'); break;
                    case 's': sb.Append(' '); break;   // \s = 行首空格（键名需要保留前导空格时用）
                    case '=': sb.Append('='); break;   // 键内含等号时转义
                    default: sb.Append('\\'); sb.Append(n); break;
                }
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 查表

        private static Dictionary<string, string> Dict(Language l)
        {
            switch (l)
            {
                case Language.JP: return _jp;
                case Language.EN: return _en;
                default: return _cn;
            }
        }

        /// <summary>
        /// 构建期开关：InitializeComponent 期间 T() 直接返回中文键，
        /// 让控件保留"原文"，随后由 ApplyTree 统一翻译（否则切换语言时无法还原原文）。
        /// </summary>
        private static bool _building;
        public static void BeginBuild() { _building = true; }
        public static void EndBuild() { _building = false; }

        /// <summary>取当前语言译文；查不到时返回 fallback（默认中文原文）。</summary>
        public static string T(string cn, string jp = null)
        {
            if (cn == null) return "";
            if (cn.Length == 0) return cn;
            if (_building) return cn;

            var cur = Dict(LanguageConfig.Current);
            if (cur.TryGetValue(cn, out var v)) return v;

            // 构建期用 string.Format 拼出的文本（如 "位置1:"）→ 反查带 {0} 的键
            string fmt = TryFormatLookup(cn);
            if (fmt != null) return fmt;

            // 当前语言缺失 → 回落
            if (LanguageConfig.Current == Language.JP && jp != null) return jp;
            if (LanguageConfig.Current != Language.CN && _cn.TryGetValue(cn, out var cnv)) return cnv;
            return cn;
        }

        private static readonly Dictionary<string, System.Text.RegularExpressions.Regex> _fmtRegex =
            new Dictionary<string, System.Text.RegularExpressions.Regex>(StringComparer.Ordinal);

        /// <summary>把 "位置1:" 这类已格式化文本还原成模板键并重新格式化。</summary>
        private static string TryFormatLookup(string text)
        {
            bool hasDigit = false;
            for (int i = 0; i < text.Length; i++)
                if (char.IsDigit(text[i])) { hasDigit = true; break; }
            if (!hasDigit) return null;

            var dict = Dict(LanguageConfig.Current);
            foreach (var kv in dict)
            {
                string key = kv.Key;
                if (key.IndexOf("{0}", StringComparison.Ordinal) < 0) continue;
                if (!_fmtRegex.TryGetValue(key, out var re))
                {
                    var parts = key.Split(new[] { "{0}" }, StringSplitOptions.None);
                    var pat = "^" + string.Join("(\\d+)", parts.Select(System.Text.RegularExpressions.Regex.Escape)) + "$";
                    re = new System.Text.RegularExpressions.Regex(pat);
                    _fmtRegex[key] = re;
                }
                var m = re.Match(text);
                if (!m.Success) continue;
                try
                {
                    object[] args = new object[m.Groups.Count - 1];
                    for (int i = 1; i < m.Groups.Count; i++) args[i - 1] = m.Groups[i].Value;
                    return string.Format(kv.Value, args);
                }
                catch { return kv.Value; }
            }
            return null;
        }

        public static string TFormat(string cn, params object[] args)
        {
            return string.Format(T(cn), args);
        }

        /// <summary>内容类文本（剧情/地图/称号等）：缺失时返回原文，便于发现漏翻。</summary>
        public static string TContent(string cn)
        {
            if (string.IsNullOrEmpty(cn)) return cn ?? "";
            var cur = Dict(LanguageConfig.Current);
            return cur.TryGetValue(cn, out var v) ? v : cn;
        }

        public static int Count(Language l) => Dict(l).Count;

        // ---------------------------------------------------------------- 控件树翻译

        /// <summary>
        /// 翻译控件树上的静态文本（Label/Button/CheckBox/GroupBox/TabPage/菜单等）。
        /// 首次调用会记录原始中文文本，之后切换语言都以此为准，避免二次翻译。
        /// 不处理 TextBox/NumericUpDown，避免覆盖用户输入。
        /// </summary>
        public static void ApplyTree(Control root)
        {
            if (root == null) return;
            ApplyControl(root);

            if (root is ToolStrip strip)
                foreach (ToolStripItem item in strip.Items)
                    ApplyMenuItem(item);

            foreach (Control child in root.Controls)
                ApplyTree(child);
        }

        private static void ApplyControl(Control c)
        {
            if (c is TextBox || c is NumericUpDown || c is RichTextBox || c is MaskedTextBox)
                return;

            // 下拉框的可视文本由 ApplyCombo 逐项翻译驱动。
            // 这里再写一次 c.Text 只会把旧语言残值留在 Text 属性上（DropDownList 设不进去也不清空），
            // 还会覆盖可编辑下拉框里用户输入的内容。
            if (!(c is ComboBox))
            {
                if (!_origin.ContainsKey(c))
                    _origin[c] = c.Text ?? "";
                string src = _origin[c];
                if (!string.IsNullOrEmpty(src))
                    c.Text = T(src);
            }

            if (c is ComboBox cb)
                ApplyCombo(cb);

            if (c is DataGridView dgv)
                ApplyGrid(dgv);
        }

        private static void ApplyCombo(ComboBox cb)
        {
            if (!_comboOrigin.TryGetValue(cb, out var origin))
            {
                if (cb.Items.Count == 0) return;
                origin = new string[cb.Items.Count];
                for (int i = 0; i < cb.Items.Count; i++)
                    origin[i] = cb.Items[i] as string;
                // 含非字符串项（对象）时不做整体替换，避免破坏数据绑定
                for (int i = 0; i < origin.Length; i++)
                    if (origin[i] == null) return;
                _comboOrigin[cb] = origin;
            }
            if (cb.Items.Count != origin.Length) return; // 动态填充过，跳过

            int sel = cb.SelectedIndex;
            bool changed = false;
            for (int i = 0; i < origin.Length; i++)
            {
                string want = T(origin[i]);
                if (!Equals(cb.Items[i], want)) { cb.Items[i] = want; changed = true; }
            }
            // DropDownList 下替换选中项不会刷新控件缓存的显示文本（会残留旧语言），
            // 必须清空选中再恢复一次，强制从新项取文本。
            if (changed && sel >= 0 && sel < cb.Items.Count)
            {
                cb.SelectedIndex = -1;
                cb.SelectedIndex = sel;
            }
            else if (sel >= 0 && sel < cb.Items.Count)
            {
                cb.SelectedIndex = sel;
            }
        }

        private static void ApplyGrid(DataGridView dgv)
        {
            if (!_gridOrigin.TryGetValue(dgv, out var origin))
            {
                origin = new string[dgv.Columns.Count];
                for (int i = 0; i < dgv.Columns.Count; i++) origin[i] = dgv.Columns[i].HeaderText;
                _gridOrigin[dgv] = origin;
            }
            if (dgv.Columns.Count != origin.Length) return;
            for (int i = 0; i < origin.Length; i++)
                dgv.Columns[i].HeaderText = T(origin[i]);
        }

        private static void ApplyMenuItem(ToolStripItem item)
        {
            if (!_itemOrigin.ContainsKey(item))
                _itemOrigin[item] = item.Text ?? "";
            string src0 = _itemOrigin[item];
            if (!string.IsNullOrEmpty(src0)) item.Text = T(src0);

            if (!(item is ToolStripMenuItem mi)) return;

            if (mi.DropDownItems.Count > 0)
                foreach (ToolStripItem sub in mi.DropDownItems)
                    ApplyMenuItem(sub);
        }
    }
}
