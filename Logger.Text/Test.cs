using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.IO;


public static class LoggerTest
{
    /// <summary>
    /// شبیه‌سازی محیط چندنخی با بار بسیار زیاد برای تست عملکرد لاگر
    /// </summary>
    /// <param name="threadCount">تعداد نخ‌ها (پیش‌فرض: 20)</param>
    /// <param name="messagesPerThread">تعداد پیام‌ها در هر نخ (پیش‌فرض: 5000)</param>
    /// <param name="testDurationMs">مدت زمان تست به میلی‌ثانیه (پیش‌فرض: 30000)</param>
    /// <param name="useComplexObjects">استفاده از آبجکت‌های پیچیده برای لاگ (پیش‌فرض: true)</param>
    /// <param name="tempLogDir">مسیر موقت برای لاگ‌ها (اختیاری)</param>
    public static void SimulateHighLoad(
        int threadCount = 20,
        int messagesPerThread = 5000,
        int testDurationMs = 30000,
        bool useComplexObjects = true,
        string tempLogDir = null)
    {
        Console.WriteLine($"=== شروع تست بار سنگین لاگر ===");
        Console.WriteLine($"تعداد نخ‌ها: {threadCount}");
        Console.WriteLine($"پیام‌ها در هر نخ: {messagesPerThread}");
        Console.WriteLine($"مدت زمان تست: {testDurationMs} میلی‌ثانیه");
        Console.WriteLine($"استفاده از آبجکت‌های پیچیده: {useComplexObjects}");
        Console.WriteLine();

        // Initialize logger with temp directory if provided
        if (!string.IsNullOrEmpty(tempLogDir))
        {
            

            Logger.Initialize(tempLogDir, true);
        }
        else
        {
            Logger.Initialize();
        }

        // Capture initial metrics
        var initialMetrics = Logger.GetMetrics();
        var stopwatch = Stopwatch.StartNew();
        var cts = new CancellationTokenSource();
        var tasks = new List<Task>();
        var threadStats = new ThreadStats[threadCount];
        var readyEvent = new CountdownEvent(threadCount);

        // Create and start tasks
        for (int i = 0; i < threadCount; i++)
        {
            int threadId = i;
            threadStats[threadId] = new ThreadStats();

            tasks.Add(Task.Run(() =>
            {
                try
                {
                    // Signal that the thread is ready
                    readyEvent.Signal();
                    // Wait for all threads to be ready
                    readyEvent.Wait();

                    var threadStopwatch = Stopwatch.StartNew();
                    var random = new Random(threadId);

                    for (int j = 0; j < messagesPerThread; j++)
                    {
                        if (cts.Token.IsCancellationRequested || stopwatch.ElapsedMilliseconds > testDurationMs)
                            break;

                        // Alternate between different log methods
                        int logType = j % 5;
                        string message = $"Thread {threadId} - Message {j}";

                        switch (logType)
                        {
                            case 0:
                                Logger.LogInfo(message);
                                threadStats[threadId].InfoCount++;
                                break;
                            case 1:
                                Logger.LogError(new Exception($"Test error {j} from thread {threadId}"));
                                threadStats[threadId].ErrorCount++;
                                break;
                            case 2:
                                if (useComplexObjects)
                                {
                                    var complexObj = CreateComplexObject(threadId, j, random);
                                    Logger.LogInfo(complexObj);
                                }
                                else
                                {
                                    Logger.LogInfo($"Simple request {j}");
                                }
                                threadStats[threadId].RequestCount++;
                                break;
                            case 3:
                                if (useComplexObjects)
                                {
                                    var complexObj = CreateComplexObject(threadId, j, random);
                                    Logger.LogInfo(complexObj);
                                }
                                else
                                {
                                    Logger.LogInfo($"Simple response {j}");
                                }
                                threadStats[threadId].ResponseCount++;
                                break;
                            case 4:
                                Logger.LogInfo($"Start operation {j}");
                                threadStats[threadId].StartCount++;
                                break;
                        }

                        threadStats[threadId].TotalMessages++;

                        // Simulate some processing time
                        if (j % 100 == 0)
                        {
                            Thread.Sleep(random.Next(1, 5));
                        }
                    }

                    threadStopwatch.Stop();
                    threadStats[threadId].ElapsedMs = threadStopwatch.ElapsedMilliseconds;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"خطا در نخ {threadId}: {ex.Message}");
                    threadStats[threadId].ErrorCount++;
                }
            }, cts.Token));
        }

        // Wait for all tasks to complete or timeout
        try
        {
            Task.WhenAll(tasks).Wait(testDurationMs);
        }
        catch (AggregateException)
        {
            // Ignore aggregate exceptions
        }

        // Cancel any remaining tasks
        cts.Cancel();

        // Wait for logger to flush
        WaitForLoggerToFlush();

        // Capture final metrics
        var finalMetrics = Logger.GetMetrics();
        stopwatch.Stop();

        // Calculate statistics
        long totalExpected = (long)threadCount * messagesPerThread;
        long totalLogged = finalMetrics.TotalLogsWritten - initialMetrics.TotalLogsWritten;
        long dropped = finalMetrics.DroppedLogsCount - initialMetrics.DroppedLogsCount;
        long writeErrors = finalMetrics.WriteErrorsCount - initialMetrics.WriteErrorsCount;
        long selfRepairActions = finalMetrics.SelfRepairActionsCount - initialMetrics.SelfRepairActionsCount;
        long circuitBreakerTrips = finalMetrics.CircuitBreakerTrippedCount - initialMetrics.CircuitBreakerTrippedCount;
        int restarts = finalMetrics.RestartCount - initialMetrics.RestartCount;

        // Calculate thread statistics
        long totalThreadMessages = 0;
        double avgThreadTime = 0;
        for (int i = 0; i < threadCount; i++)
        {
            totalThreadMessages += threadStats[i].TotalMessages;
            avgThreadTime += threadStats[i].ElapsedMs;
        }
        avgThreadTime /= threadCount;

        // Display results
        Console.WriteLine("\n=== نتایج تست بار سنگین ===");
        Console.WriteLine($"زمان کل اجرا: {stopwatch.ElapsedMilliseconds} میلی‌ثانیه");
        Console.WriteLine($"تعداد کل پیام‌های تولید شده: {totalExpected:N0}");
        Console.WriteLine($"تعداد کل پیام‌های لاگ شده: {totalLogged:N0}");
        Console.WriteLine($"تعداد پیام‌های حذف شده: {dropped:N0}");
        Console.WriteLine($"تعداد خطاهای نوشتن: {writeErrors:N0}");
        Console.WriteLine($"تعداد عملیات تعمیر خودکار: {selfRepairActions:N0}");
        Console.WriteLine($"تعداد فعال‌سازی Circuit Breaker: {circuitBreakerTrips:N0}");
        Console.WriteLine($"تعداد راه‌اندازی مجدد لاگر: {restarts:N0}");
        Console.WriteLine($"میانگین زمان نوشتن: {finalMetrics.AverageWriteTimeMs:F2} میلی‌ثانیه");
        Console.WriteLine($"بار نهایی صف: {finalMetrics.QueueLoadPercent}%");
        Console.WriteLine($"میانگین زمان اجرای هر نخ: {avgThreadTime:F2} میلی‌ثانیه");

        // Calculate success rate
        double successRate = totalExpected > 0 ? (double)(totalExpected - dropped) / totalExpected * 100 : 0;
        Console.WriteLine($"نرخ موفقیت: {successRate:F2}%");

        // Display warnings if any
        if (dropped > 0)
            Console.WriteLine($"⚠️ هشدار: {dropped:N0} پیام حذف شد!");
        if (writeErrors > 0)
            Console.WriteLine($"⚠️ هشدار: {writeErrors:N0} خطای نوشتن رخ داد!");
        if (restarts > 0)
            Console.WriteLine($"⚠️ هشدار: لاگر {restarts} بار راه‌اندازی مجدد شد!");
        if (successRate < 95)
            Console.WriteLine($"⚠️ هشدار: نرخ موفقیت کمتر از 95% است!");

        // Display per-thread statistics
        Console.WriteLine("\n=== آمار هر نخ ===");
        for (int i = 0; i < threadCount; i++)
        {
            var stats = threadStats[i];
            Console.WriteLine($"نخ {i}: پیام‌ها={stats.TotalMessages:N0}, " +
                           $"Info={stats.InfoCount:N0}, Error={stats.ErrorCount:N0}, " +
                           $"Request={stats.RequestCount:N0}, Response={stats.ResponseCount:N0}, " +
                           $"Start={stats.StartCount:N0}, زمان={stats.ElapsedMs:F2}ms");
        }

        Console.WriteLine("\n=== تست کامل شد ===");
    }

    /// <summary>
    /// منتظر می‌ماند تا لاگر تمام پیام‌ها را پردازش کند
    /// </summary>
    private static void WaitForLoggerToFlush(int timeoutMs = 60000)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastQueueCount = -1;
        var noProgressCount = 0;

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            var metrics = Logger.GetMetrics();

            // Check if queue is empty
            if (metrics.QueueLoadPercent == 0)
            {
                Console.WriteLine($"✅ صف خالی شد بعد از {stopwatch.ElapsedMilliseconds}ms");
                return;
            }

            // Check if we're making progress
            if (metrics.QueueLoadPercent == lastQueueCount)
            {
                noProgressCount++;
                if (noProgressCount > 10) // No progress for 10 checks
                {
                    Console.WriteLine($"⚠️ صف پیشرفت نمی‌کند. بار فعلی: {metrics.QueueLoadPercent}%");
                    break;
                }
            }
            else
            {
                noProgressCount = 0;
                lastQueueCount = metrics.QueueLoadPercent;
            }

            Thread.Sleep(500);
        }

        Console.WriteLine($"⏱️ زمان انتظار برای خالی شدن صف تمام شد. بار فعلی: {Logger.GetMetrics().QueueLoadPercent}%");
    }

    /// <summary>
    /// ایجاد یک آبجکت پیچیده برای تست
    /// </summary>
    private static ComplexObject CreateComplexObject(int threadId, int messageId, Random random)
    {
        return new ComplexObject
        {
            ThreadId = threadId,
            MessageId = messageId,
            Timestamp = DateTime.UtcNow,
            Data = new string('X', random.Next(100, 1000)),
            NestedObject = new NestedObject
            {
                Value = random.NextDouble(),
                Items = Enumerable.Range(0, random.Next(5, 20))
                    .Select(i => $"Item-{i}-{random.Next(1000)}")
                    .ToArray()
            },
            Metadata = new Dictionary<string, object>
            {
                ["ThreadId"] = threadId,
                ["MessageId"] = messageId,
                ["Random"] = random.Next(10000)
            }
        };
    }

    /// <summary>
    /// کلاس برای نگهداری آمار هر نخ
    /// </summary>
    private class ThreadStats
    {
        public long TotalMessages { get; set; }
        public long InfoCount { get; set; }
        public long ErrorCount { get; set; }
        public long RequestCount { get; set; }
        public long ResponseCount { get; set; }
        public long StartCount { get; set; }
        public long ElapsedMs { get; set; }
    }

    /// <summary>
    /// آبجکت پیچیده برای تست
    /// </summary>
    private class ComplexObject
    {
        public int ThreadId { get; set; }
        public int MessageId { get; set; }
        public DateTime Timestamp { get; set; }
        public string Data { get; set; }
        public NestedObject NestedObject { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// آبجکت تو در تو برای تست
    /// </summary>
    private class NestedObject
    {
        public double Value { get; set; }
        public string[] Items { get; set; }
    }

    /// <summary>
    /// تست عملکرد لاگر با سناریوهای مختلف
    /// </summary>
    public static void RunPerformanceTests()
    {
        Console.WriteLine("=== شروع تست‌های عملکردی لاگر ===\n");

        // Test 1: Basic load test
        Console.WriteLine("تست 1: بار پایین (5 نخ، 1000 پیام)");
        SimulateHighLoad(threadCount: 5, messagesPerThread: 1000, testDurationMs: 10000);
        Console.WriteLine();

        // Test 2: Medium load test
        Console.WriteLine("تست 2: بار متوسط (10 نخ، 5000 پیام)");
        SimulateHighLoad(threadCount: 10, messagesPerThread: 5000, testDurationMs: 20000);
        Console.WriteLine();

        // Test 3: High load test
        Console.WriteLine("تست 3: بار بالا (20 نخ، 10000 پیام)");
        SimulateHighLoad(threadCount: 20, messagesPerThread: 10000, testDurationMs: 30000);
        Console.WriteLine();

        // Test 4: Extreme load test
        Console.WriteLine("تست 4: بار بسیار بالا (50 نخ، 20000 پیام)");
        SimulateHighLoad(threadCount: 50, messagesPerThread: 20000, testDurationMs: 45000);
        Console.WriteLine();

        // Test 5: Complex objects test
        Console.WriteLine("تست 5: آبجکت‌های پیچیده (10 نخ، 5000 پیام)");
        SimulateHighLoad(threadCount: 10, messagesPerThread: 5000, testDurationMs: 20000, useComplexObjects: true);
        Console.WriteLine();

        // Test 6: Temporary directory test
        string tempDir = Path.Combine(Path.GetTempPath(), "LoggerTest_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Console.WriteLine($"تست 6: استفاده از دایرکتوری موقت ({tempDir})");
        SimulateHighLoad(threadCount: 15, messagesPerThread: 3000, testDurationMs: 15000, tempLogDir: tempDir);
        Console.WriteLine();

        Console.WriteLine("=== تمام تست‌های عملکردی کامل شد ===");
    }
}


////نحوه تست
//LoggerTest.SimulateHighLoad();
//// تست با 50 نخ، 10000 پیام و آبجکت‌های پیچیده
//LoggerTest.SimulateHighLoad(
//    threadCount: 50,
//    messagesPerThread: 10000,
//    testDurationMs: 60000,
//    useComplexObjects: true,
//    tempLogDir: @"C:\Temp\LoggerTest"
//);

