using HarmonyLib;
using TMPro;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace KoH2RTLFix
{
    internal static class TMPTextPatchState
    {
        [ThreadStatic]
        internal static bool ReentryGuard;

        // Cache لنتائج GetComponentInParent<TMP_InputField> - يحفظ instanceId -> isInputField
        internal static readonly ConcurrentDictionary<int, bool> InputFieldCache = new();
        
        // تنظيف الـ Cache بشكل دوري (عند تدمير الـ objects)
        internal static bool IsInputField(TMP_Text textComponent)
        {
            if (textComponent == null) return false;
            
            int instanceId = textComponent.GetInstanceID();
            
            // البحث في الـ Cache أولاً
            if (InputFieldCache.TryGetValue(instanceId, out bool cached))
                return cached;
            
            // البحث الفعلي (مكلف)
            bool isInput = textComponent.GetComponentInParent<TMP_InputField>() != null;
            
            // تخزين النتيجة - الحد الأقصى 1000 عنصر
            if (InputFieldCache.Count < 1000)
                InputFieldCache[instanceId] = isInput;
            
            return isInput;
        }
    }

    // ========================================
    // Harmony Patches
    // ========================================

    [HarmonyPatch(typeof(TMP_Text), "text", MethodType.Setter)]
    class Patch_TMP_TextSetter
    {
        private static bool IsSelfClosingTag(string tagName, string rawTagContent)
        {
            if (string.IsNullOrEmpty(tagName))
                return true;

            if (!string.IsNullOrEmpty(rawTagContent) && rawTagContent.EndsWith("/", StringComparison.Ordinal))
                return true;

            switch (tagName)
            {
                case "br":
                case "sprite":
                case "img":
                case "quad":
                    return true;
                default:
                    return false;
            }
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

                bool isClosing = inner[0] == '/';
                string raw = inner;
                if (isClosing)
                    inner = inner.Substring(1);

                int endName = 0;
                while (endName < inner.Length)
                {
                    char ch = inner[endName];
                    if (ch == ' ' || ch == '=' || ch == '>')
                        break;
                    endName++;
                }

                if (endName == 0)
                {
                    i = close;
                    continue;
                }

                string name = inner.Substring(0, endName).ToLowerInvariant();

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

        internal static void TryFixWordWrapOrder(TMP_Text textComponent)
        {
            if (TMPTextPatchState.ReentryGuard)
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

            string text = textComponent.text;
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (!RTLHelper.HasRTL(text))
                return;

            int newlineCount = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') newlineCount++;
            }

            bool looksVertical = newlineCount > 3 && (text.Length / (newlineCount + 1)) <= 3;
            if (newlineCount > 0 && !looksVertical)
                return;

            TMPTextPatchState.ReentryGuard = true;
            try
            {
                if (looksVertical)
                {
                    string compact = text.Replace("\n", string.Empty);
                    if (!string.Equals(compact, text, StringComparison.Ordinal))
                        textComponent.text = compact;
                }

                textComponent.ForceMeshUpdate(true);
                var info = textComponent.textInfo;
                if (info == null || info.lineCount <= 1)
                    return;

                string currentText = textComponent.text;
                if (string.IsNullOrEmpty(currentText))
                    return;

                int charInfoLen = info.characterInfo == null ? 0 : info.characterInfo.Length;
                if (charInfoLen == 0)
                    return;

                if (info.lineCount > 0 && currentText.Length > 0)
                {
                    int avg = currentText.Length / info.lineCount;
                    if (avg <= 1)
                        return;
                }

                var lines = new List<string>(info.lineCount);
                for (int i = 0; i < info.lineCount; i++)
                {
                    var line = info.lineInfo[i];

                    int first = line.firstCharacterIndex;
                    int last = line.lastCharacterIndex;
                    if (first < 0 || last < 0 || first >= charInfoLen || last >= charInfoLen)
                        continue;

                    var firstChar = info.characterInfo[first];
                    var lastChar = info.characterInfo[last];

                    int startIndex = firstChar.index;
                    int endIndex = lastChar.index + Math.Max(1, lastChar.stringLength);

                    if (startIndex < 0) startIndex = 0;
                    if (endIndex > currentText.Length) endIndex = currentText.Length;
                    if (endIndex <= startIndex) continue;

                    string lineText = currentText.IndexOf('<') >= 0
                        ? BuildLineWithBalancedTags(currentText, startIndex, endIndex)
                        : currentText.Substring(startIndex, endIndex - startIndex);

                    lines.Add(lineText);
                }

                if (lines.Count <= 1)
                    return;

                lines.Reverse();
                string rebuilt = string.Join("\n", lines);

                if (!string.Equals(rebuilt, currentText, StringComparison.Ordinal))
                    textComponent.text = rebuilt;
            }
            finally
            {
                TMPTextPatchState.ReentryGuard = false;
            }
        }

        static void Prefix(ref string value, TMP_Text __instance)
        {
            try
            {
                if (TMPTextPatchState.ReentryGuard)
                    return;

                if (string.IsNullOrWhiteSpace(value) || __instance == null)
                    return;

                // Skip processing if no RTL characters present
                if (!RTLHelper.HasRTL(value))
                    return;

                bool isInputField = TMPTextPatchState.IsInputField(__instance);

                value = ArabicTextProcessor.Fix(value, !isInputField);
            }
            catch (Exception ex)
            {
                KoH2ArabicRTL.Log?.LogError($"Error in TMP_Text setter patch: {ex.Message}");
            }
        }

        static void Postfix(TMP_Text __instance)
        {
            try
            {
                TryFixWordWrapOrder(__instance);
            }
            catch (Exception ex)
            {
                KoH2ArabicRTL.Log?.LogError($"Error in TMP_Text Postfix wrap fix: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(TextMeshProUGUI), "OnRectTransformDimensionsChange")]
    class Patch_TMPUGUI_DimensionsChange
    {
        static void Postfix(TextMeshProUGUI __instance)
        {
            Patch_TMP_TextSetter.TryFixWordWrapOrder(__instance);
        }
    }

    [HarmonyPatch(typeof(TextMeshPro), "OnRectTransformDimensionsChange")]
    class Patch_TMP3D_DimensionsChange
    {
        static void Postfix(TextMeshPro __instance)
        {
            Patch_TMP_TextSetter.TryFixWordWrapOrder(__instance);
        }
    }

    [HarmonyPatch]
    class Patch_TMP_TextSetText
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            return AccessTools.GetDeclaredMethods(typeof(TMP_Text))
                .Where(m => m.Name == "SetText")
                .Where(m =>
                {
                    var p = m.GetParameters();
                    return p.Length > 0 && p[0].ParameterType == typeof(string);
                });
        }

        static void Prefix(ref string __0, TMP_Text __instance)
        {
            try
            {
                if (TMPTextPatchState.ReentryGuard)
                    return;

                if (string.IsNullOrWhiteSpace(__0) || __instance == null)
                    return;

                // Skip processing if no RTL characters present
                if (!RTLHelper.HasRTL(__0))
                    return;

                bool isInputField = TMPTextPatchState.IsInputField(__instance);

                __0 = ArabicTextProcessor.Fix(__0, !isInputField);
            }
            catch (Exception ex)
            {
                KoH2ArabicRTL.Log?.LogError($"Error in TMP_Text SetText patch: {ex.Message}");
            }
        }
    }

    // Note: SetText patch removed - method doesn't exist in this TMP version
    // The text setter patch above handles most cases

    [HarmonyPatch(typeof(TextMeshProUGUI), "OnEnable")]
    class Patch_TMP_OnEnable
    {
        static void Postfix(TextMeshProUGUI __instance)
        {
            try
            {
                if (__instance == null || !RTLHelper.HasRTL(__instance.text))
                    return;

                var alignmentMode = KoH2ArabicRTL.PluginConfig?.TextAlignment?.Value ?? ModConfiguration.AlignmentOptions.Auto;

                if (alignmentMode == ModConfiguration.AlignmentOptions.Right)
                {
                    __instance.alignment = TextAlignmentOptions.Right;
                    __instance.ForceMeshUpdate(true);
                }
                else if (alignmentMode == ModConfiguration.AlignmentOptions.Left)
                {
                    __instance.alignment = TextAlignmentOptions.Left;
                    __instance.ForceMeshUpdate(true);
                }
                else if (alignmentMode == ModConfiguration.AlignmentOptions.Center)
                {
                    __instance.alignment = TextAlignmentOptions.Center;
                    __instance.ForceMeshUpdate(true);
                }
                else if (alignmentMode == ModConfiguration.AlignmentOptions.Auto)
                {
                    // Auto behavior: Force Right for RTL text
                    __instance.alignment = TextAlignmentOptions.Right;
                    __instance.ForceMeshUpdate(true);
                }
            }
            catch (Exception ex)
            {
                KoH2ArabicRTL.Log?.LogError($"Error in OnEnable patch: {ex.Message}");
            }
        }
    }
}
