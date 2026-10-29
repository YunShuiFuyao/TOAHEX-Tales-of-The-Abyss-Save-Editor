using System;

namespace TOAHEX
{
    public enum Language
    {
        CN = 0,
        JP = 1,
        EN = 2
    }

    public static class LanguageConfig
    {
        private static Language _current = Language.CN;

        public static Language Current
        {
            get => _current;
            set
            {
                if (_current != value)
                {
                    _current = value;
                    RaiseChanged();
                }
            }
        }

        public static event EventHandler LanguageChanged;

        public static void RaiseChanged()
        {
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>语言标签（自身语言显示）。</summary>
        public static string DisplayName(Language lang)
        {
            switch (lang)
            {
                case Language.JP: return "日本語";
                case Language.EN: return "English";
                default: return "中文";
            }
        }
    }
}
