using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;

public static class Logger
{
    // --- Config & Init ---
    private static string _LogDirectory;
    private static bool? _EnableLogging;
    private static readonly object _InitLock = new object();
    private const string mConfigFileName = "logger_config.xml";
    private static readonly string _ConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, mConfigFileName);
    private static readonly string _LogBaseName = "application_log.txt";
    private static readonly long _MaxLogSize = 3L * 1024 * 1024; // 3 MB
    private static readonly int _MaxBackups = 10;

    // --- Performance Optimized Fields ---
    private static readonly ConcurrentQueue<string> _LogQueue = new ConcurrentQueue<string>();
    private static readonly Thread _WriterThread;
    private static volatile bool _ShouldStop = false;
    private static readonly AutoResetEvent _WriteEvent = new AutoResetEvent(false);
    private static readonly TimeSpan _WriteInterval = TimeSpan.FromMilliseconds(50); // هر 50ms بنویس
                                                                                     // --- Serialize Optimized (با کش کردن JsonSerializer) ---
    private static readonly Newtonsoft.Json.JsonSerializerSettings _JsonSettings = new Newtonsoft.Json.JsonSerializerSettings
    {
        MaxDepth = 5,
        NullValueHandling = Newtonsoft.Json.NullValueHandling.Include,
        //ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
        Formatting = Newtonsoft.Json.Formatting.Indented // برای سرعت بیشتر — Indented نباشد
    };


    // --- Static Constructor (برای شروع نخ پس‌زمینه) ---
    static Logger()
    {
        _WriterThread = new Thread(BackgroundWriterLoop)
        {
            IsBackground = true,
            Name = "Logger-Background-Writer"
        };
        _WriterThread.Start();
    }

    // --- Properties (با Double-Check Locking) ---
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
            return _LogDirectory;
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
            return _EnableLogging.Value;
        }
    }

    private static string LogFilePath
    {
        get { return Path.Combine(LogDirectory, _LogBaseName); }
    }

    // --- Initialize ---
    public static void Initialize(string mLogDirectory = null, bool? mEnableLogging = null)
    {
        lock (_InitLock)
        {
            if (mLogDirectory != null) _LogDirectory = mLogDirectory;
            if (mEnableLogging != null) _EnableLogging = mEnableLogging;
            if (_LogDirectory != null && _EnableLogging != null)
                SaveToConfigFile();
        }
    }

    // --- Config Helpers ---
    private static void InitializeFromConfig()
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
            SaveToConfigFile();
        }
    }

    private static void LoadFromConfigFile()
    {
        try
        {
            XmlDocument oDoc = new XmlDocument();
            oDoc.Load(_ConfigFilePath);

            XmlNode oRoot = oDoc.DocumentElement;
            if (oRoot == null) throw new Exception("Root element is null.");

            XmlNode oDirNode = oRoot.SelectSingleNode("LogDirectory");
            XmlNode oEnableNode = oRoot.SelectSingleNode("EnableLogging");

            if (oDirNode != null && !string.IsNullOrWhiteSpace(oDirNode.InnerText))
                _LogDirectory = oDirNode.InnerText;

            if (oEnableNode != null && bool.TryParse(oEnableNode.InnerText, out bool mEnabled))
                _EnableLogging = mEnabled;

            _LogDirectory = _LogDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            _EnableLogging = _EnableLogging ?? true;
        }
        catch
        {
            _LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            _EnableLogging = true;
            SaveToConfigFile();
        }
    }

    private static void SaveToConfigFile()
    {
        try
        {
            XmlDocument oDoc = new XmlDocument();
            XmlDeclaration oDecl = oDoc.CreateXmlDeclaration("1.0", "utf-8", null);
            oDoc.AppendChild(oDecl);

            XmlElement oRoot = oDoc.CreateElement("LoggerConfig");
            oDoc.AppendChild(oRoot);

            XmlElement oDirElem = oDoc.CreateElement("LogDirectory");
            oDirElem.InnerText = _LogDirectory;
            oRoot.AppendChild(oDirElem);

            XmlElement oEnableElem = oDoc.CreateElement("EnableLogging");
            oEnableElem.InnerText = _EnableLogging.ToString();
            oRoot.AppendChild(oEnableElem);

            oDoc.Save(_ConfigFilePath);
        }
        catch { }
    }

    // --- Logging API (سریع — فقط Enqueue می‌کند) ---
    public static void LogRequest(object oRequest)
    {
        if (!EnableLogging) return;
        string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] START REQUEST: {SerializeObject(oRequest)}";
        _LogQueue.Enqueue(mLogMessage);
        _WriteEvent.Set(); // به نخ پس‌زمینه بگو بیدار شه
    }

    public static void LogResponse(object oResponse)
    {
        if (!EnableLogging) return;
        string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] RESPONSE: {SerializeObject(oResponse)}";
        _LogQueue.Enqueue(mLogMessage);
        _WriteEvent.Set();
    }

    public static void LogError(object oException)
    {
        if (!EnableLogging || oException == null) return;
        string mLogMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] ERROR: {SerializeObject(oException)}";
        _LogQueue.Enqueue(mLogMessage);
        _WriteEvent.Set();
    }

    // --- Background Writer Thread ---
    private static void BackgroundWriterLoop()
    {
        while (!_ShouldStop)
        {
            try
            {
                // منتظر بمان تا رویداد فعال شود یا timeout شود
                _WriteEvent.WaitOne(_WriteInterval);

                if (_LogQueue.IsEmpty) continue;

                // Batch کردن: چند لاگ را با هم بگیر
                var mBatch = new StringBuilder();
                string mItem;
                int mCount = 0;

                while (_LogQueue.TryDequeue(out mItem) && mCount < 100) // حداکثر 100 تا در هر batch
                {
                    mBatch.AppendLine(mItem);
                    mCount++;
                }

                if (mBatch.Length == 0) continue;

                // نوشتن با قفل فقط برای I/O و Rotate
                WriteBatchToDisk(mBatch.ToString());
            }
            catch { /* ignore */ }
        }

        // قبل از خروج، باقی‌مانده را بنویس
        FlushRemainingLogs();
    }

    private static void WriteBatchToDisk(string mBatchText)
    {
        // فقط برای دسترسی به فایل و Rotate — قفل می‌گیریم
        lock (_InitLock) // از _InitLock استفاده می‌کنیم چون هم برای Config و هم برای I/O مناسب است
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);

                if (File.Exists(LogFilePath))
                {
                    long mCurrentSize = new FileInfo(LogFilePath).Length;
                    if (mCurrentSize >= _MaxLogSize)
                        CreateBackup();
                }

                File.AppendAllText(LogFilePath, mBatchText, Encoding.UTF8);
            }
            catch { }
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

            File.Move(LogFilePath, mBackupPath);

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

    // --- Flush Remaining Logs on Stop (اختیاری — برای تمیز خروج) ---
    private static void FlushRemainingLogs()
    {
        var mBatch = new StringBuilder();
        string mItem;
        while (_LogQueue.TryDequeue(out mItem))
        {
            mBatch.AppendLine(mItem);
        }
        if (mBatch.Length > 0)
        {
            WriteBatchToDisk(mBatch.ToString());
        }
    }

    // --- Dispose Pattern (اختیاری — برای توقف نخ و فلاش کردن) ---
    public static void Shutdown()
    {
        _ShouldStop = true;
        _WriteEvent.Set(); // آخرین بار بیدارش کن
        if (!_WriterThread.Join(2000)) // 2 ثانیه صبر کن
        {
            _WriterThread.Abort(); // fallback (در .NET 4.0)
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
            return oObj.ToString();
        }
    }
}