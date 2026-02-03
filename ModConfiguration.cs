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
        public ConfigEntry<bool> EnablePersistentCache { get; }
        public ConfigEntry<string> PersistentCacheFileName { get; }
        public ConfigEntry<LogLevel> LoggingLevel { get; }
        public ConfigEntry<bool> EnableTextTrace { get; }
        public ConfigEntry<bool> TraceAllTextAssignments { get; }
        public ConfigEntry<int> TextTraceMaxChars { get; }
        public ConfigEntry<bool> EnableSeparateLogFile { get; }
        public ConfigEntry<string> SeparateLogFileName { get; }

        // دعم اللغات
        public ConfigEntry<bool> EnableArabic { get; }
        public ConfigEntry<bool> EnablePersian { get; }
        public ConfigEntry<bool> EnableUrdu { get; }

        // معالجة النصوص
        public ConfigEntry<bool> MirrorBrackets { get; }
        public ConfigEntry<bool> ProcessMultilineText { get; }
        public ConfigEntry<bool> IgnoreAngleBracketTags { get; }
        public ConfigEntry<bool> IgnoreCurlyBraceScopes { get; }
        public ConfigEntry<bool> IgnoreSquareBracketScopes { get; }
        public ConfigEntry<string> IgnoredScopes { get; }
        public ConfigEntry<string> KeepBaseWhenIsolated { get; }

        public ModConfiguration(ConfigFile config)
        {
            // الإعدادات العامة
            TextAlignment = config.Bind(
                "General", "TextAlignment", AlignmentOptions.Right,
                "محاذاة النص: Auto (معالجة RTL بدون تغيير المحاذاة الأصلية), Right (إجبار اليمين للنصوص العربية), Left (يسار), Center (وسط)");

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

            EnablePersistentCache = config.Bind(
                "Performance", "EnablePersistentCache", false,
                "تفعيل حفظ/تحميل الكاش من ملف عند بدء التشغيل/الإغلاق (قد يسرّع في الجلسات القادمة)");

            PersistentCacheFileName = config.Bind(
                "Performance", "PersistentCacheFileName", "KoH2RTLFix.cache",
                "اسم ملف الكاش الدائم (سيتم حفظه داخل مجلد BepInEx/config) ويمكن تغييره لكل لعبة");

            LoggingLevel = config.Bind(
                "Debug", "LoggingLevel", LogLevel.Info,
                "مستوى التسجيل (None, Fatal, Error, Warning, Message, Info, Debug, All)");

            EnableSeparateLogFile = config.Bind(
                "Debug", "EnableSeparateLogFile", false,
                "حفظ سجلات الإضافة في ملف منفصل داخل مجلد BepInEx/cache");

            SeparateLogFileName = config.Bind(
                "Debug", "SeparateLogFileName", "KoH2RTLFix.plugin.log",
                "اسم ملف اللوج المنفصل (سيتم حفظه داخل BepInEx/cache)");

            EnableTextTrace = config.Bind(
                "Debug", "EnableTextTrace", false,
                "تفعيل تتبع النصوص: تسجيل النص قبل/بعد المعالجة لتتبع ما يحدث (قد ينتج log كبير)");

            TraceAllTextAssignments = config.Bind(
                "Debug", "TraceAllTextAssignments", false,
                "تتبع جميع تعيينات النص حتى لو كان إنجليزي/بدون RTL (قد ينتج log كبير جداً)");

            TextTraceMaxChars = config.Bind(
                "Debug", "TextTraceMaxChars", 220,
                new ConfigDescription("الحد الأقصى لطول النص المسجل في تتبع النصوص",
                    new AcceptableValueRange<int>(50, 4000)));

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

            KeepBaseWhenIsolated = config.Bind(
                "TextProcessing", "KeepBaseWhenIsolated", "ه",
                "حروف تُبقى على شكلها الأساسي عند العزل (لا تتحول للشكل المعزول). مفيد للحروف التي لا يدعم الخط شكلها المعزول. مثال: ه. لإضافة حروف أخرى: اكتب الحروف متتالية بدون فواصل");
        }
    }
}
