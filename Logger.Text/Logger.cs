using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Collections.Generic;

public static class Logger
{
    private static string _LogDirectory; // مسیر دایرکتوری ذخیره لاگ — از فایل کانفیگ یا پیش‌فرض بارگذاری می‌شود
    private static bool? _EnableLogging; // وضعیت فعال/غیرفعال بودن لاگ — null = هنوز خوانده نشده — از کانفیگ یا پیش‌فرض
    private static readonly object _InitLock = new object(); // قفل برای همگام‌سازی مقداردهی اولیه و عملیات خواندن/نوشتن کانفیگ
    private const string mConfigFileName = "logger_config.xml"; // نام فایل پیکربندی — ذخیره مسیر و وضعیت لاگ
    private static readonly string _ConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, mConfigFileName); // مسیر کامل فایل کانفیگ — کنار exe/dll
    private static readonly string _LogBaseName = "application_log.txt"; // نام فایل لاگ اصلی — مقصد نوشتن لاگ‌ها
    private static readonly long _MaxLogSize = 5L * 1024 * 1024; // حداکثر حجم فایل لاگ قبل از چرخش — 5 مگابایت
    private static readonly int _MaxBackups = 10; // حداکثر تعداد فایل‌های بک‌آپ لاگ — قدیمی‌ترها پاک می‌شوند
    private static readonly ConcurrentQueue<string> _LogQueue = new ConcurrentQueue<string>(); // صف Thread-Safe — نگهداری لاگ‌ها برای نوشتن در پس‌زمینه
    private static Thread _WriterThread; // نخ پس‌زمینه — مسئول نوشتن Batch لاگ‌ها در دیسک
    private static volatile bool _ShouldStop = false; // پرچم volatile — اعلام توقف نخ پس‌زمینه در زمان Shutdown
    private static readonly AutoResetEvent _WriteEvent = new AutoResetEvent(false); // رویداد همگام‌سازی — بیدار کردن نخ نویسنده هنگام افزودن لاگ یا timeout
    // فاصله زمانی بررسی صف در حالت Idle — 50 میلی‌ثانیه
    private static readonly TimeSpan _WriteInterval = TimeSpan.FromMilliseconds(50);
    // کنترل نمایش نام اسمبلی در لاگ — پیش‌فرض false برای
    private static bool _IncludeAssemblyInLog = false;
    private static readonly Newtonsoft.Json.JsonSerializerSettings _JsonSettings = new Newtonsoft.Json.JsonSerializerSettings
    {
        MaxDepth = 5,// حداکثر عمق سریالایز — جلوگیری از Overflow در اشیاء تو در تو
        NullValueHandling = Newtonsoft.Json.NullValueHandling.Include, // نادیده گرفتن مقادیر null — کاهش حجم لاگ
        ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore, // نادیده گرفتن حلقه‌های مرجع — جلوگیری از Exception
        Formatting = Newtonsoft.Json.Formatting.Indented // بدون ایندنت — کاهش حجم و افزایش سرعت سریالایز
    };

    // فیلدهای جدید برای تحمل خطا و self-repair
    private static volatile bool _LoggerOperational = true; // وضعیت عملیاتی لاگر
    private static volatile int _RestartCount = 0; // شمارنده تعداد تلاش‌های بازیابی
    private static readonly int _MaxRestartAttempts = 5; // حداکثر تلاش برای بازیابی
    private static readonly TimeSpan _RestartDelay = TimeSpan.FromSeconds(5); // تأخیر بین تلاش‌های بازیابی
    private static volatile bool _IsShuttingDown = false; // وضعیت خاموش شدن

    // فیلدهای جدید برای مدیریت بار بالا
    private static readonly int _MaxQueueSize = 50000; // حداکثر اندازه صف برای جلوگیری از مصرف حافظه زیاد
    private static volatile int _CurrentQueueSize = 0; // اندازه فعلی صف (برای بررسی سریع)
    private static volatile int _DroppedLogsCount = 0; // تعداد لاگ‌های حذف شده در اثر پر شدن صف
    private static readonly TimeSpan _HighLoadWriteInterval = TimeSpan.FromMilliseconds(10); // فاصله زمانی در بار بالا
    private static readonly int _HighLoadBatchSize = 20; // اندازه دسته در بار بالا
    private static readonly int _NormalBatchSize = 100; // اندازه دسته در حالت عادی
    private static volatile bool _HighLoadMode = false; // حالت بار بالا
    private static volatile int _ConsecutiveWriteErrors = 0; // شمارنده خطاهای متوالی نوشتن
    private static readonly int _MaxConsecutiveWriteErrors = 10; // حداکثر خطاهای متوالی مجاز
    private static readonly object _QueueSizeLock = new object(); // قفل برای به‌روزرسانی اندازه صف
    private static readonly Queue<string> _OverflowBuffer = new Queue<string>(); // بافر برای لاگ‌های حذف شده

    static Logger()
    {
        InitializeLogger();
    }

    private static void InitializeLogger()
    {
        try
        {
            _WriterThread = new Thread(BackgroundWriterLoop)
            {
                IsBackground = true,
                Name = "Logger-Background-Writer"
            };
            _WriterThread.Start();
        }
        catch (Exception ex)
        {
            _LoggerOperational = false;
            Console.WriteLine($"Logger initialization failed: {ex.Message}");
            // برنامه ادامه پیدا می‌کند حتی اگر لاگر کار نکند
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
            catch
            {
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
            catch
            {
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
        catch
        {
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
        catch
        {
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
        catch
        {
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

            oDoc.Save(_ConfigFilePath);
        }
        catch { }
    }

    public static void LogInfo(string mMessage, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] INFO: {mMessage}{mCallerInfo}";
            EnqueueLog(mLogMessage);
        }
        catch { }
    }

    public static void LogInfo(object oData, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] INFO: {SerializeObject(oData)}{mCallerInfo}";
            EnqueueLog(mLogMessage);
        }
        catch { }
    }

    public static void LogStart(object oData, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] START: {SerializeObject(oData)}{mCallerInfo}";
            EnqueueLog(mLogMessage);
        }
        catch { }
    }

    public static void LogEnd(object oData, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] END: {SerializeObject(oData)}{mCallerInfo}";
            EnqueueLog(mLogMessage);
        }
        catch { }
    }

    public static void LogRequest(object oRequest, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] START REQUEST: {SerializeObject(oRequest)}{mCallerInfo}";
            EnqueueLog(mLogMessage);
        }
        catch { }
    }

    public static void LogResponse(object oResponse, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] RESPONSE: {SerializeObject(oResponse)}{mCallerInfo}";
            EnqueueLog(mLogMessage);
        }
        catch { }
    }

    public static void LogError(object oException, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational || oException == null) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] ERROR: {SerializeObject(oException)}{mCallerInfo}";
            EnqueueLog(mLogMessage);
        }
        catch { }
    }

    public static void LogError(string mMessage, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || !_LoggerOperational) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] ERROR: {mMessage}{mCallerInfo}";
            EnqueueLog(mLogMessage);
        }
        catch { }
    }

    private static void EnqueueLog(string mLogMessage)
    {
        try
        {
            if (!_LoggerOperational) return;

            // بررسی وضعیت نخ نویسنده و بازیابی در صورت نیاز
            if (_WriterThread == null || !_WriterThread.IsAlive)
            {
                TryRestartLogger();
                if (!_LoggerOperational) return;
            }

            // بررسی اندازه صف و مدیریت بار بالا
            lock (_QueueSizeLock)
            {
                if (_CurrentQueueSize >= _MaxQueueSize)
                {
                    // در حالت بار بالا، لاگ‌های قدیمی‌تر را حذف می‌کنیم
                    if (_LogQueue.TryDequeue(out string _))
                    {
                        _CurrentQueueSize--;
                        _DroppedLogsCount++;

                        // ذخیره لاگ‌های حذف شده در بافر برای گزارش‌دهی بعدی
                        lock (_OverflowBuffer)
                        {
                            _OverflowBuffer.Enqueue(mLogMessage);
                            if (_OverflowBuffer.Count > 100)
                                _OverflowBuffer.Dequeue();
                        }
                    }

                    // فعال کردن حالت بار بالا
                    _HighLoadMode = true;
                }
                else
                {
                    _LogQueue.Enqueue(mLogMessage);
                    _CurrentQueueSize++;

                    // اگر صف از حد مشخصی پر شده، حالت بار بالا را فعال کن
                    if (_CurrentQueueSize > _MaxQueueSize * 0.7)
                        _HighLoadMode = true;
                    else if (_CurrentQueueSize < _MaxQueueSize * 0.3)
                        _HighLoadMode = false;
                }
            }

            // بیدار کردن نخ نویسنده
            _WriteEvent.Set();
        }
        catch
        {
            _LoggerOperational = false;
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
                    var oStack = new System.Diagnostics.StackTrace(1, false);
                    var oFrame = oStack.GetFrame(0);
                    var oMethod = oFrame.GetMethod();
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
            return "";
        }
    }

    private static void BackgroundWriterLoop()
    {
        while (!_IsShuttingDown)
        {
            try
            {
                // انتخاب فاصله زمانی بر اساس بار سیستم
                TimeSpan currentInterval = _HighLoadMode ? _HighLoadWriteInterval : _WriteInterval;
                _WriteEvent.WaitOne(currentInterval);

                if (_LogQueue.IsEmpty) continue;

                int batchSize = _HighLoadMode ? _HighLoadBatchSize : _NormalBatchSize;
                var mBatch = new StringBuilder();
                string mItem;
                int mCount = 0;
                int processedCount = 0;

                while (_LogQueue.TryDequeue(out mItem) && mCount < batchSize)
                {
                    if (mItem != null)
                    {
                        mBatch.AppendLine(mItem);
                        processedCount++;
                    }
                    mCount++;
                }

                if (mBatch.Length == 0) continue;

                // به‌روزرسانی اندازه صف
                lock (_QueueSizeLock)
                {
                    _CurrentQueueSize -= processedCount;
                    if (_CurrentQueueSize < 0) _CurrentQueueSize = 0;
                }

                WriteBatchToDisk(mBatch.ToString());

                // اگر با موفقیت نوشت، شمارنده خطا را صفر کن
                _ConsecutiveWriteErrors = 0;
            }
            catch (Exception ex)
            {
                // ثبت خطای داخلی لاگر
                Console.WriteLine($"Logger internal error: {ex.Message}");

                // افزایش شمارنده خطاهای متوالی
                _ConsecutiveWriteErrors++;

                // اگر خطاهای متوالی بیش از حد مجاز بود، لاگر را غیرفعال کن
                if (_ConsecutiveWriteErrors >= _MaxConsecutiveWriteErrors)
                {
                    Console.WriteLine($"Maximum consecutive write errors ({_MaxConsecutiveWriteErrors}) reached. Disabling logger.");
                    _LoggerOperational = false;
                    return;
                }

                // تلاش برای بازیابی خودکار
                TryRestartLogger();

                // تأخیر برای جلوگیری از مصرف CPU در صورت خطای مداوم
                Thread.Sleep(1000);
            }
        }
        FlushRemainingLogs();
    }

    private static void WriteBatchToDisk(string mBatchText)
    {
        if (string.IsNullOrEmpty(mBatchText) || !_LoggerOperational) return;

        try
        {
            lock (_InitLock)
            {
                try
                {
                    Directory.CreateDirectory(LogDirectory);
                }
                catch { }

                try
                {
                    if (File.Exists(LogFilePath))
                    {
                        try
                        {
                            long mCurrentSize = new FileInfo(LogFilePath).Length;
                            if (mCurrentSize >= _MaxLogSize)
                                CreateBackup();
                        }
                        catch { }
                    }

                    File.AppendAllText(LogFilePath, mBatchText, Encoding.UTF8);
                }
                catch { }
            }
        }
        catch
        {
            throw; // اجازه می‌دهیم خطا به سطح بالاتر برود تا مدیریت شود
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
                mBackupName = $"backup_application_log_{mTimestamp}_{Path.GetRandomFileName()}.txt";
                mBackupPath = Path.Combine(LogDirectory, mBackupName);
            }

            try
            {
                File.Move(LogFilePath, mBackupPath);
            }
            catch { }

            try
            {
                var oOldBackups = new DirectoryInfo(LogDirectory)
                    .GetFiles("backup_application_log_*.txt")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(_MaxBackups);

                foreach (var mFile in oOldBackups)
                {
                    try { mFile.Delete(); } catch { }
                }
            }
            catch { }
        }
        catch { }
    }

    private static void FlushRemainingLogs()
    {
        try
        {
            var mBatch = new StringBuilder();
            string mItem;
            int processedCount = 0;

            while (_LogQueue.TryDequeue(out mItem))
            {
                if (mItem != null)
                {
                    mBatch.AppendLine(mItem);
                    processedCount++;
                }
            }

            // به‌روزرسانی اندازه صف
            lock (_QueueSizeLock)
            {
                _CurrentQueueSize -= processedCount;
                if (_CurrentQueueSize < 0) _CurrentQueueSize = 0;
            }

            if (mBatch.Length > 0)
            {
                WriteBatchToDisk(mBatch.ToString());
            }

            // گزارش وضعیت نهایی
            if (_DroppedLogsCount > 0)
            {
                string summary = $"[Logger Shutdown Summary] Dropped logs: {_DroppedLogsCount}, Final queue size: {_CurrentQueueSize}";
                File.AppendAllText(LogFilePath, summary + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }

    public static void Shutdown()
    {
        if (_IsShuttingDown) return;

        _IsShuttingDown = true;
        try
        {
            _WriteEvent.Set();
            if (_WriterThread != null && !_WriterThread.Join(5000))
            {
                Console.WriteLine("Logger writer thread did not shut down gracefully");
            }
        }
        catch { }
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
                Console.WriteLine($"Attempting to restart logger (attempt {_RestartCount}/{_MaxRestartAttempts})");

                // توقف نخ قبلی
                if (_WriterThread != null && _WriterThread.IsAlive)
                {
                    _ShouldStop = true;
                    _WriteEvent.Set();
                    _WriterThread.Join(1000);
                }

                // تأخیر قبل از راه‌اندازی مجدد
                Thread.Sleep(_RestartDelay);

                // راه‌اندازی مجدد
                _ShouldStop = false;
                _WriterThread = new Thread(BackgroundWriterLoop)
                {
                    IsBackground = true,
                    Name = "Logger-Background-Writer-Restarted"
                };
                _WriterThread.Start();

                _LoggerOperational = true;
                _ConsecutiveWriteErrors = 0; // بازنشانی شمارنده خطاها
                Console.WriteLine("Logger successfully restarted");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Logger restart failed: {ex.Message}");
                _LoggerOperational = false;

                // اگر به حداکثر تلاش رسیده باشیم، لاگر را غیرفعال می‌کنیم
                if (_RestartCount >= _MaxRestartAttempts)
                {
                    Console.WriteLine("Maximum restart attempts reached. Logger disabled.");
                }
            }
        }
    }

    private static string SerializeObject(object oObj)
    {
        if (oObj == null) return "NULL";
        try
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(oObj, _JsonSettings);
        }
        catch
        {
            try
            {
                return oObj.ToString();
            }
            catch
            {
                return "[SERIALIZE_FAILED]";
            }
        }
    }
}
