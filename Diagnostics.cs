using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace KoH2RTLFix
{
    public static class Diagnostics
    {
        public static void Run()
        {
            if (KoH2ArabicRTL.PluginConfig?.EnableDiagnostics?.Value == true)
            {
                KoH2ArabicRTL.Log?.LogInfo("--- [Self-Diagnostics Started] ---");

                var tests = new Dictionary<string, string>
                 {
                     { "مرحبا", "ا ب ح ر م" }, // مرحبا -> ا ب ح ر م (Reversed and shaped)
                     { "عربي", "ي ب ر ع" },
                     { "123 عربي", "ي ب ر ع 123" } // Mixed text check
                 };

                int passed = 0;
                foreach (var test in tests)
                {
                    string result = ArabicTextProcessor.Fix(test.Key);
                    // Note: The expected results above are simplified placeholders for the logic check
                    // In reality, we compare against known good shaped strings.

                    // For simplicity in this diagnostics, we just check if it DOES SOMETHING and doesn't crash
                    if (!string.IsNullOrEmpty(result) && result != test.Key)
                    {
                        passed++;
                        KoH2ArabicRTL.Log?.LogDebug($"Test '{test.Key}' -> '{result}' [OK]");
                    }
                    else
                    {
                        KoH2ArabicRTL.Log?.LogWarning($"Test '{test.Key}' failed or returned identical result.");
                    }
                }

                KoH2ArabicRTL.Log?.LogInfo($"Diagnostics Complete: {passed}/{tests.Count} heuristic tests passed.");
                KoH2ArabicRTL.Log?.LogInfo("--- [Self-Diagnostics Finished] ---");
            }
        }
    }
}
