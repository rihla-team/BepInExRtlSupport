using BepInEx.Configuration;
using BepInEx.Logging;

namespace KoH2RTLFix
{
    // ========================================
    // نظام التكوين (Configuration System)
    // ========================================
    public class ModConfiguration
    {
        public enum AlignmentOptions
        {
            Auto,
            Right,
            Left,
            Center
        }

        // الإعدادات العامة
        public ConfigEntry<AlignmentOptions> TextAlignment { get; }
        public ConfigEntry<bool> ConvertToEasternArabicNumerals { get; }
        public ConfigEntry<int> CacheSize { get; }
        public ConfigEntry<bool> EnablePerformanceMetrics { get; }
        public ConfigEntry<LogLevel> LoggingLevel { get; }
        public ConfigEntry<bool> EnableDiagnostics { get; }

        // دعم اللغات
        public ConfigEntry<bool> EnableArabic { get; }
        public ConfigEntry<bool> EnablePersian { get; }
        public ConfigEntry<bool> EnableUrdu { get; }

        // معالجة النصوص
        public ConfigEntry<bool> PreserveEnglishNumbers { get; }
        public ConfigEntry<bool> MirrorBrackets { get; }
        public ConfigEntry<bool> ProcessMultilineText { get; }
        public ConfigEntry<bool> IgnoreAngleBracketTags { get; }
        public ConfigEntry<bool> IgnoreCurlyBraceScopes { get; }
        public ConfigEntry<bool> IgnoreSquareBracketScopes { get; }
        public ConfigEntry<string> IgnoredScopes { get; }

        public ModConfiguration(ConfigFile config)
        {
            // الإعدادات العامة
            TextAlignment = config.Bind(
                "General", "TextAlignment", AlignmentOptions.Auto,
                "محاذاة النص: Auto (تلقائي لليمين للعربية), Right (يمين), Left (يسار), Center (وسط)");

            ConvertToEasternArabicNumerals = config.Bind(
                "General", "ConvertToEasternArabicNumerals", false,
                "تحويل الأرقام العربية الغربية (0-9) إلى الأرقام العربية الشرقية (٠-٩)");

            CacheSize = config.Bind(
                "Performance", "CacheSize", 1000,
                new ConfigDescription("الحد الأقصى لعدد النصوص المخزنة في الـ cache",
                    new AcceptableValueRange<int>(100, 10000)));

            EnablePerformanceMetrics = config.Bind(
                "Performance", "EnablePerformanceMetrics", false,
                "تفعيل قياس الأداء وتسجيل الإحصائيات");

            LoggingLevel = config.Bind(
                "Debug", "LoggingLevel", LogLevel.Info,
                "مستوى التسجيل (None, Fatal, Error, Warning, Message, Info, Debug, All)");

            EnableDiagnostics = config.Bind(
                "Debug", "EnableDiagnostics", true,
                "تفعيل الفحص الذاتي عند التشغيل للكشف عن المشاكل");

            // دعم اللغات
            EnableArabic = config.Bind(
                "Languages", "EnableArabic", true,
                "تفعيل دعم اللغة العربية");

            EnablePersian = config.Bind(
                "Languages", "EnablePersian", true,
                "تفعيل دعم اللغة الفارسية");

            EnableUrdu = config.Bind(
                "Languages", "EnableUrdu", true,
                "تفعيل دعم اللغة الأردية");

            // معالجة النصوص
            PreserveEnglishNumbers = config.Bind(
                "TextProcessing", "PreserveEnglishNumbers", true,
                "الحفاظ على الأرقام الإنجليزية في موقعها الصحيح");

            MirrorBrackets = config.Bind(
                "TextProcessing", "MirrorBrackets", true,
                "عكس الأقواس تلقائياً في النصوص RTL");

            ProcessMultilineText = config.Bind(
                "TextProcessing", "ProcessMultilineText", true,
                "معالجة النصوص متعددة السطور");

            IgnoreAngleBracketTags = config.Bind(
                "TextProcessing", "IgnoreAngleBracketTags", true,
                "تجاهل وسوم/تاجات النص بين <> (مثل TextMeshPro RichText). عند التعطيل، سيتم التعامل مع <> كحروف عادية");

            IgnoreCurlyBraceScopes = config.Bind(
                "TextProcessing", "IgnoreCurlyBraceScopes", true,
                "تجاهل النص بين {} (مثل المتغيرات/Placeholders). عند التعطيل، سيتم التعامل مع {} كحروف عادية");

            IgnoreSquareBracketScopes = config.Bind(
                "TextProcessing", "IgnoreSquareBracketScopes", true,
                "تجاهل النص بين [] (مثل الوسوم/المفاتيح). عند التعطيل، سيتم التعامل مع [] كحروف عادية");

            IgnoredScopes = config.Bind(
                "TextProcessing", "IgnoredScopes", "<>{}[]",
                "الأقواس التي يتم تجاهل ما بداخلها تماماً (مثل أكواد الألوان أو المتغيرات). كل زوج يمثل بداية ونهاية. مثال: <>[]{}");
        }
    }
}
