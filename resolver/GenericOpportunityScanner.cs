using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GRedscript.Resolver;

/// <summary>
/// R4/R5 discovery for CNAME_ARRAY_MEMBERSHIP_V1. Reads ALL current scripts
/// and optionally the authoritative GRSP capture. Never generates source.
/// Reuses GenericMembershipPass's exact shape checker, not a second rewriter.
/// </summary>
internal static class GenericOpportunityScanner
{
    private const string Rule = GenericMembershipPass.Rule;

    internal sealed record Summary(
        int Files, int Candidates, int Measured, int Unmeasured, int Rejected);

    private sealed record Evidence(long Less, long Add)
    {
        internal long Edges => checked(Less + Add);
    }

    internal static Summary Scan(string gameRoot, string? analysisFile, string outputFile)
    {
        string game = Path.GetFullPath(gameRoot);
        string output = Path.GetFullPath(outputFile);
        string scripts = Path.Combine(game, "r6", "scripts");
        if (!Directory.Exists(scripts))
            throw new InvalidDataException("Current r6/scripts not present");
        if (Inside(output, game) || (analysisFile is not null &&
            output.Equals(Path.GetFullPath(analysisFile),
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("SCAN_OUTPUT_NOT_ISOLATED");
        if (File.Exists(output) || Directory.Exists(output))
            throw new InvalidDataException("SCAN_OUTPUT_ALREADY_EXISTS");

        var source = new SourceAdmission(game);
        _ = source.AuditAll();
        string stack = source.SourceStackSha256!;
        Dictionary<string, Evidence> evidence = analysisFile is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : ReadEvidence(source, Path.GetFullPath(analysisFile), stack);
        var rows = new List<object>();
        int examined = 0, eligible = 0, measured = 0, unmeasured = 0;
        int rejected = 0, ruleMarkedFiles = 0, invalidFiles = 0;
        var rejectionCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(
                     scripts, "*.reds", SearchOption.AllDirectories)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Source reparse point refused");
            byte[] bytes = File.ReadAllBytes(file);
            string text = Encoding.UTF8.GetString(bytes);
            string relative = Path.GetRelativePath(scripts, file).Replace('\\', '/');
            if (text.Contains("// GRSP-GENERIC:" + Rule, StringComparison.Ordinal))
                ruleMarkedFiles++;

            if (!source.IsFileIndexed(file))
            {
                invalidFiles++;
                continue;
            }
            IReadOnlyList<SourceAdmission.Token> tokens;
            try { tokens = SourceAdmission.ReadTokens(text); }
            catch (InvalidDataException)
            {
                invalidFiles++;
                continue;
            }
            for (int i = 0; i + 9 < tokens.Count; i++)
            {
                // Discovery prefix only, not the admission decision.
                // Never scan CName-looking strings/comments with regex.
                if (tokens[i].Value != "for" ||
                    !Identifier(tokens[i + 1].Value) ||
                    tokens[i + 2].Value != "in" ||
                    tokens[i + 3].Value != "this" ||
                    tokens[i + 4].Value != "." ||
                    !Identifier(tokens[i + 5].Value) ||
                    tokens[i + 6].Value != "{" ||
                    tokens[i + 7].Value != "if" ||
                    tokens[i + 8].Value != "Equals" ||
                    tokens[i + 9].Value != "(")
                    continue;

                examined++;
                int line = tokens[i].Line;
                string reasonCode = GenericMembershipPass.SourceShapeReason(text, tokens, line);
                bool hasProof = reasonCode == "ELIGIBLE";
                string key = relative + "\0" + line.ToString(CultureInfo.InvariantCulture);
                evidence.TryGetValue(key, out Evidence? edges);
                bool hasMeasurements = hasProof && edges is not null &&
                    edges.Less > 0 && edges.Add > 0;

                string state = !hasProof ? "REJECTED_UNPROVEN" :
                    hasMeasurements ? "ELIGIBLE_MEASURED" : "ELIGIBLE_UNMEASURED";
                if (!hasProof)
                {
                    rejected++;
                    rejectionCounts[reasonCode] =
                        rejectionCounts.GetValueOrDefault(reasonCode) + 1;
                }
                else
                {
                    eligible++;
                    if (hasMeasurements) measured++; else unmeasured++;
                }
                rows.Add(new
                {
                    path = "r6/scripts/" + relative,
                    owner = relative.Contains('/') ? relative.Split('/')[0] : "ROOT_LEVEL",
                    line, rule = Rule, state, reasonCode,
                    structuralProof = hasProof ? "STRICT_SOURCE_SHAPE_MATCH" :
                        "NOT_PROVEN_DO_NOT_REWRITE",
                    measuredLoopOperatorEdges = hasMeasurements ? edges!.Edges : 0,
                    measuredEdgesAreNotEntriesOrPredictedSavings = true,
                    reason = !hasProof ? "Exact pure typed membership shape is not proven" :
                        hasMeasurements ? "Both captured loop operator edges are source-aligned" :
                        "Current shape matches, but reliable matched call-edge evidence is absent",
                    rewriteAuthorized = false,
                    gameplayValidated = false
                });
            }
        }

        if (!stack.Equals(source.FingerprintCurrentSources(),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "SOURCE_DRIFT_DETECTED: current REDscript stack changed during scanning");

        object report = new
        {
            schemaVersion = "grsp.generic.opportunities.v1",
            rule = Rule,
            sourceStackSha256 = stack,
            sourceFiles = source.InspectedFileCount,
            sourceScope = "ALL_LIVE_R6_SCRIPTS",
            evidenceKind = analysisFile is null ? "NOT_CAPTURED" :
                "GRSP_SOURCE_MATCHED_OPERATOR_EDGES",
            candidateLoopsExamined = examined,
            structuralMatches = eligible,
            eligibleMeasured = measured,
            eligibleUnmeasured = unmeasured,
            rejectedUnproven = rejected,
            firstFailedPrerequisiteCounts = rejectionCounts,
            filesWithExistingRuleMarkerUnverified = ruleMarkedFiles,
            unparseableFilesSkipped = invalidFiles,
            compilerValidated = false,
            gameplayValidated = false,
            generationAuthorized = false,
            deployable = false,
            sourceModified = false,
            generatedFiles = 0,
            observedEdgesAreNotLoopEntriesOrPredictedSavings = true,
            pathsOnlyInPrivateLocalReport = true,
            candidates = rows
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output,
            JsonSerializer.Serialize(report, new JsonSerializerOptions {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }), new UTF8Encoding(false));
        return new(source.InspectedFileCount, examined, measured, unmeasured, rejected);
    }

    private static Dictionary<string, Evidence> ReadEvidence(
        SourceAdmission source, string analysisFile, string expectedStack)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(analysisFile));
        JsonElement root = doc.RootElement;
        if (root.GetProperty("schemaVersion").GetString() != "grsp.resolver.input.v1" ||
            root.GetProperty("generationAuthorized").GetBoolean() ||
            root.GetProperty("capture").GetProperty("collectionProvenance").GetString() !=
                "GCO_INDEXED_COLLECTED_BYTES_VERIFIED" ||
            !expectedStack.Equals(root.GetProperty("source")
                    .GetProperty("sourceStackSha256").GetString(),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SOURCE_DRIFT_DETECTED_OR_UNVERIFIED_GRSP_EVIDENCE");

        JsonElement rows = root.GetProperty("rawTables")
            .GetProperty("Developer/RSP_CallSites.csv").GetProperty("rows");
        JsonElement observations = root.GetProperty("sourceObservations");
        if (rows.GetArrayLength() != observations.GetArrayLength())
            throw new InvalidDataException("LOSSLESS_CALLSITE_ALIGNMENT_REQUIRED");

        var grouped = new Dictionary<string, (long Less, long Add)>(
            StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (JsonElement observation in observations.EnumerateArray())
        {
            JsonElement row = rows[index++];
            string target = observation.GetProperty("target").GetString() ?? "";
            if (target is not ("OperatorLess" or "OperatorAssignAdd") ||
                observation.GetProperty("evidence").GetProperty("status").GetString() !=
                    "LINE_METHOD_NAME_MATCH_RECEIVER_UNVERIFIED")
                continue;
            string path = observation.GetProperty("capturedPath").GetString() ?? "";
            string? relative = SourceAdmission.RelativeScriptPath(path);
            if (relative is null) continue;
            string fn = observation.GetProperty("sourceFunction").GetString() ?? "";
            int line = observation.GetProperty("sourceLine").GetInt32();
            var mapped = source.Check(path, fn, line);
            if (mapped.Status != "LINE_METHOD_NAME_MATCH_RECEIVER_UNVERIFIED" ||
                !string.Equals(mapped.Sha256,
                    observation.GetProperty("evidence").GetProperty("sha256").GetString(),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            long count = long.Parse(row.GetProperty("calls").GetString()!,
                CultureInfo.InvariantCulture);
            if (count <= 0) continue;
            string key = relative + "\0" + line.ToString(CultureInfo.InvariantCulture);
            grouped.TryGetValue(key, out var old);
            if (target == "OperatorLess")
                old.Less = checked(old.Less + count);
            else
                old.Add = checked(old.Add + count);
            grouped[key] = old;
        }
        return grouped.ToDictionary(k => k.Key, v => new Evidence(v.Value.Less, v.Value.Add),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool Identifier(string value) =>
        value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') &&
        value.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    private static bool Inside(string child, string parent)
    {
        string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        return child.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            child.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}
