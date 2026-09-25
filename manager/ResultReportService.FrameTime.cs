using System.Text.Json;

namespace GRedscriptProfiler.Manager;

internal static partial class ResultReportService
{
    private static FrameTimeAnalysis? AnalyzeFrameTime(
        string captureRoot,
        IReadOnlyList<FrameMetric> grspFrames,
        IReadOnlyList<SpikeMetric> spikes)
    {
        var frameTimeRoot = Path.Combine(captureRoot, "FrameTime");
        if (!Directory.Exists(frameTimeRoot))
            return null;

        var jsonPath = Directory.EnumerateFiles(frameTimeRoot, "*.json", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals("CompanionManifest.json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (jsonPath is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            var info = root.TryGetProperty("Info", out var i) ? i : default;

            if (!root.TryGetProperty("Runs", out var runs) ||
                runs.ValueKind != JsonValueKind.Array ||
                runs.GetArrayLength() == 0)
                return null;

            var run = runs[0];
            if (!run.TryGetProperty("CaptureData", out var captureData))
                return null;

            var frameMs = ReadDoubleArray(captureData, "MsBetweenPresents");
            if (frameMs.Count == 0)
                return null;

            var timeSeconds = ReadDoubleArray(captureData, "TimeInSeconds");
            var cpu = ReadDoubleArray(captureData, "CpuActive");
            var gpu = ReadDoubleArray(captureData, "GpuActive");
            var latency = ReadDoubleArray(captureData, "PcLatency");
            var types = ReadStringArray(captureData, "FrameType");

            var count = frameMs.Count;
            var startMs = new double[count];
            if (timeSeconds.Count >= count)
            {
                for (var n = 0; n < count; n++)
                    startMs[n] = timeSeconds[n] * 1000.0;
            }
            else
            {
                var t = 0.0;
                for (var n = 0; n < count; n++)
                {
                    startMs[n] = t;
                    t += Math.Max(0, frameMs[n]);
                }
            }

            var validFrameMs = frameMs.Where(x => double.IsFinite(x) && x >= 0).ToArray();
            if (validFrameMs.Length == 0)
                return null;

            var durationMs = validFrameMs.Sum();
            var analysis = new FrameTimeAnalysis
            {
                SourceFile = Path.GetFileName(jsonPath),
                AppVersion = JsonString(info, "AppVersion"),
                GameName = JsonString(info, "GameName"),
                Gpu = JsonString(info, "GPU"),
                Processor = JsonString(info, "Processor"),
                FrameCount = count,
                DurationSeconds = durationMs / 1000.0,
                AverageFps = durationMs > 0 ? count * 1000.0 / durationMs : 0,
                MeanFrameMs = validFrameMs.Average(),
                MedianFrameMs = Percentile(validFrameMs, 0.50),
                P95FrameMs = Percentile(validFrameMs, 0.95),
                P99FrameMs = Percentile(validFrameMs, 0.99),
                MaxFrameMs = validFrameMs.Max(),
                FramesOver25Ms = validFrameMs.Count(x => x >= 25),
                FramesOver33Ms = validFrameMs.Count(x => x >= 33.3),
                FramesOver50Ms = validFrameMs.Count(x => x >= 50),
                FramesOver100Ms = validFrameMs.Count(x => x >= 100),
                MeanCpuActiveMs = MeanFinite(cpu),
                P95CpuActiveMs = PercentileFinite(cpu, 0.95),
                MeanGpuActiveMs = MeanFinite(gpu),
                P95GpuActiveMs = PercentileFinite(gpu, 0.95)
            };

            if (grspFrames.Count < 120)
            {
                analysis.SyncQuality = "STATISTICS ONLY";
                analysis.AlignmentMethod = "Not enough GRSP frames for frame-sequence alignment.";
                return analysis;
            }

            var candidates = new List<AlignmentCandidate>();
            for (var lag = -120; lag <= 120; lag++)
            {
                var capStart = Math.Max(0, -lag);
                var capEnd = Math.Min(count, grspFrames.Count - lag);
                var pairCount = capEnd - capStart;
                if (pairCount < Math.Min(300, Math.Min(count, grspFrames.Count) / 2))
                    continue;

                var x = new double[pairCount];
                var y = new double[pairCount];
                var lx = new double[pairCount];
                var ly = new double[pairCount];

                for (var p = 0; p < pairCount; p++)
                {
                    var capIndex = capStart + p;
                    var grspIndex = capIndex + lag;
                    var xv = Math.Max(0, frameMs[capIndex]);
                    var yv = Math.Max(0, grspFrames[grspIndex].FrameDurationMs);
                    x[p] = xv;
                    y[p] = yv;
                    lx[p] = Math.Log(1.0 + xv);
                    ly[p] = Math.Log(1.0 + yv);
                }

                var pearson = Pearson(x, y);
                var logPearson = Pearson(lx, ly);
                var score = pearson * 0.70 + logPearson * 0.30;
                candidates.Add(new AlignmentCandidate(lag, pairCount, pearson, logPearson, score));
            }

            if (candidates.Count == 0)
            {
                analysis.SyncQuality = "STATISTICS ONLY";
                analysis.AlignmentMethod = "No viable frame-sequence alignment window.";
                return analysis;
            }

            var ordered = candidates.OrderByDescending(x => x.Score).ToList();
            var best = ordered[0];
            var runnerUp = ordered.Skip(1)
                .Where(x => Math.Abs(x.Lag - best.Lag) >= 1)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            analysis.FrameLag = best.Lag;
            analysis.AlignmentPearson = best.Pearson;
            analysis.AlignmentLogPearson = best.LogPearson;
            analysis.RunnerUpPearson = runnerUp?.Pearson ?? 0;

            var rawGap = best.Pearson - (runnerUp?.Pearson ?? 0);
            var exact =
                best.Pairs >= Math.Min(1000, Math.Min(count, grspFrames.Count) / 2) &&
                best.Pearson >= 0.60 &&
                best.LogPearson >= 0.80 &&
                rawGap >= 0.08;

            if (!exact)
            {
                analysis.SyncQuality = best.Pearson >= 0.45 ? "COARSE" : "STATISTICS ONLY";
                analysis.AlignmentMethod =
                    $"Frame-duration sequence match was not unique enough for exact mapping (best fixed offset {best.Lag:+#;-#;0} frame(s), Pearson {best.Pearson:0.###}, runner-up {(runnerUp?.Pearson ?? 0):0.###}).";
                return analysis;
            }

            var firstCap = Math.Max(0, -best.Lag);
            var lastCap = Math.Min(count, grspFrames.Count - best.Lag);
            var offsets = new List<double>(lastCap - firstCap);
            for (var capIndex = firstCap; capIndex < lastCap; capIndex++)
            {
                var grspIndex = capIndex + best.Lag;
                offsets.Add(grspFrames[grspIndex].StartMs - startMs[capIndex]);
            }

            var medianOffset = Median(offsets);
            var mad = Median(offsets.Select(x => Math.Abs(x - medianOffset)).ToList());
            analysis.StartOffsetMedianMs = medianOffset;
            analysis.StartOffsetMadMs = mad;
            analysis.AlignedFramePairs = lastCap - firstCap;
            analysis.CapFrameXFramesBeforeOverlap = firstCap;
            analysis.CapFrameXFramesAfterOverlap = count - lastCap;
            analysis.GrspFramesBeforeOverlap = firstCap + best.Lag;
            analysis.GrspFramesAfterOverlap = grspFrames.Count - (lastCap + best.Lag);
            analysis.ExactFrameAlignment = true;
            analysis.Correlated = true;
            analysis.SyncQuality = best.Pearson >= 0.65 && mad <= 8 ? "GOOD" : "GOOD";
            analysis.AlignmentMethod =
                $"Direct frame-sequence alignment with a fixed {best.Lag:+#;-#;0}-frame offset: CapFrameX frame i ≈ GRSP frame i{FormatLag(best.Lag)}. This is an alignment offset, not a per-frame drift measurement. Duration-sequence Pearson {best.Pearson:0.###}, log-Pearson {best.LogPearson:0.###}, offset MAD {mad:0.###} ms.";

            var spikeByFrame = spikes
                .GroupBy(x => x.FrameId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(x => x.ExclusiveMs).ThenByDescending(x => x.DurationMs).ToList());

            var aligned = new List<AlignedFrame>(lastCap - firstCap);
            for (var capIndex = firstCap; capIndex < lastCap; capIndex++)
            {
                var grspIndex = capIndex + best.Lag;
                var gf = grspFrames[grspIndex];
                var cpuMs = At(cpu, capIndex);
                var gpuMs = At(gpu, capIndex);
                var latencyMs = At(latency, capIndex);
                var frameType = capIndex < types.Count ? types[capIndex] : "";
                spikeByFrame.TryGetValue(gf.FrameId, out var frameSpikes);
                var largestSpike = frameSpikes?.FirstOrDefault();

                aligned.Add(new AlignedFrame
                {
                    CapFrameIndex = capIndex,
                    GrspFrameId = gf.FrameId,
                    StartMs = startMs[capIndex],
                    FrameMs = Math.Max(0, frameMs[capIndex]),
                    CpuActiveMs = cpuMs,
                    GpuActiveMs = gpuMs,
                    PcLatencyMs = latencyMs,
                    FrameType = frameType,
                    ScriptMs = Math.Max(0, gf.ExclusiveMs),
                    ScriptCalls = gf.TotalCalls,
                    SpikeCount = gf.SpikeCount,
                    LargestSpikeOwner = largestSpike?.Owner ?? "",
                    LargestSpikeMs = largestSpike?.ExclusiveMs ?? 0
                });
            }

            if (aligned.Count == 0)
                return analysis;

            var highThreshold = Percentile(aligned.Select(x => x.ScriptMs).ToArray(), 0.90);
            analysis.HighScriptThresholdMs = highThreshold;

            foreach (var row in aligned)
            {
                row.HighScript = row.ScriptMs >= highThreshold && highThreshold > 0;
                row.Evidence = row.HighScript && row.SpikeCount > 0
                    ? "High REDscript frame · recorded spike"
                    : row.HighScript
                        ? "High REDscript frame"
                        : row.SpikeCount > 0
                            ? "Recorded spike · below top-10% script load"
                            : "REDscript-normal frame";
            }

            var slow = aligned.Where(x => x.FrameMs >= 33.3).ToList();
            var high = aligned.Where(x => x.HighScript).ToList();
            var normal = aligned.Where(x => !x.HighScript).ToList();

            analysis.SlowFrames = slow.Count;
            analysis.SlowFramesHighScript = slow.Count(x => x.HighScript);
            analysis.SlowFramesWithSpike = slow.Count(x => x.SpikeCount > 0);
            analysis.SlowFramesScriptNormal = slow.Count(x => !x.HighScript);
            analysis.HighScriptSlowRatePct = high.Count == 0 ? 0 : high.Count(x => x.FrameMs >= 33.3) * 100.0 / high.Count;
            analysis.NormalScriptSlowRatePct = normal.Count == 0 ? 0 : normal.Count(x => x.FrameMs >= 33.3) * 100.0 / normal.Count;

            var actual = aligned.Select(x => x.FrameMs).ToArray();
            var script = aligned.Select(x => x.ScriptMs).ToArray();
            analysis.PearsonFrameCorrelation = Pearson(actual, script);
            analysis.SpearmanFrameCorrelation = Spearman(actual, script);
            analysis.WorstFrames = aligned.OrderByDescending(x => x.FrameMs).Take(30).ToList();
            analysis.Timeline = aligned;

            return analysis;
        }
        catch
        {
            // Companion data must never make a valid GRSP capture unreadable.
            return new FrameTimeAnalysis
            {
                SourceFile = Path.GetFileName(jsonPath),
                SyncQuality = "UNREADABLE",
                AlignmentMethod = "CapFrameX data was present but could not be parsed safely."
            };
        }
    }

    private static string FormatLag(int lag) =>
        lag == 0 ? "" : lag > 0 ? $"+{lag}" : lag.ToString();

    private static string JsonString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";
        return "";
    }

    private static List<double> ReadDoubleArray(JsonElement parent, string name)
    {
        var values = new List<double>();
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out var array) ||
            array.ValueKind != JsonValueKind.Array)
            return values;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var n) && double.IsFinite(n))
                values.Add(n);
            else if (item.ValueKind == JsonValueKind.String &&
                     double.TryParse(item.GetString(), System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out n) &&
                     double.IsFinite(n))
                values.Add(n);
            else
                values.Add(0);
        }

        return values;
    }

    private static List<string> ReadStringArray(JsonElement parent, string name)
    {
        var values = new List<string>();
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out var array) ||
            array.ValueKind != JsonValueKind.Array)
            return values;

        foreach (var item in array.EnumerateArray())
            values.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString());

        return values;
    }

    private static double At(IReadOnlyList<double> values, int index) =>
        index >= 0 && index < values.Count && double.IsFinite(values[index]) ? values[index] : 0;

    private static double MeanFinite(IEnumerable<double> values)
    {
        var valid = values.Where(double.IsFinite).ToArray();
        return valid.Length == 0 ? 0 : valid.Average();
    }

    private static double PercentileFinite(IEnumerable<double> values, double p) =>
        Percentile(values.Where(double.IsFinite).ToArray(), p);

    private static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0)
            return 0;

        var sorted = values.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (sorted.Length == 0)
            return 0;

        p = Math.Clamp(p, 0, 1);
        var position = (sorted.Length - 1) * p;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sorted[lower];

        var weight = position - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    private static double Median(IReadOnlyList<double> values) =>
        Percentile(values, 0.5);

    private static double Pearson(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = Math.Min(x.Count, y.Count);
        if (n < 2)
            return 0;

        var sx = 0.0;
        var sy = 0.0;
        var sxx = 0.0;
        var syy = 0.0;
        var sxy = 0.0;
        var count = 0;

        for (var i = 0; i < n; i++)
        {
            var a = x[i];
            var b = y[i];
            if (!double.IsFinite(a) || !double.IsFinite(b))
                continue;

            sx += a;
            sy += b;
            sxx += a * a;
            syy += b * b;
            sxy += a * b;
            count++;
        }

        if (count < 2)
            return 0;

        var numerator = count * sxy - sx * sy;
        var dx = count * sxx - sx * sx;
        var dy = count * syy - sy * sy;
        if (dx <= 0 || dy <= 0)
            return 0;

        return numerator / Math.Sqrt(dx * dy);
    }

    private static double Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = Math.Min(x.Count, y.Count);
        if (n < 2)
            return 0;

        var xx = x.Take(n).ToArray();
        var yy = y.Take(n).ToArray();
        return Pearson(Ranks(xx), Ranks(yy));
    }

    private static double[] Ranks(IReadOnlyList<double> values)
    {
        var indexed = values.Select((value, index) => (Value: value, Index: index))
            .OrderBy(x => x.Value)
            .ToArray();
        var ranks = new double[values.Count];

        var i = 0;
        while (i < indexed.Length)
        {
            var j = i + 1;
            while (j < indexed.Length && Math.Abs(indexed[j].Value - indexed[i].Value) < 1e-12)
                j++;

            var averageRank = (i + 1 + j) / 2.0;
            for (var k = i; k < j; k++)
                ranks[indexed[k].Index] = averageRank;
            i = j;
        }

        return ranks;
    }

    private sealed record AlignmentCandidate(
        int Lag,
        int Pairs,
        double Pearson,
        double LogPearson,
        double Score);

    private sealed class FrameTimeAnalysis
    {
        public string SourceFile { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public string GameName { get; set; } = "";
        public string Gpu { get; set; } = "";
        public string Processor { get; set; } = "";
        public int FrameCount { get; set; }
        public double DurationSeconds { get; set; }
        public double AverageFps { get; set; }
        public double MeanFrameMs { get; set; }
        public double MedianFrameMs { get; set; }
        public double P95FrameMs { get; set; }
        public double P99FrameMs { get; set; }
        public double MaxFrameMs { get; set; }
        public int FramesOver25Ms { get; set; }
        public int FramesOver33Ms { get; set; }
        public int FramesOver50Ms { get; set; }
        public int FramesOver100Ms { get; set; }
        public double MeanCpuActiveMs { get; set; }
        public double P95CpuActiveMs { get; set; }
        public double MeanGpuActiveMs { get; set; }
        public double P95GpuActiveMs { get; set; }
        public string SyncQuality { get; set; } = "STATISTICS ONLY";
        public bool Correlated { get; set; }
        public bool ExactFrameAlignment { get; set; }
        public string AlignmentMethod { get; set; } = "";
        public int FrameLag { get; set; }
        public double AlignmentPearson { get; set; }
        public double AlignmentLogPearson { get; set; }
        public double RunnerUpPearson { get; set; }
        public double StartOffsetMedianMs { get; set; }
        public double StartOffsetMadMs { get; set; }
        public int AlignedFramePairs { get; set; }
        public int CapFrameXFramesBeforeOverlap { get; set; }
        public int CapFrameXFramesAfterOverlap { get; set; }
        public int GrspFramesBeforeOverlap { get; set; }
        public int GrspFramesAfterOverlap { get; set; }
        public double HighScriptThresholdMs { get; set; }
        public int SlowFrames { get; set; }
        public int SlowFramesHighScript { get; set; }
        public int SlowFramesWithSpike { get; set; }
        public int SlowFramesScriptNormal { get; set; }
        public double HighScriptSlowRatePct { get; set; }
        public double NormalScriptSlowRatePct { get; set; }
        public double PearsonFrameCorrelation { get; set; }
        public double SpearmanFrameCorrelation { get; set; }
        public List<AlignedFrame> WorstFrames { get; set; } = [];
        public List<AlignedFrame> Timeline { get; set; } = [];
    }

    private sealed class AlignedFrame
    {
        public int CapFrameIndex { get; init; }
        public long GrspFrameId { get; init; }
        public double StartMs { get; init; }
        public double FrameMs { get; init; }
        public double CpuActiveMs { get; init; }
        public double GpuActiveMs { get; init; }
        public double PcLatencyMs { get; init; }
        public string FrameType { get; init; } = "";
        public double ScriptMs { get; init; }
        public long ScriptCalls { get; init; }
        public long SpikeCount { get; init; }
        public string LargestSpikeOwner { get; init; } = "";
        public double LargestSpikeMs { get; init; }
        public bool HighScript { get; set; }
        public string Evidence { get; set; } = "";
    }
}
