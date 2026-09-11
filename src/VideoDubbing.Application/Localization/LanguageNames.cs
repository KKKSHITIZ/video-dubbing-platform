namespace VideoDubbing.Application.Localization;

/// <summary>Maps ISO-639 language codes to their native display names (fr → French, es → Español…).</summary>
public static class LanguageNames
{
    private static readonly Dictionary<string, string> Native = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English",
        ["fr"] = "French",
        ["es"] = "Español",
        ["de"] = "Deutsch",
        ["it"] = "Italiano",
        ["pt"] = "Português",
        ["nl"] = "Nederlands",
        ["ru"] = "Русский",
        ["hi"] = "हिन्दी",
        ["bn"] = "বাংলা",
        ["zh"] = "中文",
        ["ja"] = "日本語",
        ["ko"] = "한국어",
        ["ar"] = "العربية",
        ["tr"] = "Türkçe",
        ["pl"] = "Polski",
        ["sv"] = "Svenska",
        ["da"] = "Dansk",
        ["nb"] = "Norsk",
        ["fi"] = "Suomi",
        ["cs"] = "Čeština",
        ["hu"] = "Magyar",
        ["ro"] = "Română",
        ["uk"] = "Українська",
        ["el"] = "Ελληνικά"
    };

    /// <summary>Returns the native display name for a language code, or the code itself when unknown.</summary>
    public static string DisplayName(string? code) =>
        string.IsNullOrWhiteSpace(code) ? (code ?? string.Empty) : (Native.TryGetValue(code, out var name) ? name : code);
}