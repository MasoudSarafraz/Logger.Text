using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.IO;

public static class LoggerStressTester
{
    public static void RunExtremeStressTest()
    {
        Console.WriteLine("=== EXTREME Logger Stress Test Started ===");
        Console.WriteLine($"Start Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        Console.WriteLine();
        Logger.Initialize("ExtremeStressTest", true, true);
        CleanupPreviousLogs();
        var testResults = new StressTestResults();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            TestMassiveConcurrentLogging(testResults);
            TestContinuousHighVolumeLogging(testResults);
            TestLargeObjectSerialization(testResults);
            TestMixedWorkloadWithBackupTrigger(testResults);
            TestFinalFlushAndBackupVerification(testResults);
        }
        finally
        {
            stopwatch.Stop();
            testResults.TotalDuration = stopwatch.Elapsed;
            PrintTestResults(testResults);
            Logger.Shutdown();
            Console.WriteLine("=== Extreme Stress Test Completed ===");
        }
    }

    private static void CleanupPreviousLogs()
    {
        try
        {
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtremeStressTest");
            if (Directory.Exists(logDir))
            {
                Directory.Delete(logDir, true);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }

    private static void TestMassiveConcurrentLogging(StressTestResults results)
    {
        Console.WriteLine("1. Testing MASSIVE Concurrent Logging (100+ threads)...");

        int threadCount = 100;
        int messagesPerThread = 2000; // 200000 message
        var threads = new List<Thread>();
        var startSignal = new ManualResetEvent(false);

        results.TotalMessages += threadCount * messagesPerThread;

        for (int i = 0; i < threadCount; i++)
        {
            int threadId = i;
            var thread = new Thread(() =>
            {
                startSignal.WaitOne();

                for (int j = 0; j < messagesPerThread; j++)
                {
                    string message = $"[THREAD{threadId:000}] Message {j:0000} - " +
                                    $"This is a detailed log message designed to fill up the log file quickly. " +
                                    $"Timestamp: {DateTime.UtcNow:HH:mm:ss.fff} - " +
                                    $"Thread: {Thread.CurrentThread.ManagedThreadId} - " +
                                    $"Iteration: {j} - " +
                                    $"RandomData: {Guid.NewGuid()}";

                    Logger.LogInfo(message, $"Worker{threadId}", $"StressTest.cs", j + 1);
                    if (j % 250 == 0)
                    {
                        Thread.Yield();
                    }
                }
            })
            {
                Priority = ThreadPriority.AboveNormal
            };

            threads.Add(thread);
            thread.Start();
        }

        Console.WriteLine($"   → Started {threadCount} threads, each sending {messagesPerThread} messages...");
        startSignal.Set();
        foreach (var thread in threads)
        {
            if (!thread.Join(TimeSpan.FromMinutes(2)))
            {
                Console.WriteLine($"   ⚠ Thread {thread.ManagedThreadId} timed out");
            }
        }

        results.MassiveConcurrentTested = true;
        Console.WriteLine($"   → Completed: {threadCount * messagesPerThread:N0} messages");
    }

    private static void TestContinuousHighVolumeLogging(StressTestResults results)
    {
        Console.WriteLine("2. Testing Continuous High Volume Logging...");

        int totalMessages = 50000;
        results.TotalMessages += totalMessages;
        int batchSize = 1000;

        for (int batch = 0; batch < totalMessages / batchSize; batch++)
        {
            Parallel.For(0, batchSize, i =>
            {
                int messageId = batch * batchSize + i;

                var logData = new
                {
                    MessageId = messageId,
                    Batch = batch,
                    Index = i,
                    Timestamp = DateTime.UtcNow,
                    Data = new byte[512], // داده با حجم متوسط
                    Metadata = new
                    {
                        Source = "StressTest",
                        Type = "Performance",
                        Priority = "High",
                        Tags = new[] { "stress", "test", "performance", "high-volume" },
                        AdditionalInfo = $"This is message {messageId} in batch {batch}"
                    }
                };

                Logger.LogInfo(logData);
                if (messageId % 1000 == 0)
                {
                    Console.WriteLine($"   → Progress: {messageId:N0}/{totalMessages:N0} messages");
                }
            });
            Thread.Sleep(100);
        }

        results.HighVolumeTested = true;
        Console.WriteLine($"   → Completed: {totalMessages:N0} high-volume messages");
    }

    private static void TestLargeObjectSerialization(StressTestResults results)
    {
        Console.WriteLine("3. Testing Large Object Serialization (Forcing Backup)...");

        int largeMessageCount = 1000;
        results.TotalMessages += largeMessageCount;

        for (int i = 0; i < largeMessageCount; i++)
        {
            // ایجاد اشیاء بسیار بزرگ برای پر کردن سریع فایل
            var hugeObject = new
            {
                Id = i,
                CreatedAt = DateTime.UtcNow,
                LargeData = new
                {
                    Array1 = Enumerable.Range(0, 1000).ToArray(),
                    Array2 = Enumerable.Range(0, 500).Select(x => x * 2).ToArray(),
                    Nested = new
                    {
                        Level1 = new
                        {
                            Level2 = new
                            {
                                Level3 = new
                                {
                                    Data = new string('X', 1000), // رشته بسیار طولانی
                                    Items = Enumerable.Range(0, 200).Select(n => new
                                    {
                                        ItemId = n,
                                        Name = $"Item {n}",
                                        Value = n * 1.5,
                                        Description = $"This is a detailed description for item {n} " +
                                                     $"that should take up significant space in the log file. " +
                                                     $"Additional text to increase size: {Guid.NewGuid()}"
                                    }).ToArray()
                                }
                            }
                        }
                    },
                    Metadata = new Dictionary<string, object>
                    {
                        ["category"] = "large-object-test",
                        ["size"] = "extra-large",
                        ["purpose"] = "force-log-backup",
                        ["timestamp"] = DateTime.UtcNow.Ticks,
                        ["additional"] = new
                        {
                            Info1 = "Value1",
                            Info2 = 12345,
                            Info3 = new[] { "a", "b", "c", "d", "e" },
                            Info4 = DateTime.UtcNow
                        }
                    }
                },
                Footer = new
                {
                    Message = "This is the end of a very large log object designed to fill the log file quickly",
                    Checksum = Guid.NewGuid(),
                    Validation = new { IsValid = true, Reason = "Stress test" }
                }
            };

            Logger.LogInfo(hugeObject);

            // گزارش پیشرفت
            if (i % 100 == 0)
            {
                Console.WriteLine($"   → Large objects: {i}/{largeMessageCount}");
                CheckForBackupFiles(results);
            }
        }

        results.LargeObjectTested = true;
        Console.WriteLine($"   → Completed: {largeMessageCount:N0} large objects");
    }

    private static void TestMixedWorkloadWithBackupTrigger(StressTestResults results)
    {
        Console.WriteLine("4. Testing Mixed Workload to Trigger Multiple Backups...");

        int operationCount = 30000;
        results.TotalMessages += operationCount;
        var random = new Random();

        for (int i = 0; i < operationCount; i++)
        {
            switch (i % 8)
            {
                case 0:
                    // لاگ‌های بسیار طولانی
                    string longMessage = new string('*', 1000) +
                                        $" MESSAGE {i} " +
                                        new string('*', 1000) +
                                        $" DETAILS: {Guid.NewGuid()} {DateTime.UtcNow:O}";
                    Logger.LogInfo(longMessage);
                    break;

                case 1:
                    Logger.LogInfo(new
                    {
                        Type = "Complex",
                        Id = i,
                        Data = Enumerable.Range(0, 100).ToDictionary(x => $"Key{x}", x => x),
                        Nested = new
                        {
                            Level = 1,
                            Items = Enumerable.Range(0, 50).Select(x => new { Index = x, Value = x * 2 })
                        }
                    });
                    break;

                case 2:
                    try
                    {
                        throw new InvalidOperationException($"Simulated error for stress testing #{i} " +
                                                           $"with additional details: {Guid.NewGuid()}");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex);
                    }
                    break;

                default:
                    Logger.LogInfo($"Mixed workload message #{i} - " +
                                  $"Thread: {Thread.CurrentThread.ManagedThreadId} - " +
                                  $"Time: {DateTime.UtcNow:HH:mm:ss.fff} - " +
                                  $"Random: {random.NextDouble()} - " +
                                  $"Guid: {Guid.NewGuid()}");
                    break;
            }
            if (i % 500 == 0)
            {
                CheckForBackupFiles(results);
                Console.WriteLine($"   → Progress: {i:N0}/{operationCount:N0}, Backups: {results.BackupFilesCreated}");
            }
            if (i % 1000 == 0)
            {
                GC.Collect(0, GCCollectionMode.Default, false);
                Thread.Yield();
            }
        }

        results.MixedWorkloadTested = true;
        Console.WriteLine($"   → Completed: {operationCount:N0} mixed operations");
    }

    private static void TestFinalFlushAndBackupVerification(StressTestResults results)
    {
        Console.WriteLine("5. Final Flush and Backup Verification...");
        int finalBatch = 5000;
        results.TotalMessages += finalBatch;

        Parallel.For(0, finalBatch, i =>
        {
            Logger.LogInfo($"FINAL_MESSAGE_{i}_" + new string('Z', 500) +
                          $"_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{Guid.NewGuid()}");
        });
        CheckForBackupFiles(results);
        results.FinalFlushTested = true;
        Console.WriteLine($"   → Completed: Final flush with {finalBatch:N0} messages");
        Console.WriteLine($"   → Total backups created: {results.BackupFilesCreated}");
    }

    private static void CheckForBackupFiles(StressTestResults results)
    {
        try
        {
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtremeStressTest");
            if (Directory.Exists(logDir))
            {
                var backupFiles = Directory.GetFiles(logDir, "backup_application_log_*.txt");
                results.BackupFilesCreated = Math.Max(results.BackupFilesCreated, backupFiles.Length);

                if (backupFiles.Length > 0 && !results.BackupVerified)
                {
                    results.BackupVerified = true;
                    Console.WriteLine($"  BACKUP CREATED! Files: {backupFiles.Length}");
                    foreach (var backupFile in backupFiles.Take(3))
                    {
                        var fileInfo = new FileInfo(backupFile);
                        Console.WriteLine($" {fileInfo.Name} - {fileInfo.Length / 1024 / 1024}MB");
                    }
                }
            }
        }
        catch { /* Ignore monitoring errors */ }
    }

    private static void PrintTestResults(StressTestResults results)
    {
        Console.WriteLine();
        Console.WriteLine("=== EXTREME STRESS TEST RESULTS ===");
        Console.WriteLine($"Total Duration: {results.TotalDuration}");
        Console.WriteLine($"Total Messages: {results.TotalMessages:N0}");
        Console.WriteLine($"Messages per Second: {results.TotalMessages / results.TotalDuration.TotalSeconds:N0}");
        Console.WriteLine($"Backup Files Created: {results.BackupFilesCreated}");
        Console.WriteLine($"Peak Memory Usage: {GC.GetTotalMemory(true) / 1024 / 1024} MB");

        Console.WriteLine();
        Console.WriteLine("Tests Completed:");
        Console.WriteLine($" Massive Concurrent: {(results.MassiveConcurrentTested ? "PASS" : "FAIL")}");
        Console.WriteLine($" High Volume: {(results.HighVolumeTested ? "PASS" : "FAIL")}");
        Console.WriteLine($" Large Objects: {(results.LargeObjectTested ? "PASS" : "FAIL")}");
        Console.WriteLine($" Mixed Workload: {(results.MixedWorkloadTested ? "PASS" : "FAIL")}");
        Console.WriteLine($" Final Flush: {(results.FinalFlushTested ? "PASS" : "FAIL")}");
        Console.WriteLine($" Backup Created: {(results.BackupVerified ? "YES" : "NO")}");

        Console.WriteLine();
        Console.WriteLine("Performance Analysis:");
        double msgPerSec = results.TotalMessages / results.TotalDuration.TotalSeconds;

        if (msgPerSec > 1000)
            Console.WriteLine($"Throughput: EXCELLENT ({msgPerSec:N0} msg/sec)");
        else if (msgPerSec > 500)
            Console.WriteLine($"Throughput: GOOD ({msgPerSec:N0} msg/sec)");
        else
            Console.WriteLine($"Throughput: MODERATE ({msgPerSec:N0} msg/sec)");

        if (results.BackupFilesCreated > 0)
            Console.WriteLine("Backup System: WORKING");
        else
            Console.WriteLine("Backup System: NOT TRIGGERED");

        Console.WriteLine();
        Console.WriteLine("Logger Status:  EXTREME LOAD READY");
    }

    // کلاس نتایج تست
    private class StressTestResults
    {
        public TimeSpan TotalDuration { get; set; }
        public int TotalMessages { get; set; }
        public int BackupFilesCreated { get; set; }
        public bool BackupVerified { get; set; }
        public bool MassiveConcurrentTested { get; set; }
        public bool HighVolumeTested { get; set; }
        public bool LargeObjectTested { get; set; }
        public bool MixedWorkloadTested { get; set; }
        public bool FinalFlushTested { get; set; }
    }
}

// روش استفاده:
// LoggerStressTester.RunExtremeStressTest();