using System.Security.Cryptography;
using System.Text;

namespace GRedscript.Resolver;

// Read-only, conservative source evidence. A line/method match is NOT permission
// to rewrite it: receiver, call effects and whole-stack compilation are separate gates.
internal sealed record SourceMatch(
    string Status, string? ScriptPath, int SourceLine, string? Method,
    string? Sha256, string Reason, string TransformationState = "UNSAFE_OR_UNPROVEN");

internal sealed class SourceAdmission
{
    private readonly string scriptsRoot;
    private readonly Dictionary<string, ParsedFile> files =
        new(StringComparer.OrdinalIgnoreCase);

    internal int InspectedFileCount => files.Count;

    internal SourceAdmission(string gameRoot)
    {
        scriptsRoot = Path.GetFullPath(Path.Combine(gameRoot, "r6", "scripts"));
        if (!Directory.Exists(scriptsRoot))
            throw new InvalidDataException("Live r6/scripts directory not found: " + scriptsRoot);
    }

    // Whole-stack read-only source inventory for CI-only verification. This is
    // NOT REDscript compilation, receiver proof, or rewrite admission.
    // Output is aggregate-only: third-party owner names and source text remain
    // inside the private checkout and never enter public CI artifacts.
    internal object AuditAll()
    {
        int fileCount = 0;
        int lexicallyIndexed = 0;
        int structurallyUnproven = 0;
        int recognizedMethods = 0;
        long sourceBytes = 0;
        var unknown = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var paths = Directory.EnumerateFiles(
                scriptsRoot, "*.reds", System.IO.SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        foreach (string full in paths)
        {
            var info = new FileInfo(full);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Source reparse point rejected");
            fileCount++;
            byte[] bytes = File.ReadAllBytes(full);
            sourceBytes += bytes.Length;
            var parsed = ParsedFile.Parse(Encoding.UTF8.GetString(bytes),
                Convert.ToHexString(SHA256.HashData(bytes)));
            files[full] = parsed;
            if (parsed.Valid)
            {
                lexicallyIndexed++;
                recognizedMethods += parsed.Methods.Count;
            }
            else
            {
                structurallyUnproven++;
                const string category = "UNSUPPORTED_OR_INVALID_REDSCRIPT_SYNTAX";
                unknown[category] = unknown.GetValueOrDefault(category) + 1;
            }
        }
        if (fileCount == 0)
            throw new InvalidDataException("No live REDscript sources found");
        return new {
            schemaVersion = "grsp.source.audit.v1",
            scope = "ALL_DISCOVERED_CURRENT_R6_SCRIPTS",
            sourceFiles = fileCount,
            sourceBytes,
            lexicallyIndexedFiles = lexicallyIndexed,
            structurallyUnprovenFiles = structurallyUnproven,
            recognizedFunctionSpans = recognizedMethods,
            unprovenCategories = unknown,
            sourceState = "UNSAFE_OR_UNPROVEN",
            generatedFiles = 0,
            codeRewritten = false,
            compilerValidated = false,
            gameplayValidated = false,
            privateSourcePathsIncluded = false
        };
    }

    internal SourceMatch Check(string rawPath, string sourceFunction, int sourceLine)
    {
        string? relative = RelativeScriptPath(rawPath);
        if (relative is null)
            return new("INVALID_SOURCE_PATH", null, sourceLine, null, null,
                "Capture source is not a safe path under r6/scripts");

        string full = Path.GetFullPath(Path.Combine(
            scriptsRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(scriptsRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !full.EndsWith(".reds", StringComparison.OrdinalIgnoreCase))
            return new("INVALID_SOURCE_PATH", relative, sourceLine, null, null,
                "Source path escapes the REDscript root");

        if (!File.Exists(full))
            return new("MISSING_SOURCE", relative, sourceLine, null, null,
                "Capture is not an authoritative current-source inventory");

        if (!files.TryGetValue(full, out ParsedFile? parsed))
        {
            byte[] bytes = File.ReadAllBytes(full);
            string text = Encoding.UTF8.GetString(bytes);
            parsed = ParsedFile.Parse(text, Convert.ToHexString(SHA256.HashData(bytes)));
            files.Add(full, parsed);
        }

        if (!parsed.Valid)
            return new("SYNTAX_UNPROVEN", relative, sourceLine, null, parsed.Sha256,
                parsed.Diagnostic);
        if (sourceLine <= 0 || sourceLine > parsed.LineCount)
            return new("LINE_OUTSIDE_FILE", relative, sourceLine, null, parsed.Sha256,
                "Captured line is outside the current source file");

        string shortName = sourceFunction.Split("::").LastOrDefault() ?? "";
        if (shortName.Contains("wrapper$", StringComparison.OrdinalIgnoreCase) ||
            shortName.Contains("proxy$", StringComparison.OrdinalIgnoreCase))
            return new("UNRESOLVED_WRAPPER", relative, sourceLine, null, parsed.Sha256,
                "Compiled synthetic wrapper/proxy has no proven original method identity");

        var matching = parsed.Methods
            .Where(m => sourceLine >= m.StartLine && sourceLine <= m.EndLine)
            .ToArray();
        if (matching.Length != 1)
            return new("LINE_OUTSIDE_METHOD", relative, sourceLine, null, parsed.Sha256,
                "Source location does not uniquely identify a parsed function");

        var method = matching[0];
        if (!string.Equals(shortName, method.Name, StringComparison.OrdinalIgnoreCase))
            return new("UNMATCHED_SYMBOL", relative, sourceLine, method.Name,
                parsed.Sha256, "Capture symbol disagrees with the current method name");

        return new("LINE_METHOD_NAME_MATCH_RECEIVER_UNVERIFIED", relative, sourceLine,
            method.Name, parsed.Sha256,
            "Read-only name/span match; receiver, call purity, source state and ABI unproven");
    }

    internal static string? RelativeScriptPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.IndexOf('\0') >= 0)
            return null;
        string normalized = rawPath.Replace('\\', '/');
        const string anchor = "r6/scripts/";
        int at = normalized.LastIndexOf(anchor, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        string relative = normalized[(at + anchor.Length)..];
        string[] segments = relative.Split('/');
        if (segments.Length == 0 ||
            segments.Any(x => x.Length == 0 || x is "." or ".." || x.Contains(':')))
            return null;
        return relative;
    }

    private sealed record Token(string Value, int Line);
    private sealed record Method(string Name, int StartLine, int EndLine);

    private sealed class ParsedFile
    {
        internal bool Valid { get; private set; }
        internal string Diagnostic { get; private set; } = "";
        internal string Sha256 { get; private init; } = "";
        internal int LineCount { get; private init; }
        internal List<Method> Methods { get; } = new();

        internal static ParsedFile Parse(string text, string sha)
        {
            var parsed = new ParsedFile {
                Sha256 = sha,
                LineCount = text.Count(c => c == '\n') + 1
            };
            try
            {
                List<Token> tokens = Tokenize(text);
                ValidateBalance(tokens);
                for (int i = 0; i < tokens.Count - 2; i++)
                {
                    if (tokens[i].Value != "func") continue;
                    string name = tokens[i + 1].Value;
                    if (tokens[i + 2].Value != "(" || !IsIdentifier(name))
                        throw new InvalidDataException("Unrecognized function declaration");
                    int parametersEnd = FindMatching(tokens, i + 2, "(", ")");
                    int j = parametersEnd + 1;
                    while (j < tokens.Count &&
                           tokens[j].Value is not ("{" or "=" or ";"))
                        j++;
                    if (j == tokens.Count || tokens[j].Value == ";")
                        throw new InvalidDataException("Function body is not structurally proven");

                    int end;
                    if (tokens[j].Value == "{")
                        end = FindMatching(tokens, j, "{", "}");
                    else
                    {
                        // REDscript supports source-level expression bodies.
                        end = j + 1;
                        while (end < tokens.Count && tokens[end].Value != ";") end++;
                        if (end == tokens.Count)
                            throw new InvalidDataException("Unterminated expression-body function");
                    }
                    parsed.Methods.Add(new Method(name, tokens[i].Line, tokens[end].Line));
                }
                parsed.Valid = true;
                parsed.Diagnostic = "Parsed function spans only; not a compiler/semantic proof";
            }
            catch (InvalidDataException ex)
            {
                parsed.Valid = false;
                parsed.Diagnostic = ex.Message;
                parsed.Methods.Clear();
            }
            return parsed;
        }

        private static bool IsIdentifier(string s) =>
            s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') &&
            s.All(c => char.IsLetterOrDigit(c) || c == '_');

        private static int FindMatching(
            IReadOnlyList<Token> tokens, int start, string open, string close)
        {
            int depth = 0;
            for (int i = start; i < tokens.Count; i++)
            {
                if (tokens[i].Value == open) depth++;
                if (tokens[i].Value == close && --depth == 0) return i;
                // Only the closing token decrements the depth.
                if (tokens[i].Value != close && tokens[i].Value != open)
                    continue;
            }
            throw new InvalidDataException("Unbalanced function span");
        }

        private static void ValidateBalance(IReadOnlyList<Token> tokens)
        {
            var stack = new Stack<string>();
            foreach (var token in tokens)
            {
                if (token.Value is "(" or "{" or "[")
                    stack.Push(token.Value);
                else if (token.Value is ")" or "}" or "]")
                {
                    if (stack.Count == 0)
                        throw new InvalidDataException("Unmatched closing delimiter");
                    string prior = stack.Pop();
                    if ((prior, token.Value) is not
                        (("(", ")") or ("{", "}") or ("[", "]")))
                        throw new InvalidDataException("Mismatched source delimiters");
                }
            }
            if (stack.Count > 0)
                throw new InvalidDataException("Unclosed source delimiter");
        }

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            int line = 1;
            for (int i = 0; i < text.Length;)
            {
                char c = text[i];
                if (c == '\n') { line++; i++; continue; }
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    i += 2;
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    i += 2;
                    bool closed = false;
                    while (i + 1 < text.Length)
                    {
                        if (text[i] == '\n') line++;
                        if (text[i] == '*' && text[i + 1] == '/')
                        { i += 2; closed = true; break; }
                        i++;
                    }
                    if (!closed) throw new InvalidDataException("Unclosed block comment");
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    i++;
                    bool closed = false;
                    while (i < text.Length)
                    {
                        if (text[i] == '\n') line++;
                        if (text[i] == '\\' && i + 1 < text.Length)
                        { i += 2; continue; }
                        if (text[i] == quote)
                        {
                            if (i + 1 < text.Length && text[i + 1] == quote)
                            { i += 2; continue; }
                            i++; closed = true; break;
                        }
                        i++;
                    }
                    if (!closed) throw new InvalidDataException("Unclosed string literal");
                    tokens.Add(new Token("<literal>", line));
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i++;
                    while (i < text.Length &&
                        (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    tokens.Add(new Token(text[start..i], line));
                    continue;
                }
                tokens.Add(new Token(c.ToString(), line));
                i++;
            }
            return tokens;
        }
    }
}
