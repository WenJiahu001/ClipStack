using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClipStack.Core;

namespace ClipStack.Tests;

internal static class PerformanceBenchmarks
{
    // 固定合成数据，不读取用户历史；计时不作为受机器负载影响的单元测试门槛。
    internal static int Run(string reportPath, int rows = 1000)
    {
        if (rows < 100 || rows > 10000) throw new ArgumentOutOfRangeException(nameof(rows), "基准条数须为 100～10000。");
        string output = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var paths = new AppPaths(Path.Combine(Path.GetDirectoryName(output)!, "performance-data-" + Guid.NewGuid().ToString("N")));
        var settings = new AppSettings { MaxEntries = rows };
        using (var seed = new HistoryStore(paths, settings))
        {
            string padding = new('a', 6000);
            for (int i = 0; i < rows; i++)
            {
                string id = seed.Save(CapturedClip.FromText($"记录 {i:D4} {padding} 全文末尾标记 {i:D4}")).Id!;
                if (i % 10 == 0) { seed.SetFavorite(id, true); seed.SetNote(id, "性能合成收藏"); }
            }
        }
        using var store = new HistoryStore(paths, settings);
        var results = new List<object>();
        Measure("首次读取首批100条", () => store.Query(limit: 101), 1);
        Measure($"首批后完整读取{rows}条", () => store.Query(), 1);
        Measure($"重复打开{rows}条", () => store.Query());
        Measure("全文末尾搜索", () => store.Query($"全文末尾标记 {rows - 1:D4}"));
        Measure("无匹配全文搜索", () => store.Query("不存在的关键词"));
        Measure("收藏筛选", () => store.Query(filter: HistoryFilter.Favorites));
        Measure("空图片筛选", () => store.Query(filter: HistoryFilter.Image));
        int unique = 0;
        Measure("满额新增并淘汰", () => store.Save(CapturedClip.FromText("性能新增-" + unique++)));
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.Now, rows, textLength = 6000, notes = rows / 10, samples = rows > 1000 ? 3 : 7,
            results, processWorkingSetBytes = Environment.WorkingSet,
            environment = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(),
            syntheticDataOnly = true, timingsAreMachineSpecific = true
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("性能报告：" + output);
        return 0;

        void Measure(string name, Action action, int samples = 0)
        {
            if (samples == 0) samples = rows > 1000 ? 3 : 7;
            var timings = new List<double>();
            long allocated = 0;
            for (int i = 0; i < samples; i++)
            {
                long before = GC.GetTotalAllocatedBytes(true);
                long started = Stopwatch.GetTimestamp(); action();
                timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                allocated += GC.GetTotalAllocatedBytes(true) - before;
            }
            timings.Sort();
            double median = timings[timings.Count / 2];
            results.Add(new { name, medianMilliseconds = median, maxMilliseconds = timings[^1], allocatedBytesPerOperation = allocated / samples });
            Console.WriteLine($"{name}：中位数 {median:0.00} ms，分配 {allocated / samples:N0} 字节");
        }
    }
}
