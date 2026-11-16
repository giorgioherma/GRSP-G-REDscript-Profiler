using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GRedscript.Resolver;

/// <summary>
/// R4/R5 first generic candidate: replace a proven pure CName membership
/// loop with REDscript's built-in ArrayContains. This is strictly a preview,
/// NOT an output-generation authorization: SCC/full stack and gameplay remain
/// independent required gates.
/// </summary>
internal static class GenericMembershipPass
{
    internal const string Rule = "CNAME_ARRAY_MEMBERSHIP_V1";
    private const string Marker = "// GRSP-GENERIC:" + Rule;

    private sealed record Evidence(
        string RelativePath, string SourceFunction, int Line,
        long ObservedLoopEdges, string OriginalSha256);

    private sealed record Match(
        int Start, int End, string Replacement, int Line);

    internal static object Preview(
        string gameRoot, string analysisFile, string outputRoot)
    {
        string scripts = Path.GetFullPath(Path.Combine(gameRoot, "r6", "scripts"));
        string destination = Path.GetFullPath(outputRoot);
        string evidenceFile = Path.GetFullPath(analysisFile);
        if (!Directory.Exists(scripts) || !File.Exists(evidenceFile))
            throw new InvalidDataException("Missing current source or owner evidence");
        if (Within(destination, gameRoot))
            throw new InvalidDataException(
                "Generic preview must be outside the installed source root");
        if (Directory.Exists(destination) &&
            Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidDataException(
                "Generic preview refuses nonempty output (no silent overwrite)");

        using var document = JsonDocument.Parse(File.ReadAllText(evidenceFile));
        JsonElement root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetString() != "grsp.resolver.input.v1" ||
            root.GetProperty("generationAuthorized").GetBoolean() ||
            root.GetProperty("capture").GetProperty("collectionProvenance").GetString() !=
                "GCO_INDEXED_COLLECTED_BYTES_VERIFIED")
            throw new InvalidDataException("Unverified GRSP owner capture analysis");

        var source = new SourceAdmission(gameRoot);
        _ = source.AuditAll();
        string currentStack = source.SourceStackSha256!;
        string expectedStack = root.GetProperty("source")
            .GetProperty("sourceStackSha256").GetString() ?? "";
        if (!string.Equals(currentStack, expectedStack,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SOURCE_DRIFT_DETECTED: stack differs from owner analysis");

        var targets = new SortedDictionary<string, List<Evidence>>(
            StringComparer.OrdinalIgnoreCase);
        var raw = root.GetProperty("rawTables")
            .GetProperty("Developer/RSP_CallSites.csv")
            .GetProperty("rows");
        var observations = root.GetProperty("sourceObservations");
        if (observations.GetArrayLength() != raw.GetArrayLength())
            throw new InvalidDataException("Lossless callsite evidence alignment failed");

        // Native source_line on the VM loop's OperatorLess and
        // OperatorAssignAdd edges is a measurement anchor, NOT a guarantee
        // that each edge represents a callback entry or savings.
        var grouped = new Dictionary<string, (string Relative, string Function,
            int Line, string Sha, long Less, long Add)>(StringComparer.Ordinal);
        int k = 0;
        foreach (JsonElement observation in observations.EnumerateArray())
        {
            JsonElement row = raw[k++];
            string operation = observation.GetProperty("target").GetString() ?? "";
            if (operation is not ("OperatorLess" or "OperatorAssignAdd") ||
                observation.GetProperty("evidence").GetProperty("status").GetString() !=
                    "LINE_METHOD_NAME_MATCH_RECEIVER_UNVERIFIED")
                continue;
            string captured = observation.GetProperty("capturedPath").GetString() ?? "";
            string? relative = SourceAdmission.RelativeScriptPath(captured);
            if (relative is null) continue;
            int line = observation.GetProperty("sourceLine").GetInt32();
            string function = observation.GetProperty("sourceFunction").GetString() ?? "";
            string originalHash = observation.GetProperty("evidence")
                .GetProperty("sha256").GetString() ?? "";
            string key = relative + "\0" + function + "\0" + line;
            long count = long.Parse(row.GetProperty("calls").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            if (count <= 0) continue;
            if (!grouped.TryGetValue(key, out var previous))
                previous = (relative, function, line, originalHash, 0, 0);
            if (!originalHash.Equals(previous.Sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Source identity changed inside native evidence");
            if (operation == "OperatorLess")
                previous.Less = checked(previous.Less + count);
            else
                previous.Add = checked(previous.Add + count);
            grouped[key] = previous;
        }

        foreach (var group in grouped.Values)
        {
            if (group.Less == 0 || group.Add == 0) continue;
            if (!targets.TryGetValue(group.Relative, out List<Evidence>? bucket))
                targets[group.Relative] = bucket = new List<Evidence>();
            bucket.Add(new Evidence(group.Relative, group.Function, group.Line,
                checked(group.Less + group.Add), group.Sha));
        }

        var rows = new List<object>();
        var staged = new List<(string Path, byte[] Data)>();
        int applicable = 0, drifted = 0, already = 0;
        long eligibleObservedEdges = 0;
        foreach (var group in targets.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            string relative = group.Key;
            string full = Path.GetFullPath(Path.Combine(
                scripts, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!Within(full, scripts) || !File.Exists(full))
            {
                drifted++;
                rows.Add(new { path = relative, state = "UNPROVEN_OR_DRIFTED",
                    reason = "Current source missing" });
                continue;
            }
            byte[] bytes = File.ReadAllBytes(full);
            string sha = Convert.ToHexString(SHA256.HashData(bytes));
            string text = Encoding.UTF8.GetString(bytes);
            if (group.Value.Any(e => !sha.Equals(e.OriginalSha256,
                    StringComparison.OrdinalIgnoreCase)))
            {
                drifted++;
                rows.Add(new { path = relative, state = "UNPROVEN_OR_DRIFTED",
                    reason = "Source bytes differ from measured evidence" });
                continue;
            }
            var tokens = SourceAdmission.ReadTokens(text);
            var matches = new List<Match>();
            int rejected = 0;
            foreach (Evidence candidate in group.Value.OrderBy(e => e.Line))
            {
                var proof = source.Check(
                    "r6/scripts/" + relative, candidate.SourceFunction, candidate.Line);
                if (proof.Status != "LINE_METHOD_NAME_MATCH_RECEIVER_UNVERIFIED")
                {
                    rejected++;
                    continue;
                }
                Match? rewrite = TryMatch(text, tokens, candidate.Line,
                    candidate.SourceFunction);
                if (rewrite is null) { rejected++; continue; }
                if (matches.Any(m => m.Start == rewrite.Start))
                    continue;
                matches.Add(rewrite);
                eligibleObservedEdges = checked(eligibleObservedEdges +
                    candidate.ObservedLoopEdges);
            }

            bool marker = text.Contains(Marker, StringComparison.Ordinal);
            bool completed = marker && HasCompletedStructuralState(tokens, text);
            string state;
            // A marker is not permission to overwrite an unknown partial
            // optimization; validate already-generated statements first.
            if (marker && !completed)
            {
                drifted++;
                rows.Add(new { path = relative, state = "UNPROVEN_OR_DRIFTED",
                    reason = "Previously generated rule marker is structurally inconsistent" });
                continue;
            }
            if (matches.Count == 0)
            {
                state = completed ? "ALREADY_COMPLETE" : "UNPROVEN_OR_DRIFTED";
                if (completed) already++; else drifted++;
            }
            else
            {
                if (rejected > 0) { drifted++; state = "UNPROVEN_OR_DRIFTED"; }
                else
                {
                    state = marker ? "PARTIALLY_APPLICABLE" : "PRISTINE_APPLICABLE";
                    applicable++;
                }
            }
            if (state is "PRISTINE_APPLICABLE" or "PARTIALLY_APPLICABLE")
            {
                // Apply exact token spans backward, retaining all other bytes,
                // comments, formatting, lifecycle and mod ownership.
                string updated = text;
                foreach (Match m in matches.OrderByDescending(m => m.Start))
                    updated = updated[..m.Start] + m.Replacement + updated[m.End..];
                if (updated == text)
                    throw new InvalidDataException("Generic pass produced no source difference");
                staged.Add(("r6/scripts/" + relative, Encoding.UTF8.GetBytes(updated)));
            }
            rows.Add(new {
                path = relative, state, rule = Rule,
                matchedLoops = matches.Count, unprovenMatches = rejected,
                resultKind = "PREVIEW_ONLY_NOT_COMPILED"
            });
        }

        // Verify input did not change while matching. The preview is generated
        // from current source and never reaches the user's installed mods.
        if (source.FingerprintCurrentSources() != currentStack)
            throw new InvalidDataException(
                "SOURCE_DRIFT_DETECTED: source changed during preview");

        Directory.CreateDirectory(destination);
        foreach (var item in staged)
        {
            string output = Path.GetFullPath(Path.Combine(destination, "overlay",
                item.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!Within(output, destination))
                throw new InvalidDataException("Invalid source ownership in overlay");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllBytes(output, item.Data);
        }

        object result = new {
            schemaVersion = "grsp.generic.preview.v1",
            owner = "GRSP",
            rule = Rule,
            sourceStackSha256 = currentStack,
            sourceCount = source.InspectedFileCount,
            measuredOperatorEdgesInApplicableLoops = eligibleObservedEdges,
            measuredOperatorEdgesAreNotPredictedSavings = true,
            applicableFileCount = applicable,
            unprovenFileCount = drifted,
            alreadyCompleteFileCount = already,
            stagedFiles = staged.Count,
            compilerValidated = false,
            gameplayValidated = false,
            generationAuthorized = false,
            deployable = false,
            outputKind = "ISOLATED_SOURCE_PREVIEW_NOT_GAME_ZIP",
            candidates = rows
        };
        File.WriteAllText(Path.Combine(destination, "preview.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions {
                WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }), new UTF8Encoding(false));
        return result;
    }

    // The source-wide scanner reuses the exact same structural admission.
    // A missing captured function permits inspection, never rewrite authority.
    internal static bool HasSourceShape(
        string text, IReadOnlyList<SourceAdmission.Token> tokens, int line) =>
        TryMatch(text, tokens, line, null) is not null;

    private static Match? TryMatch(
        string text, IReadOnlyList<SourceAdmission.Token> t,
        int line, string? function)
    {
        var eligible = Enumerable.Range(0, t.Count)
            .Where(i => t[i].Value == "for" && t[i].Line == line).ToArray();
        if (eligible.Length != 1) return null;
        int i = eligible[0];
        if (i + 22 >= t.Count) return null;
        static bool Ident(string value) =>
            value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') &&
            value.All(c => char.IsLetterOrDigit(c) || c == '_');

        string member = t[i + 1].Value;
        string field = t[i + 5].Value;
        string value = t[i + 12].Value;
        string flag = t[i + 15].Value;
        if (!Ident(member) || !Ident(field) || !Ident(value) || !Ident(flag) ||
            member == flag || member == value || value == flag)
            return null;
        bool hasExactLoop = t[i + 2].Value == "in" &&
            t[i + 3].Value == "this" && t[i + 4].Value == "." &&
            t[i + 6].Value == "{" && t[i + 7].Value == "if" &&
            t[i + 8].Value == "Equals" && t[i + 9].Value == "(" &&
            t[i + 10].Value == member && t[i + 11].Value == "," &&
            t[i + 13].Value == ")" && t[i + 14].Value == "{" &&
            t[i + 16].Value == "=" && t[i + 17].Value == "true" &&
            t[i + 18].Value == ";" && t[i + 19].Value == "break" &&
            t[i + 20].Value == ";" && t[i + 21].Value == "}" &&
            t[i + 22].Value == "}";
        if (!hasExactLoop) return null;

        // Require the loop inside precisely the captured function body.
        int methodStart = -1;
        for (int j = 0; j < i - 3; j++)
        {
            if (t[j].Value != "func" || t[j + 2].Value != "(") continue;
            if (function is not null)
            {
                string capturedName = function.Contains("::", StringComparison.Ordinal)
                    ? function.Split("::").Last()
                    : function.Split('.').Last();
                if (!t[j + 1].Value.Equals(capturedName, StringComparison.Ordinal))
                    continue;
            }
            int open = FindOpeningMethodBrace(t, j + 2);
            if (open < 0) continue;
            int close = Closing(t, open);
            if (open < i && close > i + 22) methodStart = open;
        }
        if (methodStart < 0) return null;

        // Field must be declared exactly once as array<CName> in the
        // enclosing source class. Scope-local bool must begin false and
        // remain unmodified before the loop; searched CName is a local
        // value, never a side-effectful expression.
        int classStart = -1;
        for (int j = 0; j < methodStart - 2; j++)
        {
            if (t[j].Value != "class" || !Ident(t[j + 1].Value)) continue;
            int open = j + 2;
            while (open < methodStart && t[open].Value != "{") open++;
            if (open < methodStart && Closing(t, open) > i)
                classStart = open;
        }
        if (classStart < 0) return null;
        int fields = 0;
        for (int j = classStart + 1; j + 7 < methodStart; j++)
            if (Sequence(t, j, "let", field, ":", "array", "<", "CName", ">", ";"))
                fields++;
        if (fields != 1) return null;

        int flagDeclaration = -1, valueDeclaration = -1;
        for (int j = methodStart + 1; j + 5 < i; j++)
        {
            if (Sequence(t, j, "let", flag, ":", "Bool", "=", "false") &&
                j + 6 < i && t[j + 6].Value == ";")
            {
                if (flagDeclaration >= 0) return null;
                flagDeclaration = j + 6;
            }
            if (Sequence(t, j, "let", value, ":", "CName", "="))
            {
                if (valueDeclaration >= 0) return null;
                valueDeclaration = j + 4;
            }
        }
        if (flagDeclaration < 0 || valueDeclaration < 0) return null;
        for (int j = flagDeclaration + 1; j < i; j++)
            if (t[j].Value == flag) return null;

        // The loop body has to be source-only punctuation/identifiers,
        // not comments or string interpolations which might be lost.
        int start = t[i].Start, end = t[i + 22].End;
        string body = text[start..end];
        if (body.Contains("//", StringComparison.Ordinal) ||
            body.Contains("/*", StringComparison.Ordinal) ||
            body.Contains("<literal>", StringComparison.Ordinal))
            return null;
        int newline = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        string indent = text[newline..start];
        if (!indent.All(c => c is ' ' or '\t')) return null;
        string eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string replacement = Marker + eol + indent +
            flag + " = ArrayContains(this." + field + ", " + value + ");";
        return new Match(start, end, replacement, line);
    }

    private static bool HasCompletedStructuralState(
        IReadOnlyList<SourceAdmission.Token> t, string text)
    {
        // Every generated marker must be adjacent to EXACTLY the expected
        // typed built-in assignment; do not trust a marker elsewhere in source.
        int markers = 0;
        int after = 0;
        while (true)
        {
            int at = text.IndexOf(Marker, after, StringComparison.Ordinal);
            if (at < 0) break;
            markers++;
            int markerEnd = at + Marker.Length;
            int eol = text.IndexOf('\n', markerEnd);
            if (eol < 0 || !text[markerEnd..eol].Trim().Equals("",
                    StringComparison.Ordinal))
                return false;
            int lineEnd = text.IndexOf('\n', eol + 1);
            if (lineEnd < 0) lineEnd = text.Length;
            string line = text[(eol + 1)..lineEnd].Trim();
            var assignment = SourceAdmission.ReadTokens(line);
            if (assignment.Count != 11 ||
                assignment[1].Value != "=" ||
                assignment[2].Value != "ArrayContains" ||
                assignment[3].Value != "(" ||
                assignment[4].Value != "this" ||
                assignment[5].Value != "." ||
                assignment[7].Value != "," ||
                assignment[9].Value != ")" ||
                assignment[10].Value != ";")
                return false;
            string flag = assignment[0].Value;
            string field = assignment[6].Value;
            string needle = assignment[8].Value;
            bool hasFlag = false, hasField = false, hasNeedle = false;
            for (int i = 0; i + 7 < t.Count; i++)
            {
                if (Sequence(t, i, "let", flag, ":", "Bool", "=", "false", ";"))
                    hasFlag = true;
                if (Sequence(t, i, "let", field, ":", "array", "<", "CName", ">", ";"))
                    hasField = true;
                if (Sequence(t, i, "let", needle, ":", "CName", "="))
                    hasNeedle = true;
            }
            if (!hasFlag || !hasField || !hasNeedle)
                return false;
            after = lineEnd;
        }
        return markers > 0;
    }

    private static bool Sequence(
        IReadOnlyList<SourceAdmission.Token> t, int offset,
        params string[] expected)
    {
        if (offset + expected.Length > t.Count) return false;
        for (int j = 0; j < expected.Length; j++)
            if (t[offset + j].Value != expected[j]) return false;
        return true;
    }

    private static int FindOpeningMethodBrace(
        IReadOnlyList<SourceAdmission.Token> t, int parentheses)
    {
        int parens = 0;
        for (int i = parentheses; i < t.Count; i++)
        {
            if (t[i].Value == "(") parens++;
            else if (t[i].Value == ")") parens--;
            if (parens == 0 && t[i].Value == "{") return i;
            if (parens == 0 && t[i].Value == ";") return -1;
        }
        return -1;
    }

    private static int Closing(
        IReadOnlyList<SourceAdmission.Token> t, int open)
    {
        if (t[open].Value != "{") return -1;
        int depth = 0;
        for (int i = open; i < t.Count; i++)
        {
            if (t[i].Value == "{") depth++;
            else if (t[i].Value == "}" && --depth == 0) return i;
        }
        return -1;
    }

    private static bool Within(string path, string root)
    {
        string candidate = Path.GetFullPath(path);
        string parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return candidate.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(parent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}
