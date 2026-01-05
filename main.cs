using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;

namespace KoH2RTLFix
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class KoH2ArabicRTL : BaseUnityPlugin
    {
        // ---- معلومات الـ Plugin ----
        public static class PluginInfo
        {
            public const string PLUGIN_GUID = "com.rihla.bepinex.rtlsupport";
            public const string PLUGIN_NAME = "BepInEx RTL Support";
            public const string PLUGIN_VERSION = "3.0.0";
            public const string PLUGIN_AUTHOR = "فريق رحلة";
        }

        // ---- Logging ----
        internal static ManualLogSource Log;

        // ---- Configuration ----
        public static ModConfiguration PluginConfig { get; private set; }

        void Awake()
        {
            Log = Logger;

            try
            {
                // تحميل الإعدادات
                PluginConfig = new ModConfiguration(base.Config);

                // تطبيق Harmony patches
                var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
                harmony.PatchAll();

                Log.LogInfo($"{PluginInfo.PLUGIN_NAME} v{PluginInfo.PLUGIN_VERSION} loaded successfully.");
                Log.LogInfo($"Configuration: Alignment={PluginConfig.TextAlignment.Value}, " +
                           $"EasternArabicNumerals={PluginConfig.ConvertToEasternArabicNumerals.Value}, " +
                           $"CacheSize={PluginConfig.CacheSize.Value}");

                if (PluginConfig.EnablePerformanceMetrics.Value)
                {
                    PerformanceMonitor.Initialize();
                    Log.LogInfo("Performance monitoring enabled.");
                }

                if (PluginConfig.EnableDiagnostics.Value)
                {
                    Diagnostics.Run();
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to initialize plugin: {ex.Message}");
                Log.LogDebug(ex.StackTrace);
            }
        }

        void OnDestroy()
        {
            if (PluginConfig?.EnablePerformanceMetrics?.Value == true)
            {
                PerformanceMonitor.LogFinalReport();
            }
        }
    }
}