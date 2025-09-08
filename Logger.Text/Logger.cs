using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

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
    private static readonly Thread _WriterThread; // نخ پس‌زمینه — مسئول نوشتن Batch لاگ‌ها در دیسک
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
    static Logger()
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
        catch { }
    }

    private static string LogDirectory
    {
        get
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
    }

    private static bool EnableLogging
    {
        get
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
    }

    private static string LogFilePath => Path.Combine(LogDirectory, _LogBaseName);

    public static void Initialize(string mLogDirectory = null, bool? mEnableLogging = null, bool? mIncludeAssemblyInLog = null)
    {
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
        catch { }
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
            if (!EnableLogging) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] INFO: {mMessage}{mCallerInfo}";
            _LogQueue.Enqueue(mLogMessage);
            _WriteEvent.Set();
        }
        catch { }
    }

    public static void LogInfo(object oData, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] INFO: {SerializeObject(oData)}{mCallerInfo}";
            _LogQueue.Enqueue(mLogMessage);
            _WriteEvent.Set();
        }
        catch { }
    }

    public static void LogStart(object oData, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] START: {SerializeObject(oData)}{mCallerInfo}";
            _LogQueue.Enqueue(mLogMessage);
            _WriteEvent.Set();
        }
        catch { }
    }

    public static void LogEnd(object oData, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] END: {SerializeObject(oData)}{mCallerInfo}";
            _LogQueue.Enqueue(mLogMessage);
            _WriteEvent.Set();
        }
        catch { }
    }

    public static void LogRequest(object oRequest, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] START REQUEST: {SerializeObject(oRequest)}{mCallerInfo}";
            _LogQueue.Enqueue(mLogMessage);
            _WriteEvent.Set();
        }
        catch { }
    }

    public static void LogResponse(object oResponse, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] RESPONSE: {SerializeObject(oResponse)}{mCallerInfo}";
            _LogQueue.Enqueue(mLogMessage);
            _WriteEvent.Set();
        }
        catch { }
    }

    public static void LogError(object oException, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging || oException == null) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] ERROR: {SerializeObject(oException)}{mCallerInfo}";
            _LogQueue.Enqueue(mLogMessage);
            _WriteEvent.Set();
        }
        catch
        {
        }
    }

    public static void LogError(string mMessage, [CallerMemberName] string mMemberName = "", [CallerFilePath] string mFilePath = "", [CallerLineNumber] int mLineNumber = 0)
    {
        try
        {
            if (!EnableLogging) return;
            string mCallerInfo = BuildCallerInfo(mMemberName, mFilePath, mLineNumber);
            string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] ERROR: {mMessage}{mCallerInfo}";
            _LogQueue.Enqueue(mLogMessage);
            _WriteEvent.Set();
        }
        catch
        {
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
        while (!_ShouldStop)
        {
            try
            {
                _WriteEvent.WaitOne(_WriteInterval);
                if (_LogQueue.IsEmpty) continue;

                var mBatch = new StringBuilder();
                string mItem;
                int mCount = 0;

                while (_LogQueue.TryDequeue(out mItem) && mCount < 100)
                {
                    if (mItem != null) mBatch.AppendLine(mItem);
                    mCount++;
                }

                if (mBatch.Length == 0) continue;
                WriteBatchToDisk(mBatch.ToString());
            }
            catch { }
        }
        FlushRemainingLogs();
    }

    private static void WriteBatchToDisk(string mBatchText)
    {
        if (string.IsNullOrEmpty(mBatchText)) return;

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
        catch { }
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
            while (_LogQueue.TryDequeue(out mItem))
            {
                if (mItem != null) mBatch.AppendLine(mItem);
            }
            if (mBatch.Length > 0)
            {
                WriteBatchToDisk(mBatch.ToString());
            }
        }
        catch { }
    }

    public static void Shutdown()
    {
        try
        {
            _ShouldStop = true;
            _WriteEvent.Set();
            if (!_WriterThread.Join(2000))
            {
                try { _WriterThread.Abort(); } catch { }
            }
        }
        catch { }
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