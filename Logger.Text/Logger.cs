using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Security.Principal;

public static class Logger
{
    private static string _LogDirectory; // مسیر ذخیره لاگ‌ها
    private static bool? _EnableLogging; // وضعیت فعال بودن لاگ
    private static bool _IncludeAssemblyInLog = false; // نمایش نام اسمبلی در لاگ
    private static readonly object _InitLock = new object(); // قفل مقداردهی اولیه
    private const string mConfigFileName = "logger_config.xml"; // نام فایل کانفیگ
    private static readonly string _ConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, mConfigFileName); // مسیر فایل کانفیگ
    private static readonly string _LogBaseName = "application_log.txt"; // نام فایل لاگ اصلی
    private static readonly long _MaxLogSize = 5L * 1024 * 1024; // حداکثر حجم فایل لاگ (5MB)
    private static readonly int _MaxBackups = 10; // حداکثر تعداد فایل‌های پشتیبان
    private static int _MaxQueueSize = 50000; // حداکثر اندازه صف پیام‌ها
    private static BlockingCollection<string> _LogQueue; // صف پیام‌های لاگ
    private static Task _WriterTask; // تسک نویسنده لاگ
    private static volatile bool _ShouldStop = false; // پرچم توقف عملیات
    private static CancellationTokenSource _CancellationTokenSource = new CancellationTokenSource(); // توکن لغو عملیات
    private static TimeSpan _WriteInterval = TimeSpan.FromMilliseconds(50); // فاصله زمانی نوشتن
    private static readonly TimeSpan _FileOperationTimeout = TimeSpan.FromSeconds(5); // تایم‌اوت عملیات فایل
    private static readonly int _MaxRetryCount = 3; // حداکثر تلاش برای عملیات
    private static readonly ManualResetEventSlim _ShutdownCompleteEvent = new ManualResetEventSlim(false); // رویداد پایان خاموشی
    private static readonly Newtonsoft.Json.JsonSerializerSettings _JsonSettings = new Newtonsoft.Json.JsonSerializerSettings // تنظیمات JSON
    {
        MaxDepth = 5,
        NullValueHandling = Newtonsoft.Json.NullValueHandling.Include,
        ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
        Formatting = Newtonsoft.Json.Formatting.Indented
    };
    private static PerformanceCounter _CpuCounter; // شمارنده پردازنده
    private static PerformanceCounter _MemCounter; // شمارنده حافظه
    private static readonly object _MetricsLock = new object(); // قفل متریک‌ها
    private static LoggerMetrics _Metrics = new LoggerMetrics(); // متریک‌های عملکرد
    private static int _CurrentBatchSize = 100; // اندازه فعلی بچ
    private static int _SamplingRate = 1; // نرخ نمونه‌برداری
    private static int _SamplingCounter = 0; // شمارنده نمونه‌برداری
    private static CircuitBreaker _CircuitBreaker = new CircuitBreaker(); // مدار قطع‌کن
    private static AdaptiveThrottler _Throttler = new AdaptiveThrottler(); // تنظیم‌کننده تطبیقی
    private static SelfRepairManager _SelfRepairManager = new SelfRepairManager(); // مدیر تعمیر خودکار
    private static volatile bool _LoggerOperational = true; // وضعیت عملیاتی لاگر
    private static volatile int _RestartCount = 0; // تعداد راه‌اندازی مجدد
    private static readonly int _MaxRestartAttempts = 5; // حداکثر تلاش راه‌اندازی مجدد
    private static readonly TimeSpan _RestartDelay = TimeSpan.FromSeconds(5); // تأخیر راه‌اندازی مجدد
    private static volatile bool _IsShuttingDown = false; // وضعیت خاموش شدن
    private static volatile int _ConsecutiveWriteErrors = 0; // خطاهای متوالی نوشتن
    private static readonly int _MaxConsecutiveWriteErrors = 10; // حداکثر خطاهای متوالی
    private static volatile int _DroppedLogsCount = 0; // تعداد پیام‌های حذف شده
    private static DateTime _LastDropWarningTime = DateTime.MinValue; // زمان آخرین هشدار حذف
    private static readonly TimeSpan _DropWarningInterval = TimeSpan.FromMinutes(1); // فاصله هشدار حذف
    private static volatile bool _HighLoadMode = false; // حالت بار بالا
    private static readonly int _HighLoadBatchSize = 50; // اندازه بچ در بار بالا
    private static readonly int _NormalBatchSize = 100; // اندازه بچ عادی
    private static readonly TimeSpan _HighLoadWriteInterval = TimeSpan.FromMilliseconds(10); // فاصله نوشتن در بار بالا

    static Logger()
    {
        // Initialize performance counters
        try
        {
            _CpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _MemCounter = new PerformanceCounter("Memory", "Available MBytes");
        }
        catch
        {
            _CpuCounter = null;
            _MemCounter = null;
        }

        InitializeQueue();
        StartWriterTask();

        // Register for system events
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private static void InitializeQueue()
    {
        // Adjust queue size based on available memory
        int memoryBasedSize = GetMemoryBasedQueueSize();
        _MaxQueueSize = Math.Min(100000, Math.Max(10000, memoryBasedSize));

        _LogQueue = new BlockingCollection<string>(_MaxQueueSize);
    }

    private static void StartWriterTask()
    {
        try
        {
            _WriterTask = Task.Factory.StartNew(
                BackgroundWriterLoop,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach
            );
            _ConsecutiveWriteErrors = 0;
            _LoggerOperational = true;
        }
        catch (Exception ex)
        {
            _LoggerOperational = false;
            SafeFallbackLog($"Logger startup failed: {ex.Message}");
            TryRestartLoggerWithDelay();
        }
    }

    private static string LogDirectory
    {
        get
        {
            if (!_LoggerOperational) return string.Empty;
            try
            {
                if (_LogDirectory == null)
                {
                    lock (_InitLock)
                    {
                        if (_LogDirectory == null)
                            InitializeFromConfig();
                    }
                }
                return _LogDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            }
            catch (Exception ex)
            {
                SafeFallbackLog($"LogDirectory get failed: {ex.Message}");
                _LoggerOperational = false;
                return string.Empty;
            }
        }
    }

    private static bool EnableLogging
    {
        get
        {
            if (!_LoggerOperational) return false;
            try
            {
                if (_EnableLogging == null)
                {
                    lock (_InitLock)
                    {
                        if (_EnableLogging == null)
                            InitializeFromConfig();
                    }
                }
                return _EnableLogging.GetValueOrDefault(true);
            }
            catch (Exception ex)
            {
                SafeFallbackLog($"EnableLogging get failed: {ex.Message}");
                _LoggerOperational = false;
                return false;
            }
        }
    }

    private static string LogFilePath => Path.Combine(LogDirectory, _LogBaseName);

    public static void Initialize(string mLogDirectory = null, bool? mEnableLogging = null, bool? mIncludeAssemblyInLog = null)
    {
        if (!_LoggerOperational) return;
        try
        {
            lock (_InitLock)
            {
                if (mLogDirectory != null) _LogDirectory = mLogDirectory;
                if (mEnableLogging != null) _EnableLogging = mEnableLogging;
                if (mIncludeAssemblyInLog != null) _IncludeAssemblyInLog = mIncludeAssemblyInLog.Value;
                if (_LogDirectory != null && _EnableLogging != null)
                    SaveToConfigFile();
                InitializeQueue();
            }
        }
        catch (Exception ex)
        {
            SafeFallbackLog($"Initialize failed: {ex.Message}");
            _LoggerOperational = false;
        }
    }

    public static void LogInfo(string mMessage, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] INFO: {mMessage}{mCallerInfo}";
            SafeEnqueue(mLogMessage);
        }
        catch { /* safe */ }
    }

    public static void LogInfo(object oData, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] INFO: {SafeSerializeObject(oData)}{mCallerInfo}";
            SafeEnqueue(mLogMessage);
        }
        catch { /* Absolutely safe */ }
    }

    public static void LogStart(object oData, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] START: {SafeSerializeObject(oData)}{mCallerInfo}";
            SafeEnqueue(mLogMessage);
        }
        catch { /*  safe */ }
    }

    public static void LogEnd(object oData, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] END: {SafeSerializeObject(oData)}{mCallerInfo}";
            SafeEnqueue(mLogMessage);
        }
        catch { /* Absolutely safe */ }
    }

    public static void LogRequest(object oRequest, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] START REQUEST: {SafeSerializeObject(oRequest)}{mCallerInfo}";
            SafeEnqueue(mLogMessage);
        }
        catch { /* Absolutely safe */ }
    }

    public static void LogResponse(object oResponse, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] RESPONSE: {SafeSerializeObject(oResponse)}{mCallerInfo}";
            SafeEnqueue(mLogMessage);
        }
        catch { /* Absolutely safe */ }
    }

    public static void LogError(object oException, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational || oException == null) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] ERROR: {SafeSerializeObject(oException)}{mCallerInfo}";
            SafeEnqueue(mLogMessage);
        }
        catch { /*  safe */ }
    }

    public static void LogError(string mMessage, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] ERROR: {mMessage}{mCallerInfo}";
            SafeEnqueue(mLogMessage);
        }
        catch { /* safe */ }
    }

    public static void Shutdown()
    {
        if (_IsShuttingDown) return;

        _IsShuttingDown = true;
        try
        {
            _ShouldStop = true;
            _CancellationTokenSource.Cancel();
            _LogQueue.CompleteAdding();

            int mWaitTime = Math.Min(30000, Math.Max(5000, _LogQueue.Count * 10));
            if (!_ShutdownCompleteEvent.Wait(TimeSpan.FromMilliseconds(mWaitTime)))
            {
                SafeFallbackLog($"Graceful shutdown timeout after {mWaitTime}ms, {_LogQueue.Count} items remaining");
            }

            if (_DroppedLogsCount > 0)
            {
                string summary = $"[SHUTDOWN] Dropped logs: {_DroppedLogsCount}, Restart attempts: {_RestartCount}";
                EmergencyWriteToDisk(summary);
            }
            _CancellationTokenSource.Dispose();
            _CpuCounter?.Dispose();
            _MemCounter?.Dispose();
        }
        catch (Exception ex)
        {
            SafeFallbackLog($"Shutdown failed: {ex.Message}");
        }
    }
    private static bool ShouldSampleLog()
    {
        if (_SamplingRate <= 1) return true;
        _SamplingCounter++;
        if (_SamplingCounter % _SamplingRate == 0)
        {
            return true;
        }
        lock (_MetricsLock)
        {
            _Metrics.SampledLogsCount++;
        }

        return false;
    }

    private static int GetMemoryBasedQueueSize()
    {
        try
        {
            if (_MemCounter == null) return 50000;
            float availableMemory = _MemCounter.NextValue();
            if (availableMemory > 1000) return 100000; // >1GB
            if (availableMemory > 500) return 75000;   // >500MB
            if (availableMemory > 200) return 50000;   // >200MB
            if (availableMemory > 100) return 25000;   // >100MB
            return 10000; // <100MB
        }
        catch
        {
            return 50000;
        }
    }

    private static void UpdateIntelligentSettings()
    {
        try
        {
            float cpuUsage = _CpuCounter?.NextValue() ?? 0;
            float availableMemory = _MemCounter?.NextValue() ?? 1000;
            int queueLoad = (int)((double)_LogQueue.Count / _MaxQueueSize * 100);
            lock (_MetricsLock)
            {
                _Metrics.CpuUsage = cpuUsage;
                _Metrics.AvailableMemoryMB = availableMemory;
                _Metrics.QueueLoadPercent = queueLoad;
            }
            _Throttler.Adjust(cpuUsage, availableMemory, queueLoad);
            _CurrentBatchSize = _Throttler.OptimalBatchSize;
            _WriteInterval = _Throttler.OptimalWriteInterval;
            if (queueLoad > 80 || availableMemory < 200)
            {
                _SamplingRate = Math.Min(10, _SamplingRate + 1);
            }
            else if (queueLoad < 30 && availableMemory > 500)
            {
                _SamplingRate = Math.Max(1, _SamplingRate - 1);
            }
            _SelfRepairManager.CheckAndRepair();
        }
        catch (Exception ex)
        {
            SafeFallbackLog($"Error updating intelligent settings: {ex.Message}");
        }
    }

    private static void BackgroundWriterLoop()
    {
        var mBatch = new StringBuilder(8192);
        var mLastWriteTime = DateTime.UtcNow;
        var mItemsBuffer = new string[100];

        while (!_ShouldStop || !_LogQueue.IsCompleted)
        {
            try
            {
                UpdateIntelligentSettings();
                int mItemsCount = _LogQueue.TryTakeMultiple(mItemsBuffer, 0, mItemsBuffer.Length, _HighLoadMode ? (int)_HighLoadWriteInterval.TotalMilliseconds : (int)_WriteInterval.TotalMilliseconds);
                if (mItemsCount > 0)
                {
                    for (int j = 0; j < mItemsCount; j++)
                    {
                        mBatch.AppendLine(mItemsBuffer[j]);
                        mItemsBuffer[j] = null;
                    }
                }

                bool mShouldFlush = _ShouldStop && _LogQueue.IsCompleted;
                bool mTimeoutReached = (DateTime.UtcNow - mLastWriteTime) >=
                    (_HighLoadMode ? _HighLoadWriteInterval : _WriteInterval) && mBatch.Length > 0;
                bool mBatchFull = mBatch.Length > 8192;

                if (mShouldFlush || mTimeoutReached || mBatchFull)
                {
                    if (mBatch.Length > 0)
                    {
                        _CircuitBreaker.Execute(() =>
                        {
                            if (WriteBatchToDisk(mBatch.ToString()))
                            {
                                _ConsecutiveWriteErrors = 0;
                                _HighLoadMode = false;
                            }
                        });
                    }
                    mBatch.Clear();
                    mLastWriteTime = DateTime.UtcNow;
                }
                if (mBatch.Capacity > 65536)
                {
                    mBatch = new StringBuilder(8192);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _ConsecutiveWriteErrors++;
                SafeFallbackLog($"BackgroundWriterLoop error: {ex.Message}, Consecutive errors: {_ConsecutiveWriteErrors}");

                if (_ConsecutiveWriteErrors >= _MaxConsecutiveWriteErrors && !_ShouldStop)
                {
                    SafeFallbackLog("Maximum consecutive errors reached. Attempting restart.");
                    TryRestartLogger();
                    return;
                }

                Thread.Sleep(Math.Min(100 * _ConsecutiveWriteErrors, 1000));
            }
        }
        try
        {
            if (mBatch.Length > 0)
            {
                _CircuitBreaker.Execute(() => WriteBatchToDisk(mBatch.ToString()));
            }
        }
        catch (Exception ex)
        {
            SafeFallbackLog($"Final flush failed: {ex.Message}");
        }

        _ShutdownCompleteEvent.Set();
    }

    private class CircuitBreaker
    {
        public enum State { Closed, Open, HalfOpen }
        private State _state = State.Closed;
        private int _failureCount = 0;
        private DateTime _lastFailureTime = DateTime.MinValue;
        private readonly TimeSpan _timeout = TimeSpan.FromMinutes(1);
        private readonly int _maxFailures = 5;
        public State CurrentState => _state;
        public void Execute(Action action)
        {
            if (_state == State.Open)
            {
                if (DateTime.UtcNow - _lastFailureTime > _timeout)
                {
                    _state = State.HalfOpen;
                }
                else
                {
                    lock (_MetricsLock)
                    {
                        _Metrics.CircuitBreakerTrippedCount++;
                    }
                    return;
                }
            }

            try
            {
                action();

                if (_state == State.HalfOpen)
                {
                    _state = State.Closed;
                    _failureCount = 0;
                }
            }
            catch (Exception ex)
            {
                _failureCount++;
                _lastFailureTime = DateTime.UtcNow;

                if (_failureCount >= _maxFailures)
                {
                    _state = State.Open;
                    SafeFallbackLog("Circuit breaker tripped", ex);
                }

                throw;
            }
        }
    }

    private class AdaptiveThrottler
    {
        private int _optimalBatchSize = 100;
        private TimeSpan _optimalWriteInterval = TimeSpan.FromMilliseconds(50);
        private DateTime _lastAdjustment = DateTime.UtcNow;
        private readonly TimeSpan _adjustmentInterval = TimeSpan.FromSeconds(30);

        public int OptimalBatchSize => _optimalBatchSize;
        public TimeSpan OptimalWriteInterval => _optimalWriteInterval;

        public void Adjust(float cpuUsage, float availableMemory, int queueLoad)
        {
            if (DateTime.UtcNow - _lastAdjustment < _adjustmentInterval)
                return;
            _lastAdjustment = DateTime.UtcNow;
            if (cpuUsage > 80 || queueLoad > 80 || availableMemory < 200)
            {
                _optimalBatchSize = Math.Max(10, _optimalBatchSize - 20);
                _optimalWriteInterval = TimeSpan.FromMilliseconds(Math.Min(200, _optimalWriteInterval.TotalMilliseconds + 10));
            }
            else if (cpuUsage < 30 && queueLoad < 30 && availableMemory > 500)
            {
                _optimalBatchSize = Math.Min(500, _optimalBatchSize + 20);
                _optimalWriteInterval = TimeSpan.FromMilliseconds(Math.Max(10, _optimalWriteInterval.TotalMilliseconds - 10));
            }

            lock (_MetricsLock)
            {
                _Metrics.CurrentBatchSize = _optimalBatchSize;
                _Metrics.CurrentWriteIntervalMs = (int)_optimalWriteInterval.TotalMilliseconds;
            }
        }
    }
    private class SelfRepairManager
    {
        private DateTime _lastRepairTime = DateTime.MinValue;
        private readonly TimeSpan _repairInterval = TimeSpan.FromMinutes(5);
        private readonly object _repairLock = new object();

        public void CheckAndRepair()
        {
            if (DateTime.UtcNow - _lastRepairTime < _repairInterval)
                return;

            lock (_repairLock)
            {
                if (DateTime.UtcNow - _lastRepairTime < _repairInterval)
                    return;

                _lastRepairTime = DateTime.UtcNow;

                try
                {
                    RepairLogDirectory();
                    RepairDiskSpace();
                    RepairConfigFile();
                    RepairPermissions();
                }
                catch (Exception ex)
                {
                    SafeFallbackLog($"Error during self-repair: {ex.Message}");
                }
            }
        }

        private void RepairLogDirectory()
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                    SafeFallbackLog("Log directory was missing and has been recreated");
                }
                string testFile = Path.Combine(LogDirectory, $"access_test_{DateTime.UtcNow:yyyyMMdd_HHmmss}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            }
            catch (Exception ex)
            {
                try
                {
                    string fallbackDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log_fallback");
                    Directory.CreateDirectory(fallbackDir);
                    lock (_InitLock)
                    {
                        _LogDirectory = fallbackDir;
                    }
                    SafeFallbackLog("Log directory was inaccessible, switched to fallback directory", ex);
                }
                catch (Exception fallbackEx)
                {
                    SafeFallbackLog("Failed to create fallback log directory", fallbackEx);
                }
            }
        }

        private void RepairDiskSpace()
        {
            try
            {
                var oDriveInfo = new DriveInfo(Path.GetPathRoot(LogDirectory));
                long lFreeSpace = oDriveInfo.AvailableFreeSpace;
                long lTotalSpace = oDriveInfo.TotalSize;
                double dFreePercentage = (double)lFreeSpace / lTotalSpace * 100;
                if (dFreePercentage < 5) // Less than 5% free space
                {
                    SafeFallbackLog($"Low disk space: {dFreePercentage:F1}% free. Attempting to clean up old logs.");

                    var backupFiles = new DirectoryInfo(LogDirectory).GetFiles("backup_application_log_*").OrderBy(f => f.LastWriteTimeUtc).ToList();
                    foreach (var oFile in backupFiles)
                    {
                        try
                        {
                            oFile.Delete();
                            lFreeSpace = oDriveInfo.AvailableFreeSpace;
                            dFreePercentage = (double)lFreeSpace / lTotalSpace * 100;

                            if (dFreePercentage > 10)
                            {
                                SafeFallbackLog($"Disk space recovered to {dFreePercentage:F1}% after cleanup");
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            SafeFallbackLog($"Failed to delete backup file {oFile.Name}", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SafeFallbackLog("Error during disk space repair", ex);
            }
        }

        private void RepairConfigFile()
        {
            try
            {
                if (File.Exists(_ConfigFilePath))
                {
                    var oDoc = new XmlDocument();
                    oDoc.Load(_ConfigFilePath);
                    if (oDoc.DocumentElement == null ||
                        oDoc.DocumentElement.SelectSingleNode("LogDirectory") == null ||
                        oDoc.DocumentElement.SelectSingleNode("EnableLogging") == null)
                    {
                        throw new Exception("Invalid config file structure");
                    }
                }
            }
            catch (Exception ex)
            {
                SafeFallbackLog("Config file is corrupted, resetting to defaults", ex);

                try
                {
                    lock (_InitLock)
                    {
                        _LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
                        _EnableLogging = true;
                        SaveToConfigFile();
                    }
                }
                catch (Exception resetEx)
                {
                    SafeFallbackLog("Failed to reset config file", resetEx);
                }
            }
        }

        private void RepairPermissions()
        {
            try
            {
                string testFile = Path.Combine(LogDirectory, $"permission_test_{DateTime.UtcNow:yyyyMMdd_HHmmss}.tmp");

                try
                {
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);
                }
                catch (UnauthorizedAccessException)
                {
                    SafeFallbackLog("No write permissions to log directory, attempting to fix");

                    try
                    {
                        DirectoryInfo dirInfo = new DirectoryInfo(LogDirectory);
                        DirectorySecurity dirSecurity = dirInfo.GetAccessControl();
                        string currentUser = WindowsIdentity.GetCurrent().Name;
                        dirSecurity.AddAccessRule(new FileSystemAccessRule(
                            currentUser,
                            FileSystemRights.Write,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow));

                        dirInfo.SetAccessControl(dirSecurity);
                        File.WriteAllText(testFile, "test");
                        File.Delete(testFile);

                        SafeFallbackLog("Successfully repaired directory permissions");
                    }
                    catch (Exception permEx)
                    {
                        SafeFallbackLog("Failed to repair directory permissions", permEx);
                    }
                }
            }
            catch (Exception ex)
            {
                SafeFallbackLog("Error during permission repair", ex);
            }
        }
    }
    public class LoggerMetrics
    {
        public float CpuUsage { get; set; }
        public float AvailableMemoryMB { get; set; }
        public int QueueLoadPercent { get; set; }
        public int CurrentBatchSize { get; set; }
        public int CurrentWriteIntervalMs { get; set; }
        public long SampledLogsCount { get; set; }
        public long CircuitBreakerTrippedCount { get; set; }
        public long WriteErrorsCount { get; set; }
        public long TotalLogsWritten { get; set; }
        public double AverageWriteTimeMs { get; set; }
        public long SelfRepairActionsCount { get; set; }
        public int RestartCount { get; set; }
        public long DroppedLogsCount { get; set; }
    }

    public static LoggerMetrics GetMetrics()
    {
        lock (_MetricsLock)
        {
            return new LoggerMetrics
            {
                CpuUsage = _Metrics.CpuUsage,
                AvailableMemoryMB = _Metrics.AvailableMemoryMB,
                QueueLoadPercent = _Metrics.QueueLoadPercent,
                CurrentBatchSize = _Metrics.CurrentBatchSize,
                CurrentWriteIntervalMs = _Metrics.CurrentWriteIntervalMs,
                SampledLogsCount = _Metrics.SampledLogsCount,
                CircuitBreakerTrippedCount = _Metrics.CircuitBreakerTrippedCount,
                WriteErrorsCount = _Metrics.WriteErrorsCount,
                TotalLogsWritten = _Metrics.TotalLogsWritten,
                AverageWriteTimeMs = _Metrics.AverageWriteTimeMs,
                SelfRepairActionsCount = _Metrics.SelfRepairActionsCount,
                RestartCount = _RestartCount,
                DroppedLogsCount = _Metrics.DroppedLogsCount
            };
        }
    }
    private static void SafeEnqueue(string mLogMessage)
    {
        try
        {
            if (!_LoggerOperational || _LogQueue.IsAddingCompleted)
            {
                EmergencyWriteToDisk(mLogMessage);
                return;
            }
            if (!_LogQueue.TryAdd(mLogMessage, 50))
            {
                Interlocked.Increment(ref _DroppedLogsCount);
                EmergencyWriteToDisk(mLogMessage);
                var mNow = DateTime.UtcNow;
                if (mNow - _LastDropWarningTime > _DropWarningInterval)
                {
                    _LastDropWarningTime = mNow;
                    int mDropped = _DroppedLogsCount;
                    SafeFallbackLog($"Queue overload! Dropped {mDropped} messages. Consider optimizing log frequency.");
                }
                _HighLoadMode = true;
            }
        }
        catch
        {
            Interlocked.Increment(ref _DroppedLogsCount);
            EmergencyWriteToDisk(mLogMessage);
        }
    }

    private static void EmergencyWriteToDisk(string mLogMessage)
    {
        try
        {
            string mEmergencyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "emergency_log.txt");
            string mFullMessage = $"[EMERGENCY] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} {mLogMessage}{Environment.NewLine}";
            lock (typeof(Logger))
            {
                File.AppendAllText(mEmergencyPath, mFullMessage, Encoding.UTF8);
            }
        }
        catch { }
    }

    private static void SafeFallbackLog(string mMessage, Exception ex = null)
    {
        try
        {
            lock (_MetricsLock)
            {
                _Metrics.SelfRepairActionsCount++;
            }
            if (!EventLog.SourceExists("ApplicationLogger"))
                EventLog.CreateEventSource("ApplicationLogger", "Application");

            string logMessage = ex != null ? $"{mMessage}: {ex.Message}" : mMessage;
            EventLog.WriteEntry("ApplicationLogger", logMessage, EventLogEntryType.Error, 1001);
        }
        catch
        {
            try
            {
                string mFallbackPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logger_errors.txt");
                string mErrorText = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {mMessage}: {ex}\n";
                File.AppendAllText(mFallbackPath, mErrorText);
            }
            catch
            {
                Debug.WriteLine($"Logger Internal Error: {mMessage} - {ex}");
            }
        }
    }

    private static string BuildCallerInfo(string mMemberName, string mFilePath, int mLineNumber)
    {
        try
        {
            string mClassName = Path.GetFileNameWithoutExtension(mFilePath ?? "");
            string mAssemblyPrefix = "";
            if (_IncludeAssemblyInLog)
            {
                try
                {
                    var oStack = new StackTrace(1, false);
                    var oFrame = oStack.GetFrame(0);
                    var oMethod = oFrame?.GetMethod();
                    string mAssemblyName = oMethod?.DeclaringType?.Assembly?.GetName().Name ?? "Unknown";
                    mAssemblyPrefix = mAssemblyName + ":";
                }
                catch
                {
                    mAssemblyPrefix = "Unknown:";
                }
            }
            return $" [{mAssemblyPrefix} -> {mClassName} -> {mMemberName} Line{mLineNumber}]";
        }
        catch
        {
            return " [CallerInfoUnavailable]";
        }
    }

    private static bool WriteBatchToDisk(string mBatchText)
    {
        if (string.IsNullOrWhiteSpace(mBatchText)) return true;

        var oStopwatch = Stopwatch.StartNew();
        int mRetryCount = 0;
        bool mSuccess = false;

        while (mRetryCount < _MaxRetryCount && !mSuccess)
        {
            try
            {
                lock (_InitLock)
                {
                    // Ensure directory exists before writing
                    if (!Directory.Exists(LogDirectory))
                    {
                        Directory.CreateDirectory(LogDirectory);
                        SafeFallbackLog("Log directory was missing and has been recreated");
                    }

                    if (File.Exists(LogFilePath))
                    {
                        long mCurrentSize = new FileInfo(LogFilePath).Length;
                        if (mCurrentSize >= _MaxLogSize)
                            CreateBackup();
                    }

                    using (var oFileStream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, 8192, FileOptions.SequentialScan))
                    using (var oWriter = new StreamWriter(oFileStream, Encoding.UTF8))
                    {
                        oWriter.Write(mBatchText);
                        oWriter.Flush();
                    }
                }
                mSuccess = true;
                oStopwatch.Stop();
                lock (_MetricsLock)
                {
                    _Metrics.TotalLogsWritten++;
                    _Metrics.AverageWriteTimeMs =
                        (_Metrics.AverageWriteTimeMs * (_Metrics.TotalLogsWritten - 1) + oStopwatch.ElapsedMilliseconds) /
                        _Metrics.TotalLogsWritten;
                }
            }
            catch (Exception ex) when (mRetryCount < _MaxRetryCount - 1)
            {
                mRetryCount++;
                SafeFallbackLog($"Failed to write logs (attempt {mRetryCount})", ex);
                Thread.Sleep(500 * mRetryCount);
            }
            catch (Exception ex)
            {
                SafeFallbackLog("Failed to write logs after retries", ex);
                lock (_MetricsLock)
                {
                    _Metrics.WriteErrorsCount++;
                }
                return false;
            }
        }

        return mSuccess;
    }

    private static void CreateBackup()
    {
        int mRetryCount = 0;
        bool mSuccess = false;

        while (mRetryCount < _MaxRetryCount && !mSuccess)
        {
            try
            {
                string mTimestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
                string mBackupName = $"backup_application_log_{mTimestamp}.txt";
                string mBackupPath = Path.Combine(LogDirectory, mBackupName);
                if (File.Exists(mBackupPath))
                {
                    mBackupName = $"backup_application_log_{mTimestamp}_{Guid.NewGuid().ToString("N")}.txt";
                    mBackupPath = Path.Combine(LogDirectory, mBackupName);
                }

                File.Move(LogFilePath, mBackupPath);
                CompressOldBackups();
                CleanOldBackups();

                mSuccess = true;
            }
            catch (Exception ex) when (mRetryCount < _MaxRetryCount - 1)
            {
                mRetryCount++;
                SafeFallbackLog($"Failed to create backup (attempt {mRetryCount})", ex);
                Thread.Sleep(500 * mRetryCount);
            }
            catch (Exception ex)
            {
                SafeFallbackLog("Failed to create backup after retries", ex);
                break;
            }
        }
    }

    private static void CompressOldBackups()
    {
        try
        {
            var oBackups = new DirectoryInfo(LogDirectory).GetFiles("backup_application_log_*.txt").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)).ToList();
            foreach (var mFile in oBackups)
            {
                string mCompressedPath = mFile.FullName + ".gz";
                if (!File.Exists(mCompressedPath))
                {
                    using (var oInputStream = mFile.OpenRead())
                    using (var oOutputStream = File.Create(mCompressedPath))
                    using (var oCompressionStream = new GZipStream(oOutputStream, CompressionMode.Compress))
                    {
                        oInputStream.CopyTo(oCompressionStream);
                    }
                    mFile.Delete();
                }
            }
        }
        catch (Exception ex)
        {
            SafeFallbackLog("Failed to compress old backups", ex);
        }
    }

    private static void CleanOldBackups()
    {
        try
        {
            var oOldBackups = new DirectoryInfo(LogDirectory)
                .GetFiles("backup_application_log_*")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(_MaxBackups);

            foreach (var mFile in oOldBackups)
            {
                try
                {
                    mFile.Delete();
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            SafeFallbackLog("Failed to clean old backups", ex);
        }
    }
    private static void TryRestartLogger()
    {
        if (_IsShuttingDown || _RestartCount >= _MaxRestartAttempts) return;

        lock (_InitLock)
        {
            if (_IsShuttingDown || _RestartCount >= _MaxRestartAttempts) return;

            _RestartCount++;
            _LoggerOperational = false;

            try
            {
                SafeFallbackLog($"Attempting logger restart ({_RestartCount}/{_MaxRestartAttempts})");
                if (_WriterTask != null)
                {
                    _ShouldStop = true;
                    _CancellationTokenSource.Cancel();
                    _ShutdownCompleteEvent.Wait(1000);
                    _WriterTask = null;
                }
                Thread.Sleep(_RestartDelay);
                var oldCts = _CancellationTokenSource;
                _CancellationTokenSource = new CancellationTokenSource();
                oldCts.Dispose();

                _ShouldStop = false;
                _ConsecutiveWriteErrors = 0;
                StartWriterTask();

                SafeFallbackLog("Logger successfully restarted");
            }
            catch (Exception ex)
            {
                SafeFallbackLog($"Logger restart failed: {ex.Message}");

                if (_RestartCount >= _MaxRestartAttempts)
                {
                    SafeFallbackLog("Maximum restart attempts reached. Logger disabled.");
                }
                else
                {
                    TryRestartLoggerWithDelay();
                }
            }
        }
    }

    private static void TryRestartLoggerWithDelay()
    {
        Task.Delay(_RestartDelay).ContinueWith(_ =>
        {
            if (!_IsShuttingDown && _RestartCount < _MaxRestartAttempts)
            {
                TryRestartLogger();
            }
        });
    }

    private static void OnProcessExit(object sender, EventArgs e)
    {
        Shutdown();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            if (e.ExceptionObject is Exception ex)
            {
                SafeFallbackLog("Unhandled application exception", ex);
            }
        }
        catch { }
    }
    private static string SafeSerializeObject(object oObj)
    {
        if (oObj == null) return "NULL";

        try
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(oObj, _JsonSettings);
        }
        catch (Exception serializationEx)
        {
            try
            {
                var oProps = oObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                var oDict = new Dictionary<string, object>();
                foreach (var oProp in oProps)
                {
                    try
                    {
                        oDict[oProp.Name] = oProp.GetValue(oObj);
                    }
                    catch { }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(oDict, _JsonSettings);
            }
            catch
            {
                try
                {
                    return oObj.ToString() ?? $"[ToString failed: {serializationEx.Message}]";
                }
                catch
                {
                    return $"[SERIALIZE_FAILED: {serializationEx.Message}]";
                }
            }
        }
    }
    private static void InitializeFromConfig()
    {
        if (_LogDirectory != null && _EnableLogging != null) return;

        try
        {
            if (File.Exists(_ConfigFilePath))
            {
                LoadFromConfigFile();
            }
            else
            {
                _LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
                _EnableLogging = true;
                _IncludeAssemblyInLog = false;
                SaveToConfigFile();
            }
        }
        catch (Exception ex)
        {
            SafeFallbackLog("Logger initialization failed", ex);
            _LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            _EnableLogging = true;
            _IncludeAssemblyInLog = false;
        }
    }

    private static void LoadFromConfigFile()
    {
        try
        {
            XmlDocument oDoc = new XmlDocument();
            oDoc.Load(_ConfigFilePath);
            XmlNode oRoot = oDoc.DocumentElement;
            if (oRoot == null) throw new Exception("Invalid config");
            XmlNode oDirNode = oRoot.SelectSingleNode("LogDirectory");
            XmlNode oEnableNode = oRoot.SelectSingleNode("EnableLogging");
            XmlNode oIncludeAssemblyNode = oRoot.SelectSingleNode("IncludeAssemblyInLog");
            string mRawPath = oDirNode?.InnerText?.Trim();
            if (!string.IsNullOrEmpty(mRawPath) && mRawPath.StartsWith("~"))
            {
                string mBaseDir = AppDomain.CurrentDomain.BaseDirectory;
                string mRelativePart = mRawPath.Substring(1).TrimStart('/', '\\');
                _LogDirectory = Path.Combine(mBaseDir, mRelativePart);
            }
            else
            {
                _LogDirectory = mRawPath;
            }
            if (oEnableNode != null && bool.TryParse(oEnableNode.InnerText, out bool mEnabled))
                _EnableLogging = mEnabled;
            if (oIncludeAssemblyNode != null && bool.TryParse(oIncludeAssemblyNode.InnerText, out bool mInclude))
                _IncludeAssemblyInLog = mInclude;
            _LogDirectory = _LogDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            _EnableLogging = _EnableLogging ?? true;
        }
        catch (Exception ex)
        {
            SafeFallbackLog("Failed to load logger config", ex);
            _LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            _EnableLogging = true;
            _IncludeAssemblyInLog = false;
        }
    }

    private static void SaveToConfigFile()
    {
        try
        {
            string mRelativePath = _LogDirectory;
            string mBaseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(mRelativePath) && mRelativePath.StartsWith(mBaseDir, StringComparison.OrdinalIgnoreCase))
            {
                mRelativePath = "~" + mRelativePath.Substring(mBaseDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            XmlDocument oDoc = new XmlDocument();
            XmlDeclaration oDecl = oDoc.CreateXmlDeclaration("1.0", "utf-8", null);
            oDoc.AppendChild(oDecl);
            XmlElement oRoot = oDoc.CreateElement("LoggerConfig");
            oDoc.AppendChild(oRoot);
            XmlElement oDirElem = oDoc.CreateElement("LogDirectory");
            oDirElem.InnerText = mRelativePath ?? "~\\log";
            oRoot.AppendChild(oDirElem);
            XmlElement oEnableElem = oDoc.CreateElement("EnableLogging");
            oEnableElem.InnerText = _EnableLogging.ToString();
            oRoot.AppendChild(oEnableElem);
            XmlElement oIncludeAssemblyElem = oDoc.CreateElement("IncludeAssemblyInLog");
            oIncludeAssemblyElem.InnerText = _IncludeAssemblyInLog.ToString();
            oRoot.AppendChild(oIncludeAssemblyElem);
            string mTempFile = _ConfigFilePath + ".tmp";
            oDoc.Save(mTempFile);
            if (File.Exists(_ConfigFilePath))
                File.Replace(mTempFile, _ConfigFilePath, _ConfigFilePath + ".backup");
            else
                File.Move(mTempFile, _ConfigFilePath);
        }
        catch (Exception ex)
        {
            SafeFallbackLog("Failed to save logger config", ex);
        }
    }
}

public static class BlockingCollectionExtensions
{
    public static int TryTakeMultiple<T>(this BlockingCollection<T> oCollection, T[] oArray, int iStartIndex, int iCount, int iMillisecondsTimeout)
    {
        if (oCollection == null) throw new ArgumentNullException(nameof(oCollection));
        if (oArray == null) throw new ArgumentNullException(nameof(oArray));
        int mItemsTaken = 0;
        var mStopwatch = Stopwatch.StartNew();
        int mRemainingTimeout = iMillisecondsTimeout;
        for (int j = 0; j < iCount; j++)
        {
            T item;
            if (oCollection.TryTake(out item, mRemainingTimeout))
            {
                oArray[iStartIndex + j] = item;
                mItemsTaken++;
                if (iMillisecondsTimeout != Timeout.Infinite)
                {
                    mRemainingTimeout = iMillisecondsTimeout - (int)mStopwatch.ElapsedMilliseconds;
                    if (mRemainingTimeout <= 0) break;
                }
            }
            else
            {
                break;
            }
        }
        return mItemsTaken;
    }
}