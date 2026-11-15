using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace GRedscript.Resolver;

internal static class Program
{
    private const string Schema = "grsp.resolver.input.v1";

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "source-audit")
                return RunSourceAudit(ParseArguments(args.Skip(1).ToArray()));
            if (args.Length == 0 || args[0] != "analyze")
                throw new ArgumentException(
                    "Usage: GRedscript.Resolver source-audit --game <live game root> " +
                    "--out <external JSON path> OR analyze --capture <sealed capture folder> " +
                    "--game <live game root> --out <external JSON path> " +
                    "[--frame-evidence <GCO-PresentMon-Frames.v1.json>]");
            var options = ParseArguments(args.Skip(1).ToArray());
            string captureRoot = Path.GetFullPath(Required(options, "--capture"));
            string gameRoot = Path.GetFullPath(Required(options, "--game"));
            string output = Path.GetFullPath(Required(options, "--out"));
            if (!Directory.Exists(captureRoot))
                throw new InvalidDataException("Capture directory does not exist");
            if (Inside(output, gameRoot) || Inside(output, captureRoot))
                throw new InvalidDataException(
                    "Refusing to write Resolver output into installed game or sealed capture");

            string nativeRoot = Directory.Exists(Path.Combine(captureRoot, "G-REDscript"))
                ? Path.Combine(captureRoot, "G-REDscript")
                : captureRoot;
            string metadata = Path.Combine(nativeRoot, "Data", "Metadata");
            string completion = Path.Combine(metadata, "GCO-F11-Complete.flag");
            if (!File.Exists(completion) ||
                !string.Equals(File.ReadAllText(completion).Trim(), "F11_STOP",
                    StringComparison.Ordinal))
                throw new InvalidDataException("GCO explicit F11 completion proof is absent");

            string status = Path.Combine(metadata, "GRSP_Status.txt");
            if (!File.Exists(status))
                throw new InvalidDataException("GRSP status file missing");
            var captureStates = File.ReadLines(status)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("State:", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (captureStates.Length != 1 ||
                !string.Equals(captureStates[0], "State: COMPLETE",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GRSP capture is not exactly COMPLETE");

            string summaryFile = Path.Combine(nativeRoot, "GRSP_Summary.json");
            using var summaryDoc = JsonDocument.Parse(File.ReadAllText(summaryFile));
            var summary = summaryDoc.RootElement;
            var health = summary.GetProperty("health");
            if (health.GetProperty("frameQuality").GetString() != "GOOD" ||
                !health.GetProperty("shardMergeOk").GetBoolean() ||
                health.GetProperty("droppedSpikes").GetInt64() != 0 ||
                health.GetProperty("droppedHotPaths").GetInt64() != 0)
                throw new InvalidDataException(
                    "GRSP capture quality is insufficient for Resolver source admission");

            string collectionProvenance = GcoCollectionIndex.Verify(captureRoot, nativeRoot);

            var tables = new SortedDictionary<string, CsvTable>(StringComparer.Ordinal);
            foreach (string bucket in new[] { "Developer", "Runtime" })
            {
                string folder = Path.Combine(nativeRoot, "Data", bucket);
                if (!Directory.Exists(folder))
                    throw new InvalidDataException("Missing native evidence directory: " + bucket);
                foreach (string csv in Directory.EnumerateFiles(folder, "*.csv")
                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    tables.Add(bucket + "/" + Path.GetFileName(csv), CsvTable.Load(csv));
            }
            foreach (string required in new[] {
                "Developer/RSP_FunctionMap.csv",
                "Developer/RSP_CallSites.csv",
                "Developer/RSP_Cadence.csv",
                "Developer/RSP_WorkMap.csv",
                "Developer/RSP_CallbackPressure.csv",
                "Developer/RSP_CallbackTargets.csv",
                "Runtime/GRSP_ByFunction.csv",
                "Runtime/GRSP_Frames.csv",
                "Runtime/GRSP_Markers.csv",
                "Runtime/GRSP_ByMod.csv"
            })
                if (!tables.ContainsKey(required) || tables[required].Rows.Count == 0)
                    throw new InvalidDataException("Missing/empty required native evidence: " + required);

            var markers = tables["Runtime/GRSP_Markers.csv"].Rows;
            if (!markers.Any(x => Field(x, "event") == "START") ||
                !markers.Any(x => Field(x, "event") == "STOP_REQUEST"))
                throw new InvalidDataException("Missing actual GRSP F11 START/STOP markers");

            object frameEvidence = ReadFrameEvidence(options, captureRoot);

            // IDs are observations, not unique row keys: the real capture
            // contains repeated stable_callsite_id values with distinct counts.
            var callsites = tables["Developer/RSP_CallSites.csv"].Rows;
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            int repeatedIds = 0;
            var sourceAdmission = new SourceAdmission(gameRoot);
            _ = sourceAdmission.AuditAll();
            RequireSourceIdentity(options, sourceAdmission);
            var observations = new List<object>(callsites.Count);
            var ranks = new List<RankedObservation>(callsites.Count);
            var statusCount = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in callsites)
            {
                string id = Field(row, "stable_callsite_id");
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidDataException("Callsite identity missing");
                if (!seenIds.Add(id)) repeatedIds++;

                string funcId = Field(row, "stable_function_id");
                string owner = Field(row, "owner");
                string sourcePath = Field(row, "source_path");
                string symbol = Field(row, "source_function");
                int line = Int(row, "source_line");
                double work = Number(row, "exclusive_instrumented_ms");
                double rate = Number(row, "calls_per_sec");
                long edges = Long(row, "calls");
                if (work < 0 || rate < 0 || edges < 0)
                    throw new InvalidDataException("Negative observed work/call count");

                SourceMatch match = sourceAdmission.Check(sourcePath, symbol, line);
                statusCount[match.Status] = statusCount.GetValueOrDefault(match.Status) + 1;
                string resolution = Field(row, "target_resolution");
                string caveat = resolution.StartsWith("UNRESOLVED", StringComparison.OrdinalIgnoreCase)
                    ? "Unresolved target, no native/callee attribution or rewrite authority"
                    : "Outgoing VM call edge, not callback entry; source match is not rewrite authority";
                observations.Add(new {
                    stableCallsiteId = id,
                    stableFunctionId = funcId,
                    owner,
                    sourceFunction = symbol,
                    capturedPath = sourcePath,
                    sourceLine = line,
                    callKind = Field(row, "call_kind"),
                    target = Field(row, "target"),
                    targetResolution = resolution,
                    evidence = match,
                    candidateStage = "EVIDENCE_ONLY",
                    caveat
                });
                ranks.Add(new RankedObservation(
                    id, funcId, owner, symbol, Field(row, "target"),
                    work, rate, edges, match.Status, resolution, caveat));
            }

            int sourceCount = sourceAdmission.InspectedFileCount;
            var top = ranks.OrderByDescending(x => x.ExclusiveInstrumentedMs)
                .ThenByDescending(x => x.OutgoingEdgesPerSec)
                .ThenBy(x => x.StableCallsiteId, StringComparer.Ordinal)
                .Take(100).ToArray();

            var payload = new {
                schemaVersion = Schema,
                producer = "GRSP_OWNER_REDSCRIPT_RESOLVER",
                stage = "R3_R4_READ_ONLY_EVIDENCE_NOT_GENERATION",
                generationAuthorized = false,
                timeGeneratedUtc = DateTimeOffset.UtcNow,
                capture = new {
                    scenario = summary.GetProperty("capture").GetProperty("scenario").GetString(),
                    durationSeconds = summary.GetProperty("capture")
                        .GetProperty("durationSeconds").GetDouble(),
                    frameQuality = health.GetProperty("frameQuality").GetString(),
                    explicitF11Complete = true,
                    nativeSourceOwner = "GRSP",
                    collectionProvenance,
                    scope = "REDscript outgoing VM call edges, NOT callback entries or total CPU",
                    rawSummarySha256 = Sha(summaryFile)
                },
                frameEvidence,
                source = new {
                    inputKind = "CURRENT_LIVE_R6_SCRIPTS",
                    fileCount = sourceCount,
                    sourceStackSha256 = sourceAdmission.SourceStackSha256,
                    inspectedDistinctFiles = sourceAdmission.InspectedFileCount,
                    admissionScope = "NAME_AND_LINE_MATCH_ONLY_NO_RECEIVER_OR_SEMANTIC_PROOF",
                    transformationState = "UNSAFE_OR_UNPROVEN",
                    sourceStateProof = "NOT_IMPLEMENTED_UNTIL_RULE_SPECIFIC_POSTCONDITIONS"
                },
                counts = new {
                    rawCallsiteRows = callsites.Count,
                    distinctCallsiteIds = seenIds.Count,
                    repeatedCallsiteIdRows = repeatedIds,
                    sourceMatchStatus = statusCount
                },
                rankedObservedCallsites = top,
                sourceObservations = observations,
                rawTables = tables,
                safety = new {
                    allRawRowsPreserved = true,
                    cpuDomainsAdditive = false,
                    callbackEntryCountsKnown = false,
                    unchangedStateRatesKnown = false,
                    parserResultAuthorizesMutation = false,
                    editorWritesToLiveGame = false
                }
            };

            VerifySourceUnchanged(sourceAdmission);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using (var stream = File.Create(output))
                JsonSerializer.Serialize(stream, payload,
                    new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            Console.WriteLine($"GRSP R3/R4 PASS: {callsites.Count} callsite rows, " +
                $"{seenIds.Count} distinct IDs, {repeatedIds} repeated-ID rows, " +
                $"{sourceAdmission.InspectedFileCount} inspected source files; " +
                $"READ-ONLY evidence: {output}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("GRSP Resolver refused: " + ex.Message);
            return 2;
        }
    }

    private static int RunSourceAudit(Dictionary<string, string> options)
    {
        if (options.Keys.Any(key => key is not (
                "--game" or "--out" or "--expected-source-sha256")))
            throw new ArgumentException("Source audit accepts --game, --out and optional --expected-source-sha256");
        string gameRoot = Path.GetFullPath(Required(options, "--game"));
        string output = Path.GetFullPath(Required(options, "--out"));
        if (Inside(output, gameRoot))
            throw new InvalidDataException("Refusing to write source audit into the game");
        var source = new SourceAdmission(gameRoot);
        object result = source.AuditAll();
        RequireSourceIdentity(options, source);
        VerifySourceUnchanged(source);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        // Use a disposable file and atomic move: failures must not leave a
        // misleading partially written successful audit.
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
                JsonSerializer.Serialize(stream, result,
                    new JsonSerializerOptions {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
            File.Move(temporary, output, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        Console.WriteLine("GRSP source audit complete: read-only source evidence; " +
                          "no source transformation or compiler proof");
        return 0;
    }

    private static void RequireSourceIdentity(
        IReadOnlyDictionary<string, string> options, SourceAdmission source)
    {
        if (!options.TryGetValue("--expected-source-sha256", out string? expected))
            return;
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidDataException("Expected source SHA256 must be 64 hexadecimal characters");
        if (!string.Equals(expected, source.SourceStackSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SOURCE_DRIFT_DETECTED: live REDscript stack differs from admitted source");
    }

    private static void VerifySourceUnchanged(SourceAdmission source)
    {
        if (!string.Equals(source.SourceStackSha256,
                source.FingerprintCurrentSources(), StringComparison.Ordinal))
            throw new InvalidDataException(
                "SOURCE_DRIFT_DETECTED: live REDscript source changed during Resolver analysis");
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        if (args.Length % 2 != 0)
            throw new ArgumentException("Expected --option value pairs");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (args[i] is not ("--capture" or "--game" or "--out" or "--frame-evidence" or "--expected-source-sha256"))
                throw new ArgumentException("Unknown Resolver option: " + args[i]);
            if (!options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Duplicate Resolver option: " + args[i]);
        }
        return options;
    }

    private static string Required(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("Missing required option: " + key);

    private static bool Inside(string child, string parent)
    {
        parent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        child = Path.GetFullPath(child);
        return child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            child.StartsWith(parent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static object ReadFrameEvidence(
        Dictionary<string, string> options, string captureRoot) =>
        PresentMonEvidence.Read(options, captureRoot);

    private static string Sha(string filename) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filename)));

    private static string Field(Dictionary<string, string> row, string key) =>
        row.TryGetValue(key, out string? value)
            ? value : throw new InvalidDataException("Native CSV field absent: " + key);

    private static long Long(Dictionary<string, string> row, string key) =>
        long.TryParse(Field(row, key), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out long value)
            ? value : throw new InvalidDataException("Invalid native integer: " + key);

    private static int Int(Dictionary<string, string> row, string key) =>
        int.TryParse(Field(row, key), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int value)
            ? value : throw new InvalidDataException("Invalid native integer: " + key);

    private static double Number(Dictionary<string, string> row, string key) =>
        double.TryParse(Field(row, key), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value : throw new InvalidDataException("Invalid native number: " + key);

    private sealed record RankedObservation(
        string StableCallsiteId, string StableFunctionId, string Owner,
        string SourceFunction, string Target, double ExclusiveInstrumentedMs,
        double OutgoingEdgesPerSec, long OutgoingEdges, string SourceMatch,
        string TargetResolution, string Caveat);

    private sealed record CsvTable(
        string Sha256, string[] Header, List<Dictionary<string, string>> Rows)
    {
        internal static CsvTable Load(string filename)
        {
            using var parser = new TextFieldParser(filename);
            parser.TextFieldType = FieldType.Delimited;
            parser.SetDelimiters(",");
            parser.HasFieldsEnclosedInQuotes = true;
            var header = parser.ReadFields() ??
                throw new InvalidDataException("CSV has no header: " + filename);
            if (header.Length == 0 || header.Any(string.IsNullOrWhiteSpace) ||
                header.Distinct(StringComparer.Ordinal).Count() != header.Length)
                throw new InvalidDataException("Invalid CSV columns: " + filename);
            // Retain every row, including repeated stable IDs. Keep all source-native
            // fields as strings, rather than losing precision through JSON numbers.
            var rows = new List<Dictionary<string, string>>();
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields() ??
                    throw new InvalidDataException("Invalid CSV row: " + filename);
                if (fields.Length != header.Length)
                    throw new InvalidDataException("Invalid native CSV field count: " + filename);
                var row = new Dictionary<string, string>(header.Length,
                    StringComparer.Ordinal);
                for (int i = 0; i < header.Length; i++)
                    row.Add(header[i], fields[i]);
                rows.Add(row);
            }
            return new(Sha(filename), header, rows);
        }
    }
}
