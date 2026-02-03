using HarmonyLib;
using TMPro;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace KoH2RTLFix
{
    internal static class TMPTextPatchState
    {
        // استخدام متغير بسيط بدلاً من ThreadStatic لبيئة Unity أحادية الخيط
        internal static int ReentryDepth = 0;

        // Cache لنتائج GetComponentInParent<TMP_InputField>
        internal static readonly ConcurrentDictionary<int, bool> InputFieldCache = new();

        // تنظيف الـ Cache عند تدمير الكائن
        internal static void CleanupCache(int instanceId)
        {
            InputFieldCache.TryRemove(instanceId, out _);
        }

        // كشف InputField محسّن
        internal static bool IsInputField(TMP_Text textComponent)
        {
            if (textComponent == null) return false;

            int instanceId = textComponent.GetInstanceID();

            // البحث في الـ Cache أولاً
            if (InputFieldCache.TryGetValue(instanceId, out bool cached))
                return cached;

            // البحث في المكونات المباشرة أولاً (أسرع)
            bool isInput = textComponent.GetComponent<TMP_InputField>() != null;
            
            // إذا لم يُعثر عليه، ابحث في الآباء
            if (!isInput)
                isInput = textComponent.GetComponentInParent<TMP_InputField>() != null;

            // تخزين النتيجة (سيتم التنظيف عند OnDestroy)
            InputFieldCache[instanceId] = isInput;

            return isInput;
        }
    }

    internal static class Patches
    {
        // مهلة زمنية قصوى للعمليات الطويلة (100ms)
        private const int MAX_PROCESSING_TIME_MS = 100;

        // Cache لمنع الحلقات اللانهائية
        private static readonly ConcurrentDictionary<int, string> LastProcessedTextCache = new();
        
        // عداد محاولات المعالجة لكل عنصر
        private static readonly ConcurrentDictionary<int, int> ProcessingAttempts = new();

        internal static string TracePreview(string text, int maxLength = 100)
        {
            if (string.IsNullOrEmpty(text)) return "<null>";
            if (text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "...";
        }

        // دالة محسّنة للكشف عن أحرف RTL
        internal static bool HasRTLLettersOutsideTags(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c == '<')
                {
                    int close = text.IndexOf('>', i + 1);
                    if (close != -1)
                    {
                        string inner = text.Substring(i + 1, close - i - 1);
                        if (TryParseLikelyRichTextTag(inner, out _, out _))
                        {
                            i = close;
                            continue;
                        }
                    }
                }

                if (RTLHelper.IsRTL(c))
                    return true;
            }

            return false;
        }

        internal static string TraceLabel(TMP_Text t)
        {
            if (t == null) return "TMP_Text(<null>)";
            string name;
            try { name = t.gameObject != null ? t.gameObject.name : "<no-go>"; }
            catch { name = "<err>"; }
            return t.GetType().Name + "('" + name + "', id=" + t.GetInstanceID() + ")";
        }

        private static bool IsValidRichTextTagName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (ch > 0x7F)
                    return false;

                bool ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || ch == '-';
                if (i > 0)
                    ok = ok || (ch >= '0' && ch <= '9');

                if (!ok)
                    return false;
            }

            return true;
        }

        private static bool TryParseLikelyRichTextTag(string rawTagContent, out bool isClosing, out string nameLower)
        {
            isClosing = false;
            nameLower = null;

            if (string.IsNullOrEmpty(rawTagContent))
                return false;

            string inner = rawTagContent;
            if (inner.Length > 0 && inner[0] == '/')
            {
                isClosing = true;
                inner = inner.Substring(1);
            }

            int endName = 0;
            while (endName < inner.Length)
            {
                char ch = inner[endName];
                if (ch == ' ' || ch == '=' || ch == '>')
                    break;
                endName++;
            }

            if (endName == 0)
                return false;

            string name = inner.Substring(0, endName);
            if (!IsValidRichTextTagName(name))
                return false;

            nameLower = name.ToLowerInvariant();
            return true;
        }

        internal static bool ContainsLikelyRichTextTags(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '<')
                    continue;

                int close = text.IndexOf('>', i + 1);
                if (close == -1)
                    continue;

                string inner = text.Substring(i + 1, close - i - 1);
                if (TryParseLikelyRichTextTag(inner, out _, out _))
                    return true;
            }

            return false;
        }

        private static bool IsSelfClosingTag(string tagName, string rawTagContent)
        {
            if (string.IsNullOrEmpty(tagName))
                return true;

            if (!string.IsNullOrEmpty(rawTagContent) && rawTagContent.EndsWith("/", StringComparison.Ordinal))
                return true;

            return tagName switch
            {
                "br" or "sprite" or "img" or "quad" => true,
                _ => false,
            };
        }

        private static void ApplyTagEdits(string s, List<(string name, string openTag)> stack)
        {
            if (string.IsNullOrEmpty(s))
                return;

            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '<')
                    continue;

                int close = s.IndexOf('>', i + 1);
                if (close == -1)
                    return;

                string inner = s.Substring(i + 1, close - i - 1);
                if (inner.Length == 0)
                {
                    i = close;
                    continue;
                }

                string raw = inner;
                if (!TryParseLikelyRichTextTag(inner, out bool isClosing, out string name))
                {
                    i = close;
                    continue;
                }

                if (isClosing)
                {
                    for (int sidx = stack.Count - 1; sidx >= 0; sidx--)
                    {
                        if (stack[sidx].name == name)
                        {
                            stack.RemoveAt(sidx);
                            break;
                        }
                    }
                }
                else
                {
                    if (!IsSelfClosingTag(name, raw))
                    {
                        string openTag = s.Substring(i, close - i + 1);
                        stack.Add((name, openTag));
                    }
                }

                i = close;
            }
        }

        private static string BuildLineWithBalancedTags(string fullText, int startIndex, int endIndex)
        {
            // حماية من قيم خارج النطاق
            if (startIndex < 0) startIndex = 0;
            if (endIndex > fullText.Length) endIndex = fullText.Length;
            if (endIndex <= startIndex) return string.Empty;

            var stack = new List<(string name, string openTag)>();

            if (startIndex > 0)
            {
                ApplyTagEdits(fullText.Substring(0, startIndex), stack);
            }

            var sb = new StringBuilder();
            for (int i = 0; i < stack.Count; i++)
                sb.Append(stack[i].openTag);

            string segment = fullText.Substring(startIndex, endIndex - startIndex);
            sb.Append(segment);

            ApplyTagEdits(segment, stack);
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                sb.Append("</");
                sb.Append(stack[i].name);
                sb.Append('>');
            }

            return sb.ToString();
        }

        internal static void ClearCacheForId(int instanceId)
        {
            LastProcessedTextCache.TryRemove(instanceId, out _);
            ProcessingAttempts.TryRemove(instanceId, out _);
            TMPTextPatchState.CleanupCache(instanceId);
        }

        // دالة محسّنة لكشف نهاية الفقرة
        private static bool IsHardLineBreak(TMP_TextInfo info, int lineIndex, string sourceText)
        {
            // آخر سطر دائماً نهاية فقرة
            if (lineIndex >= info.lineCount - 1)
                return true;

            var currentLine = info.lineInfo[lineIndex];
            var nextLine = info.lineInfo[lineIndex + 1];

            int currentLast = currentLine.lastCharacterIndex;
            int nextFirst = nextLine.firstCharacterIndex;

            if (nextFirst <= currentLast)
                return false;

            // فحص الحروف الفاصلة في characterInfo
            for (int i = currentLast + 1; i < nextFirst && i < info.characterInfo.Length; i++)
            {
                char ch = info.characterInfo[i].character;
                if (ch == '\n' || ch == '\r' || ch == '\v')
                    return true;
            }

            // فحص النص الأصلي كخطة احتياطية
            if (currentLast < info.characterInfo.Length && nextFirst < info.characterInfo.Length)
            {
                int endIdx = info.characterInfo[currentLast].index + 
                             Math.Max(1, info.characterInfo[currentLast].stringLength);
                int startIdx = info.characterInfo[nextFirst].index;

                if (endIdx < sourceText.Length && startIdx <= sourceText.Length)
                {
                    for (int k = endIdx; k < startIdx && k < sourceText.Length; k++)
                    {
                        if (sourceText[k] == '\n' || sourceText[k] == '\r')
                            return true;
                    }
                }
            }

            return false;
        }

        internal static void TryFixWordWrapOrder(TMP_Text textComponent)
        {
            // حماية من إعادة الدخول
            if (TMPTextPatchState.ReentryDepth > 0)
                return;

            if (textComponent == null)
                return;

            if (TMPTextPatchState.IsInputField(textComponent))
                return;

            if (!textComponent.enableWordWrapping)
                return;

            var rt = textComponent.rectTransform;
            if (rt != null)
            {
                float width = rt.rect.width;
                if (width <= 0f || width < 10f)
                    return;
            }

            string currentText = textComponent.text;
            if (string.IsNullOrWhiteSpace(currentText))
                return;

            int instanceId = textComponent.GetInstanceID();

            // حماية من الحلقات اللانهائية - فحص المحاولات
            int attempts = ProcessingAttempts.GetOrAdd(instanceId, 0);
            if (attempts > 3)
            {
                if (KoH2ArabicRTL.PluginConfig?.LoggingLevel?.Value == BepInEx.Logging.LogLevel.Debug)
                    PluginLog.Warning($"[RTLFix] Max attempts reached for {TraceLabel(textComponent)}");
                return;
            }

            // حماية من معالجة نفس النص مرتين
            if (LastProcessedTextCache.TryGetValue(instanceId, out string lastProcessed) &&
                string.Equals(lastProcessed, currentText, StringComparison.Ordinal))
            {
                return;
            }

            if (!HasRTLLettersOutsideTags(currentText))
                return;

            TMPTextPatchState.ReentryDepth++;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            try
            {
                ProcessingAttempts[instanceId] = attempts + 1;

                textComponent.ForceMeshUpdate(true);
                var info = textComponent.textInfo;
                if (info == null || info.lineCount <= 1)
                {
                    ProcessingAttempts[instanceId] = 0; // نجحت المعالجة
                    return;
                }

                // حماية من عدد أسطر كبير جداً
                if (info.lineCount > 500)
                {
                    if (KoH2ArabicRTL.PluginConfig?.LoggingLevel?.Value == BepInEx.Logging.LogLevel.Debug)
                        PluginLog.Warning($"[RTLFix] Line count too high ({info.lineCount}) for {TraceLabel(textComponent)}");

                    LastProcessedTextCache[instanceId] = currentText;
                    return;
                }

                string sourceText = textComponent.text;
                bool hasRichText = ContainsLikelyRichTextTags(sourceText);

                int charInfoLen = info.characterInfo == null ? 0 : info.characterInfo.Length;
                if (charInfoLen == 0)
                {
                    ProcessingAttempts[instanceId] = 0;
                    return;
                }

                // جمع الأسطر مع محتواها المرئي
                var extractedLines = new List<(string Content, bool IsParagraphEnd)>();

                for (int i = 0; i < info.lineCount; i++)
                {
                    // فحص المهلة الزمنية
                    if (stopwatch.ElapsedMilliseconds > MAX_PROCESSING_TIME_MS)
                    {
                        PluginLog.Warning($"[RTLFix] Processing timeout for {TraceLabel(textComponent)}");
                        LastProcessedTextCache[instanceId] = currentText;
                        return;
                    }

                    var line = info.lineInfo[i];

                    int first = line.firstCharacterIndex;
                    int last = line.lastCharacterIndex;

                    // حماية من القيم
                    if (first < 0) first = 0;
                    if (last >= charInfoLen) last = charInfoLen - 1;

                    string lineText = string.Empty;
                    int startIndex = 0;
                    int endIndex = 0;

                    // استخراج النص فقط إذا كان النطاق صحيحاً
                    if (first < charInfoLen && last < charInfoLen && first <= last)
                    {
                        var firstChar = info.characterInfo[first];
                        var lastChar = info.characterInfo[last];

                        startIndex = firstChar.index;
                        endIndex = lastChar.index + Math.Max(1, lastChar.stringLength);

                        if (startIndex < 0) startIndex = 0;
                        if (endIndex > sourceText.Length) endIndex = sourceText.Length;

                        if (endIndex > startIndex)
                        {
                            lineText = hasRichText
                                ? BuildLineWithBalancedTags(sourceText, startIndex, endIndex)
                                : sourceText.Substring(startIndex, endIndex - startIndex);
                        }
                    }

                    // كشف نهاية الفقرة محسّن
                    bool isParagraphEnd = IsHardLineBreak(info, i, sourceText);

                    extractedLines.Add((lineText, isParagraphEnd));
                }

                if (extractedLines.Count == 0)
                {
                    ProcessingAttempts[instanceId] = 0;
                    return;
                }

                // إعادة بناء النص: عكس الأسطر فقط داخل كل فقرة
                var finalLines = new List<string>();
                var currentParagraph = new List<string>();

                foreach (var (content, isEnd) in extractedLines)
                {
                    currentParagraph.Add(content);
                    if (isEnd)
                    {
                        currentParagraph.Reverse();
                        finalLines.AddRange(currentParagraph);
                        currentParagraph.Clear();
                    }
                }

                // إفراغ المتبقي إن وُجد
                if (currentParagraph.Count > 0)
                {
                    currentParagraph.Reverse();
                    finalLines.AddRange(currentParagraph);
                }

                string rebuilt = string.Join("\n", finalLines);

                if (!string.Equals(rebuilt, currentText, StringComparison.Ordinal))
                {
                    // حفظ قبل التحديث لمنع إعادة الدخول
                    LastProcessedTextCache[instanceId] = rebuilt;

                    if (PluginLog.TextTraceEnabled)
                    {
                        string sizeInfo = rt != null ? $" size=({rt.rect.width:F1}x{rt.rect.height:F1})" : "";
                        PluginLog.Debug("[RTLTrace] WordWrapFix " + TraceLabel(textComponent) +
                             sizeInfo +
                             " lines=" + finalLines.Count +
                             " IN='" + TracePreview(currentText) +
                             "' OUT='" + TracePreview(rebuilt) + "'");
                    }

                    textComponent.text = rebuilt;
                    ProcessingAttempts[instanceId] = 0; // نجحت المعالجة
                }
                else
                {
                    LastProcessedTextCache[instanceId] = currentText;
                    ProcessingAttempts[instanceId] = 0;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Error in WordWrapFix: {ex.Message}");
                LastProcessedTextCache[instanceId] = currentText;
            }
            finally
            {
                TMPTextPatchState.ReentryDepth--;
            }
        }

        internal static string GetSizeString(Component component)
        {
            try
            {
                if (component == null) return "";
                if (component is TMP_Text tmp)
                {
                    var rt = tmp.rectTransform;
                    if (rt != null) return $" size=({rt.rect.width:F1}x{rt.rect.height:F1})";
                }
                else if (component is Text txt)
                {
                    var rt = txt.rectTransform;
                    if (rt != null) return $" size=({rt.rect.width:F1}x{rt.rect.height:F1})";
                }
                else if (component is TextMesh tm)
                {
                    return " size=(3D)";
                }
            }
            catch { }
            return "";
        }

        internal static void Prefix(ref string value, TMP_Text __instance)
        {
            try
            {
                if (TMPTextPatchState.ReentryDepth > 0)
                    return;

                if (string.IsNullOrWhiteSpace(value) || __instance == null)
                    return;

                // تخطي المعالجة إذا لم تكن هناك أحرف RTL
                bool shouldProcess = RTLHelper.HasRTL(value);
                bool trace = PluginLog.TextTraceEnabled;
                bool traceAll = KoH2ArabicRTL.PluginConfig?.TraceAllTextAssignments?.Value == true && PluginLog.DebugEnabled;

                if (!shouldProcess && !traceAll)
                    return;

                string sizeStr = trace || traceAll ? GetSizeString(__instance) : "";

                bool isInputField = TMPTextPatchState.IsInputField(__instance);
                string original = value;

                if (trace || traceAll)
                {
                    PluginLog.Debug("[RTLTrace] Capture TMP_Text.text setter " + TraceLabel(__instance) +
                        sizeStr +
                        " inputField=" + isInputField +
                        " ligatures=" + (!isInputField) +
                        " IN='" + TracePreview(original) + "'");
                }

                if (!shouldProcess)
                {
                    if (traceAll)
                    {
                        PluginLog.Debug("[RTLTrace] Skip TMP_Text.text setter " + TraceLabel(__instance) +
                            " reason=NoRTL OUT='" + TracePreview(original) + "'");
                    }
                    return;
                }

                string fixedText = ArabicTextProcessor.Fix(original, !isInputField);
                value = fixedText;

                if (trace || traceAll)
                {
                    bool changed = !string.Equals(fixedText, original, StringComparison.Ordinal);
                    PluginLog.Debug("[RTLTrace] Process TMP_Text.text setter " + TraceLabel(__instance) +
                        sizeStr +
                        " changed=" + changed +
                        " OUT='" + TracePreview(fixedText) + "'");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Error in TMP_Text setter patch: {ex.Message}");
            }
        }

        internal static void Postfix(TMP_Text __instance)
        {
            try
            {
                TryFixWordWrapOrder(__instance);
                TryApplyRTLAlignment(__instance);
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Error in TMP_Text Postfix wrap fix: {ex.Message}");
            }
        }

        internal static void TryApplyRTLAlignment(TMP_Text __instance)
        {
            if (__instance == null) return;
            
            // تخطي InputFields
            if (TMPTextPatchState.IsInputField(__instance)) return;

            var alignmentMode = KoH2ArabicRTL.PluginConfig?.TextAlignment?.Value ?? ModConfiguration.AlignmentOptions.Auto;
            bool hasRTL = HasRTLLettersOutsideTags(__instance.text);

            TextAlignmentOptions? targetAlignment = null;

            switch (alignmentMode)
            {
                case ModConfiguration.AlignmentOptions.Right:
                    if (hasRTL) targetAlignment = GetRightAlignedVersion(__instance.alignment);
                    break;
                case ModConfiguration.AlignmentOptions.Left:
                    targetAlignment = TextAlignmentOptions.Left;
                    break;
                case ModConfiguration.AlignmentOptions.Center:
                    targetAlignment = TextAlignmentOptions.Center;
                    break;
                case ModConfiguration.AlignmentOptions.Auto:
                    // في وضع Auto: لا نغير المحاذاة الأصلية، فقط نعالج النص RTL
                    // تغيير المحاذاة يخرب تصميم الواجهة الأصلي
                    break;
            }

            if (targetAlignment.HasValue && __instance.alignment != targetAlignment.Value)
            {
                __instance.alignment = targetAlignment.Value;
            }
        }

        // دالة مساعدة لتحويل المحاذاة لليمين مع الحفاظ على المحاذاة الرأسية
        internal static TextAlignmentOptions GetRightAlignedVersion(TextAlignmentOptions current)
        {
            // استخراج المحاذاة الرأسية (Top, Middle, Bottom, Baseline, etc.)
            // TextAlignmentOptions هو enum مع قيم مركبة
            // Top = 0x100, Middle = 0x200, Bottom = 0x400, Baseline = 0x800, etc.
            // Left = 0x1, Center = 0x2, Right = 0x4, Justified = 0x8, etc.
            
            int verticalMask = (int)current & 0xFF00;  // الجزء الرأسي
            
            // إذا لم يكن هناك محاذاة رأسية محددة، استخدم Top
            if (verticalMask == 0)
                verticalMask = (int)TextAlignmentOptions.Top;
            
            return (TextAlignmentOptions)(verticalMask | (int)TextAlignmentOptions.Right);
        }
    }

    // ===== HARMONY PATCHES =====

    [HarmonyPatch(typeof(TextMesh), "set_text")]
    class Patch_TextMesh_text_setter
    {
        static void Prefix(ref string value, TextMesh __instance)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value) || __instance == null)
                    return;

                bool shouldProcess = RTLHelper.HasRTL(value);
                bool trace = PluginLog.TextTraceEnabled;
                bool traceAll = KoH2ArabicRTL.PluginConfig?.TraceAllTextAssignments?.Value == true && PluginLog.DebugEnabled;

                if (!shouldProcess && !traceAll)
                    return;

                string original = value;
                string sizeStr = trace || traceAll ? Patches.GetSizeString(__instance) : "";

                if (trace || traceAll)
                {
                    PluginLog.Debug("[RTLTrace] Capture TextMesh.text setter TextMesh('" + __instance.gameObject.name + "', id=" + __instance.GetInstanceID() + ")" +
                        sizeStr +
                        " IN='" + Patches.TracePreview(original) + "'");
                }

                if (!shouldProcess)
                {
                    if (traceAll)
                    {
                        PluginLog.Debug("[RTLTrace] Skip TextMesh.text setter TextMesh('" + __instance.gameObject.name + "', id=" + __instance.GetInstanceID() + ")" +
                            " reason=NoRTL OUT='" + Patches.TracePreview(original) + "'");
                    }
                    return;
                }

                string fixedText = ArabicTextProcessor.Fix(original, true);
                value = fixedText;

                if (trace || traceAll)
                {
                    bool changed = !string.Equals(fixedText, original, StringComparison.Ordinal);
                    PluginLog.Debug("[RTLTrace] Process TextMesh.text setter TextMesh('" + __instance.gameObject.name + "', id=" + __instance.GetInstanceID() + ")" +
                        sizeStr +
                        " changed=" + changed +
                        " OUT='" + Patches.TracePreview(fixedText) + "'");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Error in TextMesh setter patch: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(GUIContent), "set_text")]
    class Patch_GUIContent_text_setter
    {
        static void Prefix(ref string value, GUIContent __instance)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;

                bool shouldProcess = RTLHelper.HasRTL(value);
                bool trace = PluginLog.TextTraceEnabled;
                bool traceAll = KoH2ArabicRTL.PluginConfig?.TraceAllTextAssignments?.Value == true && PluginLog.DebugEnabled;

                if (!shouldProcess && !traceAll)
                    return;

                string original = value;
                if (trace || traceAll)
                {
                    PluginLog.Debug("[RTLTrace] Capture GUIContent.text setter" +
                        " IN='" + Patches.TracePreview(original) + "'");
                }

                if (!shouldProcess)
                {
                    if (traceAll)
                    {
                        PluginLog.Debug("[RTLTrace] Skip GUIContent.text setter" +
                            " reason=NoRTL OUT='" + Patches.TracePreview(original) + "'");
                    }
                    return;
                }

                string fixedText = ArabicTextProcessor.Fix(original, true);
                value = fixedText;

                if (trace || traceAll)
                {
                    bool changed = !string.Equals(fixedText, original, StringComparison.Ordinal);
                    PluginLog.Debug("[RTLTrace] Process GUIContent.text setter" +
                        " changed=" + changed +
                        " OUT='" + Patches.TracePreview(fixedText) + "'");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Error in GUIContent setter patch: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(TMP_Text), "set_text")]
    class Patch_TMP_Text_text_setter
    {
        static void Prefix(ref string value, TMP_Text __instance)
        {
            Patches.Prefix(ref value, __instance);
        }

        static void Postfix(TMP_Text __instance)
        {
            Patches.Postfix(__instance);
        }
    }

    [HarmonyPatch(typeof(TextMeshProUGUI), "OnRectTransformDimensionsChange")]
    class Patch_TMPUGUI_DimensionsChange
    {
        static void Postfix(TextMeshProUGUI __instance)
        {
            Patches.TryFixWordWrapOrder(__instance);
        }
    }

    [HarmonyPatch(typeof(TextMeshPro), "OnRectTransformDimensionsChange")]
    class Patch_TMP_DimensionsChange
    {
        static void Postfix(TextMeshPro __instance)
        {
            Patches.TryFixWordWrapOrder(__instance);
        }
    }

    [HarmonyPatch(typeof(Text), "set_text")]
    class Patch_UnityUI_Text_setter
    {
        static void Prefix(ref string value, Text __instance)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value) || __instance == null)
                    return;

                bool shouldProcess = RTLHelper.HasRTL(value);
                bool trace = PluginLog.TextTraceEnabled;
                bool traceAll = KoH2ArabicRTL.PluginConfig?.TraceAllTextAssignments?.Value == true && PluginLog.DebugEnabled;

                if (!shouldProcess && !traceAll)
                    return;

                string original = value;
                string sizeStr = trace || traceAll ? Patches.GetSizeString(__instance) : "";

                if (trace || traceAll)
                {
                    PluginLog.Debug("[RTLTrace] Capture UnityUI.Text.text setter Text('" + __instance.gameObject.name + "', id=" + __instance.GetInstanceID() + ")" +
                        sizeStr +
                        " IN='" + Patches.TracePreview(original) + "'");
                }

                if (!shouldProcess)
                {
                    if (traceAll)
                    {
                        PluginLog.Debug("[RTLTrace] Skip UnityUI.Text.text setter Text('" + __instance.gameObject.name + "', id=" + __instance.GetInstanceID() + ")" +
                            " reason=NoRTL OUT='" + Patches.TracePreview(original) + "'");
                    }
                    return;
                }

                string fixedText = ArabicTextProcessor.Fix(original, true);
                value = fixedText;

                if (trace || traceAll)
                {
                    bool changed = !string.Equals(fixedText, original, StringComparison.Ordinal);
                    PluginLog.Debug("[RTLTrace] Process UnityUI.Text.text setter Text('" + __instance.gameObject.name + "', id=" + __instance.GetInstanceID() + ")" +
                        sizeStr +
                        " changed=" + changed +
                        " OUT='" + Patches.TracePreview(fixedText) + "'");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Error in UnityUI.Text setter patch: {ex.Message}");
            }
        }
    }

    [HarmonyPatch]
    class Patch_TMP_TextSetText
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var methods = AccessTools.GetDeclaredMethods(typeof(TMP_Text))
                ?.Where(m => m != null && m.Name == "SetText")
                .Where(m =>
                {
                    var p = m.GetParameters();
                    return p.Length > 0 && p[0].ParameterType == typeof(string);
                })
                .ToList();
            
            // إذا لم يتم العثور على أي methods، نعيد قائمة فارغة بدلاً من null
            if (methods == null || methods.Count == 0)
            {
                PluginLog.Warning("No TMP_Text.SetText methods found to patch");
                return Enumerable.Empty<MethodBase>();
            }
            
            return methods;
        }

        static void Prefix(ref string __0, TMP_Text __instance)
        {
            try
            {
                if (TMPTextPatchState.ReentryDepth > 0)
                    return;

                if (string.IsNullOrWhiteSpace(__0) || __instance == null)
                    return;

                // تخطي المعالجة إذا لم تكن هناك أحرف RTL
                if (!RTLHelper.HasRTL(__0))
                    return;

                bool isInputField = TMPTextPatchState.IsInputField(__instance);
                bool trace = PluginLog.TextTraceEnabled;
                string original = __0;
                string sizeStr = trace ? Patches.GetSizeString(__instance) : "";

                if (trace)
                {
                    PluginLog.Debug("[RTLTrace] Capture TMP_Text.SetText " + Patches.TraceLabel(__instance) +
                        sizeStr +
                        " inputField=" + isInputField +
                        " ligatures=" + (!isInputField) +
                        " IN='" + Patches.TracePreview(original) + "'");
                }

                string fixedText = ArabicTextProcessor.Fix(original, !isInputField);
                __0 = fixedText;

                if (trace)
                {
                    bool changed = !string.Equals(fixedText, original, StringComparison.Ordinal);
                    PluginLog.Debug("[RTLTrace] Process TMP_Text.SetText " + Patches.TraceLabel(__instance) +
                        sizeStr +
                        " changed=" + changed +
                        " OUT='" + Patches.TracePreview(fixedText) + "'");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Error in TMP_Text SetText patch: {ex.Message}");
            }
        }
    }

    // ===== باتش OnEnable محسّن =====
    [HarmonyPatch(typeof(TextMeshProUGUI), "OnEnable")]
    class Patch_TMP_OnEnable
    {
        static void Postfix(TextMeshProUGUI __instance)
        {
            try
            {
                if (__instance == null) return;

                // FIX: تنظيف Cache عند إعادة تفعيل الكائن لتجنب مشكلة "النص العالق"
                Patches.ClearCacheForId(__instance.GetInstanceID());

                var alignmentMode = KoH2ArabicRTL.PluginConfig?.TextAlignment?.Value ?? ModConfiguration.AlignmentOptions.Auto;
                bool hasRTL = Patches.HasRTLLettersOutsideTags(__instance.text);

                // تحديد المحاذاة المطلوبة
                TextAlignmentOptions? targetAlignment = null;

                switch (alignmentMode)
                {
                    case ModConfiguration.AlignmentOptions.Right:
                        targetAlignment = TextAlignmentOptions.Right;
                        break;
                    case ModConfiguration.AlignmentOptions.Left:
                        targetAlignment = TextAlignmentOptions.Left;
                        break;
                    case ModConfiguration.AlignmentOptions.Center:
                        targetAlignment = TextAlignmentOptions.Center;
                        break;
                    case ModConfiguration.AlignmentOptions.Auto:
                        // في وضع Auto: لا نغير المحاذاة الأصلية، فقط نعالج النص RTL
                        // تغيير المحاذاة يخرب تصميم الواجهة الأصلي
                        break;
                }

                if (targetAlignment.HasValue && __instance.alignment != targetAlignment.Value)
                {
                    __instance.alignment = targetAlignment.Value;
                    __instance.ForceMeshUpdate(true);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Error in OnEnable: {ex.Message}");
            }
        }
    }

    // ===== باتش جديد: OnDestroy لتنظيف الذاكرة =====
    [HarmonyPatch(typeof(TMP_Text), "OnDestroy")]
    class Patch_TMP_OnDestroy
    {
        static void Prefix(TMP_Text __instance)
        {
            try
            {
                if (__instance == null) return;
                
                // تنظيف جميع الـ Caches المرتبطة بهذا الكائن
                Patches.ClearCacheForId(__instance.GetInstanceID());
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Error in OnDestroy cleanup: {ex.Message}");
            }
        }
    }
}