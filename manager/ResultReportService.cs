using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GRedscriptProfiler.Manager;

internal sealed record ResultReportBuildResult(
    string ReportPath,
    string SummaryPath,
    int FindingsCount,
    bool HasFrameTimeData,
    string FrameTimeSyncQuality);

internal static partial class ResultReportService
{
    public const string ReportFileName = "GRSP_Report.html";
    public const string SummaryFileName = "GRSP_Summary.json";

    private static readonly HashSet<string> RuntimeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "GRSP_Summary.csv",
        "GRSP_ByMod.csv",
        "GRSP_ByFunction.csv",
        "GRSP_Timeline.csv",
        "GRSP_Frames.csv",
        "GRSP_Spikes.csv",
        "GRSP_Markers.csv",
        "GRSP_FrameworkCandidates.csv"
    };

    private static readonly HashSet<string> MetadataFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "GRSP_Status.txt",
        "RSP_Alpha_Status.txt",
        "LATEST.txt",
        "RSP_SessionIndex.csv"
    };

    public static string GetArchiveRelativePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var fileName = Path.GetFileName(normalized);

        if (normalized.StartsWith("Developer/", StringComparison.OrdinalIgnoreCase))
            return Path.Combine("Data", "Developer", normalized["Developer/".Length..].Replace('/', Path.DirectorySeparatorChar));

        if (normalized.StartsWith("FrameTime/", StringComparison.OrdinalIgnoreCase))
            return normalized.Replace('/', Path.DirectorySeparatorChar);

        if (normalized.StartsWith("LiveMetadata/", StringComparison.OrdinalIgnoreCase))
            return Path.Combine("Data", "Metadata", normalized["LiveMetadata/".Length..].Replace('/', Path.DirectorySeparatorChar));

        if (RuntimeFiles.Contains(fileName))
            return Path.Combine("Data", "Runtime", fileName);

        if (MetadataFiles.Contains(fileName))
            return Path.Combine("Data", "Metadata", fileName);

        return Path.Combine("Data", "Metadata", normalized.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string RuntimePath(string captureRoot, string fileName)
    {
        var canonical = Path.Combine(captureRoot, "Data", "Runtime", fileName);
        return File.Exists(canonical) ? canonical : Path.Combine(captureRoot, fileName);
    }

    private static string DeveloperRoot(string captureRoot)
    {
        var canonical = Path.Combine(captureRoot, "Data", "Developer");
        return Directory.Exists(canonical) ? canonical : Path.Combine(captureRoot, "Developer");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static ResultReportBuildResult Generate(string captureRoot)
    {
        if (string.IsNullOrWhiteSpace(captureRoot))
            throw new ArgumentException("Capture folder is empty.", nameof(captureRoot));

        captureRoot = Path.GetFullPath(captureRoot);
        if (!Directory.Exists(captureRoot))
            throw new DirectoryNotFoundException("Capture folder was not found: " + captureRoot);

        var a = Analyze(captureRoot);
        var summaryPath = Path.Combine(captureRoot, SummaryFileName);
        var reportPath = Path.Combine(captureRoot, ReportFileName);

        var summary = new
        {
            schemaVersion = "1.1",
            generatedUtc = DateTime.UtcNow.ToString("O"),
            interop = new
            {
                contractVersion = "1.0",
                producer = "G-REDscript-Profiler",
                domain = "redscript"
            },
            scope = a.FrameTime is null
                ? "Observed REDscript call-edge workload"
                : "Observed REDscript call-edge workload with optional CapFrameX frametime correlation",
            capture = new
            {
                version = a.Summary.Version,
                title = a.Summary.Scenario,
                scenario = a.Summary.Scenario,
                durationSeconds = Round(a.Summary.DurationMs / 1000.0, 3),
                frames = a.Summary.Frames,
                measuredCalls = a.Summary.ObservedCalls,
                callsPerSecond = Round(a.Summary.CallsPerSec, 3),
                exclusiveMsPerSecond = Round(a.Summary.ExclusiveMsPerSec, 6),
                exclusiveInstrumentedMsPerSecond = Round(a.Summary.ExclusiveMsPerSec, 6),
                measuredOneCorePct = Round(a.Summary.ExclusiveMsPerSec / 10.0, 6),
                averageScriptMsPerFrame = Round(a.Summary.AverageScriptMsPerFrame, 6),
                p95ScriptMsPerFrame = Round(a.Summary.P95ScriptMsPerFrame, 6),
                p99ScriptMsPerFrame = Round(a.Summary.P99ScriptMsPerFrame, 6),
                maxScriptMsPerFrame = Round(a.Summary.MaxScriptMsPerFrame, 6)
            },
            health = new
            {
                frameQuality = a.Summary.FrameQuality,
                shardMergeOk = a.Summary.ShardMergeOk,
                namedStaticCalls = a.Summary.NamedStaticCalls,
                unresolvedStaticCalls = a.Summary.UnresolvedStaticCalls,
                droppedSpikes = a.Summary.DroppedSpikes,
                droppedHotPaths = a.Summary.DroppedHotPaths
            },
            topOwners = a.Owners.Take(12).Select(x => new
            {
                owner = x.Owner,
                infrastructure = IsInfrastructureOwner(x.Owner),
                calls = x.Calls,
                callsPerSecond = Round(x.CallsPerSec, 3),
                exclusiveInstrumentedMs = Round(x.ExclusiveInstrumentedMs, 6),
                exclusiveMsPerSecond = Round(x.ExclusiveMsPerSec, 6),
                measuredOneCorePct = Round(x.ExclusiveMsPerSec / 10.0, 6),
                measuredSharePct = Round(x.SharePct, 3),
                activeFramePct = Round(x.ActiveFramePct, 3),
                maxFrameExclusiveMs = Round(x.MaxFrameExclusiveMs, 6),
                spikeCount = x.SpikeCount,
                workloadPattern = x.WorkloadPattern,
                attributionNote = x.AttributionNote
            }),
            topFunctions = a.Functions.Take(16).Select(x => new
            {
                owner = x.Owner,
                function = x.SourceFunction,
                sourcePath = x.SourcePath,
                sourceLine = x.SourceLine,
                callsPerSecond = Round(x.CallsPerSec, 3),
                exclusiveInstrumentedMs = Round(x.ExclusiveInstrumentedMs, 6),
                exclusiveMsPerSecond = Round(x.ExclusiveMsPerSec, 6),
                activeFramePct = Round(x.ActiveFramePct, 3),
                maxCallMs = Round(x.MaxCallMs, 6),
                spikesOver1ms = x.Over1Ms,
                spikesOver5ms = x.Over5Ms,
                spikesOver16_67ms = x.Over16Ms
            }),
            worstScriptFrames = a.HeavyFrames.Take(15).Select(x => new
            {
                frameId = x.FrameId,
                startMs = Round(x.StartMs, 3),
                frameDurationMs = Round(x.FrameDurationMs, 6),
                exclusiveInstrumentedMs = Round(x.ExclusiveMs, 6),
                calls = x.TotalCalls,
                uniqueOwners = x.UniqueOwners,
                largestCallMs = Round(x.LargestCallMs, 6),
                spikeCount = x.SpikeCount
            }),
            recordedSpikes = a.Spikes.Take(16).Select(x => new
            {
                captureMs = Round(x.CaptureMs, 3),
                frameId = x.FrameId,
                owner = x.Owner,
                sourceFunction = x.SourceFunction,
                sourceLine = x.SourceLine,
                target = x.Target,
                durationMs = Round(x.DurationMs, 6),
                exclusiveInstrumentedMs = Round(x.ExclusiveMs, 6)
            }),
            framework = new
            {
                infrastructureOwner = "G-RedRuntime",
                infrastructureWork = a.FrameworkOwner is null
                    ? null
                    : new
                    {
                        callsPerSecond = Round(a.FrameworkOwner.CallsPerSec, 3),
                        exclusiveMsPerSecond = Round(a.FrameworkOwner.ExclusiveMsPerSec, 6),
                        measuredSharePct = Round(a.FrameworkOwner.SharePct, 3)
                    },
                infrastructureFunctions = a.FrameworkFunctions.Take(12).Select(x => new
                {
                    function = x.SourceFunction,
                    callsPerSecond = Round(x.CallsPerSec, 3),
                    exclusiveMsPerSecond = Round(x.ExclusiveMsPerSec, 6),
                    activeFramePct = Round(x.ActiveFramePct, 3),
                    maxCallMs = Round(x.MaxCallMs, 6)
                }),
                candidates = a.FrameworkCandidates.Take(20).Select(x => new
                {
                    owner = x.Owner,
                    infrastructure = IsInfrastructureOwner(x.Owner),
                    callsPerSecond = Round(x.CallsPerSec, 3),
                    activeFramePct = Round(x.ActiveFramePct, 3),
                    callsPerActiveFrame = Round(x.CallsPerActiveFrame, 3),
                    maxDescendantsPerRoot = x.MaxDescendantsPerRoot,
                    frameworkSharedTargets = x.FrameworkSharedTargets,
                    wrapperCalls = x.WrapperCalls,
                    crossModCallsOut = x.CrossModCallsOut,
                    crossModCallsIn = x.CrossModCallsIn,
                    threads = x.Threads,
                    signals = SplitPipe(x.Signals),
                    frameworkPrimitives = SplitPipe(x.FrameworkPrimitives)
                }),
                liveSourceIntegration = a.SourceIntegrations
                    .Where(x => x.SourceAvailable || x.ReferencesGRedRuntime)
                    .Select(x => new
                    {
                        owner = x.Owner,
                        sourceAvailable = x.SourceAvailable,
                        filesScanned = x.FilesScanned,
                        usesGRedRuntime = x.ReferencesGRedRuntime,
                        services = x.Services,
                        runtimeVersion = x.RuntimeVersion
                    })
            },
            frameTime = BuildFrameTimeSummary(a.FrameTime),
            findings = a.Findings.Select(x => new
            {
                x.Title,
                x.Evidence,
                x.Explanation
            }),
            dataIndex = BuildDataIndex(captureRoot),
            data = EnumerateDataFiles(captureRoot)
        };

        File.WriteAllText(
            summaryPath,
            JsonSerializer.Serialize(summary, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(false));

        File.WriteAllText(
            reportPath,
            BuildHtml(a, captureRoot),
            new UTF8Encoding(false));

        return new ResultReportBuildResult(
            reportPath,
            summaryPath,
            a.Findings.Count,
            a.FrameTime is not null,
            a.FrameTime?.SyncQuality ?? "NONE");
    }

    private static object? BuildFrameTimeSummary(FrameTimeAnalysis? ft)
    {
        if (ft is null)
            return null;

        return new
        {
            source = ft.SourceFile,
            appVersion = ft.AppVersion,
            game = ft.GameName,
            gpu = ft.Gpu,
            processor = ft.Processor,
            frames = new
            {
                count = ft.FrameCount,
                durationSeconds = Round(ft.DurationSeconds, 3),
                averageFps = Round(ft.AverageFps, 3),
                meanMs = Round(ft.MeanFrameMs, 6),
                medianMs = Round(ft.MedianFrameMs, 6),
                p95Ms = Round(ft.P95FrameMs, 6),
                p99Ms = Round(ft.P99FrameMs, 6),
                maxMs = Round(ft.MaxFrameMs, 6),
                over25Ms = ft.FramesOver25Ms,
                over33_3Ms = ft.FramesOver33Ms,
                over50Ms = ft.FramesOver50Ms,
                over100Ms = ft.FramesOver100Ms,
                meanCpuActiveMs = Round(ft.MeanCpuActiveMs, 6),
                p95CpuActiveMs = Round(ft.P95CpuActiveMs, 6),
                meanGpuActiveMs = Round(ft.MeanGpuActiveMs, 6),
                p95GpuActiveMs = Round(ft.P95GpuActiveMs, 6)
            },
            sync = new
            {
                quality = ft.SyncQuality,
                correlated = ft.Correlated,
                exactAlignment = ft.ExactFrameAlignment,
                alignmentMethod = ft.AlignmentMethod,
                exactFrameAlignment = ft.ExactFrameAlignment,
                method = ft.AlignmentMethod,
                frameLag = ft.FrameLag,
                alignmentOffsetFrames = ft.FrameLag,
                alignedFramePairs = ft.AlignedFramePairs,
                capFrameXFramesBeforeOverlap = ft.CapFrameXFramesBeforeOverlap,
                capFrameXFramesAfterOverlap = ft.CapFrameXFramesAfterOverlap,
                grspFramesBeforeOverlap = ft.GrspFramesBeforeOverlap,
                grspFramesAfterOverlap = ft.GrspFramesAfterOverlap,
                durationSequencePearson = Round(ft.AlignmentPearson, 6),
                durationSequenceLogPearson = Round(ft.AlignmentLogPearson, 6),
                runnerUpPearson = Round(ft.RunnerUpPearson, 6),
                startOffsetMedianMs = Round(ft.StartOffsetMedianMs, 3),
                startOffsetMadMs = Round(ft.StartOffsetMadMs, 3)
            },
            correlation = ft.Correlated
                ? new
                {
                    highScriptThresholdMs = Round(ft.HighScriptThresholdMs, 6),
                    slowFrames = ft.SlowFrames,
                    slowFramesHighScript = ft.SlowFramesHighScript,
                    slowFramesWithRecordedSpike = ft.SlowFramesWithSpike,
                    slowFramesScriptNormal = ft.SlowFramesScriptNormal,
                    highScriptFrameSlowRatePct = Round(ft.HighScriptSlowRatePct, 3),
                    normalScriptFrameSlowRatePct = Round(ft.NormalScriptSlowRatePct, 3),
                    pearsonFrame = Round(ft.PearsonFrameCorrelation, 6),
                    spearmanFrame = Round(ft.SpearmanFrameCorrelation, 6)
                }
                : null,
            worstFrames = ft.WorstFrames.Take(20).Select(x => new
            {
                frameIndex = x.CapFrameIndex,
                capFrameXIndex = x.CapFrameIndex,
                grspFrameId = x.GrspFrameId,
                startMs = Round(x.StartMs, 3),
                frameMs = Round(x.FrameMs, 6),
                cpuActiveMs = Round(x.CpuActiveMs, 6),
                gpuActiveMs = Round(x.GpuActiveMs, 6),
                scriptMs = Round(x.ScriptMs, 6),
                scriptCalls = x.ScriptCalls,
                spikeCount = x.SpikeCount,
                largestRecordedSpikeOwner = x.LargestSpikeOwner,
                largestRecordedSpikeMs = Round(x.LargestSpikeMs, 6),
                highScript = x.HighScript,
                evidence = x.Evidence
            })
        };
    }

    private static ResultAnalysis Analyze(string captureRoot)
    {
        var summaryRows = ReadCsv(RuntimePath(captureRoot, "GRSP_Summary.csv"));
        if (summaryRows.Count == 0)
        {
            if (File.Exists(Path.Combine(captureRoot, "RSP_Alpha_Capture.csv")) ||
                File.Exists(Path.Combine(captureRoot, "Data", "Metadata", "RSP_Alpha_Capture.csv")))
                throw new InvalidOperationException(
                    "This is a legacy Alpha capture. The v1.0 report builder expects the public GRSP_* output set produced by G-REDscript Profiler 1.0.0.");
            throw new InvalidOperationException("GRSP_Summary.csv was not found or contains no capture row.");
        }

        var summary = ParseSummary(summaryRows[0]);
        var owners = ReadCsv(RuntimePath(captureRoot, "GRSP_ByMod.csv"))
            .Select(ParseOwner)
            .Where(x => !string.IsNullOrWhiteSpace(x.Owner))
            .OrderByDescending(x => x.ExclusiveMsPerSec)
            .ToList();

        var functions = ReadCsv(RuntimePath(captureRoot, "GRSP_ByFunction.csv"))
            .Select(ParseFunction)
            .Where(x => !string.IsNullOrWhiteSpace(x.Owner) || !string.IsNullOrWhiteSpace(x.SourceFunction))
            .OrderByDescending(x => x.ExclusiveMsPerSec)
            .ToList();

        var frames = ReadCsv(RuntimePath(captureRoot, "GRSP_Frames.csv"))
            .Select(ParseFrame)
            .OrderBy(x => x.FrameId)
            .ToList();

        var spikes = ReadCsv(RuntimePath(captureRoot, "GRSP_Spikes.csv"))
            .Select(ParseSpike)
            .OrderByDescending(x => x.ExclusiveMs)
            .ThenByDescending(x => x.DurationMs)
            .ToList();

        var candidates = ReadCsv(RuntimePath(captureRoot, "GRSP_FrameworkCandidates.csv"))
            .Select(ParseFrameworkCandidate)
            .Where(x => !string.IsNullOrWhiteSpace(x.Owner))
            .ToList();

        var frameworkOwner = owners.FirstOrDefault(x => IsInfrastructureOwner(x.Owner));
        var frameworkFunctions = functions
            .Where(x => IsInfrastructureOwner(x.Owner))
            .OrderByDescending(x => x.ExclusiveMsPerSec)
            .ToList();

        // Triage candidates are shown in measured-cost order so the heuristic
        // signal is placed beside actual observed contribution. This is not an
        // automatic rewrite/removal ranking.
        var ownerCost = owners.ToDictionary(x => x.Owner, x => x.ExclusiveMsPerSec, StringComparer.OrdinalIgnoreCase);
        candidates = candidates
            .OrderByDescending(x => ownerCost.TryGetValue(x.Owner, out var cost) ? cost : 0)
            .ThenByDescending(x => x.CallsPerSec)
            .ToList();

        var sourceIntegrations = AnalyzeSourceIntegrations(owners, functions);
        var frameTime = AnalyzeFrameTime(captureRoot, frames, spikes);
        var analysis = new ResultAnalysis
        {
            Summary = summary,
            Owners = owners,
            Functions = functions,
            Frames = frames,
            HeavyFrames = frames.OrderByDescending(x => x.ExclusiveMs).Take(20).ToList(),
            Spikes = spikes.Take(30).ToList(),
            FrameworkCandidates = candidates,
            FrameworkOwner = frameworkOwner,
            FrameworkFunctions = frameworkFunctions,
            SourceIntegrations = sourceIntegrations,
            FrameTime = frameTime
        };

        analysis.Findings = BuildFindings(analysis).Take(8).ToList();
        return analysis;
    }

    private static List<Finding> BuildFindings(ResultAnalysis a)
    {
        var findings = new List<Finding>();
        var top = a.Owners.FirstOrDefault();
        if (top is not null)
        {
            findings.Add(new(
                "Largest measured REDscript workload",
                $"{top.Owner}: {F(top.ExclusiveMsPerSec)} ms/s ({F(top.SharePct, 1)}% of measured REDscript work)",
                "This is the largest sustained observed call-edge contribution in this capture. It is not automatically a defect or a removal recommendation."));
        }

        if (a.FrameworkOwner is not null)
        {
            findings.Add(new(
                "G-RedRuntime infrastructure workload",
                $"G-RedRuntime: {F(a.FrameworkOwner.ExclusiveMsPerSec)} ms/s ({F(a.FrameworkOwner.SharePct, 1)}% of measured REDscript work; {F(a.FrameworkOwner.CallsPerSec, 0)} calls/s)",
                "G-RedRuntime carries shared scheduling, state, input and routing services for client mods. Treat this as infrastructure traffic and inspect its internal function breakdown before attributing the total as pure framework overhead."));
        }

        var topCalls = a.Owners.OrderByDescending(x => x.CallsPerSec).FirstOrDefault();
        if (topCalls is not null)
        {
            var explanation = IsInfrastructureOwner(topCalls.Owner)
                ? "The highest call volume belongs to shared infrastructure. High routing/service traffic can be meaningful, but call count alone does not mean the framework is expensive."
                : "High call volume can expose repeated polling, wrappers or routing even when each individual call is cheap.";
            findings.Add(new(
                "Highest observed call volume",
                $"{topCalls.Owner}: {N(topCalls.Calls)} calls ({F(topCalls.CallsPerSec, 0)} calls/s)",
                explanation));
        }

        var worstFrame = a.HeavyFrames.FirstOrDefault();
        if (worstFrame is not null)
        {
            findings.Add(new(
                "Heaviest measured script frame",
                $"Frame {worstFrame.FrameId}: {F(worstFrame.ExclusiveMs)} ms exclusive-instrumented REDscript work, {N(worstFrame.TotalCalls)} calls, {worstFrame.SpikeCount} recorded spikes",
                "This is the busiest game frame in the GRSP measurement domain. It is script-side observed work, not whole-frame CPU time."));
        }

        var worstSpike = a.Spikes.FirstOrDefault();
        if (worstSpike is not null)
        {
            findings.Add(new(
                "Largest recorded REDscript spike",
                $"{worstSpike.Owner} · {worstSpike.SourceFunction} → {worstSpike.Target} · {F(worstSpike.ExclusiveMs)} ms exclusive",
                "This is the largest individual call event retained by the public spike threshold in this capture."));
        }

        var candidate = a.FrameworkCandidates
            .FirstOrDefault(x => !IsInfrastructureOwner(x.Owner) &&
                                 !x.Signals.Equals("NO_STRONG_SIGNAL", StringComparison.OrdinalIgnoreCase));
        if (candidate is not null)
        {
            var integration = a.SourceIntegrations
                .FirstOrDefault(x => x.Owner.Equals(candidate.Owner, StringComparison.OrdinalIgnoreCase));

            var alreadyIntegrated = integration?.ReferencesGRedRuntime == true;
            var serviceText = alreadyIntegrated && integration!.Services.Count > 0
                ? " Detected services: " + string.Join(", ", integration.Services) + "."
                : "";

            findings.Add(new(
                alreadyIntegrated ? "Residual framework triage signal" : "Framework triage signal",
                $"{candidate.Owner}: {candidate.Signals.Replace("|", " · ")}",
                alreadyIntegrated
                    ? "The live source already references G-RedRuntime, so this is residual workload to inspect inside the current integration rather than a recommendation to adopt the framework again." + serviceText
                    : "These are heuristic signals from observed workload shape. They identify source worth inspecting for shared services, caching, eventing or wrapper consolidation; they do not prove that a rewrite is semantically safe."));
        }

        if (a.FrameTime is { Correlated: true } ft)
        {
            findings.Add(new(
                "Rendered-frame / REDscript association",
                $"{ft.SlowFramesHighScript} of {ft.SlowFrames} frames ≥33.3 ms occurred in the top 10% of measured script-load frames; {ft.SlowFramesWithSpike} also contained a recorded REDscript spike",
                $"Frame-sequence alignment is {ft.SyncQuality.ToLowerInvariant()} (duration-sequence Pearson {F(ft.AlignmentPearson, 3)}). Timing association is evidence, not proof that REDscript caused each slow frame."));

            var worst = ft.WorstFrames.FirstOrDefault();
            if (worst is not null && worst.FrameMs >= 100 && !worst.HighScript)
            {
                findings.Add(new(
                    "Large stall outside high REDscript load",
                    $"{F(worst.FrameMs)} ms rendered frame with {F(worst.ScriptMs)} ms measured REDscript work",
                    "The worst rendered stall is not proportionally explained by the measured REDscript layer. CPU/GPU active and other engine/system work remain separate evidence."));
            }
        }

        return findings;
    }

    private static CaptureSummary ParseSummary(Dictionary<string, string> r) => new()
    {
        Version = S(r, "version"),
        Scenario = S(r, "scenario"),
        StartUnixMs = L(r, "start_unix_ms"),
        StopUnixMs = L(r, "stop_unix_ms"),
        DurationMs = D(r, "duration_ms"),
        ObservedCalls = L(r, "observed_calls"),
        CallsPerSec = D(r, "calls_per_sec"),
        TotalExclusiveMs = D(r, "total_exclusive_instrumented_ms"),
        ExclusiveMsPerSec = D(r, "exclusive_ms_per_sec"),
        AverageScriptMsPerFrame = D(r, "average_script_ms_per_frame"),
        P95ScriptMsPerFrame = D(r, "p95_script_ms_per_frame"),
        P99ScriptMsPerFrame = D(r, "p99_script_ms_per_frame"),
        MaxScriptMsPerFrame = D(r, "max_script_ms_per_frame"),
        Frames = L(r, "frames"),
        FramesOver1Ms = L(r, "frames_script_over_1ms"),
        FramesOver5Ms = L(r, "frames_script_over_5ms"),
        FramesOver16Ms = L(r, "frames_script_over_16_67ms"),
        SpikeEvents = L(r, "spike_events"),
        TopOwner = S(r, "top_owner"),
        TopOwnerMsPerSec = D(r, "top_owner_exclusive_ms_per_sec"),
        FrameQuality = S(r, "frame_quality"),
        ShardMergeOk = B(r, "shard_merge_ok"),
        NamedStaticCalls = L(r, "named_static_calls"),
        UnresolvedStaticCalls = L(r, "unresolved_static_calls"),
        DroppedSpikes = L(r, "dropped_spikes"),
        DroppedHotPaths = L(r, "dropped_hot_paths"),
        TimelineBucketMs = D(r, "timeline_bucket_ms")
    };

    private static OwnerMetric ParseOwner(Dictionary<string, string> r) => new()
    {
        Rank = (int)L(r, "rank"),
        Owner = S(r, "owner"),
        Calls = L(r, "calls"),
        CallsPerSec = D(r, "calls_per_sec"),
        ExclusiveInstrumentedMs = D(r, "exclusive_instrumented_ms"),
        ExclusiveMsPerSec = D(r, "exclusive_ms_per_sec"),
        SharePct = D(r, "observed_exclusive_share_pct"),
        ActiveFramePct = D(r, "active_frame_pct"),
        MaxCallMs = D(r, "max_call_ms"),
        MaxFrameExclusiveMs = D(r, "max_frame_exclusive_ms"),
        SpikeCount = L(r, "spike_count"),
        MaxSpikeMs = D(r, "max_spike_ms"),
        WrapperCalls = L(r, "wrapper_calls"),
        WorkloadPattern = S(r, "workload_pattern"),
        AttributionNote = S(r, "attribution_note")
    };

    private static FunctionMetric ParseFunction(Dictionary<string, string> r)
    {
        var total = D(r, "exclusive_instrumented_ms");
        var duration = D(r, "calls_per_sec") > 0 && L(r, "outgoing_calls") > 0
            ? L(r, "outgoing_calls") / D(r, "calls_per_sec")
            : 0;
        return new FunctionMetric
        {
            Owner = S(r, "owner"),
            SourcePath = S(r, "source_path"),
            SourceLine = L(r, "source_line"),
            SourceFunction = S(r, "source_function"),
            OutgoingCalls = L(r, "outgoing_calls"),
            CallsPerSec = D(r, "calls_per_sec"),
            ExclusiveInstrumentedMs = total,
            ExclusiveMsPerSec = duration > 0 ? total / duration : 0,
            ActiveFramePct = D(r, "active_frame_pct"),
            CallsPerActiveFrame = D(r, "calls_per_active_frame"),
            MaxCallsPerFrame = L(r, "max_calls_per_frame"),
            MaxCallMs = D(r, "max_call_ms"),
            Over1Ms = L(r, "over_1ms"),
            Over5Ms = L(r, "over_5ms"),
            Over16Ms = L(r, "over_16_67ms")
        };
    }

    private static FrameMetric ParseFrame(Dictionary<string, string> r) => new()
    {
        FrameId = L(r, "frame_id"),
        StartMs = D(r, "frame_start_ms"),
        EndMs = D(r, "frame_end_ms"),
        StartUnixMs = D(r, "frame_start_unix_ms"),
        EndUnixMs = D(r, "frame_end_unix_ms"),
        FrameDurationMs = D(r, "frame_duration_ms"),
        TotalCalls = L(r, "total_calls"),
        UniqueOwners = L(r, "unique_owners"),
        MaxCallDepth = L(r, "max_call_depth"),
        InclusiveMs = D(r, "observed_inclusive_ms"),
        ExclusiveMs = D(r, "exclusive_instrumented_ms"),
        LargestCallMs = D(r, "largest_call_ms"),
        SpikeCount = L(r, "spike_count"),
        Partial = B(r, "partial")
    };

    private static SpikeMetric ParseSpike(Dictionary<string, string> r) => new()
    {
        CaptureMs = D(r, "capture_ms"),
        UnixMs = D(r, "unix_ms"),
        FrameId = L(r, "frame_id"),
        Owner = S(r, "owner"),
        SourcePath = S(r, "source_path"),
        SourceFunction = S(r, "source_function"),
        SourceLine = L(r, "source_line"),
        Target = S(r, "target"),
        DurationMs = D(r, "duration_ms"),
        ExclusiveMs = D(r, "exclusive_instrumented_ms")
    };

    private static FrameworkCandidate ParseFrameworkCandidate(Dictionary<string, string> r) => new()
    {
        Owner = S(r, "owner"),
        Calls = L(r, "calls"),
        CallsPerSec = D(r, "calls_per_sec"),
        ActiveFramePct = D(r, "active_frame_pct"),
        CallsPerActiveFrame = D(r, "calls_per_active_frame"),
        RootCalls = L(r, "root_calls"),
        AvgDescendantsPerRoot = D(r, "avg_descendants_per_root"),
        MaxDescendantsPerRoot = L(r, "max_descendants_per_root"),
        UniqueTargets = L(r, "unique_targets"),
        SharedTargets = L(r, "shared_targets_ge3owners"),
        FrameworkSharedTargets = L(r, "framework_shared_targets_ge3owners"),
        WrapperCalls = L(r, "wrapper_calls"),
        CrossModCallsOut = L(r, "cross_mod_calls_out"),
        CrossModCallsIn = L(r, "cross_mod_calls_in"),
        Threads = L(r, "threads"),
        Signals = S(r, "signals"),
        FrameworkPrimitives = S(r, "framework_primitives")
    };

    private static bool IsInfrastructureOwner(string owner) =>
        owner.Equals("G-RedRuntime", StringComparison.OrdinalIgnoreCase) ||
        owner.Equals("G-REDruntime", StringComparison.OrdinalIgnoreCase);

    private static string[] SplitPipe(string value) =>
        value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static object BuildDataIndex(string captureRoot)
    {
        var files = EnumerateDataFiles(captureRoot).ToList();

        static bool Starts(string value, string prefix) =>
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        var runtime = files.Where(x => Starts(x, "Data/Runtime/")).ToArray();
        var scheduler = files.Where(x => Starts(x, "Data/Scheduler/")).ToArray();
        var developer = files.Where(x => Starts(x, "Data/Developer/")).ToArray();
        var metadata = files.Where(x =>
                Starts(x, "Data/Metadata/") ||
                x.Equals("CaptureTitle.txt", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var frameTime = files.Where(x => Starts(x, "FrameTime/")).ToArray();

        var indexed = new HashSet<string>(
            runtime.Concat(scheduler).Concat(developer).Concat(metadata).Concat(frameTime),
            StringComparer.OrdinalIgnoreCase);

        return new
        {
            runtime,
            scheduler,
            developer,
            metadata,
            frameTime,
            other = files.Where(x => !indexed.Contains(x)).ToArray()
        };
    }

    private static IEnumerable<string> EnumerateDataFiles(string captureRoot) =>
        Directory.EnumerateFiles(captureRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(captureRoot, path).Replace('\\', '/'))
            .Where(x => !x.Equals(ReportFileName, StringComparison.OrdinalIgnoreCase) &&
                        !x.Equals(SummaryFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(200);

    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var rows = new List<Dictionary<string, string>>();
        if (!File.Exists(path))
            return rows;

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
            return rows;

        var headers = ParseCsvLine(headerLine.TrimStart('\uFEFF'))
            .Select(x => x.Trim())
            .ToArray();

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var values = ParseCsvLine(line);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length; i++)
                row[headers[i]] = i < values.Count ? values[i] : "";
            rows.Add(row);
        }

        return rows;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == ',' && !quoted)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        values.Add(current.ToString());
        return values;
    }

    private static string S(Dictionary<string, string> row, params string[] names)
    {
        foreach (var name in names)
            if (row.TryGetValue(name, out var value))
                return value?.Trim() ?? "";
        return "";
    }

    private static double D(Dictionary<string, string> row, params string[] names)
    {
        var text = S(row, names);
        if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
            return double.IsFinite(value) ? value : 0;
        return 0;
    }

    private static long L(Dictionary<string, string> row, params string[] names)
    {
        var text = S(row, names);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
            return (long)Math.Round(d);
        return 0;
    }

    private static bool B(Dictionary<string, string> row, params string[] names) =>
        bool.TryParse(S(row, names), out var value) && value;

    private static double Percent(long part, long whole) =>
        whole <= 0 ? 0 : part * 100.0 / whole;

    private static double Round(double value, int digits) =>
        double.IsFinite(value) ? Math.Round(value, digits) : 0;

    private static string F(double value, int digits = 3) =>
        double.IsFinite(value)
            ? value.ToString("N" + digits, CultureInfo.InvariantCulture)
            : "—";

    private static string N(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string H(string? value) =>
        WebUtility.HtmlEncode(value ?? "");

    private static string Href(string value) =>
        Uri.EscapeDataString(value).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

    private sealed class ResultAnalysis
    {
        public CaptureSummary Summary { get; init; } = new();
        public List<OwnerMetric> Owners { get; init; } = [];
        public List<FunctionMetric> Functions { get; init; } = [];
        public List<FrameMetric> Frames { get; init; } = [];
        public List<FrameMetric> HeavyFrames { get; init; } = [];
        public List<SpikeMetric> Spikes { get; init; } = [];
        public List<FrameworkCandidate> FrameworkCandidates { get; init; } = [];
        public OwnerMetric? FrameworkOwner { get; init; }
        public List<FunctionMetric> FrameworkFunctions { get; init; } = [];
        public List<SourceIntegrationMetric> SourceIntegrations { get; init; } = [];
        public FrameTimeAnalysis? FrameTime { get; init; }
        public List<Finding> Findings { get; set; } = [];
    }

    private sealed class CaptureSummary
    {
        public string Version { get; init; } = "";
        public string Scenario { get; init; } = "";
        public long StartUnixMs { get; init; }
        public long StopUnixMs { get; init; }
        public double DurationMs { get; init; }
        public long ObservedCalls { get; init; }
        public double CallsPerSec { get; init; }
        public double TotalExclusiveMs { get; init; }
        public double ExclusiveMsPerSec { get; init; }
        public double AverageScriptMsPerFrame { get; init; }
        public double P95ScriptMsPerFrame { get; init; }
        public double P99ScriptMsPerFrame { get; init; }
        public double MaxScriptMsPerFrame { get; init; }
        public long Frames { get; init; }
        public long FramesOver1Ms { get; init; }
        public long FramesOver5Ms { get; init; }
        public long FramesOver16Ms { get; init; }
        public long SpikeEvents { get; init; }
        public string TopOwner { get; init; } = "";
        public double TopOwnerMsPerSec { get; init; }
        public string FrameQuality { get; init; } = "";
        public bool ShardMergeOk { get; init; }
        public long NamedStaticCalls { get; init; }
        public long UnresolvedStaticCalls { get; init; }
        public long DroppedSpikes { get; init; }
        public long DroppedHotPaths { get; init; }
        public double TimelineBucketMs { get; init; }
    }

    private sealed class OwnerMetric
    {
        public int Rank { get; init; }
        public string Owner { get; init; } = "";
        public long Calls { get; init; }
        public double CallsPerSec { get; init; }
        public double ExclusiveInstrumentedMs { get; init; }
        public double ExclusiveMsPerSec { get; init; }
        public double SharePct { get; init; }
        public double ActiveFramePct { get; init; }
        public double MaxCallMs { get; init; }
        public double MaxFrameExclusiveMs { get; init; }
        public long SpikeCount { get; init; }
        public double MaxSpikeMs { get; init; }
        public long WrapperCalls { get; init; }
        public string WorkloadPattern { get; init; } = "";
        public string AttributionNote { get; init; } = "";
    }

    private sealed class FunctionMetric
    {
        public string Owner { get; init; } = "";
        public string SourcePath { get; init; } = "";
        public long SourceLine { get; init; }
        public string SourceFunction { get; init; } = "";
        public long OutgoingCalls { get; init; }
        public double CallsPerSec { get; init; }
        public double ExclusiveInstrumentedMs { get; init; }
        public double ExclusiveMsPerSec { get; init; }
        public double ActiveFramePct { get; init; }
        public double CallsPerActiveFrame { get; init; }
        public long MaxCallsPerFrame { get; init; }
        public double MaxCallMs { get; init; }
        public long Over1Ms { get; init; }
        public long Over5Ms { get; init; }
        public long Over16Ms { get; init; }
    }

    private sealed class FrameMetric
    {
        public long FrameId { get; init; }
        public double StartMs { get; init; }
        public double EndMs { get; init; }
        public double StartUnixMs { get; init; }
        public double EndUnixMs { get; init; }
        public double FrameDurationMs { get; init; }
        public long TotalCalls { get; init; }
        public long UniqueOwners { get; init; }
        public long MaxCallDepth { get; init; }
        public double InclusiveMs { get; init; }
        public double ExclusiveMs { get; init; }
        public double LargestCallMs { get; init; }
        public long SpikeCount { get; init; }
        public bool Partial { get; init; }
    }

    private sealed class SpikeMetric
    {
        public double CaptureMs { get; init; }
        public double UnixMs { get; init; }
        public long FrameId { get; init; }
        public string Owner { get; init; } = "";
        public string SourcePath { get; init; } = "";
        public string SourceFunction { get; init; } = "";
        public long SourceLine { get; init; }
        public string Target { get; init; } = "";
        public double DurationMs { get; init; }
        public double ExclusiveMs { get; init; }
    }

    private sealed class FrameworkCandidate
    {
        public string Owner { get; init; } = "";
        public long Calls { get; init; }
        public double CallsPerSec { get; init; }
        public double ActiveFramePct { get; init; }
        public double CallsPerActiveFrame { get; init; }
        public long RootCalls { get; init; }
        public double AvgDescendantsPerRoot { get; init; }
        public long MaxDescendantsPerRoot { get; init; }
        public long UniqueTargets { get; init; }
        public long SharedTargets { get; init; }
        public long FrameworkSharedTargets { get; init; }
        public long WrapperCalls { get; init; }
        public long CrossModCallsOut { get; init; }
        public long CrossModCallsIn { get; init; }
        public long Threads { get; init; }
        public string Signals { get; init; } = "";
        public string FrameworkPrimitives { get; init; } = "";
    }

    private sealed record Finding(string Title, string Evidence, string Explanation);
}
