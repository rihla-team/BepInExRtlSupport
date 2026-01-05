using System.Diagnostics;

namespace KoH2RTLFix
{
    // ========================================
    // مراقب الأداء (Performance Monitor)
    // ========================================
    public static class PerformanceMonitor
    {
        private static long _totalProcessed = 0;
        private static long _totalProcessingTimeMs = 0;
        private static long _cacheHits = 0;
        private static long _cacheMisses = 0;
        private static readonly object _lock = new();
        private static Stopwatch _sessionTimer;

        public static void Initialize()
        {
            _sessionTimer = Stopwatch.StartNew();
            _totalProcessed = 0;
            _totalProcessingTimeMs = 0;
            _cacheHits = 0;
            _cacheMisses = 0;
        }

        public static void RecordProcessing(long elapsedMs, bool wasCached)
        {
            lock (_lock)
            {
                _totalProcessed++;
                _totalProcessingTimeMs += elapsedMs;
                if (wasCached)
                    _cacheHits++;
                else
                    _cacheMisses++;
            }
        }

        public static void LogFinalReport()
        {
            if (_sessionTimer == null) return;

            _sessionTimer.Stop();
            var totalSeconds = _sessionTimer.Elapsed.TotalSeconds;
            var avgMs = _totalProcessed > 0 ? (double)_totalProcessingTimeMs / _totalProcessed : 0;
            var hitRate = (_cacheHits + _cacheMisses) > 0
                ? (double)_cacheHits / (_cacheHits + _cacheMisses) * 100 : 0;

            KoH2ArabicRTL.Log.LogInfo($"=== RTL Performance Report ===");
            KoH2ArabicRTL.Log.LogInfo($"Session Duration: {totalSeconds:F1}s");
            KoH2ArabicRTL.Log.LogInfo($"Total Processed: {_totalProcessed}");
            KoH2ArabicRTL.Log.LogInfo($"Average Processing Time: {avgMs:F3}ms");
            KoH2ArabicRTL.Log.LogInfo($"Cache Hit Rate: {hitRate:F1}% ({_cacheHits}/{_cacheHits + _cacheMisses})");
        }
    }
}
