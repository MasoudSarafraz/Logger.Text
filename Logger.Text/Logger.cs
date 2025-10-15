using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
public static class Logger
{
    private static string _LogDirectory;
    private static bool? _EnableLogging;
    private static bool _IncludeAssemblyInLog = false;
    private static readonly object _InitLock = new object();
    private const string mConfigFileName = "logger_config.xml";
    private static readonly string _ConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, mConfigFileName);
    private static readonly string _LogBaseName = "application_log.txt";
    private static readonly long _MaxLogSize = 5L * 1024 * 1024;
    private static readonly int _MaxBackups = 10;
    private static int _MaxQueueSize = 50000;
    private static BlockingCollection<LogEntry> _LogQueue;
    private static Task _WriterTask;
    private static volatile bool _ShouldStop = false;
    private static CancellationTokenSource _CancellationTokenSource = new CancellationTokenSource();
    private static TimeSpan _WriteInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan _FileOperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly int _MaxRetryCount = 3;
    private static readonly ManualResetEventSlim _ShutdownCompleteEvent = new ManualResetEventSlim(false);
    private static readonly Newtonsoft.Json.JsonSerializerSettings _JsonSettings = new Newtonsoft.Json.JsonSerializerSettings { MaxDepth = 5, NullValueHandling = Newtonsoft.Json.NullValueHandling.Include, ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore, Formatting = Newtonsoft.Json.Formatting.Indented };
    private static PerformanceCounter _CpuCounter;
    private static PerformanceCounter _MemCounter;
    private static readonly object _MetricsLock = new object();
    private static LoggerMetrics _Metrics = new LoggerMetrics();
    private static int _CurrentBatchSize = 100;
    private static int _SamplingRate = 1;
    private static int _SamplingCounter = 0;
    private static CircuitBreaker _CircuitBreaker = new CircuitBreaker();
    private static AdaptiveThrottler _Throttler = new AdaptiveThrottler();
    private static SelfRepairManager _SelfRepairManager = new SelfRepairManager();
    private static volatile bool _LoggerOperational = true;
    private static volatile int _RestartCount = 0;
    private static readonly int _MaxRestartAttempts = 5;
    private static readonly TimeSpan _RestartDelay = TimeSpan.FromSeconds(5);
    private static volatile bool _IsShuttingDown = false;
    private static volatile int _ConsecutiveWriteErrors = 0;
    private static readonly int _MaxConsecutiveWriteErrors = 10;
    private static volatile int _DroppedLogsCount = 0;
    private static DateTime _LastDropWarningTime = DateTime.MinValue;
    private static readonly TimeSpan _DropWarningInterval = TimeSpan.FromMinutes(1);
    private static volatile bool _HighLoadMode = false;
    private static readonly int _HighLoadBatchSize = 50;
    private static readonly int _NormalBatchSize = 100;
    private static readonly TimeSpan _HighLoadWriteInterval = TimeSpan.FromMilliseconds(10);
    private static ILogSink _logSink;
    private static HttpListener _embeddedServer;
    private static Task _serverTask;
    private static readonly CancellationTokenSource _serverCts = new CancellationTokenSource();
    private static readonly string _reportHtmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log_report.html");
    private static bool _WebDashboardEnabled = true;
    private static int _WebDashboardPort = 8080;
    static Logger()
    {
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
    }
    private static void InitializeQueue()
    {
        int mMemoryBasedSize = GetMemoryBasedQueueSize();
        _MaxQueueSize = Math.Min(100000, Math.Max(10000, mMemoryBasedSize));
        _LogQueue = new BlockingCollection<LogEntry>(_MaxQueueSize);
    }
    private static void StartWriterTask()
    {
        try
        {
            _WriterTask = Task.Factory.StartNew(BackgroundWriterLoop, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach);
            _ConsecutiveWriteErrors = 0;
            _LoggerOperational = true;
        }
        catch (Exception mEx)
        {
            _LoggerOperational = false;
            SafeFallbackLog(string.Format("Logger startup failed: {0}", mEx.Message));
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
            catch (Exception mEx)
            {
                SafeFallbackLog(string.Format("LogDirectory get failed: {0}", mEx.Message));
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
            catch (Exception mEx)
            {
                SafeFallbackLog(string.Format("EnableLogging get failed: {0}", mEx.Message));
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
                InitializeFromConfig();
                if (mLogDirectory != null) _LogDirectory = mLogDirectory;
                if (mEnableLogging != null) _EnableLogging = mEnableLogging;
                if (mIncludeAssemblyInLog != null) _IncludeAssemblyInLog = mIncludeAssemblyInLog.Value;
                SaveToConfigFile();
                InitializeQueue();
                Configure(new JsonFileSink(LogDirectory));
                // --- اصلاح مهم: راه‌اندازی وب سرور نباید کل سیستم را از کار بیندازد ---
                try
                {
                    if (_WebDashboardEnabled) StartWebDashboard(_WebDashboardPort);
                }
                catch (Exception mEx)
                {
                    // فقط یک هشدار چاپ کن و ادامه بده
                    SafeFallbackLog(string.Format("Web dashboard failed to start, but file logging will continue: {0}", mEx.Message));
                }
            }
        }
        catch (Exception mEx)
        {
            SafeFallbackLog(string.Format("Initialize failed: {0}", mEx.Message));
            _LoggerOperational = false;
        }
    }
    public static void Configure(ILogSink mSink)
    {
        if (mSink == null) throw new ArgumentNullException("mSink");
        _logSink = mSink;
        StartWriterTask();
    }
    public static void LogInfo(string mMessage, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational || _logSink == null) return;
            var mLogEntry = new LogEntry { Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), Level = "INFO", Message = mMessage, CallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber) };
            SafeEnqueue(mLogEntry);
        }
        catch { }
    }
    public static void LogInfo(object oData, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational || _logSink == null) return;
            var mLogEntry = new LogEntry { Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), Level = "INFO", Message = SafeSerializeObject(oData), CallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber) };
            SafeEnqueue(mLogEntry);
        }
        catch { }
    }
    public static void LogError(string mMessage, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational || _logSink == null) return;
            var mLogEntry = new LogEntry { Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), Level = "ERROR", Message = mMessage, CallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber) };
            SafeEnqueue(mLogEntry);
        }
        catch { }
    }
    public static void LogError(object oException, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational || _logSink == null || oException == null) return;
            var mLogEntry = new LogEntry { Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), Level = "ERROR", Message = SafeSerializeObject(oException), CallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber) };
            SafeEnqueue(mLogEntry);
        }
        catch { }
    }
    //public static void StartWebDashboard(int mPort)
    //{
    //    // --- اصلاح: اطمینان از وجود فایل HTML ---
    //    EnsureReportFileExists();

    //    if (_embeddedServer != null && _embeddedServer.IsListening)
    //    {
    //        Console.WriteLine(string.Format("[Logger] Dashboard is already running on {0}", _embeddedServer.Prefixes.FirstOrDefault()));
    //        return;
    //    }
    //    try
    //    {
    //        _embeddedServer = new HttpListener();
    //        _embeddedServer.Prefixes.Add(string.Format("http://localhost:{0}/", mPort));
    //        _embeddedServer.Start();
    //        Console.WriteLine(string.Format("[Logger] Web dashboard started successfully at http://localhost:{0}", mPort));
    //        _serverTask = Task.Factory.StartNew(() =>
    //        {
    //            while (!_serverCts.Token.IsCancellationRequested)
    //            {
    //                try
    //                {
    //                    var mContext = _embeddedServer.GetContext();
    //                    Task.Factory.StartNew(() => HandleRequest(mContext), _serverCts.Token);
    //                }
    //                catch (ObjectDisposedException) { break; }
    //                catch (HttpListenerException) { break; }
    //            }
    //        }, _serverCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    //    }
    //    catch (HttpListenerException mEx)
    //    {
    //        // این خطا در متد Initialize نیز مدیریت می‌شود، اما اینجا هم برای اطمینان باقی می‌ماند
    //        throw new Exception(string.Format("Could not start web server on port {0}. Please run 'netsh http add urlacl url=http://localhost:{0}/ user=Everyone' as an Administrator.", mPort), mEx);
    //    }
    //}
    public static void StartWebDashboard(int mPort)
    {
        // --- اطمینان از وجود فایل HTML ---
        EnsureReportFileExists();

        if (_embeddedServer != null && _embeddedServer.IsListening)
        {
            Console.WriteLine(string.Format("[Logger] Dashboard is already running on {0}", _embeddedServer.Prefixes.FirstOrDefault()));
            return;
        }
        try
        {
            _embeddedServer = new HttpListener();
            _embeddedServer.Prefixes.Add(string.Format("http://localhost:{0}/", mPort));
            _embeddedServer.Start();
            Console.WriteLine(string.Format("[Logger] Web dashboard started successfully at http://localhost:{0}", mPort));
            _serverTask = Task.Factory.StartNew(() =>
            {
                while (!_serverCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var mContext = _embeddedServer.GetContext();
                        Task.Factory.StartNew(() => HandleRequest(mContext), _serverCts.Token);
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (HttpListenerException) { break; }
                }
            }, _serverCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (HttpListenerException mEx)
        {
            throw new Exception(string.Format("Could not start web server on port {0}. Please run 'netsh http add urlacl url=http://localhost:{0}/ user=Everyone' as an Administrator.", mPort), mEx);
        }
    }
    // --- متد جدید برای ساخت فایل HTML ---
    private static void EnsureReportFileExists()
    {
        if (!File.Exists(_reportHtmlPath))
        {
            try
            {
                string mHtmlContent = GetHtmlTemplate();
                File.WriteAllText(_reportHtmlPath, mHtmlContent, Encoding.UTF8);
                Console.WriteLine(string.Format("[Logger] Created default dashboard file at: {0}", _reportHtmlPath));
            }
            catch (Exception mEx)
            {
                SafeFallbackLog(string.Format("Failed to create dashboard HTML file: {0}", mEx.Message));
            }
        }
    }
    public static void OpenDashboard()
    {
        try
        {
            new Process { StartInfo = new ProcessStartInfo(string.Format("http://localhost:{0}", _WebDashboardPort)) { UseShellExecute = true } }.Start();
        }
        catch (Exception mEx)
        {
            Console.WriteLine(string.Format("Could not open dashboard: {0}", mEx.Message));
        }
    }
    //private static void HandleRequest(HttpListenerContext mContext)
    //{
    //    var mRequest = mContext.Request;
    //    var mResponse = mContext.Response;
    //    try
    //    {
    //        if (mRequest.Url.AbsolutePath == "/")
    //        {
    //            ServeFile(mResponse, _reportHtmlPath, "text/html");
    //        }
    //        else if (mRequest.Url.AbsolutePath == "/api/logs")
    //        {
    //            string mLogFilePath = Path.Combine(LogDirectory, "application_log.jsonl");
    //            ServeFile(mResponse, mLogFilePath, "application/json; charset=utf-8");
    //        }
    //        else
    //        {
    //            mResponse.StatusCode = 404;
    //            mResponse.Close();
    //        }
    //    }
    //    catch (Exception mEx)
    //    {
    //        Console.WriteLine(string.Format("[Logger] Web server error: {0}", mEx.Message));
    //        mResponse.StatusCode = 500;
    //        mResponse.Close();
    //    }
    //}
    private static void HandleRequest(HttpListenerContext mContext)
    {
        var mRequest = mContext.Request;
        var mResponse = mContext.Response;
        try
        {
            if (mRequest.Url.AbsolutePath == "/")
            {
                ServeFile(mResponse, _reportHtmlPath, "text/html");
            }
            else if (mRequest.Url.AbsolutePath == "/api/logs")
            {
                string mLogFilePath = Path.Combine(LogDirectory, "application_log.jsonl");
                ServeFile(mResponse, mLogFilePath, "application/json; charset=utf-8");
            }
            // --- این بخش جدید و بسیار مهم است ---
            else if (mRequest.Url.AbsolutePath == "/api/metrics")
            {
                Console.WriteLine("[Logger] Serving metrics..."); // خط برای اطمینان از دریافت درخواست
                var mMetrics = GetMetrics();
                string mJsonMetrics = Newtonsoft.Json.JsonConvert.SerializeObject(mMetrics, _JsonSettings);
                byte[] mMetricsBytes = Encoding.UTF8.GetBytes(mJsonMetrics);
                mResponse.ContentType = "application/json; charset=utf-8";
                mResponse.ContentLength64 = mMetricsBytes.Length;
                mResponse.OutputStream.Write(mMetricsBytes, 0, mMetricsBytes.Length);
                mResponse.Close();
            }
            else
            {
                mResponse.StatusCode = 404;
                mResponse.Close();
            }
        }
        catch (Exception mEx)
        {
            Console.WriteLine(string.Format("[Logger] Web server error: {0}", mEx.Message));
            mResponse.StatusCode = 500;
            mResponse.Close();
        }
    }
    private static void ServeFile(HttpListenerResponse mResponse, string mFilePath, string mContentType)
    {
        if (!File.Exists(mFilePath))
        {
            mResponse.StatusCode = 404;
            byte[] mNotFoundBytes = Encoding.UTF8.GetBytes("File not found.");
            mResponse.OutputStream.Write(mNotFoundBytes, 0, mNotFoundBytes.Length);
            mResponse.Close();
            return;
        }
        byte[] mFileBytes = File.ReadAllBytes(mFilePath);
        mResponse.ContentType = mContentType;
        mResponse.ContentLength64 = mFileBytes.Length;
        mResponse.OutputStream.Write(mFileBytes, 0, mFileBytes.Length);
        mResponse.Close();
    }
    public static void Shutdown()
    {
        if (_IsShuttingDown) return;
        _IsShuttingDown = true;
        try
        {
            _serverCts.Cancel();
            if (_embeddedServer != null && _embeddedServer.IsListening)
            {
                _embeddedServer.Stop();
                _embeddedServer.Close();
            }
            if (_serverTask != null) { try { _serverTask.Wait(); } catch { } }
            _ShouldStop = true;
            _CancellationTokenSource.Cancel();
            _LogQueue.CompleteAdding();
            int mWaitTime = Math.Min(30000, Math.Max(5000, _LogQueue.Count * 10));
            if (!_ShutdownCompleteEvent.Wait(TimeSpan.FromMilliseconds(mWaitTime)))
            {
                SafeFallbackLog(string.Format("Graceful shutdown timeout after {0}ms, {1} items remaining", mWaitTime, _LogQueue.Count));
            }
            if (_DroppedLogsCount > 0)
            {
                string mSummary = string.Format("[SHUTDOWN] Dropped logs: {0}, Restart attempts: {1}", _DroppedLogsCount, _RestartCount);
                EmergencyWriteToDisk(mSummary);
            }
            _CancellationTokenSource.Dispose();
            _CpuCounter?.Dispose();
            _MemCounter?.Dispose();
        }
        catch (Exception mEx)
        {
            SafeFallbackLog(string.Format("Shutdown failed: {0}", mEx.Message));
        }
    }
    private static bool ShouldSampleLog()
    {
        if (_SamplingRate <= 1) return true;
        _SamplingCounter++;
        if (_SamplingCounter % _SamplingRate == 0) { return true; }
        lock (_MetricsLock) { _Metrics.SampledLogsCount++; }
        return false;
    }
    private static int GetMemoryBasedQueueSize()
    {
        try
        {
            if (_MemCounter == null) return 50000;
            float mAvailableMemory = _MemCounter.NextValue();
            if (mAvailableMemory > 1000) return 100000;
            if (mAvailableMemory > 500) return 75000;
            if (mAvailableMemory > 200) return 50000;
            if (mAvailableMemory > 100) return 25000;
            return 10000;
        }
        catch { return 50000; }
    }
    private static void UpdateIntelligentSettings()
    {
        try
        {
            float mCpuUsage = _CpuCounter?.NextValue() ?? 0;
            float mAvailableMemory = _MemCounter?.NextValue() ?? 1000;
            int mQueueLoad = (int)((double)_LogQueue.Count / _MaxQueueSize * 100);
            lock (_MetricsLock)
            {
                _Metrics.CpuUsage = mCpuUsage;
                _Metrics.AvailableMemoryMB = mAvailableMemory;
                _Metrics.QueueLoadPercent = mQueueLoad;
            }
            _Throttler.Adjust(mCpuUsage, mAvailableMemory, mQueueLoad);
            _CurrentBatchSize = _Throttler.OptimalBatchSize;
            _WriteInterval = _Throttler.OptimalWriteInterval;
            if (mQueueLoad > 80 || mAvailableMemory < 200) { _SamplingRate = Math.Min(10, _SamplingRate + 1); }
            else if (mQueueLoad < 30 && mAvailableMemory > 500) { _SamplingRate = Math.Max(1, _SamplingRate - 1); }
            _SelfRepairManager.CheckAndRepair();
        }
        catch (Exception mEx) { SafeFallbackLog(string.Format("Error updating intelligent settings: {0}", mEx.Message)); }
    }
    private static void BackgroundWriterLoop()
    {
        var mBatch = new List<LogEntry>(100);
        var mLastWriteTime = DateTime.UtcNow;
        while (!_ShouldStop || !_LogQueue.IsCompleted)
        {
            try
            {
                UpdateIntelligentSettings();
                LogEntry mEntry;
                while (_LogQueue.TryTake(out mEntry, 10) && mBatch.Count < _CurrentBatchSize) { mBatch.Add(mEntry); }
                bool mShouldFlush = _ShouldStop && _LogQueue.IsCompleted;
                bool mTimeoutReached = (DateTime.UtcNow - mLastWriteTime) >= _WriteInterval && mBatch.Count > 0;
                bool mBatchFull = mBatch.Count >= _CurrentBatchSize;
                if (mShouldFlush || mTimeoutReached || mBatchFull)
                {
                    if (mBatch.Count > 0)
                    {
                        _CircuitBreaker.Execute(() =>
                        {
                            _logSink.Write(mBatch);
                            var mStringBuilder = new StringBuilder();
                            foreach (var entry in mBatch) { mStringBuilder.AppendLine(LogEntryToString(entry)); }
                            WriteBatchToDisk(mStringBuilder.ToString());
                        });
                        _ConsecutiveWriteErrors = 0;
                    }
                    mBatch.Clear();
                    mLastWriteTime = DateTime.UtcNow;
                }
            }
            catch (Exception mEx)
            {
                _ConsecutiveWriteErrors++;
                SafeFallbackLog(string.Format("BackgroundWriterLoop error: {0}, Consecutive errors: {1}", mEx.Message, _ConsecutiveWriteErrors));
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
            if (mBatch.Count > 0)
            {
                _logSink.Write(mBatch);
                var mStringBuilder = new StringBuilder();
                foreach (var entry in mBatch) { mStringBuilder.AppendLine(LogEntryToString(entry)); }
                WriteBatchToDisk(mStringBuilder.ToString());
            }
        }
        catch (Exception mEx) { SafeFallbackLog(string.Format("Final flush failed: {0}", mEx.Message)); }
        _ShutdownCompleteEvent.Set();
    }
    private static string LogEntryToString(LogEntry mEntry)
    {
        return string.Format("[{0}] {1}: {2}{3}", mEntry.Timestamp, mEntry.Level, mEntry.Message, mEntry.CallerInfo);
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
        public void Execute(Action mAction)
        {
            if (_state == State.Open)
            {
                if (DateTime.UtcNow - _lastFailureTime > _timeout) { _state = State.HalfOpen; }
                else { lock (_MetricsLock) { _Metrics.CircuitBreakerTrippedCount++; } return; }
            }
            try
            {
                mAction();
                if (_state == State.HalfOpen) { _state = State.Closed; _failureCount = 0; }
            }
            catch (Exception mEx)
            {
                _failureCount++;
                _lastFailureTime = DateTime.UtcNow;
                if (_failureCount >= _maxFailures)
                {
                    _state = State.Open;
                    SafeFallbackLog("Circuit breaker tripped", mEx);
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
        public void Adjust(float mCpuUsage, float mAvailableMemory, int mQueueLoad)
        {
            if (DateTime.UtcNow - _lastAdjustment < _adjustmentInterval) return;
            _lastAdjustment = DateTime.UtcNow;
            if (mCpuUsage > 80 || mQueueLoad > 80 || mAvailableMemory < 200)
            {
                _optimalBatchSize = Math.Max(10, _optimalBatchSize - 20);
                _optimalWriteInterval = TimeSpan.FromMilliseconds(Math.Min(200, _optimalWriteInterval.TotalMilliseconds + 10));
            }
            else if (mCpuUsage < 30 && mQueueLoad < 30 && mAvailableMemory > 500)
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
            if (DateTime.UtcNow - _lastRepairTime < _repairInterval) return;
            lock (_repairLock)
            {
                if (DateTime.UtcNow - _lastRepairTime < _repairInterval) return;
                _lastRepairTime = DateTime.UtcNow;
                try { RepairLogDirectory(); RepairDiskSpace(); RepairConfigFile(); RepairPermissions(); }
                catch (Exception mEx) { SafeFallbackLog(string.Format("Error during self-repair: {0}", mEx.Message)); }
            }
        }
        private void RepairLogDirectory()
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) { Directory.CreateDirectory(LogDirectory); SafeFallbackLog("Log directory was missing and has been recreated"); }
                string mTestFile = Path.Combine(LogDirectory, string.Format("access_test_{0:yyyyMMdd_HHmmss}.tmp", DateTime.UtcNow));
                File.WriteAllText(mTestFile, "test");
                File.Delete(mTestFile);
            }
            catch (Exception mEx)
            {
                try
                {
                    string mFallbackDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log_fallback");
                    Directory.CreateDirectory(mFallbackDir);
                    lock (_InitLock) { _LogDirectory = mFallbackDir; }
                    SafeFallbackLog("Log directory was inaccessible, switched to fallback directory", mEx);
                }
                catch (Exception mFallbackEx) { SafeFallbackLog("Failed to create fallback log directory", mFallbackEx); }
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
                if (dFreePercentage < 5)
                {
                    SafeFallbackLog(string.Format("Low disk space: {0:F1}% free. Attempting to clean up old logs.", dFreePercentage));
                    var oBackupFiles = new DirectoryInfo(LogDirectory).GetFiles("backup_application_log_*").OrderBy(f => f.LastWriteTimeUtc).ToList();
                    foreach (var oFile in oBackupFiles)
                    {
                        try { oFile.Delete(); lFreeSpace = oDriveInfo.AvailableFreeSpace; dFreePercentage = (double)lFreeSpace / lTotalSpace * 100; if (dFreePercentage > 10) { SafeFallbackLog(string.Format("Disk space recovered to {0:F1}% after cleanup", dFreePercentage)); break; } }
                        catch (Exception mEx) { SafeFallbackLog(string.Format("Failed to delete backup file {0}", oFile.Name), mEx); }
                    }
                }
            }
            catch (Exception mEx) { SafeFallbackLog("Error during disk space repair", mEx); }
        }
        private void RepairConfigFile()
        {
            try
            {
                if (File.Exists(_ConfigFilePath))
                {
                    var oDoc = new XmlDocument();
                    oDoc.Load(_ConfigFilePath);
                    if (oDoc.DocumentElement == null || oDoc.DocumentElement.SelectSingleNode("LogDirectory") == null || oDoc.DocumentElement.SelectSingleNode("EnableLogging") == null) { throw new Exception("Invalid config file structure"); }
                }
            }
            catch (Exception mEx)
            {
                SafeFallbackLog("Config file is corrupted, resetting to defaults", mEx);
                try { lock (_InitLock) { _LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log"); _EnableLogging = true; _WebDashboardEnabled = true; _WebDashboardPort = 8080; SaveToConfigFile(); } }
                catch (Exception mResetEx) { SafeFallbackLog("Failed to reset config file", mResetEx); }
            }
        }
        private void RepairPermissions()
        {
            try
            {
                string mTestFile = Path.Combine(LogDirectory, string.Format("permission_test_{0:yyyyMMdd_HHmmss}.tmp", DateTime.UtcNow));
                try { File.WriteAllText(mTestFile, "test"); File.Delete(mTestFile); }
                catch (UnauthorizedAccessException)
                {
                    SafeFallbackLog("No write permissions to log directory, attempting to fix");
                    try
                    {
                        DirectoryInfo mDirInfo = new DirectoryInfo(LogDirectory);
                        DirectorySecurity mDirSecurity = mDirInfo.GetAccessControl();
                        string mCurrentUser = WindowsIdentity.GetCurrent().Name;
                        mDirSecurity.AddAccessRule(new FileSystemAccessRule(mCurrentUser, FileSystemRights.Write, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                        mDirInfo.SetAccessControl(mDirSecurity);
                        File.WriteAllText(mTestFile, "test");
                        File.Delete(mTestFile);
                        SafeFallbackLog("Successfully repaired directory permissions");
                    }
                    catch (Exception mPermEx) { SafeFallbackLog("Failed to repair directory permissions", mPermEx); }
                }
            }
            catch (Exception mEx) { SafeFallbackLog("Error during permission repair", mEx); }
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
            return new LoggerMetrics { CpuUsage = _Metrics.CpuUsage, AvailableMemoryMB = _Metrics.AvailableMemoryMB, QueueLoadPercent = _Metrics.QueueLoadPercent, CurrentBatchSize = _Metrics.CurrentBatchSize, CurrentWriteIntervalMs = _Metrics.CurrentWriteIntervalMs, SampledLogsCount = _Metrics.SampledLogsCount, CircuitBreakerTrippedCount = _Metrics.CircuitBreakerTrippedCount, WriteErrorsCount = _Metrics.WriteErrorsCount, TotalLogsWritten = _Metrics.TotalLogsWritten, AverageWriteTimeMs = _Metrics.AverageWriteTimeMs, SelfRepairActionsCount = _Metrics.SelfRepairActionsCount, RestartCount = _RestartCount, DroppedLogsCount = _Metrics.DroppedLogsCount };
        }
    }
    private static void SafeEnqueue(LogEntry mLogEntry)
    {
        try
        {
            if (!_LoggerOperational || _LogQueue.IsAddingCompleted) { EmergencyWriteToDisk(SafeSerializeObject(mLogEntry)); return; }
            if (!_LogQueue.TryAdd(mLogEntry, 50))
            {
                Interlocked.Increment(ref _DroppedLogsCount);
                EmergencyWriteToDisk(SafeSerializeObject(mLogEntry));
                var mNow = DateTime.UtcNow;
                if (mNow - _LastDropWarningTime > _DropWarningInterval)
                {
                    _LastDropWarningTime = mNow;
                    int mDropped = _DroppedLogsCount;
                    SafeFallbackLog(string.Format("Queue overload! Dropped {0} messages. Consider optimizing log frequency.", mDropped));
                }
                _HighLoadMode = true;
            }
        }
        catch { Interlocked.Increment(ref _DroppedLogsCount); EmergencyWriteToDisk(SafeSerializeObject(mLogEntry)); }
    }
    private static void EmergencyWriteToDisk(string mLogMessage)
    {
        try
        {
            string mEmergencyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "emergency_log.txt");
            string mFullMessage = string.Format("[EMERGENCY] {0:yyyy-MM-dd HH:mm:ss.fff} {1}{2}", DateTime.UtcNow, mLogMessage, Environment.NewLine);
            lock (typeof(Logger)) { File.AppendAllText(mEmergencyPath, mFullMessage, Encoding.UTF8); }
        }
        catch { }
    }
    private static void SafeFallbackLog(string mMessage, Exception mEx = null)
    {
        try
        {
            lock (_MetricsLock) { _Metrics.SelfRepairActionsCount++; }
            if (!EventLog.SourceExists("ApplicationLogger")) EventLog.CreateEventSource("ApplicationLogger", "Application");
            string mLogMessage = mEx != null ? string.Format("{0}: {1}", mMessage, mEx.Message) : mMessage;
            EventLog.WriteEntry("ApplicationLogger", mLogMessage, EventLogEntryType.Error, 1001);
        }
        catch
        {
            try
            {
                string mFallbackPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logger_errors.txt");
                string mErrorText = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}: {2}\n", DateTime.UtcNow, mMessage, mEx);
                File.AppendAllText(mFallbackPath, mErrorText);
            }
            catch { Debug.WriteLine(string.Format("Logger Internal Error: {0} - {1}", mMessage, mEx)); }
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
                try { var oStack = new StackTrace(1, false); var oFrame = oStack.GetFrame(0); var oMethod = oFrame?.GetMethod(); string mAssemblyName = oMethod?.DeclaringType?.Assembly?.GetName().Name ?? "Unknown"; mAssemblyPrefix = mAssemblyName + ":"; }
                catch { mAssemblyPrefix = "Unknown:"; }
            }
            return string.Format(" [{0} -> {1} -> {2} Line{3}]", mAssemblyPrefix, mClassName, mMemberName, mLineNumber);
        }
        catch { return " [CallerInfoUnavailable]"; }
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
                    if (!Directory.Exists(LogDirectory)) { Directory.CreateDirectory(LogDirectory); SafeFallbackLog("Log directory was missing and has been recreated"); }
                    if (File.Exists(LogFilePath))
                    {
                        long mCurrentSize = new FileInfo(LogFilePath).Length;
                        if (mCurrentSize >= _MaxLogSize) CreateBackup();
                    }
                    using (var oFileStream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, 8192, FileOptions.SequentialScan))
                    using (var oWriter = new StreamWriter(oFileStream, Encoding.UTF8)) { oWriter.Write(mBatchText); oWriter.Flush(); }
                }
                mSuccess = true;
                oStopwatch.Stop();
                lock (_MetricsLock)
                {
                    _Metrics.TotalLogsWritten++;
                    _Metrics.AverageWriteTimeMs = (_Metrics.AverageWriteTimeMs * (_Metrics.TotalLogsWritten - 1) + oStopwatch.ElapsedMilliseconds) / _Metrics.TotalLogsWritten;
                }
            }
            catch (Exception mEx) when (mRetryCount < _MaxRetryCount - 1) { mRetryCount++; SafeFallbackLog(string.Format("Failed to write logs (attempt {0})", mRetryCount), mEx); Thread.Sleep(500 * mRetryCount); }
            catch (Exception mEx) { SafeFallbackLog("Failed to write logs after retries", mEx); lock (_MetricsLock) { _Metrics.WriteErrorsCount++; } return false; }
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
                string mBackupName = string.Format("backup_application_log_{0}.txt", mTimestamp);
                string mBackupPath = Path.Combine(LogDirectory, mBackupName);
                if (File.Exists(mBackupPath)) { mBackupName = string.Format("backup_application_log_{0}_{1}.txt", mTimestamp, Guid.NewGuid().ToString("N")); mBackupPath = Path.Combine(LogDirectory, mBackupName); }
                File.Move(LogFilePath, mBackupPath);
                CompressOldBackups();
                CleanOldBackups();
                mSuccess = true;
            }
            catch (Exception mEx) when (mRetryCount < _MaxRetryCount - 1) { mRetryCount++; SafeFallbackLog(string.Format("Failed to create backup (attempt {0})", mRetryCount), mEx); Thread.Sleep(500 * mRetryCount); }
            catch (Exception mEx) { SafeFallbackLog("Failed to create backup after retries", mEx); break; }
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
                    using (var oCompressionStream = new System.IO.Compression.GZipStream(oOutputStream, System.IO.Compression.CompressionMode.Compress)) { oInputStream.CopyTo(oCompressionStream); }
                    mFile.Delete();
                }
            }
        }
        catch (Exception mEx) { SafeFallbackLog("Failed to compress old backups", mEx); }
    }
    private static void CleanOldBackups()
    {
        try
        {
            var oOldBackups = new DirectoryInfo(LogDirectory).GetFiles("backup_application_log_*").OrderByDescending(f => f.LastWriteTimeUtc).Skip(_MaxBackups);
            foreach (var mFile in oOldBackups) { try { mFile.Delete(); } catch { } }
        }
        catch (Exception mEx) { SafeFallbackLog("Failed to clean old backups", mEx); }
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
                SafeFallbackLog(string.Format("Attempting logger restart ({0}/{1})", _RestartCount, _MaxRestartAttempts));
                if (_WriterTask != null) { _ShouldStop = true; _CancellationTokenSource.Cancel(); _ShutdownCompleteEvent.Wait(1000); _WriterTask = null; }
                Thread.Sleep(_RestartDelay);
                var oldCts = _CancellationTokenSource;
                _CancellationTokenSource = new CancellationTokenSource();
                oldCts.Dispose();
                _ShouldStop = false;
                _ConsecutiveWriteErrors = 0;
                StartWriterTask();
                SafeFallbackLog("Logger successfully restarted");
            }
            catch (Exception mEx)
            {
                SafeFallbackLog(string.Format("Logger restart failed: {0}", mEx.Message));
                if (_RestartCount >= _MaxRestartAttempts) { SafeFallbackLog("Maximum restart attempts reached. Logger disabled."); }
                else { TryRestartLoggerWithDelay(); }
            }
        }
    }
    private static void TryRestartLoggerWithDelay()
    {
        Task.Delay(_RestartDelay).ContinueWith(_ => { if (!_IsShuttingDown && _RestartCount < _MaxRestartAttempts) { TryRestartLogger(); } });
    }
    private static void OnProcessExit(object sender, EventArgs e) { Shutdown(); }
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) { try { if (e.ExceptionObject is Exception ex) { SafeFallbackLog("Unhandled application exception", ex); } } catch { } }
    private static string SafeSerializeObject(object oObj)
    {
        if (oObj == null) return "NULL";
        try { return Newtonsoft.Json.JsonConvert.SerializeObject(oObj, _JsonSettings); }
        catch (Exception mSerializationEx)
        {
            try
            {
                var oProps = oObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                var oDict = new Dictionary<string, object>();
                foreach (var oProp in oProps) { try { oDict[oProp.Name] = oProp.GetValue(oObj); } catch { } }
                return Newtonsoft.Json.JsonConvert.SerializeObject(oDict, _JsonSettings);
            }
            catch
            {
                try { return oObj.ToString() ?? string.Format("[ToString failed: {0}]", mSerializationEx.Message); }
                catch { return string.Format("[SERIALIZE_FAILED: {0}]", mSerializationEx.Message); }
            }
        }
    }
    private static void InitializeFromConfig()
    {
        if (_LogDirectory != null && _EnableLogging != null) return;
        try
        {
            if (File.Exists(_ConfigFilePath)) { LoadFromConfigFile(); }
            else
            {
                _LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
                _EnableLogging = true;
                _IncludeAssemblyInLog = false;
                _WebDashboardEnabled = true;
                _WebDashboardPort = 8080;
                SaveToConfigFile();
            }
        }
        catch (Exception mEx) { SafeFallbackLog("Logger initialization failed", mEx); _LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log"); _EnableLogging = true; _WebDashboardEnabled = true; _WebDashboardPort = 8080; }
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
            XmlNode oDashboardEnabledNode = oRoot.SelectSingleNode("WebDashboardEnabled");
            XmlNode oDashboardPortNode = oRoot.SelectSingleNode("WebDashboardPort");
            string mRawPath = oDirNode?.InnerText?.Trim();
            if (!string.IsNullOrEmpty(mRawPath) && mRawPath.StartsWith("~"))
            {
                string mBaseDir = AppDomain.CurrentDomain.BaseDirectory;
                string mRelativePart = mRawPath.Substring(1).TrimStart('/', '\\');
                _LogDirectory = Path.Combine(mBaseDir, mRelativePart);
            }
            else { _LogDirectory = mRawPath; }
            if (oEnableNode != null && bool.TryParse(oEnableNode.InnerText, out bool mEnabled)) _EnableLogging = mEnabled;
            if (oIncludeAssemblyNode != null && bool.TryParse(oIncludeAssemblyNode.InnerText, out bool mInclude)) _IncludeAssemblyInLog = mInclude;
            if (oDashboardEnabledNode != null && bool.TryParse(oDashboardEnabledNode.InnerText, out bool mDashboardEnabled)) _WebDashboardEnabled = mDashboardEnabled;
            if (oDashboardPortNode != null && int.TryParse(oDashboardPortNode.InnerText, out int mDashboardPort)) _WebDashboardPort = mDashboardPort;
            _LogDirectory = _LogDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            _EnableLogging = _EnableLogging ?? true;
        }
        catch (Exception mEx) { SafeFallbackLog("Failed to load logger config", mEx); _LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log"); _EnableLogging = true; _WebDashboardEnabled = true; _WebDashboardPort = 8080; }
    }
    private static void SaveToConfigFile()
    {
        try
        {
            string mRelativePath = _LogDirectory;
            string mBaseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(mRelativePath) && mRelativePath.StartsWith(mBaseDir, StringComparison.OrdinalIgnoreCase)) { mRelativePath = "~" + mRelativePath.Substring(mBaseDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
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
            XmlElement oDashboardEnabledElem = oDoc.CreateElement("WebDashboardEnabled");
            oDashboardEnabledElem.InnerText = _WebDashboardEnabled.ToString();
            oRoot.AppendChild(oDashboardEnabledElem);
            XmlElement oDashboardPortElem = oDoc.CreateElement("WebDashboardPort");
            oDashboardPortElem.InnerText = _WebDashboardPort.ToString();
            oRoot.AppendChild(oDashboardPortElem);
            string mTempFile = _ConfigFilePath + ".tmp";
            oDoc.Save(mTempFile);
            if (File.Exists(_ConfigFilePath)) File.Replace(mTempFile, _ConfigFilePath, _ConfigFilePath + ".backup");
            else File.Move(mTempFile, _ConfigFilePath);
        }
        catch (Exception mEx) { SafeFallbackLog("Failed to save logger config", mEx); }
    }
    private static string GetHtmlTemplate()
    {
        return @"
<!DOCTYPE html>
<html lang='fa' dir='rtl'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>مرکز تحلیل لاگ‌ها</title>
    <script src='https://cdn.jsdelivr.net/npm/chart.js'></script>
    <style>
        @import url('https://fonts.googleapis.com/css2?family=Vazirmatn:wght@400;500;700&display=swap');
        :root {
            --bg-color: #0a0b1e;
            --surface-color: rgba(255, 255, 255, 0.05);
            --surface-color-glass: rgba(255, 255, 255, 0.07);
            --border-color: rgba(255, 255, 255, 0.1);
            --text-primary: #ffffff;
            --text-secondary: #a0a9c9;
            --accent-primary: #00d4ff;
            --accent-success: #00ff88;
            --accent-warning: #ffb700;
            --accent-danger: #ff4757;
            --shadow: 0 8px 32px rgba(0, 0, 0, 0.37);
        }
        * { box-sizing: border-box; }
        body {
            font-family: 'Vazirmatn', sans-serif;
            background: linear-gradient(135deg, #1e3c72 0%, #2a5298 100%);
            color: var(--text-primary);
            margin: 0;
            padding: 0;
            min-height: 100vh;
            backdrop-filter: blur(10px);
        }
        .container {
            max-width: 1800px;
            margin: 0 auto;
            padding: 30px;
        }
        header {
            text-align: center;
            margin-bottom: 40px;
        }
        h1 {
            font-size: 42px;
            font-weight: 700;
            margin: 0;
            background: linear-gradient(90deg, var(--accent-primary), var(--accent-success));
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
        }
        .status-indicator {
            display: inline-flex;
            align-items: center;
            gap: 10px;
            margin-top: 10px;
            font-size: 14px;
            color: var(--text-secondary);
        }
        .status-dot {
            width: 12px;
            height: 12px;
            background-color: var(--accent-success);
            border-radius: 50%;
            animation: pulse 2s infinite;
        }
        @keyframes pulse { 0% { box-shadow: 0 0 0 0 rgba(0, 255, 136, 0.7); } 70% { box-shadow: 0 0 0 10px rgba(0, 255, 136, 0); } 100% { box-shadow: 0 0 0 0 rgba(0, 255, 136, 0); } }
        .metrics-hero {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(250px, 1fr));
            gap: 20px;
            margin-bottom: 40px;
        }
        .metric-card {
            background: var(--surface-color-glass);
            backdrop-filter: blur(10px);
            border: 1px solid var(--border-color);
            border-radius: 20px;
            padding: 30px;
            text-align: center;
            box-shadow: var(--shadow);
            transition: transform 0.3s ease, box-shadow 0.3s ease;
        }
        .metric-card:hover { transform: translateY(-5px); }
        .metric-icon { font-size: 36px; margin-bottom: 15px; }
        .metric-value {
            font-size: 36px;
            font-weight: 700;
            color: var(--text-primary);
            line-height: 1;
        }
        .metric-label {
            font-size: 14px;
            color: var(--text-secondary);
            margin-top: 10px;
        }
        .main-content {
            display: grid;
            grid-template-columns: 2fr 1fr;
            gap: 30px;
        }
        .panel {
            background: var(--surface-color-glass);
            backdrop-filter: blur(10px);
            border: 1px solid var(--border-color);
            border-radius: 20px;
            padding: 25px;
            box-shadow: var(--shadow);
        }
        .panel-title {
            font-size: 20px;
            font-weight: 600;
            margin-bottom: 20px;
            display: flex;
            align-items: center;
            gap: 10px;
        }
        .log-stream {
            height: 60vh;
            overflow-y: auto;
            overflow-x: hidden;
            font-family: 'Courier New', Courier, monospace;
            font-size: 13px;
        }
        .log-stream::-webkit-scrollbar { width: 8px; }
        .log-stream::-webkit-scrollbar-track { background: var(--surface-color); border-radius: 10px; }
        .log-stream::-webkit-scrollbar-thumb { background: var(--border-color); border-radius: 10px; }
        .log-entry {
            padding: 12px;
            border-bottom: 1px solid var(--border-color);
            transition: background-color 0.2s;
            animation: slideIn 0.5s ease-out;
            display: flex;
            align-items: center;
            gap: 10px;
        }
        @keyframes slideIn { from { opacity: 0; transform: translateY(-10px); } to { opacity: 1; transform: translateY(0); } }
        .log-entry:last-child { border-bottom: none; }
        .log-entry:hover { background-color: var(--surface-color); }
        .log-time { color: var(--text-secondary); white-space: nowrap; }
        .log-level { padding: 3px 8px; border-radius: 5px; font-size: 11px; font-weight: 600; white-space: nowrap; }
        .level-INFO { background-color: rgba(0, 212, 255, 0.2); color: var(--accent-primary); }
        .level-ERROR { background-color: rgba(255, 71, 87, 0.2); color: var(--accent-danger); }
        .level-START { background-color: rgba(0, 255, 136, 0.2); color: var(--accent-success); }
        .level-END { background-color: rgba(255, 183, 0, 0.2); color: var(--accent-warning); }
        .log-message-content {
            flex-grow: 1;
            min-width: 0;
            word-break: break-word;
        }
        .controls-stream {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-top: 15px;
        }
        .toggle-btn {
            background: var(--accent-primary);
            color: var(--bg-color);
            border: none;
            padding: 10px 20px;
            border-radius: 10px;
            cursor: pointer;
            font-weight: 600;
            transition: background 0.3s;
        }
        .toggle-btn:hover { background: var(--accent-success); }
        .error-list {
            height: 250px;
            overflow-y: auto;
        }
        .error-item {
            padding: 12px;
            background: var(--surface-color);
            border-radius: 10px;
            margin-bottom: 10px;
            font-size: 13px;
            word-break: break-word;
            cursor: help;
        }
        .error-count {
            color: var(--accent-danger);
            font-weight: 700;
        }
        .chart-container {
            height: 250px;
            margin-top: 20px;
        }
    </style>
</head>
<body>
<div class='container'>
    <header>
        <h1>مرکز تحلیل لاگ‌ها</h1>
        <div class='status-indicator'>
            <span class='status-dot'></span>
            <span id='connectionStatus'>متصل</span>
        </div>
    </header>

    <div class='metrics-hero' id='metricsGrid'>
        <!-- متریک‌ها به صورت داینامیک اینجا قرار می‌گیرند -->
    </div>

    <main class='main-content'>
        <section class='panel'>
            <div class='panel-title'><span>📡</span> جریان زنده لاگ‌ها</div>
            <div class='log-stream' id='logStream'></div>
            <div class='controls-stream'>
                <span id='logCount'>0 لاگ</span>
                <button id='toggleScroll' class='toggle-btn'>قفل اسکرول (چسبیده به بالا)</button>
            </div>
        </section>

        <aside class='panel'>
            <div class='panel-title'><span>🔥</span> پرکاربردترین خطاها</div>
            <div class='error-list' id='errorList'></div>
            
            <div class='panel-title' style='margin-top: 30px;'><span>📊</span> توزیع لاگ‌ها</div>
            <div class='chart-container'>
                <canvas id='logLevelChart'></canvas>
            </div>
        </aside>
    </main>
</div>

<script>
    let allLogs = [];
    let levelChart;
    let lastTimestamp = null;
    let autoScroll = true;

    const state = { level: '', search: '' };

    async function fetchData() {
        try {
            const [logsResponse, metricsResponse] = await Promise.all([
                fetch('/api/logs?t=' + Date.now()),
                fetch('/api/metrics?t=' + Date.now())
            ]);
            if (!logsResponse.ok || !metricsResponse.ok) {
                throw new Error(`Failed to fetch data: ${logsResponse.status} / ${metricsResponse.status}`);
            }

            const logText = await logsResponse.text();
            const newLogs = logText.trim() ? logText.trim().split('\n').map(line => JSON.parse(line)) : [];
            
            const newestLog = newLogs.length > 0 ? newLogs[newLogs.length - 1] : null;
            const newTimestamp = newestLog ? newestLog.Timestamp : null;

            const metrics = await metricsResponse.json();
            updateMetrics(metrics);

            if (newTimestamp !== lastTimestamp) {
                allLogs = newLogs;
                updateLogStream();
                updateErrorList();
                updateLevelChart();
                lastTimestamp = newTimestamp;
            }

            document.getElementById('connectionStatus').textContent = 'متصل';
            document.querySelector('.status-dot').style.backgroundColor = 'var(--accent-success)';

        } catch (error) {
            console.error('Error fetching data:', error);
            document.getElementById('connectionStatus').textContent = 'خطا در اتصال';
            document.querySelector('.status-dot').style.backgroundColor = 'var(--accent-danger)';
        }
    }

    function updateMetrics(metrics) {
        const grid = document.getElementById('metricsGrid');
        grid.innerHTML = `
            <div class='metric-card'><div class='metric-icon'>💻</div><div class='metric-value'>${metrics.CpuUsage.toFixed(1)}%</div><div class='metric-label'>مصرف CPU</div></div>
            <div class='metric-card'><div class='metric-icon'>🧠</div><div class='metric-value'>${metrics.AvailableMemoryMB.toFixed(0)} MB</div><div class='metric-label'>حافظه آزاد</div></div>
            <div class='metric-card'><div class='metric-icon'>📦</div><div class='metric-value'>${metrics.QueueLoadPercent}%</div><div class='metric-label'>بار صف</div></div>
            <div class='metric-card'><div class='metric-icon'>🗑️</div><div class='metric-value'>${metrics.DroppedLogsCount}</div><div class='metric-label'>لاگ‌های حذف شده</div></div>
        `;
    }

    function updateLogStream() {
        const stream = document.getElementById('logStream');
        const logsToShow = allLogs.slice(-50).reverse();
        
        stream.innerHTML = logsToShow.map(log => `
            <div class='log-entry'>
                <span class='log-time'>${new Date(log.Timestamp).toLocaleTimeString('fa-IR')}</span>
                <span class='log-level level-${log.Level}'>${log.Level}</span>
                <div class='log-message-content'>${log.Message}</div>
            </div>
        `).join('');

        document.getElementById('logCount').textContent = `${allLogs.length} لاگ`;

        if (autoScroll) {
            stream.scrollTop = 0;
        }
    }

    function updateErrorList() {
        const errorLogs = allLogs.filter(l => l.Level === 'ERROR');
        if (errorLogs.length === 0) {
            document.getElementById('errorList').innerHTML = `<p style='text-align:center; color:var(--text-secondary);'>هیچ خطایی یافت نشد.</p>`;
            return;
        }

        const errorCounts = new Map();
        errorLogs.forEach(log => {
            errorCounts.set(log.Message, (errorCounts.get(log.Message) || 0) + 1);
        });

        const sortedErrors = Array.from(errorCounts.entries()).sort((a, b) => b[1] - a[1]).slice(0, 10);
        
        const list = document.getElementById('errorList');
        list.innerHTML = sortedErrors.map(error => {
            const fullMessage = error[0];
            const shortMessage = fullMessage.length > 80 ? fullMessage.substring(0, 80) + '...' : fullMessage;
            const safeFullMessage = fullMessage.replace(/'/g, '&apos;');
            
            return `
                <div class='error-item' title='${safeFullMessage}'>
                    <span class='error-count'>${error[1]}x</span> ${shortMessage}
                </div>
            `;
        }).join('');
    }

    function updateLevelChart() {
        const levelCounts = allLogs.reduce((acc, log) => { acc[log.Level] = (acc[log.Level] || 0) + 1; return acc; }, {});
        const ctx = document.getElementById('logLevelChart').getContext('2d');
        if (levelChart) { levelChart.destroy(); levelChart = null; }
        levelChart = new Chart(ctx, {
            type: 'doughnut',
            data: { labels: Object.keys(levelCounts), datasets: [{ data: Object.values(levelCounts), backgroundColor: ['#00d4ff', '#ff4757', '#ffb700', '#00ff88'] }] },
            options: { responsive: true, maintainAspectRatio: false, plugins: { legend: { position: 'bottom', labels: { color: '#a0a9c9' } } } }
        });
    }

    // Event Listeners
    document.getElementById('toggleScroll').addEventListener('click', () => {
        autoScroll = !autoScroll;
        const btn = document.getElementById('toggleScroll');
        btn.textContent = autoScroll ? 'قفل اسکرول (چسبیده به بالا)' : 'اسکرول آزاد';
        if (autoScroll) {
            document.getElementById('logStream').scrollTop = 0;
        }
    });

    // Initial Load and Auto-refresh
    fetchData();
    setInterval(fetchData, 5000);
</script>
</body>
</html>";
    }
}
public static class BlockingCollectionExtensions
{
    public static int TryTakeMultiple<T>(this BlockingCollection<T> oCollection, T[] oArray, int iStartIndex, int iCount, int iMillisecondsTimeout)
    {
        if (oCollection == null) throw new ArgumentNullException("oCollection");
        if (oArray == null) throw new ArgumentNullException("oArray");
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
            else { break; }
        }
        return mItemsTaken;
    }
}