using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace KoH2RTLFix
{
    // ========================================
    // محرك معالجة النصوص المحسّن
    // ========================================
    public static class ArabicTextProcessor
    {
        // الحروف التي لا تتصل بما بعدها
        private static readonly HashSet<char> NonForwardConnectors =
        [
            'ا', 'أ', 'إ', 'آ', 'د', 'ذ', 'ر', 'ز', 'و', 'ؤ', 'ة', 'ى',
            // Persian/Urdu additions
            'ۀ', 'ۃ'
        ];

        private static readonly Dictionary<char, char> PresentationToBaseMap = BuildPresentationToBaseMap();

        // الـ Cache المحسّن باستخدام ConcurrentDictionary (Thread-safe بدون lock)
        private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
        private static int _cacheCount = 0;

        // StringBuilder Pool لإعادة الاستخدام
        private static readonly ConcurrentBag<StringBuilder> _sbPool = new();
        private const int MaxPoolSize = 32;

        private struct CacheEntry
        {
            public string Value;
            public long AccessTime;
        }

        // استعارة StringBuilder من الـ Pool
        private static StringBuilder RentStringBuilder(int capacity = 256)
        {
            if (_sbPool.TryTake(out var sb))
            {
                sb.Clear();
                if (sb.Capacity < capacity)
                    sb.EnsureCapacity(capacity);
                return sb;
            }
            return new StringBuilder(capacity);
        }

        // إرجاع StringBuilder للـ Pool
        private static void ReturnStringBuilder(StringBuilder sb)
        {
            if (sb == null || sb.Capacity > 8192) return; // لا تحتفظ بـ StringBuilder كبير جداً
            if (_sbPool.Count < MaxPoolSize)
                _sbPool.Add(sb);
        }

        // Forms moving to ArabicGlyphForms.cs


        public static string Fix(string input, bool useLigatures = true)
        {
            if (string.IsNullOrEmpty(input)) return input;

            var stopwatch = KoH2ArabicRTL.PluginConfig?.EnablePerformanceMetrics?.Value == true
                ? Stopwatch.StartNew() : null;
            bool wasCached = false;

            try
            {
                // التحقق من الـ Cache (بدون lock - ConcurrentDictionary thread-safe)
                if (_cache.TryGetValue(input, out var entry))
                {
                    // تحديث وقت الوصول (atomic operation)
                    _cache[input] = new CacheEntry { Value = entry.Value, AccessTime = DateTime.UtcNow.Ticks };
                    wasCached = true;
                    return entry.Value;
                }

                // 1. Mask ignored scopes
                string protectedText = ProtectText(input, out var protectedParts);

                // 2. Process (Shape and Reverse)
                string result = FixInternal(protectedText, useLigatures);

                // 3. Restore ignored scopes
                result = RestoreText(result, protectedParts);

                // إضافة للـ Cache (بدون lock)
                int maxSize = KoH2ArabicRTL.PluginConfig?.CacheSize?.Value ?? 1000;
                int currentCount = Interlocked.Increment(ref _cacheCount);

                if (currentCount > maxSize)
                {
                    // تنظيف الـ Cache في thread منفصل لتجنب التأخير
                    Interlocked.Exchange(ref _cacheCount, maxSize / 2);
                    CleanupCacheAsync(maxSize / 2);
                }

                _cache[input] = new CacheEntry
                {
                    Value = result,
                    AccessTime = DateTime.UtcNow.Ticks
                };

                return result;
            }
            catch (Exception ex)
            {
                KoH2ArabicRTL.Log?.LogError($"Error processing text: {ex.Message}");
                KoH2ArabicRTL.Log?.LogDebug($"Input: {input?.Substring(0, Math.Min(50, input?.Length ?? 0))}...");
                return input; // إرجاع النص الأصلي في حالة الخطأ
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

        private static int _cleanupInProgress = 0;

        private static void CleanupCacheAsync(int targetSize)
        {
            // تجنب تشغيل عدة عمليات تنظيف متزامنة
            if (Interlocked.CompareExchange(ref _cleanupInProgress, 1, 0) != 0)
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    CleanupCacheInternal(targetSize);
                }
                finally
                {
                    Interlocked.Exchange(ref _cleanupInProgress, 0);
                }
            });
        }

        private static void CleanupCacheInternal(int targetSize)
        {
            // تنظيف سريع بدون LINQ - نحذف أقدم العناصر
            int toRemove = _cache.Count - targetSize;
            if (toRemove <= 0) return;

            long threshold = DateTime.UtcNow.Ticks - TimeSpan.TicksPerMinute * 5; // 5 دقائق
            int removed = 0;

            foreach (var kvp in _cache)
            {
                if (kvp.Value.AccessTime < threshold)
                {
                    _cache.TryRemove(kvp.Key, out _);
                    removed++;
                    if (removed >= toRemove) break;
                }
            }

            // إذا لم نحذف ما يكفي، نحذف بشكل عشوائي
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
            KoH2ArabicRTL.Log?.LogDebug($"Cache cleaned: removed {removed} entries");
        }

        private static string FixInternal(string input, bool useLigatures)
        {
            // معالجة النصوص متعددة السطور
            if (KoH2ArabicRTL.PluginConfig?.ProcessMultilineText?.Value == true && input.IndexOf('\n') >= 0)
            {
                return ProcessMultiline(input);
            }

            // 0. Normalize pre-shaped / presentation form characters to base letters
            // so shaping + reversing works correctly even if the source text is already "visually shaped".
            input = NormalizePresentationForms(input);

            // 1. تحويل الأرقام إن كان مطلوباً
            // تحويل الأرقام العربية الغربية إلى الأرقام العربية الشرقية
            if (KoH2ArabicRTL.PluginConfig?.ConvertToEasternArabicNumerals?.Value == true)
            {
                input = ConvertToEasternArabicNumerals(input);
            }

            char[] chars = input.ToCharArray();
            var sb = RentStringBuilder(chars.Length);

            for (int i = 0; i < chars.Length; i++)
            {
                char curr = chars[i];

                // تخطي التشكيل
                if (RTLHelper.IsDiacritic(curr))
                    continue;

                // معالجة اللام ألف (Lam-Alef Ligatures)
                if (useLigatures && curr == 'ل' && i + 1 < chars.Length)
                {
                    char nextChar = chars[i + 1];
                    // Skip diacritics

                    char ligature = '\0';
                    if (nextChar == 'ا') ligature = '\uFEFB';      // Laa
                    else if (nextChar == 'أ') ligature = '\uFEF7'; // Laa with Hamza Above
                    else if (nextChar == 'إ') ligature = '\uFEF9'; // Laa with Hamza Below
                    else if (nextChar == 'آ') ligature = '\uFEF5'; // Laa with Madda

                    if (ligature != '\0')
                    {
                        char prev = GetPreviousNonDiacritic(chars, i);
                        bool linkPrev = ArabicGlyphForms.Forms.ContainsKey(prev) && !NonForwardConnectors.Contains(prev);

                        if (linkPrev)
                        {
                            // Use Final form
                            ligature = (char)(ligature + 1);
                        }

                        sb.Append(ligature);
                        i++; // Skip the Alef
                        continue;
                    }
                }

                if (!ArabicGlyphForms.Forms.ContainsKey(curr))
                {
                    sb.Append(curr);
                    AppendDiacritics(sb, chars, ref i);
                    continue;
                }

                char prevC = GetPreviousNonDiacritic(chars, i);
                char nextC = GetNextNonDiacritic(chars, i);

                bool linkPrevC = ArabicGlyphForms.Forms.ContainsKey(prevC) && !NonForwardConnectors.Contains(prevC);
                bool linkNextC = ArabicGlyphForms.Forms.ContainsKey(nextC) && nextC != '\0';

                bool canLinkNext = !NonForwardConnectors.Contains(curr);

                (char iso, char ini, char med, char fin) = ArabicGlyphForms.Forms[curr];

                if (linkPrevC && linkNextC && canLinkNext)
                    sb.Append(med);
                else if (linkPrevC)
                    sb.Append(fin);
                else if (linkNextC && canLinkNext)
                    sb.Append(ini);
                else
                    sb.Append(iso);

                AppendDiacritics(sb, chars, ref i);
            }

            string shaped = sb.ToString();
            ReturnStringBuilder(sb);
            return SmartReverse(shaped);
        }

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

        private static string NormalizePresentationForms(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

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

            var sb = new StringBuilder(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];

                // Lam-Alef ligatures (presentation forms) need expansion to two base letters
                switch (c)
                {
                    case '\uFEF5': // Lam + Alef with Madda Above (isolated)
                    case '\uFEF6': // Lam + Alef with Madda Above (final)
                        sb.Append('ل');
                        sb.Append('آ');
                        continue;
                    case '\uFEF7': // Lam + Alef with Hamza Above (isolated)
                    case '\uFEF8': // Lam + Alef with Hamza Above (final)
                        sb.Append('ل');
                        sb.Append('أ');
                        continue;
                    case '\uFEF9': // Lam + Alef with Hamza Below (isolated)
                    case '\uFEFA': // Lam + Alef with Hamza Below (final)
                        sb.Append('ل');
                        sb.Append('إ');
                        continue;
                    case '\uFEFB': // Lam + Alef (isolated)
                    case '\uFEFC': // Lam + Alef (final)
                        sb.Append('ل');
                        sb.Append('ا');
                        continue;
                }

                if (PresentationToBaseMap.TryGetValue(c, out char baseChar))
                {
                    sb.Append(baseChar);
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

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

        private static string ConvertToEasternArabicNumerals(string input)
        {
            var sb = new StringBuilder(input.Length);
            bool insideTag = false;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];

                if (c == '<')
                {
                    insideTag = true;
                    sb.Append(c);
                    continue;
                }
                if (c == '>')
                {
                    insideTag = false;
                    sb.Append(c);
                    continue;
                }

                if (insideTag)
                {
                    sb.Append(c);
                    continue;
                }

                if (c >= '0' && c <= '9')
                {
                    // Context check: Don't convert if part of an ID/Tag identifier.
                    // Look behind for '_' or Latin letters.
                    bool isId = false;
                    if (i > 0)
                    {
                        char prev = input[i - 1];
                        if (prev == '_' || (prev >= 'A' && prev <= 'Z') || (prev >= 'a' && prev <= 'z'))
                        {
                            isId = true;
                        }
                    }

                    if (!isId)
                        sb.Append((char)(c - '0' + '٠'));
                    else
                        sb.Append(c);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
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

        private static char MirrorBracket(char c)
        {
            switch (c)
            {
                case '(': return ')';
                case ')': return '(';
                case '[': return ']';
                case ']': return '[';
                case '{': return '}';
                case '}': return '{';
                case '<': return '>';
                case '>': return '<';
                case '«': return '»';
                case '»': return '«';
                default: return c;
            }
        }

        // تحديد نوع الحرف للـ bidirectional algorithm
        private enum CharType { RTL, LTR, Neutral, Number }

        // Placeholder marker
        private const char MASK_MARKER = '\uFFFF';

        // Cache للإعدادات (تُحدَّث عند تغييرها فقط)
        private static string _cachedScopes = null;
        private static bool _lastIgnoreAngle = true;
        private static bool _lastIgnoreCurly = true;
        private static bool _lastIgnoreSquare = true;

        private static string GetCachedScopes()
        {
            var config = KoH2ArabicRTL.PluginConfig;
            if (config == null) return "<>{}[]";

            bool ignoreAngle = config.IgnoreAngleBracketTags?.Value ?? true;
            bool ignoreCurly = config.IgnoreCurlyBraceScopes?.Value ?? true;
            bool ignoreSquare = config.IgnoreSquareBracketScopes?.Value ?? true;

            // التحقق من تغيير الإعدادات
            if (_cachedScopes == null || 
                ignoreAngle != _lastIgnoreAngle || 
                ignoreCurly != _lastIgnoreCurly || 
                ignoreSquare != _lastIgnoreSquare)
            {
                string scopes = config.IgnoredScopes?.Value ?? "<>{}[]";

                if (!ignoreAngle)
                    scopes = scopes.Replace("<>", string.Empty);
                if (!ignoreCurly)
                    scopes = scopes.Replace("{}", string.Empty);
                if (!ignoreSquare)
                    scopes = scopes.Replace("[]", string.Empty);

                _cachedScopes = scopes;
                _lastIgnoreAngle = ignoreAngle;
                _lastIgnoreCurly = ignoreCurly;
                _lastIgnoreSquare = ignoreSquare;
            }

            return _cachedScopes;
        }

        private static CharType GetCharType(char c)
        {
            // Treat brackets as Separators/RTL based on context? 
            // Current strict logic: Brackets are RTL to mirror, but separation handles placement.
            if (c == '(' || c == ')' || c == '[' || c == ']' || c == '{' || c == '}' || c == '<' || c == '>' || c == '«' || c == '»')
                return CharType.RTL;

            // Treat Mask Markers and PUA (indices) as LTR so they stay as a block
            if (c == MASK_MARKER || (c >= 0xE000 && c <= 0xF8FF))
                return CharType.LTR;

            // Numbers must be checked BEFORE Arabic because Eastern Arabic numerals are in Arabic block
            if (RTLHelper.IsNumber(c))
                return CharType.Number;

            // RTL characters (Arabic, Persian, Urdu, etc.)
            if (RTLHelper.IsArabic(c) || RTLHelper.IsPresentationForm(c))
                return CharType.RTL;

            // Latin letters
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                return CharType.LTR;

            // Neutral (spaces, punctuation, etc.)
            return CharType.Neutral;
        }

        private static string ProtectText(string text, out List<string> protectedParts)
        {
            protectedParts = new List<string>();
            string scopes = GetCachedScopes();

            if (string.IsNullOrEmpty(scopes) || scopes.Length % 2 != 0)
                return text;

            var sb = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                bool matched = false;
                // Check for start of any scope
                for (int s = 0; s < scopes.Length; s += 2)
                {
                    char start = scopes[s];
                    char end = scopes[s + 1];

                    if (text[i] == start)
                    {
                        // Find closing
                        int closeIdx = -1;
                        int depth = 1;
                        for (int k = i + 1; k < text.Length; k++)
                        {
                            if (text[k] == start) depth++;
                            else if (text[k] == end)
                            {
                                depth--;
                                if (depth == 0)
                                {
                                    closeIdx = k;
                                    break;
                                }
                            }
                        }

                        if (closeIdx != -1)
                        {
                            // Found complete scope
                            string content = text.Substring(i, closeIdx - i + 1);
                            protectedParts.Add(content);

                            // Insert Mask: MARKER + Index(PUA) + MARKER
                            // Use 0xE000 + index
                            char indexChar = (char)(0xE000 + protectedParts.Count - 1);
                            sb.Append(MASK_MARKER);
                            sb.Append(indexChar);
                            sb.Append(MASK_MARKER);

                            i = closeIdx; // Advance
                            matched = true;
                            break;
                        }
                    }
                }

                if (!matched)
                {
                    sb.Append(text[i]);
                }
            }
            return sb.ToString();
        }

        private static string RestoreText(string text, List<string> protectedParts)
        {
            if (protectedParts == null || protectedParts.Count == 0) return text;

            int totalLength = text.Length;
            for (int i = 0; i < protectedParts.Count; i++)
                totalLength += protectedParts[i].Length;
            var sb = new StringBuilder(totalLength);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == MASK_MARKER && i + 2 < text.Length && text[i + 2] == MASK_MARKER)
                {
                    char indexChar = text[i + 1];
                    int index = indexChar - 0xE000;
                    if (index >= 0 && index < protectedParts.Count)
                    {
                        sb.Append(protectedParts[index]);
                        i += 2; // Skip Marker+Index+Marker
                        continue;
                    }
                }
                sb.Append(text[i]);
            }
            return sb.ToString();
        }

        private static string SmartReverse(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var segments = new List<(StringBuilder content, CharType type)>();
            StringBuilder current = RentStringBuilder(text.Length / 2);
            StringBuilder weakBuffer = RentStringBuilder(32);
            CharType currentDir = CharType.Neutral;

            foreach (char c in text)
            {
                // Classification
                CharType type = GetCharType(c);
                // Treat Numbers as LTR for segmentation if they are not specifically handled otherwise
                if (type == CharType.Number) type = CharType.LTR;

                bool isStrong = type == CharType.RTL || type == CharType.LTR;

                if (!isStrong)
                {
                    // Weak/Punctuation/Separator
                    weakBuffer.Append(c);
                    continue;
                }

                // Strong Character
                if (currentDir == CharType.Neutral)
                {
                    // Start of new segment
                    currentDir = type;
                    // Prepend any initial weaks (like opening brackets/quotes)
                    if (weakBuffer.Length > 0)
                    {
                        current.Append(weakBuffer);
                        weakBuffer.Clear();
                    }
                    current.Append(c);
                }
                else if (type == currentDir)
                {
                    // Continuation
                    // Flush accumulated weaks (internal punctuation/spaces)
                    if (weakBuffer.Length > 0)
                    {
                        current.Append(weakBuffer);
                        weakBuffer.Clear();
                    }
                    current.Append(c);
                }
                else
                {
                    // Change of direction (RTL <-> LTR)
                    // We need to decide where the 'weakBuffer' (spaces/punctuation) belongs.
                    // Rule: Neutrals should attach to the RTL segment to preserve visual spacing in RTL context.

                    if (currentDir == CharType.LTR && type == CharType.RTL)
                    {
                        // LTR -> RTL Transition
                        // Previous was LTR. Next is RTL.
                        // We need to split the weak buffer:
                        // Punctuation/Symbols (Non-Whitespace) -> Should stay with LTR (Suffix).
                        // Spaces (Whitespace) -> Should go to RTL (Prefix) to separate.

                        StringBuilder ltrSuffix = new StringBuilder();
                        StringBuilder rtlPrefix = new StringBuilder();

                        string bufferContent = weakBuffer.ToString();
                        foreach (char wbChar in bufferContent)
                        {
                            if (char.IsWhiteSpace(wbChar))
                                rtlPrefix.Append(wbChar);
                            else
                                ltrSuffix.Append(wbChar);
                        }

                        // 1. Append valid suffix to LTR current
                        if (ltrSuffix.Length > 0)
                            current.Append(ltrSuffix);

                        segments.Add((current, currentDir));

                        // 2. Start new RTL segment WITH whitespace prefix
                        current = new StringBuilder();
                        if (rtlPrefix.Length > 0)
                            current.Append(rtlPrefix);

                        // Clear master weakBuffer as we consumed it
                        weakBuffer.Clear();

                        currentDir = type;
                        current.Append(c);
                    }
                    else
                    {
                        // RTL -> LTR Transition (or others)
                        // Previous was RTL. Next is LTR.
                        // Weak buffer should belong to the OLD (RTL) segment.

                        // 1. Flush weak buffer to OLD segment
                        if (weakBuffer.Length > 0)
                        {
                            current.Append(weakBuffer);
                            weakBuffer.Clear();
                        }
                        segments.Add((current, currentDir));

                        // 2. Start new segment
                        current = new StringBuilder();
                        currentDir = type;
                        current.Append(c);
                    }
                }
            }

            // Flush remaining
            if (weakBuffer.Length > 0)
            {
                // If ending with LTR, spaces at end don't matter much visually, 
                // but for consistency with the rule "Attach to RTL", 
                // if we are in LTR mode, they just append.
                // If we are in RTL mode, they append (and get reversed to start).
                current.Append(weakBuffer);
                weakBuffer.Clear();
            }
            if (current.Length > 0)
            {
                segments.Add((current, currentDir));
            }

            // Reverse segments
            segments.Reverse();

            var result = RentStringBuilder(text.Length);
            try
            {
                foreach (var (content, type) in segments)
                {
                    if (type == CharType.RTL)
                    {
                        // Reverse RTL content
                        // Note: If space was at start (from LTR->RTL logic), it is now at index 0.
                        // string " ABC". Reverse -> "CBA ". Correct.
                        for (int i = content.Length - 1; i >= 0; i--)
                        {
                            result.Append(MirrorBracket(content[i]));
                        }
                    }
                    else
                    {
                        // LTR and Neutral - append as is
                        result.Append(content);
                    }
                    // إرجاع الـ StringBuilder للـ Pool
                    ReturnStringBuilder(content);
                }
                return result.ToString();
            }
            finally
            {
                ReturnStringBuilder(result);
                ReturnStringBuilder(weakBuffer);
            }
        }

        public static void ClearCache()
        {
            _cache.Clear();
            Interlocked.Exchange(ref _cacheCount, 0);
            KoH2ArabicRTL.Log?.LogInfo("Text cache cleared.");
        }
    }
}
