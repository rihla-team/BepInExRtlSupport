using System.Linq;

namespace KoH2RTLFix
{
    // ========================================
    // دوال مساعدة للتحقق من الأحرف
    // ========================================
    public static class RTLHelper
    {
        // نطاقات Unicode للغات RTL
        public static bool IsArabic(char c) =>
            (c >= 0x0600 && c <= 0x06FF) ||  // Arabic
            (c >= 0x0750 && c <= 0x077F) ||  // Arabic Supplement
            (c >= 0x08A0 && c <= 0x08FF) ||  // Arabic Extended-A
            (c >= 0xFB50 && c <= 0xFDFF) ||  // Arabic Presentation Forms-A
            (c >= 0xFE70 && c <= 0xFEFF);    // Arabic Presentation Forms-B

        // Persian-specific characters (not shared with standard Arabic)
        public static bool IsPersianSpecific(char c) =>
            c == 'پ' || c == 'چ' || c == 'ژ' || c == 'گ' || c == 'ک' || c == 'ی';

        // Urdu-specific characters (not shared with standard Arabic)  
        public static bool IsUrduSpecific(char c) =>
            c == 'ٹ' || c == 'ڈ' || c == 'ڑ' || c == 'ں' || c == 'ے' || c == 'ہ';

        // Check if char is Persian (includes shared Arabic range + Persian-specific)
        public static bool IsPersian(char c) =>
            IsPersianSpecific(c) || IsArabic(c);

        // Check if char is Urdu (includes shared Arabic range + Urdu-specific)
        public static bool IsUrdu(char c) =>
            IsUrduSpecific(c) || IsArabic(c);

        public static bool IsRTL(char c)
        {
            if (KoH2ArabicRTL.PluginConfig == null) return IsArabic(c);

            // Check language-specific characters first to avoid overlap issues
            // Persian-specific chars only count if Persian is enabled
            if (KoH2ArabicRTL.PluginConfig.EnablePersian.Value && IsPersianSpecific(c))
                return true;

            // Urdu-specific chars only count if Urdu is enabled
            if (KoH2ArabicRTL.PluginConfig.EnableUrdu.Value && IsUrduSpecific(c))
                return true;

            // Shared Arabic range - check if any language using it is enabled
            if (IsArabic(c))
            {
                return KoH2ArabicRTL.PluginConfig.EnableArabic.Value ||
                       KoH2ArabicRTL.PluginConfig.EnablePersian.Value ||
                       KoH2ArabicRTL.PluginConfig.EnableUrdu.Value;
            }

            return false;
        }

        public static bool HasRTL(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;

            // If Eastern Arabic numeral conversion is enabled, numbers essentially become RTL 'triggers' 
            // because they need processing
            bool checkNumbers = KoH2ArabicRTL.PluginConfig?.ConvertToEasternArabicNumerals?.Value == true;

            foreach (char c in s)
            {
                if (IsRTL(c)) return true;
                if (checkNumbers && IsNumber(c)) return true;
            }
            return false;
        }

        public static bool IsNumber(char c) =>
            (c >= '0' && c <= '9') || (c >= '٠' && c <= '٩');

        public static bool IsDiacritic(char c) =>
            (c >= 0x064B && c <= 0x0652) ||  // Arabic diacritics
            (c >= 0x0610 && c <= 0x061A) ||  // Arabic additional marks
            c == 0x0670;                      // Superscript Alef

        public static bool IsPresentationForm(char c) =>
            (c >= 0xFE70 && c <= 0xFEFF) ||  // Arabic Presentation Forms-B
            (c >= 0xFB50 && c <= 0xFDFF);    // Arabic Presentation Forms-A

        public static bool AlreadyFixed(string s) =>
            !string.IsNullOrEmpty(s) && s.Any(IsPresentationForm);
    }
}
