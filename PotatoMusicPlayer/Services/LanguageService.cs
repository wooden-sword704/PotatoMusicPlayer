using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace PotatoMusicPlayer.Services
{
    /// <summary>
    /// JSONベースの表示文字列を管理するサービス。
    /// Resources/Languages/*.json を1言語1ファイルで扱う。
    /// 言語の追加はJSONファイルの追加のみ(コード変更不要)。
    /// 各ファイルは Language.DisplayName で表示名を自己申告する。
    /// </summary>
    public class LanguageService
    {
        public const string DefaultCode = "en-US";

        private readonly Dictionary<string, string> _strings = new Dictionary<string, string>();
        public string CurrentLanguage { get; private set; } = DefaultCode;

        public LanguageService(string languageCode) => Load(languageCode);

        public static string LanguagesDirectory =>
            Path.Combine(AppContext.BaseDirectory, "Resources", "Languages");

        public sealed class LanguageInfo
        {
            public string Code { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
        }

        /// <summary>利用可能な言語をコード順で列挙する。</summary>
        public static List<LanguageInfo> GetAvailableLanguages()
        {
            var result = new List<LanguageInfo>();
            string dir = LanguagesDirectory;
            if (!Directory.Exists(dir))
                return result;

            foreach (string file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var values = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file));
                    if (values == null)
                        continue;
                    string code = Path.GetFileNameWithoutExtension(file);
                    if (!values.TryGetValue("Language.DisplayName", out string display) ||
                        string.IsNullOrWhiteSpace(display))
                        display = code;
                    result.Add(new LanguageInfo { Code = code, DisplayName = display });
                }
                catch
                {
                    // 壊れたファイルは一覧から外す
                }
            }
            return result;
        }

        /// <summary>旧設定(enum数値・enum名)を現在のコードへ正規化する。</summary>
        public static string NormalizeCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return DefaultCode;
            if (code == "0" || string.Equals(code, "Japanese", StringComparison.OrdinalIgnoreCase))
                return "ja-JP";
            if (code == "1" || string.Equals(code, "EnglishUS", StringComparison.OrdinalIgnoreCase))
                return DefaultCode;
            string file = Path.Combine(LanguagesDirectory, code + ".json");
            return File.Exists(file) ? code : DefaultCode;
        }

        public void Load(string languageCode)
        {
            CurrentLanguage = NormalizeCode(languageCode);
            _strings.Clear();
            string path = Path.Combine(LanguagesDirectory, CurrentLanguage + ".json");
            if (!File.Exists(path))
                return;

            var values = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
            if (values == null)
                return;
            foreach (var value in values)
                _strings[value.Key] = value.Value;
        }

        public string Get(string key) => _strings.TryGetValue(key, out var value) ? value : key;
    }
}
