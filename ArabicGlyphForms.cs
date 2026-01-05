using System.Collections.Generic;

namespace KoH2RTLFix
{
    public static class ArabicGlyphForms
    {
        // قاموس الأشكال (iso, ini, med, fin)
        public static readonly Dictionary<char, (char iso, char ini, char med, char fin)> Forms = new Dictionary<char, (char iso, char ini, char med, char fin)>
        {
            // Arabic base letters
            {'ا', ('\uFE8D', '\uFE8D', '\uFE8D', '\uFE8E')},
            {'ب', ('\uFE8F', '\uFE91', '\uFE92', '\uFE90')},
            {'ت', ('\uFE95', '\uFE97', '\uFE98', '\uFE96')},
            {'ث', ('\uFE99', '\uFE9B', '\uFE9C', '\uFE9A')},
            {'ج', ('\uFE9D', '\uFE9F', '\uFEA0', '\uFE9E')},
            {'ح', ('\uFEA1', '\uFEA3', '\uFEA4', '\uFEA2')},
            {'خ', ('\uFEA5', '\uFEA7', '\uFEA8', '\uFEA6')},
            {'د', ('\uFEA9', '\uFEA9', '\uFEA9', '\uFEAA')},
            {'ذ', ('\uFEAB', '\uFEAB', '\uFEAB', '\uFEAC')},
            {'ر', ('\uFEAD', '\uFEAD', '\uFEAD', '\uFEAE')},
            {'ز', ('\uFEAF', '\uFEAF', '\uFEAF', '\uFEB0')},
            {'س', ('\uFEB1', '\uFEB3', '\uFEB4', '\uFEB2')},
            {'ش', ('\uFEB5', '\uFEB7', '\uFEB8', '\uFEB6')},
            {'ص', ('\uFEB9', '\uFEBB', '\uFEBC', '\uFEBA')},
            {'ض', ('\uFEBD', '\uFEBF', '\uFEC0', '\uFEBE')},
            {'ط', ('\uFEC1', '\uFEC3', '\uFEC4', '\uFEC2')},
            {'ظ', ('\uFEC5', '\uFEC7', '\uFEC8', '\uFEC6')},
            {'ع', ('\uFEC9', '\uFECB', '\uFECC', '\uFECA')},
            {'غ', ('\uFECD', '\uFECF', '\uFED0', '\uFECE')},
            {'ف', ('\uFED1', '\uFED3', '\uFED4', '\uFED2')},
            {'ق', ('\uFED5', '\uFED7', '\uFED8', '\uFED6')},
            {'ك', ('\uFED9', '\uFEDB', '\uFEDC', '\uFEDA')},
            {'ل', ('\uFEDD', '\uFEDF', '\uFEE0', '\uFEDE')},
            {'م', ('\uFEE1', '\uFEE3', '\uFEE4', '\uFEE2')},
            {'ن', ('\uFEE5', '\uFEE7', '\uFEE8', '\uFEE6')},
            {'ه', ('\uFEE9', '\uFEEB', '\uFEEC', '\uFEEA')},
            {'و', ('\uFEED', '\uFEED', '\uFEED', '\uFEEE')},
            {'ي', ('\uFEF1', '\uFEF3', '\uFEF4', '\uFEF2')},
            {'ى', ('\uFEEF', '\uFEEF', '\uFEF0', '\uFEF0')},
            {'ئ', ('\uFE89', '\uFE8B', '\uFE8C', '\uFE8A')},
            {'ؤ', ('\uFE85', '\uFE85', '\uFE86', '\uFE86')},
            {'إ', ('\uFE87', '\uFE87', '\uFE88', '\uFE88')},
            {'أ', ('\uFE83', '\uFE83', '\uFE84', '\uFE84')},
            {'آ', ('\uFE81', '\uFE81', '\uFE82', '\uFE82')},
            {'ة', ('\uFE93', '\uFE93', '\uFE93', '\uFE94')},
            
            // Persian/Urdu specific letters
            {'پ', ('\uFB56', '\uFB58', '\uFB59', '\uFB57')},
            {'چ', ('\uFB7A', '\uFB7C', '\uFB7D', '\uFB7B')},
            {'ژ', ('\uFB8A', '\uFB8A', '\uFB8A', '\uFB8B')},
            {'گ', ('\uFB92', '\uFB94', '\uFB95', '\uFB93')},
            {'ک', ('\uFB8E', '\uFB90', '\uFB91', '\uFB8F')},
            
            // Urdu specific
            {'ٹ', ('\uFB66', '\uFB68', '\uFB69', '\uFB67')},
            {'ڈ', ('\uFB88', '\uFB88', '\uFB88', '\uFB89')},
            {'ڑ', ('\uFB8C', '\uFB8C', '\uFB8C', '\uFB8D')},
            {'ں', ('\uFB9E', '\uFB9E', '\uFB9E', '\uFB9F')},
            {'ے', ('\uFBAE', '\uFBAE', '\uFBAF', '\uFBAF')},
            {'ہ', ('\uFBA6', '\uFBA8', '\uFBA9', '\uFBA7')},
        };
    }
}
