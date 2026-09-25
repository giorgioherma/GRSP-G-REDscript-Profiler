using System.Reflection;
using System.Text;
using System.Text.Json;

namespace GRedscriptProfiler.Manager;

internal static partial class ResultReportService
{
    private static string BuildHtml(ResultAnalysis a, string captureRoot)
    {
        var sb = new StringBuilder(96_000);
        sb.Append("""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>G-REDscript Profiler — Capture Report</title>
<style>
:root{color-scheme:dark;--bg:#080d12;--panel:#101820;--panel2:#0b1219;--line:#233845;--line2:#315968;--text:#e8f3f6;--muted:#8da0a9;--accent:#36f4f4;--accent2:#5eff82;--magenta:#ff3fd7;--amber:#ffd840;--red:#ff5264}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 "Segoe UI",Arial,sans-serif}
.wrap{max-width:1260px;margin:auto;padding:24px}.hero{display:flex;justify-content:space-between;gap:20px;align-items:flex-start;margin-bottom:18px;padding:2px 0 16px;border-bottom:1px solid var(--line)}
.brandblock{display:flex;gap:14px;align-items:flex-start}.brandmark{width:62px;height:62px;object-fit:contain;flex:0 0 auto}.hero h1{margin:0 0 5px;font-size:29px;color:var(--accent);text-shadow:0 0 14px #36f4f426}.sub,.muted{color:var(--muted)}.scope{max-width:790px;color:var(--muted);margin-top:8px}
.badge{display:inline-block;border:1px solid var(--line2);background:var(--panel);padding:5px 9px;border-radius:999px;color:var(--accent2);font-size:12px;font-weight:700;box-shadow:0 0 12px #36f4f412}
.grid{display:grid;grid-template-columns:repeat(4,1fr);gap:10px}.card{background:var(--panel);border:1px solid var(--line);border-radius:9px;padding:14px}.metric{border-color:var(--line2)}.metric .v{color:var(--accent);text-shadow:0 0 12px #36f4f41f}.metric:nth-child(4n) .v{color:#ff73df;text-shadow:0 0 12px #ff3fd71f}
.k{font-size:12px;color:var(--muted);text-transform:uppercase;letter-spacing:.05em}.v{font-size:25px;font-weight:700;margin-top:4px}.s{font-size:12px;color:var(--muted);margin-top:3px}
.section{margin-top:22px}.section h2{font-size:19px;margin:0 0 10px;padding-bottom:6px;border-bottom:1px solid var(--line);color:#dffbff}.section h3{font-size:15px;margin:16px 0 8px;color:#dce7f1}
.findings{display:grid;grid-template-columns:repeat(2,1fr);gap:10px}.finding{background:var(--panel);border:1px solid var(--line);border-left:3px solid var(--accent);border-radius:8px;padding:13px}.finding:nth-child(even){border-left-color:var(--magenta)}
.finding h3{margin:0 0 5px;font-size:15px;color:#dffbff}.evidence{font-weight:650}.why{color:var(--muted);margin-top:5px}
.note{background:var(--panel2);border:1px solid var(--line);border-radius:8px;padding:11px 13px;color:var(--muted);margin:10px 0}.note b{color:var(--text)}
table{width:100%;border-collapse:collapse;background:var(--panel);border:1px solid var(--line);border-radius:8px;overflow:hidden}
th,td{padding:8px 10px;border-bottom:1px solid #1c2a33;text-align:left;vertical-align:top}th{font-size:12px;color:#bfeff4;background:#121d25;position:sticky;top:0}
td.num,th.num{text-align:right;font-variant-numeric:tabular-nums}.barwrap{width:130px;height:8px;background:#1b2a33;border-radius:99px;overflow:hidden;margin-top:5px}.bar{height:100%;background:linear-gradient(90deg,var(--accent),var(--magenta));border-radius:99px}
.mono{font-family:Consolas,"Courier New",monospace}.two{display:grid;grid-template-columns:1fr 1fr;gap:12px}.healthline{display:grid;grid-template-columns:180px 1fr;gap:8px;padding:4px 0;border-bottom:1px solid #1c2a33}.healthline:last-child{border-bottom:0}
details{background:var(--panel2);border:1px solid var(--line);border-radius:8px;padding:10px 12px;margin-top:10px}summary{cursor:pointer;font-weight:650;color:#dffbff}
.links a{color:var(--accent);text-decoration:none}.links a:hover{color:#ff73df}.footer{color:var(--muted);font-size:12px;margin:28px 0 8px;padding-top:10px;border-top:1px solid var(--line)}
.pill{display:inline-block;border:1px solid var(--line2);border-radius:999px;padding:2px 7px;margin:1px 3px 1px 0;font-size:11px;color:#cfeff3}.infra{color:#ff73df}.good{color:var(--accent2)}.warn{color:var(--amber)}.bad{color:var(--red)}
.chartbox{position:relative;background:var(--panel);border:1px solid var(--line2);border-radius:9px;padding:12px;margin-top:10px;box-shadow:0 0 16px #36f4f40d}.chartbox canvas{display:block;width:100%;height:330px;background:#080f15;border-radius:6px}.toolbar{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin-bottom:10px}.toolbar button,.toolbar select{background:#121d25;color:var(--text);border:1px solid var(--line2);border-radius:6px;padding:6px 9px}.toolbar button:hover,.toolbar select:hover{border-color:var(--accent)}.zoomhint{margin-left:auto;color:var(--muted);font-size:12px}.legend{display:flex;gap:14px;flex-wrap:wrap;color:var(--muted);font-size:12px;margin-top:8px}.sw{display:inline-block;width:12px;height:3px;vertical-align:middle;margin-right:5px}.sw.ft{background:#36f4f4}.sw.cpu{background:#ffd840}.sw.gpu{background:#5eff82}.sw.script{background:#ff55d7}.charttip{position:absolute;display:none;pointer-events:none;z-index:4;background:#070c11;border:1px solid var(--line2);border-radius:6px;padding:7px 9px;white-space:pre-line;font-size:12px;box-shadow:0 6px 24px #0009}
@media(max-width:900px){.grid{grid-template-columns:repeat(2,1fr)}.findings,.two{grid-template-columns:1fr}.wrap{padding:14px}}
@media(max-width:520px){.grid{grid-template-columns:1fr}.hero{display:block}}
</style>
</head>
<body><div class="wrap">
""");

        var logo = GetEmbeddedLogoDataUri();
        sb.Append("<div class=\"hero\"><div class=\"brandblock\">");
        if (!string.IsNullOrWhiteSpace(logo))
            sb.Append("<img class=\"brandmark\" src=\"").Append(logo).Append("\" alt=\"G-RED\">");
        sb.Append("<div><h1>G-REDscript Profiler</h1><div class=\"sub\">")
            .Append(a.FrameTime is null ? "REDscript capture report" : "REDscript + CapFrameX capture report")
            .Append("</div><div class=\"scope\">Observed REDscript call-edge work during this capture: sustained owner cost, call volume, hot functions, script-heavy frames, recorded spikes and framework/service signals.");
        if (a.FrameTime is not null)
            sb.Append(" CapFrameX adds rendered frametime and CPU/GPU active evidence; when the frame sequences match strongly, GRSP aligns them frame-for-frame.");
        sb.Append("</div></div></div><div><span class=\"badge\">")
            .Append(a.FrameTime is null ? "REDSCRIPT MEASUREMENT" : "REDSCRIPT + CAPFRAMEX")
            .Append("</span></div></div>");

        sb.Append("<div class=\"grid\">");
        MetricCard(sb, "Capture", F(a.Summary.DurationMs / 1000.0, 1) + " s", H(a.Summary.Scenario) + " · " + N(a.Summary.Frames) + " script frames");
        MetricCard(sb, "Measured REDscript work", F(a.Summary.ExclusiveMsPerSec) + " ms/s", F(a.Summary.AverageScriptMsPerFrame) + " ms average / game frame");
        MetricCard(sb, "Observed calls", N(a.Summary.ObservedCalls), F(a.Summary.CallsPerSec, 0) + " calls/s");
        var top = a.Owners.FirstOrDefault();
        MetricCard(sb, "Largest sustained owner", top is null ? "—" : H(top.Owner), top is null ? "No owner table" : F(top.ExclusiveMsPerSec) + " ms/s · " + F(top.SharePct, 1) + "%");
        sb.Append("</div>");

        AppendFindings(sb, a);
        if (a.FrameTime is not null)
            AppendFrameTime(sb, a.FrameTime);
        AppendHealth(sb, a);
        AppendOwnerWorkload(sb, a);
        AppendCallVolume(sb, a);
        AppendFunctions(sb, a);
        AppendHeavyFrames(sb, a);
        AppendSpikes(sb, a);
        AppendFramework(sb, a);
        AppendDataLinks(sb, captureRoot);

        sb.Append("<div class=\"footer\">GRSP measures observed REDscript call-edge work. Exclusive-instrumented time is not whole CPU time, GPU time or guaranteed full VM self-time. Wrapper boundaries can contain uninstrumented/native/base work. CapFrameX overlap is timing evidence, not an automatic causation verdict or a subtraction budget.</div>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static void AppendFindings(StringBuilder sb, ResultAnalysis a)
    {
        sb.Append("<div class=\"section\"><h2>What stands out</h2>");
        if (a.Findings.Count == 0)
        {
            sb.Append("<div class=\"note\">No strong summary findings were derived, but the full native measurement set remains available below.</div>");
        }
        else
        {
            sb.Append("<div class=\"findings\">");
            foreach (var finding in a.Findings)
            {
                sb.Append("<div class=\"finding\"><h3>").Append(H(finding.Title))
                    .Append("</h3><div class=\"evidence\">").Append(H(finding.Evidence))
                    .Append("</div><div class=\"why\">").Append(H(finding.Explanation))
                    .Append("</div></div>");
            }
            sb.Append("</div>");
        }
        sb.Append("</div>");
    }

    private static void AppendHealth(StringBuilder sb, ResultAnalysis a)
    {
        var ok = a.Summary.ShardMergeOk &&
                 a.Summary.UnresolvedStaticCalls == 0 &&
                 a.Summary.DroppedSpikes == 0 &&
                 a.Summary.DroppedHotPaths == 0 &&
                 a.Summary.FrameQuality.Equals("GOOD", StringComparison.OrdinalIgnoreCase);

        sb.Append("<div class=\"section\"><h2>Capture health &amp; trust gates</h2><div class=\"two\"><div class=\"card\">")
            .Append("<div class=\"healthline\"><span class=\"muted\">Overall</span><span class=\"").Append(ok ? "good" : "warn").Append("\"><b>").Append(ok ? "GOOD" : "CHECK").Append("</b></span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Frame quality</span><span>").Append(H(a.Summary.FrameQuality)).Append("</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Shard merge</span><span>").Append(a.Summary.ShardMergeOk ? "OK" : "NOT OK").Append("</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Unresolved static calls</span><span>").Append(N(a.Summary.UnresolvedStaticCalls)).Append("</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Dropped spike rows</span><span>").Append(N(a.Summary.DroppedSpikes)).Append("</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Dropped hot paths</span><span>").Append(N(a.Summary.DroppedHotPaths)).Append("</span></div></div>");

        sb.Append("<div class=\"card\"><h3>Script-frame distribution</h3>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Average</span><span>").Append(F(a.Summary.AverageScriptMsPerFrame)).Append(" ms</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">P95 / P99</span><span>").Append(F(a.Summary.P95ScriptMsPerFrame)).Append(" / ").Append(F(a.Summary.P99ScriptMsPerFrame)).Append(" ms</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Maximum</span><span>").Append(F(a.Summary.MaxScriptMsPerFrame)).Append(" ms</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Frames &gt; 5 ms script</span><span>").Append(N(a.Summary.FramesOver5Ms)).Append("</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Frames &gt; 16.67 ms script</span><span>").Append(N(a.Summary.FramesOver16Ms)).Append("</span></div>")
            .Append("</div></div>")
            .Append("<div class=\"note\"><b>Trust the measurement domain, not an interpretation shortcut.</b> A high owner/function row means GRSP observed more instrumented contribution there during this workload. It does not by itself mean the mod is badly written, removable, or semantically safe to rewrite.</div></div>");
    }

    private static void AppendOwnerWorkload(StringBuilder sb, ResultAnalysis a)
    {
        if (a.Owners.Count == 0)
            return;

        sb.Append("<div class=\"section\"><h2>Sustained REDscript workload by owner</h2>")
            .Append("<div class=\"note\">Ranked by measured exclusive-instrumented ms/s. <span class=\"infra\">G-RedRuntime</span> is shared infrastructure and is labeled separately from ordinary mod owners.</div>")
            .Append("<table><thead><tr><th>Owner</th><th class=\"num\">ms/s</th><th class=\"num\">Share</th><th class=\"num\">Calls/s</th><th class=\"num\">Active frames</th><th class=\"num\">Max frame</th><th>Pattern</th><th>Weight</th></tr></thead><tbody>");

        var max = Math.Max(0.000001, a.Owners.Take(15).Max(x => x.ExclusiveMsPerSec));
        foreach (var x in a.Owners.Take(15))
        {
            var width = Math.Clamp(x.ExclusiveMsPerSec / max * 100.0, 0, 100);
            sb.Append("<tr><td><b class=\"").Append(IsInfrastructureOwner(x.Owner) ? "infra" : "").Append("\">").Append(H(x.Owner)).Append("</b>");
            if (IsInfrastructureOwner(x.Owner))
                sb.Append(" <span class=\"muted\">(infrastructure)</span>");
            if (!string.IsNullOrWhiteSpace(x.AttributionNote))
                sb.Append("<div class=\"muted\">").Append(H(x.AttributionNote.Replace('_', ' '))).Append("</div>");
            sb.Append("</td><td class=\"num\">").Append(F(x.ExclusiveMsPerSec))
                .Append("</td><td class=\"num\">").Append(F(x.SharePct, 1)).Append("%")
                .Append("</td><td class=\"num\">").Append(F(x.CallsPerSec, 0))
                .Append("</td><td class=\"num\">").Append(F(x.ActiveFramePct, 1)).Append("%")
                .Append("</td><td class=\"num\">").Append(F(x.MaxFrameExclusiveMs)).Append(" ms")
                .Append("</td><td>").Append(H(x.WorkloadPattern))
                .Append("</td><td><div class=\"barwrap\"><div class=\"bar\" style=\"width:").Append(F(width, 1)).Append("%\"></div></div></td></tr>");
        }
        sb.Append("</tbody></table></div>");
    }

    private static void AppendCallVolume(StringBuilder sb, ResultAnalysis a)
    {
        if (a.Owners.Count == 0)
            return;

        sb.Append("<div class=\"section\"><h2>Call volume</h2>")
            .Append("<div class=\"note\">A separate view from time cost. High call volume can reveal repeated polling, wrapper/routing traffic or inner-loop work even when individual calls are cheap.</div>")
            .Append("<table><thead><tr><th>Owner</th><th class=\"num\">Calls</th><th class=\"num\">Call share</th><th class=\"num\">Calls/s</th><th class=\"num\">Measured ms/s</th><th class=\"num\">Wrapper calls</th></tr></thead><tbody>");

        foreach (var x in a.Owners.OrderByDescending(x => x.CallsPerSec).Take(15))
        {
            sb.Append("<tr><td><b class=\"").Append(IsInfrastructureOwner(x.Owner) ? "infra" : "").Append("\">").Append(H(x.Owner)).Append("</b></td>")
                .Append("<td class=\"num\">").Append(N(x.Calls))
                .Append("</td><td class=\"num\">").Append(F(Percent(x.Calls, a.Summary.ObservedCalls), 1)).Append("%")
                .Append("</td><td class=\"num\">").Append(F(x.CallsPerSec, 0))
                .Append("</td><td class=\"num\">").Append(F(x.ExclusiveMsPerSec))
                .Append("</td><td class=\"num\">").Append(N(x.WrapperCalls)).Append("</td></tr>");
        }
        sb.Append("</tbody></table></div>");
    }

    private static void AppendFunctions(StringBuilder sb, ResultAnalysis a)
    {
        if (a.Functions.Count == 0)
            return;

        sb.Append("<div class=\"section\"><h2>Hot REDscript functions</h2>")
            .Append("<div class=\"note\">Function rows show where observed instrumented time accumulates. Wrapper functions require attribution caution because downstream/base/native work may be inside the observed boundary.</div>")
            .Append("<table><thead><tr><th>Owner</th><th>Function</th><th>Source</th><th class=\"num\">ms/s</th><th class=\"num\">Calls/s</th><th class=\"num\">Active frames</th><th class=\"num\">Max call</th></tr></thead><tbody>");

        foreach (var x in a.Functions.Take(20))
        {
            sb.Append("<tr><td><b class=\"").Append(IsInfrastructureOwner(x.Owner) ? "infra" : "").Append("\">").Append(H(x.Owner)).Append("</b></td>")
                .Append("<td><span class=\"mono\">").Append(H(x.SourceFunction)).Append("</span></td>")
                .Append("<td><span class=\"mono muted\">").Append(H(ShortSourcePath(x.SourcePath))).Append(":").Append(x.SourceLine).Append("</span></td>")
                .Append("<td class=\"num\">").Append(F(x.ExclusiveMsPerSec))
                .Append("</td><td class=\"num\">").Append(F(x.CallsPerSec, 0))
                .Append("</td><td class=\"num\">").Append(F(x.ActiveFramePct, 1)).Append("%")
                .Append("</td><td class=\"num\">").Append(F(x.MaxCallMs)).Append(" ms</td></tr>");
        }
        sb.Append("</tbody></table></div>");
    }

    private static void AppendHeavyFrames(StringBuilder sb, ResultAnalysis a)
    {
        if (a.HeavyFrames.Count == 0)
            return;

        sb.Append("<div class=\"section\"><h2>Heaviest measured script frames</h2>")
            .Append("<div class=\"note\">These are game frames ranked by GRSP's measured exclusive-instrumented script work, not by rendered frametime.</div>")
            .Append("<table><thead><tr><th class=\"num\">Frame</th><th class=\"num\">Capture time</th><th class=\"num\">Script work</th><th class=\"num\">Game-frame duration</th><th class=\"num\">Calls</th><th class=\"num\">Owners</th><th class=\"num\">Largest call</th><th class=\"num\">Spikes</th></tr></thead><tbody>");

        foreach (var x in a.HeavyFrames.Take(15))
        {
            sb.Append("<tr><td class=\"num\">").Append(x.FrameId)
                .Append("</td><td class=\"num\">").Append(F(x.StartMs / 1000.0, 3)).Append(" s")
                .Append("</td><td class=\"num\"><b>").Append(F(x.ExclusiveMs)).Append(" ms</b>")
                .Append("</td><td class=\"num\">").Append(F(x.FrameDurationMs)).Append(" ms")
                .Append("</td><td class=\"num\">").Append(N(x.TotalCalls))
                .Append("</td><td class=\"num\">").Append(N(x.UniqueOwners))
                .Append("</td><td class=\"num\">").Append(F(x.LargestCallMs)).Append(" ms")
                .Append("</td><td class=\"num\">").Append(N(x.SpikeCount)).Append("</td></tr>");
        }
        sb.Append("</tbody></table></div>");
    }

    private static void AppendSpikes(StringBuilder sb, ResultAnalysis a)
    {
        if (a.Spikes.Count == 0)
            return;

        sb.Append("<div class=\"section\"><h2>Recorded REDscript call spikes</h2>")
            .Append("<table><thead><tr><th>Owner</th><th>Call</th><th class=\"num\">Exclusive</th><th class=\"num\">Observed duration</th><th class=\"num\">Frame</th><th class=\"num\">Capture time</th></tr></thead><tbody>");
        foreach (var x in a.Spikes.Take(20))
        {
            sb.Append("<tr><td><b>").Append(H(x.Owner)).Append("</b></td><td><span class=\"mono\">")
                .Append(H(x.SourceFunction)).Append(" → ").Append(H(x.Target)).Append("</span>")
                .Append("<div class=\"muted mono\">").Append(H(ShortSourcePath(x.SourcePath))).Append(":").Append(x.SourceLine).Append("</div></td>")
                .Append("<td class=\"num\"><b>").Append(F(x.ExclusiveMs)).Append(" ms</b></td>")
                .Append("<td class=\"num\">").Append(F(x.DurationMs)).Append(" ms</td>")
                .Append("<td class=\"num\">").Append(x.FrameId).Append("</td>")
                .Append("<td class=\"num\">").Append(F(x.CaptureMs / 1000.0, 3)).Append(" s</td></tr>");
        }
        sb.Append("</tbody></table></div>");
    }

    private static void AppendFramework(StringBuilder sb, ResultAnalysis a)
    {
        if (a.FrameworkOwner is null && a.FrameworkCandidates.Count == 0)
            return;

        sb.Append("<div class=\"section\"><h2>G-RedRuntime &amp; framework opportunities</h2>")
            .Append("<div class=\"note\"><b>G-RedRuntime is shared infrastructure.</b> Its scheduler, state cache, context, input, event and hook services can legitimately carry traffic on behalf of client mods. Framework-candidate signals below are heuristic triage hints, never automatic rewrite instructions. When the original live REDscript source is still available, this report also scans it read-only so an already integrated mod is not presented as if it still needs to adopt G-RedRuntime.</div>");

        if (a.FrameworkOwner is not null)
        {
            sb.Append("<div class=\"grid\">");
            MetricCard(sb, "Framework work", F(a.FrameworkOwner.ExclusiveMsPerSec) + " ms/s", F(a.FrameworkOwner.SharePct, 1) + "% of measured REDscript work");
            MetricCard(sb, "Framework calls", F(a.FrameworkOwner.CallsPerSec, 0) + "/s", N(a.FrameworkOwner.Calls) + " observed calls");
            var fc = a.FrameworkCandidates.FirstOrDefault(x => IsInfrastructureOwner(x.Owner));
            MetricCard(sb, "Cross-mod traffic", fc is null ? "—" : N(fc.CrossModCallsOut + fc.CrossModCallsIn), fc is null ? "No framework-candidate row" : N(fc.Threads) + " observed threads");
            MetricCard(sb, "Shared query targets", fc is null ? "—" : N(fc.FrameworkSharedTargets), "Targets shared by ≥3 owners");
            sb.Append("</div>");
        }

        if (a.FrameworkFunctions.Count > 0)
        {
            sb.Append("<h3>Largest G-RedRuntime internals</h3><table><thead><tr><th>Function</th><th class=\"num\">ms/s</th><th class=\"num\">Calls/s</th><th class=\"num\">Active frames</th><th class=\"num\">Max call</th></tr></thead><tbody>");
            foreach (var x in a.FrameworkFunctions.Take(12))
            {
                sb.Append("<tr><td><span class=\"mono infra\">").Append(H(x.SourceFunction)).Append("</span></td>")
                    .Append("<td class=\"num\">").Append(F(x.ExclusiveMsPerSec))
                    .Append("</td><td class=\"num\">").Append(F(x.CallsPerSec, 0))
                    .Append("</td><td class=\"num\">").Append(F(x.ActiveFramePct, 1)).Append("%")
                    .Append("</td><td class=\"num\">").Append(F(x.MaxCallMs)).Append(" ms</td></tr>");
            }
            sb.Append("</tbody></table>");
        }

        if (a.FrameworkCandidates.Count > 0)
        {
            var cost = a.Owners.ToDictionary(x => x.Owner, x => x.ExclusiveMsPerSec, StringComparer.OrdinalIgnoreCase);
            var integration = a.SourceIntegrations.ToDictionary(x => x.Owner, StringComparer.OrdinalIgnoreCase);
            sb.Append("<h3>Framework triage candidates</h3><table><thead><tr><th>Owner</th><th class=\"num\">Measured ms/s</th><th class=\"num\">Calls/s</th><th class=\"num\">Active frames</th><th>Current live integration</th><th>Signals</th><th>Possible framework primitive</th></tr></thead><tbody>");
            foreach (var x in a.FrameworkCandidates.Take(20))
            {
                cost.TryGetValue(x.Owner, out var ownerMs);
                integration.TryGetValue(x.Owner, out var source);

                sb.Append("<tr><td><b class=\"").Append(IsInfrastructureOwner(x.Owner) ? "infra" : "").Append("\">").Append(H(x.Owner)).Append("</b></td>")
                    .Append("<td class=\"num\">").Append(F(ownerMs))
                    .Append("</td><td class=\"num\">").Append(F(x.CallsPerSec, 0))
                    .Append("</td><td class=\"num\">").Append(F(x.ActiveFramePct, 1)).Append("%</td><td>");

                if (source is null || !source.SourceAvailable)
                {
                    sb.Append("<span class=\"muted\">source unavailable</span>");
                }
                else if (IsInfrastructureOwner(x.Owner))
                {
                    sb.Append("<span class=\"good\"><b>G-RedRuntime core</b></span>");
                    if (!string.IsNullOrWhiteSpace(source.RuntimeVersion))
                        sb.Append("<div class=\"muted\">").Append(H(source.RuntimeVersion)).Append("</div>");
                }
                else if (source.ReferencesGRedRuntime)
                {
                    sb.Append("<span class=\"good\"><b>already integrated</b></span>");
                    if (source.Services.Count > 0)
                    {
                        sb.Append("<div>");
                        foreach (var service in source.Services)
                            sb.Append("<span class=\"pill\">").Append(H(service)).Append("</span>");
                        sb.Append("</div>");
                    }
                }
                else
                {
                    sb.Append("<span class=\"muted\">no G-RedRuntime reference detected</span>");
                }

                sb.Append("</td><td>");
                foreach (var signal in SplitPipe(x.Signals))
                    sb.Append("<span class=\"pill\">").Append(H(signal.Replace('_', ' '))).Append("</span>");
                sb.Append("</td><td>");
                foreach (var primitive in SplitPipe(x.FrameworkPrimitives))
                    sb.Append("<span class=\"pill\">").Append(H(primitive.Replace('_', ' '))).Append("</span>");
                sb.Append("</td></tr>");
            }
            sb.Append("</tbody></table>")
              .Append("<div class=\"note\">Source integration detection is read-only and best-effort. It recognizes the G-RedRuntime APIs actually present in the current source tree, including Scheduler, StateCache, ContextService, InputHub, EventBus, HookBus, DirtyFlags and the GRedHotpathCache pattern. A remaining profiler signal on an <b>already integrated</b> mod means inspect the residual hot path; it does not mean the framework integration failed.</div>");
        }

        sb.Append("</div>");
    }

    private static void AppendFrameTime(StringBuilder sb, FrameTimeAnalysis ft)
    {
        sb.Append("<div class=\"section\"><h2>Frametime &amp; REDscript overlap</h2><div class=\"grid\">");
        MetricCard(sb, "Average", F(ft.AverageFps, 1) + " FPS", F(ft.MeanFrameMs) + " ms mean · " + N(ft.FrameCount) + " frames");
        MetricCard(sb, "P95 / P99", F(ft.P95FrameMs) + " / " + F(ft.P99FrameMs) + " ms", "Median " + F(ft.MedianFrameMs) + " ms");
        MetricCard(sb, "Worst rendered frame", F(ft.MaxFrameMs) + " ms", "≥50 ms " + N(ft.FramesOver50Ms) + " · ≥100 ms " + N(ft.FramesOver100Ms));
        MetricCard(sb, "Frames ≥33.3 ms", N(ft.FramesOver33Ms), "≥25 ms " + N(ft.FramesOver25Ms));
        sb.Append("</div>");

        var syncClass = ft.SyncQuality == "GOOD" ? "good" : ft.SyncQuality == "COARSE" ? "warn" : "bad";
        sb.Append("<div class=\"two\"><div class=\"card\"><h3>CapFrameX capture</h3>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Source</span><span>").Append(H(ft.SourceFile)).Append("</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Version</span><span>").Append(H(ft.AppVersion)).Append("</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">CPU</span><span>").Append(H(ft.Processor)).Append("</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">GPU</span><span>").Append(H(ft.Gpu)).Append("</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">CPU active</span><span>").Append(F(ft.MeanCpuActiveMs)).Append(" ms mean · ").Append(F(ft.P95CpuActiveMs)).Append(" ms P95</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">GPU active</span><span>").Append(F(ft.MeanGpuActiveMs)).Append(" ms mean · ").Append(F(ft.P95GpuActiveMs)).Append(" ms P95</span></div></div>")
            .Append("<div class=\"card\"><h3>Synchronization</h3>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Status</span><span class=\"").Append(syncClass).Append("\"><b>").Append(H(ft.SyncQuality)).Append("</b></span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Frame lag</span><span>").Append(ft.ExactFrameAlignment ? H(FormatLag(ft.FrameLag)) : "—").Append("</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Duration sequence</span><span>Pearson ").Append(F(ft.AlignmentPearson, 3)).Append(" · log ").Append(F(ft.AlignmentLogPearson, 3)).Append("</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Start-offset MAD</span><span>").Append(ft.ExactFrameAlignment ? F(ft.StartOffsetMadMs, 3) + " ms" : "—").Append("</span></div>")
            .Append("<div class=\"healthline\"><span class=\"muted\">Method</span><span>").Append(H(ft.AlignmentMethod)).Append("</span></div></div></div>");

        if (!ft.Correlated)
        {
            sb.Append("<div class=\"note\"><b>CapFrameX frametime statistics are valid, but frame-by-frame REDscript correlation is disabled.</b> The frame sequence was not unique/strong enough for safe exact alignment. Raw companion data remains preserved under <span class=\"mono\">FrameTime/</span>.</div></div>");
            return;
        }

        sb.Append("<div class=\"grid\">");
        MetricCard(sb, "Slow frames in high script", ft.SlowFramesHighScript + " / " + ft.SlowFrames, "High script = top 10% of aligned GRSP frames");
        MetricCard(sb, "Slow frames with spike", N(ft.SlowFramesWithSpike), "Frames ≥33.3 ms containing a recorded REDscript spike");
        MetricCard(sb, "Script-normal slow frames", N(ft.SlowFramesScriptNormal), "Slow rendered frames below the top-10% script threshold");
        MetricCard(sb, "Frame correlation", "ρ " + F(ft.SpearmanFrameCorrelation, 3), "Pearson " + F(ft.PearsonFrameCorrelation, 3));
        sb.Append("</div>");

        sb.Append("<div class=\"note\"><b>High-script frames had rendered frames ≥33.3 ms in ")
            .Append(F(ft.HighScriptSlowRatePct, 2)).Append("% of aligned frames versus ")
            .Append(F(ft.NormalScriptSlowRatePct, 2)).Append("% for script-normal frames.</b> ")
            .Append("This is a timing association inside this capture. It does not prove that REDscript caused each stall; CPU active, GPU active, native engine work and system stalls remain separate layers.</div>");

        var timelineJson = JsonSerializer.Serialize(
            ft.Timeline.Select(x => new
            {
                t = Round(x.StartMs, 3),
                ft = Round(x.FrameMs, 4),
                cpu = Round(x.CpuActiveMs, 4),
                gpu = Round(x.GpuActiveMs, 4),
                script = Round(x.ScriptMs, 4),
                calls = x.ScriptCalls,
                frame = x.GrspFrameId,
                spikes = x.SpikeCount,
                spikeOwner = x.LargestSpikeOwner,
                spikeMs = Round(x.LargestSpikeMs, 4),
                high = x.HighScript
            }),
            JsonOptions);
        var worstTime = ft.WorstFrames.FirstOrDefault()?.StartMs ?? 0;

        sb.Append("<div class=\"chartbox\"><div class=\"toolbar\">")
            .Append("<button onclick=\"grspFtFull()\">Full capture</button>")
            .Append("<button onclick=\"grspFtWorst()\">Around worst frame</button>")
            .Append("<label>Scale <select id=\"grspFtScale\" onchange=\"grspFtDraw()\"><option value=\"auto\">Auto</option><option value=\"50\">0–50 ms</option><option value=\"100\">0–100 ms</option><option value=\"250\">0–250 ms</option><option value=\"500\">0–500 ms</option></select></label>")
            .Append("<span class=\"zoomhint\">Shift + mouse wheel over graph: zoom</span></div>")
            .Append("<canvas id=\"grspFtChart\"></canvas><div id=\"grspFtTip\" class=\"charttip\"></div>")
            .Append("<div class=\"legend\"><span><i class=\"sw ft\"></i>Frametime</span><span><i class=\"sw cpu\"></i>CPU active</span><span><i class=\"sw gpu\"></i>GPU active</span><span><i class=\"sw script\"></i>Measured REDscript work / game frame</span></div></div>")
            .Append("<script>window.__grspFt=").Append(timelineJson)
            .Append(";window.__grspFtWorst=").Append(JsonSerializer.Serialize(worstTime)).Append(";</script>");

        sb.Append("""
<script>
(function(){
  const data=window.__grspFt||[],canvas=document.getElementById('grspFtChart'),tip=document.getElementById('grspFtTip');
  if(!canvas||!data.length)return;
  let xmin=data[0].t,xmax=data[data.length-1].t+data[data.length-1].ft;
  function visible(){return data.filter(p=>p.t+p.ft>=xmin&&p.t<=xmax);}
  function resize(){const dpr=window.devicePixelRatio||1,w=Math.max(320,canvas.clientWidth),h=330;canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);const ctx=canvas.getContext('2d');ctx.setTransform(dpr,0,0,dpr,0,0);grspFtDraw();}
  window.grspFtFull=function(){xmin=data[0].t;xmax=data[data.length-1].t+data[data.length-1].ft;grspFtDraw();};
  window.grspFtWorst=function(){const w=window.__grspFtWorst||0;xmin=Math.max(data[0].t,w-5000);xmax=Math.min(data[data.length-1].t+data[data.length-1].ft,w+5000);grspFtDraw();};
  window.grspFtDraw=function(){
    const ctx=canvas.getContext('2d'),w=canvas.clientWidth,h=330,padL=48,padR=18,padT=12,padB=30,v=visible();ctx.clearRect(0,0,w,h);if(!v.length)return;
    const sel=document.getElementById('grspFtScale');let ymax=sel&&sel.value!=='auto'?Number(sel.value):0;
    if(!ymax){for(const p of v)ymax=Math.max(ymax,p.ft,p.cpu,p.gpu,p.script);ymax=Math.max(20,Math.ceil(ymax/10)*10);}
    const span=Math.max(1,xmax-xmin),px=t=>padL+(t-xmin)/span*(w-padL-padR),py=y=>padT+(1-Math.min(y,ymax)/ymax)*(h-padT-padB);
    ctx.strokeStyle='#20343f';ctx.lineWidth=1;ctx.fillStyle='#8da0a9';ctx.font='11px Segoe UI';
    for(let i=0;i<=5;i++){const y=ymax*i/5,yy=py(y);ctx.beginPath();ctx.moveTo(padL,yy);ctx.lineTo(w-padR,yy);ctx.stroke();ctx.fillText(y.toFixed(0)+' ms',4,yy+4);}
    function line(key,color,width){ctx.strokeStyle=color;ctx.lineWidth=width;ctx.beginPath();let started=false;for(const p of v){const val=p[key];if(val<0)continue;const x=px(p.t),y=py(val);if(!started){ctx.moveTo(x,y);started=true;}else ctx.lineTo(x,y);}ctx.stroke();}
    line('cpu','#ffd840',1);line('gpu','#5eff82',1);line('script','#ff55d7',1.4);line('ft','#36f4f4',2);
    ctx.fillStyle='#8da0a9';ctx.textAlign='left';ctx.fillText((xmin/1000).toFixed(1)+' s',padL,h-8);ctx.textAlign='right';ctx.fillText((xmax/1000).toFixed(1)+' s',w-padR,h-8);ctx.textAlign='left';
  };
  canvas.addEventListener('mousemove',ev=>{
    const r=canvas.getBoundingClientRect(),padL=48,padR=18,ratio=Math.max(0,Math.min(1,(ev.clientX-r.left-padL)/(r.width-padL-padR))),t=xmin+ratio*(xmax-xmin);
    let lo=0,hi=data.length-1;while(lo<hi){const m=(lo+hi)>>1;if(data[m].t<t)lo=m+1;else hi=m;}const p=data[Math.max(0,lo-1)];if(!p){tip.style.display='none';return;}
    tip.textContent=(p.t/1000).toFixed(3)+' s · GRSP frame '+p.frame+'\nFrametime: '+p.ft.toFixed(2)+' ms\nCPU active: '+p.cpu.toFixed(2)+' ms\nGPU active: '+p.gpu.toFixed(2)+' ms\nREDscript: '+p.script.toFixed(2)+' ms · '+p.calls+' calls\nRecorded spikes: '+p.spikes+(p.spikeOwner?'\nLargest spike: '+p.spikeOwner+' · '+p.spikeMs.toFixed(2)+' ms':'')+(p.high?'\nTop-10% script frame':'');
    tip.style.display='block';tip.style.left=Math.min(r.width-250,Math.max(8,ev.clientX-r.left+12))+'px';tip.style.top=Math.max(8,ev.clientY-r.top-125)+'px';
  });
  canvas.addEventListener('wheel',ev=>{
    if(!ev.shiftKey)return;ev.preventDefault();
    const fullMin=data[0].t,fullMax=data[data.length-1].t+data[data.length-1].ft,fullSpan=fullMax-fullMin,current=xmax-xmin;if(fullSpan<=0)return;
    const rect=canvas.getBoundingClientRect(),padL=48,padR=18,ratio=Math.max(0,Math.min(1,(ev.clientX-rect.left-padL)/(rect.width-padL-padR))),anchor=xmin+ratio*current,factor=ev.deltaY<0?.78:1.28,minSpan=Math.min(fullSpan,500),nextSpan=Math.max(minSpan,Math.min(fullSpan,current*factor));
    let nextMin=anchor-ratio*nextSpan,nextMax=nextMin+nextSpan;if(nextMin<fullMin){nextMin=fullMin;nextMax=fullMin+nextSpan;}if(nextMax>fullMax){nextMax=fullMax;nextMin=fullMax-nextSpan;}xmin=nextMin;xmax=nextMax;grspFtDraw();
  },{passive:false});
  canvas.addEventListener('mouseleave',()=>tip.style.display='none');window.addEventListener('resize',resize);requestAnimationFrame(resize);
})();
</script>
""");

        if (ft.WorstFrames.Count > 0)
        {
            sb.Append("<h3>Worst rendered-frame events</h3><table><thead><tr><th>Time</th><th class=\"num\">Rendered frame</th><th class=\"num\">CPU active</th><th class=\"num\">GPU active</th><th class=\"num\">REDscript</th><th class=\"num\">Script calls</th><th>Recorded spike</th><th>Aligned evidence</th></tr></thead><tbody>");
            foreach (var x in ft.WorstFrames.Take(20))
            {
                sb.Append("<tr><td>").Append(F(x.StartMs / 1000.0, 3)).Append(" s</td>")
                    .Append("<td class=\"num\"><b>").Append(F(x.FrameMs)).Append(" ms</b></td>")
                    .Append("<td class=\"num\">").Append(F(x.CpuActiveMs)).Append(" ms</td>")
                    .Append("<td class=\"num\">").Append(F(x.GpuActiveMs)).Append(" ms</td>")
                    .Append("<td class=\"num\">").Append(F(x.ScriptMs)).Append(" ms</td>")
                    .Append("<td class=\"num\">").Append(N(x.ScriptCalls)).Append("</td><td>");
                if (!string.IsNullOrWhiteSpace(x.LargestSpikeOwner))
                    sb.Append(H(x.LargestSpikeOwner)).Append(" <span class=\"muted\">(").Append(F(x.LargestSpikeMs)).Append(" ms)</span>");
                else
                    sb.Append("<span class=\"muted\">—</span>");
                sb.Append("</td><td>").Append(H(x.Evidence)).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }

        sb.Append("</div>");
    }

    private static void AppendDataLinks(StringBuilder sb, string captureRoot)
    {
        sb.Append("<div class=\"section links\"><h2>Full measurement data</h2>")
            .Append("<div class=\"note\">The report is the starting point. The original GRSP CSV outputs, developer views and copied frame-time companion data remain intact for deeper inspection and future TOTAL integration.</div>");

        var groups = new[]
        {
            ("Public capture", new[] {"GRSP_Summary.csv","GRSP_ByMod.csv","GRSP_ByFunction.csv","GRSP_Timeline.csv","GRSP_Frames.csv","GRSP_Spikes.csv","GRSP_Markers.csv","GRSP_FrameworkCandidates.csv","GRSP_Status.txt"}),
            ("Developer", Array.Empty<string>())
        };

        sb.Append("<div class=\"two\"><div class=\"card\"><h3>Public capture</h3>");
        foreach (var file in groups[0].Item2)
        {
            if (File.Exists(Path.Combine(captureRoot, file)))
                sb.Append("<div><a href=\"").Append(Href(file)).Append("\">").Append(H(file)).Append("</a></div>");
        }
        sb.Append("</div><div class=\"card\"><h3>Developer</h3>");
        var dev = Path.Combine(captureRoot, "Developer");
        if (Directory.Exists(dev))
        {
            foreach (var path in Directory.EnumerateFiles(dev, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(captureRoot, path).Replace('\\', '/');
                sb.Append("<div><a href=\"").Append(Href(relative)).Append("\">").Append(H(relative)).Append("</a></div>");
            }
        }
        else
        {
            sb.Append("<span class=\"muted\">No Developer/ data in this capture.</span>");
        }
        sb.Append("</div></div>");

        var ft = Path.Combine(captureRoot, "FrameTime");
        if (Directory.Exists(ft))
        {
            sb.Append("<details><summary>Frame-time companion files</summary>");
            foreach (var path in Directory.EnumerateFiles(ft, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(50))
            {
                var relative = Path.GetRelativePath(captureRoot, path).Replace('\\', '/');
                sb.Append("<div><a href=\"").Append(Href(relative)).Append("\">").Append(H(relative)).Append("</a></div>");
            }
            sb.Append("</details>");
        }

        sb.Append("<p class=\"muted\">Machine-readable condensed interpretation: <a href=\"")
            .Append(Href(SummaryFileName)).Append("\">").Append(SummaryFileName).Append("</a>.</p></div>");
    }

    private static string ShortSourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var normalized = path.Replace('/', '\\');
        var marker = "\\r6\\scripts\\";
        var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? "r6\\scripts\\" + normalized[(index + marker.Length)..] : Path.GetFileName(normalized);
    }

    private static string? GetEmbeddedLogoDataUri()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("GRedscriptProfiler.GRedIcon.png");
            if (stream is null)
                return null;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return "data:image/png;base64," + Convert.ToBase64String(buffer.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static void MetricCard(StringBuilder sb, string key, string value, string sub)
    {
        sb.Append("<div class=\"card metric\"><div class=\"k\">").Append(H(key))
            .Append("</div><div class=\"v\">").Append(value)
            .Append("</div><div class=\"s\">").Append(sub)
            .Append("</div></div>");
    }
}
