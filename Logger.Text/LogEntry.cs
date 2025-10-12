using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

public class LogEntry
{
    public string Timestamp { get; set; }
    public string Level { get; set; }
    public string Message { get; set; }
    public string CallerInfo { get; set; }
    public Dictionary<string, object> Properties { get; set; }
}

public interface ILogSink
{
    void Write(IEnumerable<LogEntry> mLogEntries);
    void Shutdown();
}

public class JsonFileSink : ILogSink
{
    private readonly string _logDirectory;
    private readonly string _logFileName;
    private readonly long _maxFileSize;
    private readonly int _maxBackups;
    private readonly object _lock = new object();
    private readonly Newtonsoft.Json.JsonSerializerSettings _JsonSettings = new Newtonsoft.Json.JsonSerializerSettings { DateFormatString = "yyyy-MM-ddTHH:mm:ss.fffZ" };

    public JsonFileSink(string mLogDirectory, string mLogFileName = "application_log.jsonl", long mMaxFileSize = 5 * 1024 * 1024, int mMaxBackups = 10)
    {
        _logDirectory = mLogDirectory;
        _logFileName = mLogFileName;
        _maxFileSize = mMaxFileSize;
        _maxBackups = mMaxBackups;
        if (!Directory.Exists(_logDirectory))
        {
            Directory.CreateDirectory(_logDirectory);
        }
    }
    public void Write(IEnumerable<LogEntry> mLogEntries)
    {
        if (mLogEntries == null) return;
        lock (_lock)
        {
            string mLogFilePath = Path.Combine(_logDirectory, _logFileName);
            if (File.Exists(mLogFilePath) && new FileInfo(mLogFilePath).Length >= _maxFileSize)
            {
                RotateLogFile();
            }
            using (var mWriter = new StreamWriter(mLogFilePath, append: true, Encoding.UTF8))
            {
                foreach (var mEntry in mLogEntries)
                {
                    string mJsonLine = Newtonsoft.Json.JsonConvert.SerializeObject(mEntry, _JsonSettings);
                    mWriter.WriteLine(mJsonLine);
                }
            }
        }
    }
    private void RotateLogFile()
    {
        string mLogFilePath = Path.Combine(_logDirectory, _logFileName);
        string mTimestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        string mBackupPath = Path.Combine(_logDirectory, string.Format("backup_{0}_{1}", mTimestamp, _logFileName));
        File.Move(mLogFilePath, mBackupPath);
        CleanOldBackups();
    }
    private void CleanOldBackups()
    {
        var mBackupFiles = new DirectoryInfo(_logDirectory).GetFiles(string.Format("backup_*_{0}", _logFileName)).OrderByDescending(f => f.CreationTime).Skip(_maxBackups);
        foreach (var mFile in mBackupFiles)
        {
            try { mFile.Delete(); } catch { }
        }
    }
    public void Shutdown()
    {
    }
}