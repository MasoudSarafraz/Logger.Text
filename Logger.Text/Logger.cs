using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Collections.Generic;

public static class Logger
{
    private static string _LogDirectory; // مسیر ذخیره لاگ‌ها
    private static bool? _EnableLogging; // وضعیت فعال بودن لاگ‌گیری
    private static readonly object _InitLock = new object(); // قفل برای مقداردهی اولیه
    private const string mConfigFileName = "logger_config.xml"; // نام فایل کانفیگ
    private static readonly string _ConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, mConfigFileName); // مسیر کامل فایل کانفیگ
    private static readonly string _LogBaseName = "application_log.txt"; // نام فایل لاگ اصلی
    private static readonly long _MaxLogSize = 5L * 1024 * 1024; // حداکثر حجم فایل لاگ (5MB)
    private static readonly int _MaxBackups = 10; // حداکثر تعداد فایل‌های پشتیبان
    private static bool _IncludeAssemblyInLog = false; // نمایش نام اسمبلی در لاگ
    private static readonly BlockingCollection<string> _LogQueue = new BlockingCollection<string>(new ConcurrentQueue<string>(), boundedCapacity: 50000); // صف پیام‌های لاگ
    private static Task _WriterTask; // تسک نویسنده لاگ
    private static volatile bool _ShouldStop = false; // پرچم توقف
    private static readonly ManualResetEventSlim _ShutdownCompleteEvent = new ManualResetEventSlim(false); // رویداد پایان shutdown
    private static readonly TimeSpan _WriteInterval = TimeSpan.FromMilliseconds(50); // فاصله نوشتن
    private static volatile bool _LoggerOperational = true; // وضعیت عملیاتی لاگر
    private static volatile int _RestartCount = 0; // تعداد راه‌اندازی مجدد
    private static readonly int _MaxRestartAttempts = 5; // حداکثر تلاش برای راه‌اندازی مجدد
    private static readonly TimeSpan _RestartDelay = TimeSpan.FromSeconds(5); // تاخیر بین راه‌اندازی مجدد
    private static volatile bool _IsShuttingDown = false; // وضعیت خاموش شدن
    private static volatile int _ConsecutiveWriteErrors = 0; // تعداد خطاهای متوالی نوشتن
    private static readonly int _MaxConsecutiveWriteErrors = 10; // حداکثر خطاهای متوالی مجاز
    private static volatile int _DroppedLogsCount = 0; // تعداد لاگ‌های حذف شده
    private static DateTime _LastDropWarningTime = DateTime.MinValue; // زمان آخرین اخطار
    private static readonly TimeSpan _DropWarningInterval = TimeSpan.FromMinutes(1); // فاصله اخطارها
    private static readonly TimeSpan _HighLoadWriteInterval = TimeSpan.FromMilliseconds(10); // فاصله نوشتن در بار بالا
    private static volatile bool _HighLoadMode = false; // حالت بار بالا
    private static readonly int _HighLoadBatchSize = 50; // اندازه بچ در بار بالا
    private static readonly int _NormalBatchSize = 100; // اندازه بچ معمولی
    private static readonly Newtonsoft.Json.JsonSerializerSettings _JsonSettings = new Newtonsoft.Json.JsonSerializerSettings
    {
        MaxDepth = 5,
        NullValueHandling = Newtonsoft.Json.NullValueHandling.Include,
        ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
        Formatting = Newtonsoft.Json.Formatting.Indented
    };

    static Logger()
    {
        StartWriterTask();
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
            }
        }
        catch (Exception ex)
        {
            SafeFallbackLog($"Initialize failed: {ex.Message}");
            _LoggerOperational = false;
        }
    }

    private static void InitializeFromConfig()
    {
        try
        {
            if (_LogDirectory != null && _EnableLogging != null) return;

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
            SafeFallbackLog($"InitializeFromConfig failed: {ex.Message}");
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
            SafeFallbackLog($"LoadFromConfigFile failed: {ex.Message}");
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
            SafeFallbackLog($"SaveToConfigFile failed: {ex.Message}");
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
        catch { /* Absolutely safe */ }
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
        catch { /* Absolutely safe */ }
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
        catch { /* Absolutely safe */ }
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
        catch { /* Absolutely safe */ }
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

            // Non-blocking enqueue with timeout
            if (!_LogQueue.TryAdd(mLogMessage, 50))
            {
                Interlocked.Increment(ref _DroppedLogsCount);
                EmergencyWriteToDisk(mLogMessage);

                // Periodic warning about dropped messages
                var mNow = DateTime.UtcNow;
                if (mNow - _LastDropWarningTime > _DropWarningInterval)
                {
                    _LastDropWarningTime = mNow;
                    int mDropped = _DroppedLogsCount; // خواندن مستقیم از volatile field
                    SafeFallbackLog($"Queue overload! Dropped {mDropped} messages. Consider optimizing log frequency.");
                }

                // Enable high load mode
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
        catch { /* Last line of defense */ }
    }

    private static void SafeFallbackLog(string mMessage)
    {
        try
        {
            string mFallbackPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logger_errors.txt");
            string mFullMessage = $"[LOGGER_ERROR] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} {mMessage}{Environment.NewLine}";

            File.AppendAllText(mFallbackPath, mFullMessage, Encoding.UTF8);
        }
        catch { /* If this fails, give up */ }
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
                    var oStack = new System.Diagnostics.StackTrace(1, false);
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

    private static void BackgroundWriterLoop()
    {
        var mBatch = new StringBuilder(8192);
        var mLastWriteTime = DateTime.UtcNow;
        var mItemsBuffer = new string[100];

        while (!_ShouldStop || !_LogQueue.IsCompleted)
        {
            try
            {
                // Batch processing with timeout
                int mItemsCount = _LogQueue.TryTakeMultiple(mItemsBuffer, 0, mItemsBuffer.Length,
                    _HighLoadMode ? (int)_HighLoadWriteInterval.TotalMilliseconds : (int)_WriteInterval.TotalMilliseconds);

                if (mItemsCount > 0)
                {
                    for (int i = 0; i < mItemsCount; i++)
                    {
                        mBatch.AppendLine(mItemsBuffer[i]);
                        mItemsBuffer[i] = null; // Help GC
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
                        if (WriteBatchToDisk(mBatch.ToString()))
                        {
                            _ConsecutiveWriteErrors = 0;
                            _HighLoadMode = false; // Disable high load mode on success
                        }
                        mBatch.Clear();
                    }
                    mLastWriteTime = DateTime.UtcNow;
                }

                // Memory management
                if (mBatch.Capacity > 65536)
                {
                    mBatch = new StringBuilder(8192);
                }
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

        // Final flush
        try
        {
            if (mBatch.Length > 0)
            {
                WriteBatchToDisk(mBatch.ToString());
            }
        }
        catch (Exception ex)
        {
            SafeFallbackLog($"Final flush failed: {ex.Message}");
        }

        _ShutdownCompleteEvent.Set();
    }

    private static bool WriteBatchToDisk(string mBatchText)
    {
        if (string.IsNullOrEmpty(mBatchText)) return true;

        try
        {
            lock (_InitLock)
            {
                Directory.CreateDirectory(LogDirectory);

                if (File.Exists(LogFilePath))
                {
                    var mFileInfo = new FileInfo(LogFilePath);
                    if (mFileInfo.Length >= _MaxLogSize)
                    {
                        CreateBackup();
                    }
                }

                // High-performance writing with FileStream
                using (var mStream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, 8192, FileOptions.SequentialScan))
                using (var mWriter = new StreamWriter(mStream, Encoding.UTF8))
                {
                    mWriter.Write(mBatchText);
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            SafeFallbackLog($"WriteBatchToDisk failed: {ex.Message}");
            return false;
        }
    }

    private static void CreateBackup()
    {
        try
        {
            string mTimestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            string mBackupName = $"backup_application_log_{mTimestamp}.txt";
            string mBackupPath = Path.Combine(LogDirectory, mBackupName);

            if (File.Exists(mBackupPath))
            {
                mBackupName = $"backup_application_log_{mTimestamp}_{Guid.NewGuid():N}.txt";
                mBackupPath = Path.Combine(LogDirectory, mBackupName);
            }

            File.Move(LogFilePath, mBackupPath);

            // Cleanup old backups
            try
            {
                var oOldBackups = new DirectoryInfo(LogDirectory)
                    .GetFiles("backup_application_log_*.txt")
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .Skip(_MaxBackups);

                foreach (var mFile in oOldBackups)
                {
                    try { mFile.Delete(); } catch { }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            SafeFallbackLog($"CreateBackup failed: {ex.Message}");
        }
    }

    public static void Shutdown()
    {
        if (_IsShuttingDown) return;

        _IsShuttingDown = true;
        try
        {
            _ShouldStop = true;
            _LogQueue.CompleteAdding();

            // Adaptive shutdown timeout based on queue size
            int mWaitTime = Math.Min(30000, Math.Max(5000, _LogQueue.Count * 10));
            if (!_ShutdownCompleteEvent.Wait(TimeSpan.FromMilliseconds(mWaitTime)))
            {
                SafeFallbackLog($"Graceful shutdown timeout after {mWaitTime}ms, {_LogQueue.Count} items remaining");
            }

            // Final status report
            if (_DroppedLogsCount > 0)
            {
                string summary = $"[SHUTDOWN] Dropped logs: {_DroppedLogsCount}, Restart attempts: {_RestartCount}";
                EmergencyWriteToDisk(summary);
            }
        }
        catch (Exception ex)
        {
            SafeFallbackLog($"Shutdown failed: {ex.Message}");
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

                // Cleanup
                if (_WriterTask != null)
                {
                    _ShouldStop = true;
                    _ShutdownCompleteEvent.Wait(1000);
                    _WriterTask = null;
                }

                Thread.Sleep(_RestartDelay);

                // Restart
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
                return oObj.ToString() ?? $"[ToString failed: {serializationEx.Message}]";
            }
            catch
            {
                return $"[SERIALIZE_FAILED: {serializationEx.Message}]";
            }
        }
    }
}
public static class BlockingCollectionExtensions
{
    public static int TryTakeMultiple<T>(this BlockingCollection<T> collection, T[] array, int startIndex, int count, int millisecondsTimeout)
    {
        if (collection == null) throw new ArgumentNullException(nameof(collection));
        if (array == null) throw new ArgumentNullException(nameof(array));

        int mItemsTaken = 0;
        var mStopwatch = System.Diagnostics.Stopwatch.StartNew();
        int mRemainingTimeout = millisecondsTimeout;

        for (int i = 0; i < count; i++)
        {
            T item;
            if (collection.TryTake(out item, mRemainingTimeout))
            {
                array[startIndex + i] = item;
                mItemsTaken++;

                if (millisecondsTimeout != Timeout.Infinite)
                {
                    mRemainingTimeout = millisecondsTimeout - (int)mStopwatch.ElapsedMilliseconds;
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