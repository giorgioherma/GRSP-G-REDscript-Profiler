using System.Security.Cryptography;
using System.Text.Json;

namespace GRedscript.Resolver;

/// <summary>
/// When a GCO collection index exists, independently verify the sealed
/// collected GRSP files before admitting their raw measurements. Standalone
/// GRSP native captures keep their existing F11/status admission path.
/// </summary>
internal static class GcoCollectionIndex
{
    internal static string Verify(string captureRoot, string nativeRoot)
    {
        string index = Path.Combine(captureRoot, "collection.json");
        if (!File.Exists(index))
            return "NATIVE_F11_ONLY_NO_GCO_INDEX";

        using var doc = JsonDocument.Parse(File.ReadAllText(index));
        var root = doc.RootElement;
        string session = root.GetProperty("SessionId").GetString() ?? "";
        if (session.Length != 32 || !session.All(Uri.IsHexDigit))
            throw new InvalidDataException("GCO collection session identity invalid");

        var files = root.GetProperty("Redscript").GetProperty("Files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
            throw new InvalidDataException("GCO REDscript collection file index missing");

        var indexedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in files.EnumerateArray())
            VerifyFile(nativeRoot, record, indexedPaths, "GRSP");
        foreach (string required in new[] {
            "GRSP_Summary.json",
            "Data/Metadata/GRSP_Status.txt",
            "Data/Metadata/GCO-F11-Complete.flag",
            "Data/Runtime/GRSP_Markers.csv",
            "Data/Developer/RSP_CallSites.csv",
            "Data/Developer/RSP_CallbackPressure.csv",
            "Data/Developer/RSP_CallbackTargets.csv"
        })
            if (!indexedPaths.Contains(required))
                throw new InvalidDataException(
                    "GCO collection index omits required GRSP evidence");

        // Any native CSV consumed by the Resolver must be indexed, not an
        // unsealed extra file silently accepted alongside the trusted set.
        foreach (string bucket in new[] { "Developer", "Runtime" })
        {
            string directory = Path.Combine(nativeRoot, "Data", bucket);
            if (!Directory.Exists(directory))
                throw new InvalidDataException("Missing collected GRSP evidence directory");
            foreach (string file in Directory.EnumerateFiles(directory, "*.csv"))
            {
                string path = "Data/" + bucket + "/" + Path.GetFileName(file);
                if (!indexedPaths.Contains(path))
                    throw new InvalidDataException("Unindexed native GRSP evidence: " + path);
            }
        }

        if (root.TryGetProperty("FrameTimeEvidence", out JsonElement frame) &&
            frame.ValueKind is JsonValueKind.Object)
        {
            var frameIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            VerifyFile(captureRoot, frame, frameIndex, "PresentMon");
        }
        return "GCO_INDEXED_COLLECTED_BYTES_VERIFIED";
    }

    private static void VerifyFile(
        string root, JsonElement record, HashSet<string> indexed, string domain)
    {
        string relative = record.GetProperty("Path").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(relative) ||
            Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Contains('\\') ||
            relative.Split('/').Any(x => x is "" or "." or "..") ||
            !indexed.Add(relative))
            throw new InvalidDataException(domain + " collection file path invalid or duplicated");

        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(fullRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(full) ||
            (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(domain + " indexed collected file missing or linked");

        long size = record.GetProperty("Bytes").GetInt64();
        string expectedHash = record.GetProperty("Sha256").GetString() ?? "";
        if (size < 0 || expectedHash.Length != 64 ||
            !expectedHash.All(Uri.IsHexDigit))
            throw new InvalidDataException(domain + " indexed size/hash invalid");
        using var stream = File.OpenRead(full);
        if (stream.Length != size ||
            !Convert.ToHexString(SHA256.HashData(stream)).Equals(
                expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                domain + " indexed collected file hash/size mismatch");
    }
}
