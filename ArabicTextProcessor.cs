using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BepInEx;

namespace KoH2RTLFix
{
    // ========================================
    // محرك معالجة النصوص العربية المحسّن
    // ========================================
    public static class ArabicTextProcessor
    {
        private const char CacheKeySeparator = '\u001F';
        private const char MASK_MARKER = '\uFFFF'; // علامة الحماية للنصوص المحمية

        // الحروف التي لا تتصل بما بعدها
        private static readonly HashSet<char> NonForwardConnectors =
        [
            'ا', 'أ', 'إ', 'آ', 'د', 'ذ', 'ر', 'ز', 'و', 'ؤ', 'ة', 'ى',
            // Persian/Urdu additions
            'ۀ', 'ۃ'
        ];

        private static readonly Dictionary<char, char> PresentationToBaseMap = BuildPresentationToBaseMap();
        private static readonly Dictionary<char, FormType> FormTypeMap = BuildFormTypeMap();

        // ===== Cache Management =====
        private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
        private static long _cacheCount = 0; // استخدام long بدلاً من int للـ atomic operations

        // ===== StringBuilder Pool =====
        private static readonly ConcurrentBag<StringBuilder> _sbPool = new();
        private const int MaxPoolSize = 32;
        private const int MaxStringBuilderCapacity = 8192; // حد أقصى لحجم StringBuilder المحفوظ
        private static long _sbPoolCount = 0;

        // ===== Cache Cleanup =====
        private static int _cleanupInProgress = 0;
        private const int CleanupThresholdMinutes = 5; // تنظيف العناصر الأقدم من 5 دقائق

        // ===== Configuration Cache =====
        private static string _cachedScopes = null;
        private static bool _lastIgnoreAngle = true;
        private static bool _lastIgnoreCurly = true;
        private static bool _lastIgnoreSquare = true;

        private struct CacheEntry
        {
            public string Value;
            public long AccessTime;
        }

        private enum CharType { RTL, LTR, Neutral, Number }
        private enum FormType { Isolated, Initial, Medial, Final }

        // ========================================
        // الدوال المساعدة - Hash & Keys
        // ========================================

        private static uint Fnv1a32(string s)
        {
            unchecked
            {
                uint hash = 2166136261u;
                if (s != null)
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        hash ^= s[i];
                        hash *= 16777619u;
                    }
                }
                return hash;
            }
        }

        private static uint GetProcessingSignature(bool useLigatures)
        {
            unchecked
            {
                uint sig = 0;
                if (useLigatures) sig |= 1u << 0;

                var cfg = KoH2ArabicRTL.PluginConfig;
                if (cfg != null)
                {
                    if (cfg.ConvertToEasternArabicNumerals.Value) sig |= 1u << 1;
                    if (cfg.MirrorBrackets.Value) sig |= 1u << 2;
                    if (cfg.ProcessMultilineText.Value) sig |= 1u << 3;
                    if (cfg.IgnoreAngleBracketTags.Value) sig |= 1u << 4;
                    if (cfg.IgnoreCurlyBraceScopes.Value) sig |= 1u << 5;
                    if (cfg.IgnoreSquareBracketScopes.Value) sig |= 1u << 6;
                    sig ^= Fnv1a32(cfg.IgnoredScopes.Value) * 2654435761u;
                }

                return sig;
            }
        }

        private static string BuildCacheKey(string input, bool useLigatures)
        {
            uint sig = GetProcessingSignature(useLigatures);
            return sig.ToString("X8") + CacheKeySeparator + input;
        }

        private static string TracePreview(string s)
        {
            if (s == null) return "<null>";
            int max = PluginLog.TextTraceMaxChars;
            if (max <= 0) return string.Empty;
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "...";
        }

        // ========================================
        // StringBuilder Pool Management
        // ========================================

        private static StringBuilder RentStringBuilder(int capacity = 256)
        {
            if (_sbPool.TryTake(out var sb))
            {
                Interlocked.Decrement(ref _sbPoolCount);
                sb.Clear();
                if (sb.Capacity < capacity)
                    sb.EnsureCapacity(capacity);
                return sb;
            }
            return new StringBuilder(capacity);
        }

        private static void ReturnStringBuilder(StringBuilder sb)
        {
            if (sb == null) return;
            
            // لا تحتفظ بـ StringBuilder كبير جداً
            if (sb.Capacity > MaxStringBuilderCapacity) 
                return;

            long currentCount = Interlocked.Read(ref _sbPoolCount);
            if (currentCount < MaxPoolSize)
            {
                Interlocked.Increment(ref _sbPoolCount);
                _sbPool.Add(sb);
            }
        }

        // ========================================
        // الدالة الرئيسية - Fix
        // ========================================

        public static string Fix(string input, bool useLigatures = true)
        {
            if (string.IsNullOrEmpty(input)) return input;

            bool trace = PluginLog.TextTraceEnabled;
            if (trace)
            {
                PluginLog.Debug("[RTLTrace] ArabicTextProcessor.Fix IN ligatures=" + useLigatures +
                    " len=" + input.Length +
                    " IN='" + TracePreview(input) + "'");
            }

            var stopwatch = KoH2ArabicRTL.PluginConfig?.EnablePerformanceMetrics?.Value == true
                ? Stopwatch.StartNew() : null;
            bool wasCached = false;

            try
            {
                // التحقق من الـ Cache
                string cacheKey = BuildCacheKey(input, useLigatures);
                if (_cache.TryGetValue(cacheKey, out var entry))
                {
                    // تحديث وقت الوصول
                    _cache[cacheKey] = new CacheEntry { Value = entry.Value, AccessTime = DateTime.UtcNow.Ticks };
                    wasCached = true;
                    
                    if (trace)
                    {
                        bool changed = !string.Equals(entry.Value, input, StringComparison.Ordinal);
                        PluginLog.Debug("[RTLTrace] ArabicTextProcessor.Fix CacheHit changed=" + changed +
                            " OUT='" + TracePreview(entry.Value) + "'");
                    }
                    return entry.Value;
                }

                // المعالجة الفعلية
                string result = ProcessText(input, useLigatures);

                if (trace)
                {
                    bool changed = !string.Equals(result, input, StringComparison.Ordinal);
                    PluginLog.Debug("[RTLTrace] ArabicTextProcessor.Fix CacheMiss changed=" + changed +
                        " OUT='" + TracePreview(result) + "'");
                }

                // إضافة للـ Cache
                AddToCache(cacheKey, result);

                return result;
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Error processing text: {ex.Message}");
                PluginLog.Debug($"Input preview: {input?.Substring(0, Math.Min(50, input?.Length ?? 0))}...");
                return input; // إرجاع النص الأصلي عند الخطأ
            }
            finally
            {
                if (stopwatch != null)
                {
                    stopwatch.Stop();
                    PerformanceMonitor.RecordProcessing(stopwatch.ElapsedMilliseconds, wasCached);
                }
            }
        }

        // ========================================
        // معالجة النص - الخطوات الرئيسية
        // ========================================

        private static string ProcessText(string input, bool useLigatures)
        {
            // 1. حماية النصوص الخاصة (tags, scopes)
            string protectedText = ProtectText(input, out var protectedParts);

            // 2. معالجة النص (تشكيل وعكس)
            string result = FixInternal(protectedText, useLigatures);

            // 3. استعادة النصوص المحمية
            result = RestoreText(result, protectedParts);

            return result;
        }

        private static string FixInternal(string input, bool useLigatures)
        {
            // معالجة النصوص متعددة السطور
            if (KoH2ArabicRTL.PluginConfig?.ProcessMultilineText?.Value == true && input.IndexOf('\n') >= 0)
            {
                return ProcessMultiline(input);
            }

            // كشف إذا كان النص في Visual Order (معكوس ومشكّل مسبقاً)
            bool isVisualOrder = false;
            try
            {
                isVisualOrder = IsLikelyVisualOrder(input);
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Error in IsLikelyVisualOrder: {ex.Message}");
            }

            // تطبيع presentation forms إلى base characters
            input = NormalizePresentationForms(input);

            // إذا كان Visual Order، نحتاج لعكسه أولاً للحصول على Logical Order
            if (isVisualOrder)
            {
                try
                {
                    input = SmartReverse(input);
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"Error in SmartReverse (Un-reverse): {ex.Message}");
                }
            }

            // تحويل الأرقام إن كان مطلوباً
            if (KoH2ArabicRTL.PluginConfig?.ConvertToEasternArabicNumerals?.Value == true)
            {
                input = ConvertToEasternArabicNumerals(input);
            }

            // التحقق من وجود أحرف RTL
            if (!HasRTLLetters(input))
                return input;

            // التشكيل والعكس (دائماً نطبق التشكيل لاتصال الحروف)
            string shaped = ShapeArabicText(input.ToCharArray(), useLigatures);
            return SmartReverse(shaped);
        }

        // ========================================
        // مساعدات الكشف
        // ========================================

        private static bool HasRTLLetters(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (RTLHelper.IsRTL(text[i]))
                    return true;
            }
            return false;
        }

        // ========================================
        // التشكيل العربي (Arabic Shaping)
        // ========================================

        private static string ShapeArabicText(char[] chars, bool useLigatures)
        {
            var sb = RentStringBuilder(chars.Length);

            try
            {
                for (int i = 0; i < chars.Length; i++)
                {
                    char curr = chars[i];

                    // تخطي التشكيل
                    if (RTLHelper.IsDiacritic(curr))
                        continue;

                    // معالجة لام-ألف (Lam-Alef Ligatures)
                    if (useLigatures && TryProcessLamAlef(chars, ref i, sb))
                        continue;

                    // معالجة الحروف العادية
                    if (!ArabicGlyphForms.Forms.ContainsKey(curr))
                    {
                        sb.Append(curr);
                        AppendDiacritics(sb, chars, ref i);
                        continue;
                    }

                    // تحديد شكل الحرف بناءً على موقعه
                    char shaped = GetShapedForm(chars, i, curr);
                    sb.Append(shaped);
                    AppendDiacritics(sb, chars, ref i);
                }

                return sb.ToString();
            }
            finally
            {
                ReturnStringBuilder(sb);
            }
        }

        private static bool TryProcessLamAlef(char[] chars, ref int i, StringBuilder sb)
        {
            char curr = chars[i];
            if (curr != 'ل' || i + 1 >= chars.Length)
                return false;

            char nextChar = chars[i + 1];
            char ligature = GetLamAlefLigature(nextChar);
            
            if (ligature == '\0')
                return false;

            // تحديد الشكل (معزول أو نهائي) بناءً على الحرف السابق
            char prev = GetPreviousNonDiacritic(chars, i);
            bool linkPrev = ArabicGlyphForms.Forms.ContainsKey(prev) && !NonForwardConnectors.Contains(prev);

            if (linkPrev)
                ligature = (char)(ligature + 1); // Final form

            sb.Append(ligature);
            i++; // تخطي الألف
            return true;
        }

        private static char GetLamAlefLigature(char alef)
        {
            return alef switch
            {
                'ا' => '\uFEFB',  // Lam-Alef
                'أ' => '\uFEF7',  // Lam-Alef with Hamza Above
                'إ' => '\uFEF9',  // Lam-Alef with Hamza Below
                'آ' => '\uFEF5',  // Lam-Alef with Madda
                _ => '\0'
            };
        }

        // حروف تحتاج للبقاء على شكلها الأساسي عند العزل (يتم قراءتها من الإعدادات)
        private static HashSet<char> _keepBaseWhenIsolatedCache = null;
        
        private static HashSet<char> GetKeepBaseWhenIsolated()
        {
            // قراءة من الإعدادات وتخزينها في cache
            var configValue = KoH2ArabicRTL.PluginConfig?.KeepBaseWhenIsolated?.Value ?? "ه";
            
            // إعادة بناء الـ cache إذا تغيرت القيمة
            if (_keepBaseWhenIsolatedCache == null)
            {
                _keepBaseWhenIsolatedCache = new HashSet<char>(configValue);
            }
            
            return _keepBaseWhenIsolatedCache;
        }

        private static char GetShapedForm(char[] chars, int i, char curr)
        {
            char prevC = GetPreviousNonDiacritic(chars, i);
            char nextC = GetNextNonDiacritic(chars, i);

            bool linkPrevC = ArabicGlyphForms.Forms.ContainsKey(prevC) && !NonForwardConnectors.Contains(prevC);
            bool linkNextC = ArabicGlyphForms.Forms.ContainsKey(nextC) && nextC != '\0';
            bool canLinkNext = !NonForwardConnectors.Contains(curr);

            var forms = ArabicGlyphForms.Forms[curr];

            if (linkPrevC && linkNextC && canLinkNext)
                return forms.med;
            else if (linkPrevC)
                return forms.fin;
            else if (linkNextC && canLinkNext)
                return forms.ini;
            else
            {
                // للحروف التي لا يدعم الخط شكلها المعزول، نُبقي على الحرف الأساسي
                if (GetKeepBaseWhenIsolated().Contains(curr))
                    return curr;
                return forms.iso;
            }
        }

        // ========================================
        // معالجة النصوص متعددة السطور
        // ========================================

        private static string ProcessMultiline(string input)
        {
            var lines = input.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            for (int i = 0; i < lines.Length; i++)
            {
                if (RTLHelper.HasRTL(lines[i]))
                {
                    lines[i] = FixInternal(lines[i], true);
                }
            }
            
            return string.Join("\n", lines);
        }

        // ========================================
        // تطبيع Presentation Forms
        // ========================================

        private static string NormalizePresentationForms(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            // فحص سريع: هل يوجد presentation forms؟
            bool hasPresentation = false;
            for (int i = 0; i < input.Length; i++)
            {
                if (RTLHelper.IsPresentationForm(input[i]))
                {
                    hasPresentation = true;
                    break;
                }
            }

            if (!hasPresentation) return input;

            var sb = RentStringBuilder(input.Length);
            
            try
            {
                for (int i = 0; i < input.Length; i++)
                {
                    char c = input[i];

                    // تحويل Lam-Alef ligatures
                    if (TryExpandLamAlef(c, sb))
                        continue;

                    // تحويل presentation forms الأخرى
                    if (PresentationToBaseMap.TryGetValue(c, out char baseChar))
                        sb.Append(baseChar);
                    else
                        sb.Append(c);
                }

                return sb.ToString();
            }
            finally
            {
                ReturnStringBuilder(sb);
            }
        }

        private static bool TryExpandLamAlef(char c, StringBuilder sb)
        {
            switch (c)
            {
                case '\uFEF5': // Lam + Alef with Madda (isolated)
                case '\uFEF6': // Lam + Alef with Madda (final)
                    sb.Append('ل').Append('آ');
                    return true;
                case '\uFEF7': // Lam + Alef with Hamza Above (isolated)
                case '\uFEF8': // Lam + Alef with Hamza Above (final)
                    sb.Append('ل').Append('أ');
                    return true;
                case '\uFEF9': // Lam + Alef with Hamza Below (isolated)
                case '\uFEFA': // Lam + Alef with Hamza Below (final)
                    sb.Append('ل').Append('إ');
                    return true;
                case '\uFEFB': // Lam + Alef (isolated)
                case '\uFEFC': // Lam + Alef (final)
                    sb.Append('ل').Append('ا');
                    return true;
                default:
                    return false;
            }
        }

        // ========================================
        // كشف Visual Order
        // ========================================

        private static bool IsLikelyVisualOrder(string input)
        {
            if (string.IsNullOrEmpty(input)) return false;

            // إذا لم يكن هناك presentation forms، فهو ليس visual order
            bool hasPresentationForms = false;
            int conflictScore = 0;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                
                if (!FormTypeMap.TryGetValue(c, out FormType type))
                {
                    if (RTLHelper.IsPresentationForm(c)) 
                        hasPresentationForms = true;
                    continue;
                }

                hasPresentationForms = true;

                // Final form متبوع بحرف عربي = Visual Order بالتأكيد
                if (type == FormType.Final && i + 1 < input.Length)
                {
                    char next = input[i + 1];
                    if (RTLHelper.IsArabic(next))
                        conflictScore += 5;
                }
            }

            // فحص آخر حرف
            if (input.Length > 0)
            {
                char last = input[input.Length - 1];
                if (FormTypeMap.TryGetValue(last, out FormType lastType))
                {
                    // Initial/Medial في النهاية = Visual Order
                    if (lastType == FormType.Initial || lastType == FormType.Medial)
                        conflictScore += 5;
                }
            }

            return hasPresentationForms && conflictScore > 0;
        }

        // ========================================
        // تحويل الأرقام
        // ========================================

        private static string ConvertToEasternArabicNumerals(string input)
        {
            var sb = RentStringBuilder(input.Length);
            bool insideTag = false;

            try
            {
                for (int i = 0; i < input.Length; i++)
                {
                    char c = input[i];

                    // تتبع Tags
                    if (c == '<') insideTag = true;
                    else if (c == '>') insideTag = false;

                    // لا تحوّل الأرقام داخل Tags
                    if (insideTag || !IsConvertibleDigit(input, i, c))
                    {
                        sb.Append(c);
                        continue;
                    }

                    // تحويل إلى رقم عربي شرقي
                    sb.Append((char)(c - '0' + '٠'));
                }

                return sb.ToString();
            }
            finally
            {
                ReturnStringBuilder(sb);
            }
        }

        private static bool IsConvertibleDigit(string text, int index, char c)
        {
            if (c < '0' || c > '9') 
                return false;

            // لا تحوّل إذا كان جزءاً من معرّف (ID)
            if (index > 0)
            {
                char prev = text[index - 1];
                if (prev == '_' || (prev >= 'A' && prev <= 'Z') || (prev >= 'a' && prev <= 'z'))
                    return false;
            }

            return true;
        }

        // ========================================
        // عكس النص الذكي (Smart Reverse)
        // ========================================

        private static string SmartReverse(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            try
            {
                // تقسيم النص إلى segments (RTL/LTR)
                var segments = SegmentText(text);

                // عكس ترتيب الـ segments
                segments.Reverse();

                // بناء النص النهائي
                return BuildReversedText(segments);
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Error in SmartReverse: {ex.Message}");
                return text;
            }
        }

        private static List<(StringBuilder content, CharType type)> SegmentText(string text)
        {
            var segments = new List<(StringBuilder content, CharType type)>();
            StringBuilder current = RentStringBuilder(text.Length / 2);
            StringBuilder weakBuffer = RentStringBuilder(32);
            CharType currentDir = CharType.Neutral;

            foreach (char c in text)
            {
                CharType type = GetCharType(c);
                
                // الأرقام تُعامل كـ LTR
                if (type == CharType.Number) 
                    type = CharType.LTR;

                bool isStrong = type == CharType.RTL || type == CharType.LTR;

                if (!isStrong)
                {
                    // حروف ضعيفة (punctuation, spaces)
                    weakBuffer.Append(c);
                    continue;
                }

                // حرف قوي
                HandleStrongCharacter(c, type, ref currentDir, ref current, weakBuffer, segments);
            }

            // معالجة المتبقي
            FlushRemainingContent(current, weakBuffer, currentDir, segments);

            // إعادة weakBuffer للـ pool
            ReturnStringBuilder(weakBuffer);

            return segments;
        }

        private static void HandleStrongCharacter(
            char c, 
            CharType type, 
            ref CharType currentDir, 
            ref StringBuilder current, 
            StringBuilder weakBuffer, 
            List<(StringBuilder, CharType)> segments)
        {
            if (currentDir == CharType.Neutral)
            {
                // بداية segment جديد
                currentDir = type;
                if (weakBuffer.Length > 0)
                {
                    current.Append(weakBuffer);
                    weakBuffer.Clear();
                }
                current.Append(c);
            }
            else if (type == currentDir)
            {
                // استمرار نفس الـ segment
                if (weakBuffer.Length > 0)
                {
                    current.Append(weakBuffer);
                    weakBuffer.Clear();
                }
                current.Append(c);
            }
            else
            {
                // تغيير الاتجاه
                HandleDirectionChange(c, type, ref currentDir, ref current, weakBuffer, segments);
            }
        }

        private static void HandleDirectionChange(
            char c,
            CharType newType,
            ref CharType currentDir,
            ref StringBuilder current,
            StringBuilder weakBuffer,
            List<(StringBuilder, CharType)> segments)
        {
            if (currentDir == CharType.LTR && newType == CharType.RTL)
            {
                // LTR -> RTL: نقل punctuation للـ RTL segment الجديد
                SplitWeakBuffer(weakBuffer, out var ltrSuffix, out var rtlPrefix);
                
                if (ltrSuffix.Length > 0)
                    current.Append(ltrSuffix);
                
                segments.Add((current, currentDir));
                
                current = RentStringBuilder(32);
                if (rtlPrefix.Length > 0)
                    current.Append(rtlPrefix);
                
                ReturnStringBuilder(ltrSuffix);
                ReturnStringBuilder(rtlPrefix);
                weakBuffer.Clear();
            }
            else
            {
                // RTL -> LTR: الـ weak buffer يبقى مع RTL
                if (weakBuffer.Length > 0)
                {
                    current.Append(weakBuffer);
                    weakBuffer.Clear();
                }
                segments.Add((current, currentDir));
                current = RentStringBuilder(32);
            }

            currentDir = newType;
            current.Append(c);
        }

        private static void SplitWeakBuffer(
            StringBuilder weakBuffer,
            out StringBuilder ltrSuffix,
            out StringBuilder rtlPrefix)
        {
            ltrSuffix = RentStringBuilder(weakBuffer.Length);
            rtlPrefix = RentStringBuilder(weakBuffer.Length);

            for (int i = 0; i < weakBuffer.Length; i++)
            {
                char ch = weakBuffer[i];
                bool isQuote = IsQuotationMark(ch);
                
                if (isQuote)
                    ltrSuffix.Append(ch);
                else if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
                    rtlPrefix.Append(ch);
                else
                    ltrSuffix.Append(ch);
            }
        }

        private static bool IsQuotationMark(char ch)
        {
            return ch == '"' || ch == '\'' || ch == '«' || ch == '»' ||
                   ch == '\u201C' || ch == '\u201D' || ch == '\u2018' || ch == '\u2019';
        }

        private static void FlushRemainingContent(
            StringBuilder current,
            StringBuilder weakBuffer,
            CharType currentDir,
            List<(StringBuilder, CharType)> segments)
        {
            if (weakBuffer.Length > 0)
            {
                if (currentDir == CharType.LTR)
                {
                    // فصل punctuation النهائي
                    SplitWeakBuffer(weakBuffer, out var ltrSuffix, out var trailingRtl);
                    
                    if (ltrSuffix.Length > 0)
                        current.Append(ltrSuffix);
                    
                    ReturnStringBuilder(ltrSuffix);
                    
                    segments.Add((current, currentDir));
                    
                    if (trailingRtl.Length > 0)
                    {
                        var rtlSeg = RentStringBuilder(trailingRtl.Length);
                        rtlSeg.Append(trailingRtl);
                        segments.Add((rtlSeg, CharType.RTL));
                        // Note: rtlSeg will be returned to pool in BuildReversedText after use
                    }
                    
                    ReturnStringBuilder(trailingRtl);
                    current = null;
                }
                else
                {
                    current.Append(weakBuffer);
                    segments.Add((current, currentDir));
                    current = null;
                }
                
                weakBuffer.Clear();
            }

            if (current != null && current.Length > 0)
            {
                segments.Add((current, currentDir));
            }
        }

        private static string BuildReversedText(List<(StringBuilder content, CharType type)> segments)
        {
            var result = RentStringBuilder(segments.Sum(s => s.content.Length));

            try
            {
                foreach (var (content, type) in segments)
                {
                    if (type == CharType.RTL)
                    {
                        // عكس محتوى RTL مع mirror الأقواس
                        for (int i = content.Length - 1; i >= 0; i--)
                        {
                            result.Append(MirrorBracket(content[i]));
                        }
                    }
                    else
                    {
                        // LTR والـ Neutral كما هي
                        result.Append(content);
                    }
                    
                    ReturnStringBuilder(content);
                }

                return result.ToString();
            }
            finally
            {
                ReturnStringBuilder(result);
            }
        }

        // ========================================
        // حماية واستعادة النصوص
        // ========================================

        private static string ProtectText(string text, out List<string> protectedParts)
        {
            string scopes = GetCachedScopes();

            if (string.IsNullOrEmpty(scopes) || scopes.Length % 2 != 0)
            {
                protectedParts = null;
                return text;
            }

            // فحص سريع: هل يوجد أي scope start؟
            if (!HasAnyScopeStart(text, scopes))
            {
                protectedParts = null;
                return text;
            }

            protectedParts = new List<string>();
            var sb = RentStringBuilder(text.Length);

            try
            {
                for (int i = 0; i < text.Length; i++)
                {
                    bool matched = TryMatchScope(text, scopes, ref i, protectedParts, sb);
                    if (!matched)
                        sb.Append(text[i]);
                }

                return sb.ToString();
            }
            finally
            {
                ReturnStringBuilder(sb);
            }
        }

        private static bool HasAnyScopeStart(string text, string scopes)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                for (int s = 0; s < scopes.Length; s += 2)
                {
                    if (ch == scopes[s])
                        return true;
                }
            }
            return false;
        }

        private static bool TryMatchScope(
            string text,
            string scopes,
            ref int i,
            List<string> protectedParts,
            StringBuilder sb)
        {
            for (int s = 0; s < scopes.Length; s += 2)
            {
                char start = scopes[s];
                char end = scopes[s + 1];

                if (text[i] == start)
                {
                    int closeIdx = FindClosingScope(text, i, start, end);
                    
                    if (closeIdx != -1)
                    {
                        string content = text.Substring(i, closeIdx - i + 1);
                        protectedParts.Add(content);

                        // إدراج marker
                        char indexChar = (char)(0xE000 + protectedParts.Count - 1);
                        sb.Append(MASK_MARKER);
                        sb.Append(indexChar);
                        sb.Append(MASK_MARKER);

                        i = closeIdx;
                        return true;
                    }
                }
            }
            return false;
        }

        private static int FindClosingScope(string text, int start, char openChar, char closeChar)
        {
            int depth = 1;
            for (int k = start + 1; k < text.Length; k++)
            {
                if (text[k] == openChar) depth++;
                else if (text[k] == closeChar)
                {
                    depth--;
                    if (depth == 0)
                        return k;
                }
            }
            return -1;
        }

        private static string RestoreText(string text, List<string> protectedParts)
        {
            if (protectedParts == null || protectedParts.Count == 0) 
                return text;

            int totalLength = text.Length;
            for (int i = 0; i < protectedParts.Count; i++)
                totalLength += protectedParts[i].Length;

            var sb = RentStringBuilder(totalLength);

            try
            {
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == MASK_MARKER && i + 2 < text.Length && text[i + 2] == MASK_MARKER)
                    {
                        char indexChar = text[i + 1];
                        int index = indexChar - 0xE000;
                        
                        if (index >= 0 && index < protectedParts.Count)
                        {
                            sb.Append(protectedParts[index]);
                            i += 2;
                            continue;
                        }
                    }
                    sb.Append(text[i]);
                }

                return sb.ToString();
            }
            finally
            {
                ReturnStringBuilder(sb);
            }
        }

        // ========================================
        // دوال مساعدة
        // ========================================

        private static string GetCachedScopes()
        {
            var config = KoH2ArabicRTL.PluginConfig;
            if (config == null) return "<>{}[]";

            bool ignoreAngle = config.IgnoreAngleBracketTags?.Value ?? true;
            bool ignoreCurly = config.IgnoreCurlyBraceScopes?.Value ?? true;
            bool ignoreSquare = config.IgnoreSquareBracketScopes?.Value ?? true;

            if (_cachedScopes == null ||
                ignoreAngle != _lastIgnoreAngle ||
                ignoreCurly != _lastIgnoreCurly ||
                ignoreSquare != _lastIgnoreSquare)
            {
                string scopes = config.IgnoredScopes?.Value ?? "<>{}[]";

                if (!ignoreAngle) scopes = scopes.Replace("<>", string.Empty);
                if (!ignoreCurly) scopes = scopes.Replace("{}", string.Empty);
                if (!ignoreSquare) scopes = scopes.Replace("[]", string.Empty);

                _cachedScopes = scopes;
                _lastIgnoreAngle = ignoreAngle;
                _lastIgnoreCurly = ignoreCurly;
                _lastIgnoreSquare = ignoreSquare;
            }

            return _cachedScopes;
        }

        private static CharType GetCharType(char c)
        {
            // الأقواس = RTL
            if (c == '(' || c == ')' || c == '[' || c == ']' || c == '{' || c == '}' || 
                c == '<' || c == '>' || c == '«' || c == '»')
                return CharType.RTL;

            // Markers = LTR
            if (c == MASK_MARKER || (c >= 0xE000 && c <= 0xF8FF))
                return CharType.LTR;

            // الأرقام
            if (RTLHelper.IsNumber(c))
                return CharType.Number;

            // العربية
            if (RTLHelper.IsArabic(c) || RTLHelper.IsPresentationForm(c))
                return CharType.RTL;

            // اللاتينية
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                return CharType.LTR;

            // محايد
            return CharType.Neutral;
        }

        private static char MirrorBracket(char c)
        {
            if (KoH2ArabicRTL.PluginConfig?.MirrorBrackets?.Value != true)
                return c;

            return c switch
            {
                '(' => ')',
                ')' => '(',
                '[' => ']',
                ']' => '[',
                '{' => '}',
                '}' => '{',
                '<' => '>',
                '>' => '<',
                '«' => '»',
                '»' => '«',
                _ => c
            };
        }

        private static char GetPreviousNonDiacritic(char[] chars, int index)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                if (!RTLHelper.IsDiacritic(chars[i]))
                    return chars[i];
            }
            return '\0';
        }

        private static char GetNextNonDiacritic(char[] chars, int index)
        {
            for (int i = index + 1; i < chars.Length; i++)
            {
                if (!RTLHelper.IsDiacritic(chars[i]))
                    return chars[i];
            }
            return '\0';
        }

        private static void AppendDiacritics(StringBuilder sb, char[] chars, ref int index)
        {
            while (index + 1 < chars.Length && RTLHelper.IsDiacritic(chars[index + 1]))
            {
                sb.Append(chars[++index]);
            }
        }

        // ========================================
        // إدارة الـ Cache
        // ========================================

        private static void AddToCache(string key, string value)
        {
            int maxSize = KoH2ArabicRTL.PluginConfig?.CacheSize?.Value ?? 1000;
            long currentCount = Interlocked.Increment(ref _cacheCount);

            if (currentCount > maxSize)
            {
                Interlocked.Exchange(ref _cacheCount, maxSize / 2);
                CleanupCacheAsync(maxSize / 2);
            }

            _cache[key] = new CacheEntry
            {
                Value = value,
                AccessTime = DateTime.UtcNow.Ticks
            };
        }

        private static void CleanupCacheAsync(int targetSize)
        {
            if (Interlocked.CompareExchange(ref _cleanupInProgress, 1, 0) != 0)
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    CleanupCacheInternal(targetSize);
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"Error in cache cleanup: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _cleanupInProgress, 0);
                }
            });
        }

        private static void CleanupCacheInternal(int targetSize)
        {
            int toRemove = _cache.Count - targetSize;
            if (toRemove <= 0) return;

            long threshold = DateTime.UtcNow.Ticks - TimeSpan.FromMinutes(CleanupThresholdMinutes).Ticks;
            int removed = 0;

            // حذف العناصر القديمة
            foreach (var kvp in _cache)
            {
                if (kvp.Value.AccessTime < threshold)
                {
                    _cache.TryRemove(kvp.Key, out _);
                    removed++;
                    if (removed >= toRemove) break;
                }
            }

            // إذا لم يكفِ، احذف عشوائياً
            if (removed < toRemove)
            {
                foreach (var kvp in _cache)
                {
                    _cache.TryRemove(kvp.Key, out _);
                    removed++;
                    if (removed >= toRemove) break;
                }
            }

            Interlocked.Exchange(ref _cacheCount, _cache.Count);
            PluginLog.Debug($"Cache cleaned: removed {removed} entries");
        }

        public static void ClearCache()
        {
            _cache.Clear();
            Interlocked.Exchange(ref _cacheCount, 0);
            PluginLog.Info("Text cache cleared.");
        }

        // ========================================
        // Persistent Cache
        // ========================================

        public static void LoadPersistentCache()
        {
            if (KoH2ArabicRTL.PluginConfig?.EnablePersistentCache?.Value != true) return;
            
            try
            {
                var fileName = KoH2ArabicRTL.PluginConfig.PersistentCacheFileName?.Value ?? "rtl_cache.txt";
                var path = Path.Combine(BepInEx.Paths.ConfigPath, fileName);
                
                if (!File.Exists(path)) return;

                var lines = File.ReadAllLines(path);
                int loaded = 0;
                int skippedLegacy = 0;

                foreach (var line in lines)
                {
                    var parts = line.Split(new[] { '\t' }, 2);
                    if (parts.Length != 2) continue;

                    try
                    {
                        var key = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
                        var value = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));

                        if (string.IsNullOrEmpty(key) || key.IndexOf(CacheKeySeparator) < 0)
                        {
                            skippedLegacy++;
                            continue;
                        }

                        _cache[key] = new CacheEntry { Value = value, AccessTime = DateTime.UtcNow.Ticks };
                        loaded++;
                    }
                    catch { /* تجاهل الأسطر التالفة */ }
                }

                Interlocked.Exchange(ref _cacheCount, _cache.Count);
                PluginLog.Info($"Loaded {loaded} entries from persistent cache." + 
                    (skippedLegacy > 0 ? $" SkippedLegacy={skippedLegacy}." : string.Empty));
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Failed to load persistent cache: {ex.Message}");
            }
        }

        public static void SavePersistentCache()
        {
            if (KoH2ArabicRTL.PluginConfig?.EnablePersistentCache?.Value != true) return;
            
            try
            {
                var fileName = KoH2ArabicRTL.PluginConfig.PersistentCacheFileName?.Value ?? "rtl_cache.txt";
                var path = Path.Combine(BepInEx.Paths.ConfigPath, fileName);
                var sb = RentStringBuilder();

                try
                {
                    foreach (var kvp in _cache)
                    {
                        var key64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(kvp.Key));
                        var val64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(kvp.Value.Value));
                        sb.AppendLine(key64 + "\t" + val64);
                    }
                    
                    File.WriteAllText(path, sb.ToString());
                    PluginLog.Info($"Saved {_cache.Count} entries to persistent cache.");
                }
                finally
                {
                    ReturnStringBuilder(sb);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Failed to save persistent cache: {ex.Message}");
            }
        }

        // ========================================
        // بناء الخرائط الثابتة
        // ========================================

        private static Dictionary<char, char> BuildPresentationToBaseMap()
        {
            var map = new Dictionary<char, char>();

            foreach (var kvp in ArabicGlyphForms.Forms)
            {
                char baseChar = kvp.Key;
                var forms = kvp.Value;

                if (!map.ContainsKey(forms.iso)) map[forms.iso] = baseChar;
                if (!map.ContainsKey(forms.ini)) map[forms.ini] = baseChar;
                if (!map.ContainsKey(forms.med)) map[forms.med] = baseChar;
                if (!map.ContainsKey(forms.fin)) map[forms.fin] = baseChar;
            }

            return map;
        }

        private static Dictionary<char, FormType> BuildFormTypeMap()
        {
            var map = new Dictionary<char, FormType>();
            
            foreach (var kvp in ArabicGlyphForms.Forms)
            {
                if (kvp.Value.iso != '\0') map[kvp.Value.iso] = FormType.Isolated;
                if (kvp.Value.ini != '\0') map[kvp.Value.ini] = FormType.Initial;
                if (kvp.Value.med != '\0') map[kvp.Value.med] = FormType.Medial;
                if (kvp.Value.fin != '\0') map[kvp.Value.fin] = FormType.Final;
            }

            // إضافة لام-ألف يدوياً
            map['\uFEFB'] = FormType.Isolated; map['\uFEFC'] = FormType.Final;
            map['\uFEF5'] = FormType.Isolated; map['\uFEF6'] = FormType.Final;
            map['\uFEF7'] = FormType.Isolated; map['\uFEF8'] = FormType.Final;
            map['\uFEF9'] = FormType.Isolated; map['\uFEFA'] = FormType.Final;

            return map;
        }
    }
}