using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace GRedscript.Resolver;

/// <summary>
/// Independently checks GCO's derived frame chronology against every original
/// PresentMon CSV row and the GRSP F11 QPC/Unix markers. This is evidence
/// verification, not another clock conversion for profiling/reporting.
/// </summary>
internal static class PresentMonEvidence
{
    internal static object Read(
        IReadOnlyDictionary<string, string> options, string captureRoot)
    {
        if (!options.TryGetValue("--frame-evidence", out string? handoff))
            return new { provider = "NOT_CAPTURED", status = "NO_FRAME_EVIDENCE_PROVIDED" };

        using var doc = JsonDocument.Parse(File.ReadAllText(handoff));
        JsonElement root = doc.RootElement;
        if (Value(root, "schemaVersion") != "gco.presentmon.chronology.v1" ||
            Value(root, "producer") != "GCO_PRESENTMON_2_6" ||
            Value(root, "correlation") != "CET_GRSP_F11_INTERSECTION_VERIFIED" ||
            Value(root, "intervalSemantics") !=
                "MILLISECONDS_BETWEEN_CONSECUTIVE_PRESENT_EVENTS_NOT_DISPLAY_LATENCY" ||
            Value(root, "clock") !=
                "WINDOWS_QPC_INTERPOLATED_FROM_GRSP_F11_UNIX_ANCHORS")
            throw new InvalidDataException("Unverified/non-PresentMon chronology");

        string relative = Value(root, "sourceFile");
        if (string.IsNullOrWhiteSpace(relative) ||
            Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Contains('\\') ||
            relative.Split('/').Any(x => x is "" or "." or ".."))
            throw new InvalidDataException("Invalid frame source path");

        string parent = Path.GetFullPath(captureRoot).TrimEnd(Path.DirectorySeparatorChar);
        string csv = Path.GetFullPath(Path.Combine(parent,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!csv.StartsWith(parent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(csv) ||
            (File.GetAttributes(csv) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Original PresentMon CSV missing or linked");

        using (var file = File.OpenRead(csv))
        {
            string hash = Convert.ToHexString(SHA256.HashData(file));
            if (!hash.Equals(Value(root, "sourceSha256"),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PresentMon original CSV hash mismatch");
        }

        JsonElement frames = root.GetProperty("frames");
        int total = root.GetProperty("sourceFrameCount").GetInt32();
        if (frames.ValueKind != JsonValueKind.Array || total < 2 ||
            frames.GetArrayLength() != total)
            throw new InvalidDataException("PresentMon frames missing or truncated");

        string markerRoot = Directory.Exists(Path.Combine(parent, "G-REDscript"))
            ? Path.Combine(parent, "G-REDscript") : parent;
        var (qStart, qStop, uStart, uStop) = ReadGrspMarkers(
            Path.Combine(markerRoot, "Data", "Runtime", "GRSP_Markers.csv"));
        long overlapStart = root.GetProperty("overlapStartUnixMs").GetInt64();
        long overlapStop = root.GetProperty("overlapEndUnixMs").GetInt64();
        long cetStart = root.GetProperty("cetStartUnixMs").GetInt64();
        if (overlapStart < uStart || overlapStop > uStop ||
            overlapStart >= overlapStop || cetStart > overlapStart ||
            cetStart < 1_000_000_000_000L)
            throw new InvalidDataException("PresentMon F11 overlap disagrees with GRSP markers");

        using var parser = new TextFieldParser(csv);
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        string[] header = parser.ReadFields() ??
            throw new InvalidDataException("PresentMon original CSV header missing");
        int app = Required(header, "Application");
        int cpuColumn = Required(header, "CPUStartQPC");
        int presentColumn = Required(header, "TimeInQPC");
        int intervalColumn = Required(header, "MsBetweenPresents");
        int cpuBusy = Array.IndexOf(header, "MsCPUBusy");
        int gpuBusy = Array.IndexOf(header, "MsGPUBusy");
        int gpuTime = Array.IndexOf(header, "MsGPUTime");
        if (header.Distinct(StringComparer.Ordinal).Count() != header.Length)
            throw new InvalidDataException("Duplicate PresentMon CSV column");

        long sourceRow = 0, previousQpc = 0, previousCpu = 0;
        double previousStart = double.NegativeInfinity;
        int checkedFrames = 0;
        foreach (JsonElement frame in frames.EnumerateArray())
        {
            string[] fields;
            do
            {
                if (parser.EndOfData)
                    throw new InvalidDataException("PresentMon chronology contains fabricated frames");
                fields = parser.ReadFields() ??
                    throw new InvalidDataException("Original PresentMon row unreadable");
                sourceRow++;
                if (fields.Length != header.Length)
                    throw new InvalidDataException("Original PresentMon row width mismatch");
            } while (!fields[app].Equals("Cyberpunk2077.exe",
                           StringComparison.OrdinalIgnoreCase));

            long csvCpu = PositiveLong(fields[cpuColumn], "CPUStartQPC");
            long csvPresent = PositiveLong(fields[presentColumn], "TimeInQPC");
            double csvInterval = PositiveDouble(fields[intervalColumn], "MsBetweenPresents");
            long frameRow = frame.GetProperty("sourceRow").GetInt64();
            long frameCpu = frame.GetProperty("cpuStartQpc").GetInt64();
            long framePresent = frame.GetProperty("presentQpc").GetInt64();
            double interval = frame.GetProperty("intervalMs").GetDouble();
            double start = frame.GetProperty("relativeStartMs").GetDouble();
            if (frameRow != sourceRow || frameCpu != csvCpu ||
                framePresent != csvPresent || !Close(interval, csvInterval) ||
                !double.IsFinite(start) || start < previousStart ||
                csvPresent <= previousQpc || csvCpu < previousCpu ||
                csvPresent < csvCpu)
                throw new InvalidDataException(
                    "PresentMon frame does not match its original CSV row or chronology");

            CheckOptional(frame, "cpuBusyMs", fields, cpuBusy);
            CheckOptional(frame, "gpuBusyMs", fields, gpuBusy);
            CheckOptional(frame, "gpuTimeMs", fields, gpuTime);

            decimal projectedCpu = ProjectQpc(csvCpu, qStart, qStop, uStart, uStop);
            long roundedCpu = decimal.ToInt64(decimal.Round(
                projectedCpu, 0, MidpointRounding.AwayFromZero));
            if (roundedCpu < overlapStart || roundedCpu >= overlapStop)
                throw new InvalidDataException("PresentMon frame outside common F11 overlap");
            decimal projectedPresent = ProjectQpc(
                csvPresent, qStart, qStop, uStart, uStop);
            double derivedStart = (double)(projectedPresent - cetStart) - csvInterval;
            if (Math.Abs(start - derivedStart) > 0.05)
                throw new InvalidDataException(
                    "PresentMon frame time disagrees with verified GRSP QPC/Unix anchors");

            previousQpc = csvPresent;
            previousCpu = csvCpu;
            previousStart = start;
            checkedFrames++;
        }

        // Reject omitted original game frames, even when the chronology has
        // valid monotonic rows. GCO's collected CSV is game-only and sealed.
        while (!parser.EndOfData)
        {
            string[] remaining = parser.ReadFields() ??
                throw new InvalidDataException("Unreadable remaining PresentMon row");
            if (remaining.Length != header.Length)
                throw new InvalidDataException("Invalid remaining PresentMon row width");
            if (remaining[app].Equals("Cyberpunk2077.exe",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PresentMon chronology omits original game frames");
        }
        if (checkedFrames != total)
            throw new InvalidDataException("PresentMon chronology frame count mismatch");

        return new {
            provider = "PRESENTMON_2_6",
            intervalSemantics = "PRESENT_TO_PRESENT_NOT_DISPLAY_LATENCY",
            observationOnly = true,
            sourceRowsVerifiedAgainstOriginalCsv = checkedFrames,
            nativeF11QpcUnixAnchorsVerified = true,
            chronology = root.Clone()
        };
    }

    private static string Value(JsonElement element, string field) =>
        element.GetProperty(field).GetString() ?? "";

    private static int Required(IReadOnlyList<string> names, string field)
    {
        for (int i = 0; i < names.Count; i++)
            if (names[i] == field) return i;
        throw new InvalidDataException("PresentMon source column missing: " + field);
    }

    private static long PositiveLong(string value, string field) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
            out long n) && n > 0
            ? n : throw new InvalidDataException("Invalid PresentMon QPC: " + field);

    private static double PositiveDouble(string value, string field) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
            out double n) && double.IsFinite(n) && n > 0
            ? n : throw new InvalidDataException("Invalid PresentMon interval: " + field);

    private static bool Close(double a, double b) =>
        double.IsFinite(a) && double.IsFinite(b) &&
        Math.Abs(a - b) <= 0.000001;

    private static void CheckOptional(
        JsonElement frame, string field, IReadOnlyList<string> csv, int index)
    {
        JsonElement value = frame.GetProperty(field);
        if (index < 0 || string.IsNullOrWhiteSpace(csv[index]))
        {
            if (value.ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("Unmeasured PresentMon metric was fabricated");
            return;
        }
        if (!double.TryParse(csv[index], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double original) ||
            !double.IsFinite(original) || original < 0 ||
            value.ValueKind != JsonValueKind.Number ||
            !Close(value.GetDouble(), original))
            throw new InvalidDataException("PresentMon optional metric disagrees with original CSV");
    }

    private static decimal ProjectQpc(
        long tick, long q0, long q1, long u0, long u1) =>
        (decimal)u0 + ((decimal)tick - q0) * (u1 - u0) / (q1 - q0);

    private static (long Q0, long Q1, long U0, long U1) ReadGrspMarkers(string path)
    {
        if (!File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("GRSP F11 QPC/Unix markers missing");
        using var parser = new TextFieldParser(path);
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        var header = parser.ReadFields() ??
            throw new InvalidDataException("GRSP marker CSV header missing");
        int eventColumn = Required(header, "event");
        int qpcColumn = Required(header, "qpc");
        int unixColumn = Required(header, "unix_ms");
        long q0 = 0, q1 = 0, u0 = 0, u1 = 0;
        int starts = 0, stops = 0;
        while (!parser.EndOfData)
        {
            var row = parser.ReadFields() ??
                throw new InvalidDataException("Invalid native F11 marker row");
            if (row.Length != header.Length)
                throw new InvalidDataException("Malformed native F11 marker");
            if (row[eventColumn] == "START")
            {
                q0 = PositiveLong(row[qpcColumn], "GRSP F11 START");
                u0 = PositiveLong(row[unixColumn], "GRSP START unix_ms");
                starts++;
            }
            else if (row[eventColumn] == "STOP_REQUEST")
            {
                q1 = PositiveLong(row[qpcColumn], "GRSP F11 STOP");
                u1 = PositiveLong(row[unixColumn], "GRSP STOP unix_ms");
                stops++;
            }
        }
        if (starts != 1 || stops != 1 || q1 <= q0 || u1 <= u0 ||
            u0 < 1_000_000_000_000L)
            throw new InvalidDataException("GRSP F11 QPC/Unix marker pair ambiguous or invalid");
        return (q0, q1, u0, u1);
    }
}
