"""High-value owner contracts: real CLI, output, hashes, source drift and safety."""
import csv
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
DLL = ROOT / "resolver" / "bin" / "Release" / "net10.0" / "GRedscript.Resolver.dll"


def write_csv(path, columns, rows):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as handle:
        w = csv.DictWriter(handle, fieldnames=columns, lineterminator="\n")
        w.writeheader()
        w.writerows(rows)


class GrspResolverContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not DLL.is_file():
            subprocess.run(["dotnet", "build",
                            str(ROOT / "resolver" / "GRSP.Resolver.csproj"),
                            "-c", "Release"], check=True)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.capture = self.root / "capture"
        self.native = self.capture / "G-REDscript"
        self.game = self.root / "game"
        self.output = self.root / "output" / "resolver.json"
        self.source = self.game / "r6" / "scripts" / "Example" / "Example.reds"
        self.source.parent.mkdir(parents=True)
        self.source.write_text(
            'module Example\n'
            'public class ExampleController {\n'
            '  public func Work() -> Void {\n'
            '    let result: Int32 = 1;\n'
            '  }\n'
            '}\n', encoding="utf-8")
        meta = self.native / "Data" / "Metadata"
        meta.mkdir(parents=True)
        (meta / "GCO-F11-Complete.flag").write_text("F11_STOP\n")
        (meta / "GRSP_Status.txt").write_text("State: COMPLETE\n")
        summary = {
            "health": {"frameQuality": "GOOD", "shardMergeOk": True,
                       "droppedSpikes": 0, "droppedHotPaths": 0},
            "capture": {"scenario": "WORLD", "durationSeconds": 2.0},
        }
        (self.native / "GRSP_Summary.json").write_text(json.dumps(summary))
        dev = self.native / "Data" / "Developer"
        runtime = self.native / "Data" / "Runtime"
        sourcepath = r"D:\Games\Cyberpunk 2077\r6\scripts\Example\Example.reds"
        columns = [
            "capture_id", "stable_callsite_id", "stable_function_id",
            "owner", "source_path", "source_function", "source_line",
            "call_kind", "target", "target_resolution",
            "calls", "calls_per_sec", "exclusive_instrumented_ms",
        ]
        row = dict(zip(columns, [
            "1", "RSPC-DUPLICATE", "RSPF-ONE", "Example",
            sourcepath, "Example.ExampleController::Work", "4",
            "static", "SetData", "DIRECT", "2", "1.0", "0.3",
        ]))
        second = dict(row, calls="3", exclusive_instrumented_ms="0.4")
        write_csv(dev / "RSP_CallSites.csv", columns, [row, second])
        for name in (
            "RSP_FunctionMap.csv", "RSP_Cadence.csv", "RSP_WorkMap.csv",
            "RSP_CallbackPressure.csv", "RSP_CallbackTargets.csv",
        ):
            write_csv(dev / name, ["id", "note"], [{"id": "1", "note": "x"}])
        for name in ("GRSP_ByFunction.csv", "GRSP_Frames.csv", "GRSP_ByMod.csv"):
            write_csv(runtime / name, ["id"], [{"id": "1"}])
        write_csv(runtime / "GRSP_Markers.csv", ["event"], [
            {"event": "START"}, {"event": "STOP_REQUEST"}])
        mon = self.capture / "PresentMon" / "PresentMon.csv"
        mon.parent.mkdir(parents=True)
        mon.write_text("qpc,interval\n10,1\n20,1\n", encoding="utf-8")
        self.mon = mon
        chronology = {
            "schemaVersion": "gco.presentmon.chronology.v1",
            "producer": "GCO_PRESENTMON_2_6",
            "correlation": "CET_GRSP_F11_INTERSECTION_VERIFIED",
            "sourceFile": "PresentMon/PresentMon.csv",
            "sourceSha256": hashlib.sha256(mon.read_bytes()).hexdigest(),
            "sourceFrameCount": 2,
            "frames": [
                {"sourceRow": 1, "relativeStartMs": 0.0,
                 "intervalMs": 10.0, "presentQpc": 10},
                {"sourceRow": 2, "relativeStartMs": 10.0,
                 "intervalMs": 10.0, "presentQpc": 20},
            ],
        }
        self.chronology = self.root / "chronology.json"
        self.chronology.write_text(json.dumps(chronology), encoding="utf-8")

    def run_cli(self, *extra, with_frames=True, output=None):
        cmd = ["dotnet", str(DLL), "analyze",
               "--capture", str(self.capture), "--game", str(self.game),
               "--out", str(output or self.output)]
        if with_frames:
            cmd += ["--frame-evidence", str(self.chronology)]
        return subprocess.run(cmd + list(extra), capture_output=True,
                              text=True, timeout=60)

    def run_source_audit(self, output=None):
        return subprocess.run(
            ["dotnet", str(DLL), "source-audit",
             "--game", str(self.game), "--out", str(output or self.output)],
            capture_output=True, text=True, timeout=90)

    def test_read_only_full_source_audit_proves_no_rewrite(self):
        before = self.source.read_bytes()
        r = self.run_source_audit()
        self.assertEqual(0, r.returncode, r.stderr)
        payload = json.loads(self.output.read_text(encoding="utf-8"))
        self.assertEqual("grsp.source.audit.v1", payload["schemaVersion"])
        self.assertEqual(1, payload["sourceFiles"])
        self.assertEqual(1, payload["lexicallyIndexedFiles"])
        self.assertEqual(1, payload["recognizedFunctionSpans"])
        self.assertEqual(0, payload["structurallyUnprovenFiles"])
        self.assertEqual("UNSAFE_OR_UNPROVEN", payload["sourceState"])
        self.assertFalse(payload["codeRewritten"])
        self.assertFalse(payload["compilerValidated"])
        self.assertFalse(payload["privateSourcePathsIncluded"])
        self.assertEqual(before, self.source.read_bytes())
        self.assertNotIn("Example.reds", self.output.read_text())

    def test_source_audit_records_unproven_syntax_without_assuming_proof(self):
        self.source.write_text("public class Broken {\n")
        r = self.run_source_audit()
        self.assertEqual(0, r.returncode, r.stderr)
        payload = json.loads(self.output.read_text())
        self.assertEqual(1, payload["sourceFiles"])
        self.assertEqual(1, payload["structurallyUnprovenFiles"])
        self.assertEqual(0, payload["recognizedFunctionSpans"])
        self.assertFalse(payload["compilerValidated"])

    def test_source_audit_refuses_output_inside_game(self):
        target = self.game / "audit.json"
        r = self.run_source_audit(output=target)
        self.assertNotEqual(0, r.returncode)
        self.assertFalse(target.exists())

    def test_all_rows_preserved_and_source_match_is_not_rewrite_permission(self):
        r = self.run_cli()
        self.assertEqual(0, r.returncode, r.stderr)
        report = json.loads(self.output.read_text(encoding="utf-8"))
        self.assertEqual("grsp.resolver.input.v1", report["schemaVersion"])
        self.assertEqual("PRESENTMON_2_6", report["frameEvidence"]["provider"])
        self.assertEqual(2, len(report["frameEvidence"]["chronology"]["frames"]))
        self.assertEqual(2, report["counts"]["rawCallsiteRows"])
        self.assertEqual(1, report["counts"]["distinctCallsiteIds"])
        self.assertEqual(1, report["counts"]["repeatedCallsiteIdRows"])
        table = report["rawTables"]["Developer/RSP_CallSites.csv"]["rows"]
        self.assertEqual(["2", "3"], [x["calls"] for x in table])
        source = report["sourceObservations"][0]["evidence"]
        self.assertEqual("LINE_METHOD_NAME_MATCH_RECEIVER_UNVERIFIED",
                         source["status"])
        self.assertEqual("UNSAFE_OR_UNPROVEN", source["transformationState"])
        self.assertFalse(report["generationAuthorized"])
        self.assertEqual(1, report["source"]["inspectedDistinctFiles"])

    def test_missing_f11_completion_refuses(self):
        (self.native / "Data" / "Metadata" /
         "GCO-F11-Complete.flag").unlink()
        r = self.run_cli()
        self.assertNotEqual(0, r.returncode)
        self.assertIn("F11", r.stderr)
        self.assertFalse(self.output.exists())

    def test_incomplete_grsp_status_must_not_match_complete(self):
        (self.native / "Data" / "Metadata" /
         "GRSP_Status.txt").write_text("State: INCOMPLETE\n")
        r = self.run_cli()
        self.assertNotEqual(0, r.returncode)
        self.assertIn("not exactly COMPLETE", r.stderr)
        self.assertFalse(self.output.exists())

    def test_corrupt_presentmon_refuses(self):
        self.mon.write_text("changed\n")
        r = self.run_cli()
        self.assertNotEqual(0, r.returncode)
        self.assertIn("hash mismatch", r.stderr)
        self.assertFalse(self.output.exists())

    def test_unknown_frame_evidence_is_not_fake_capframex(self):
        r = self.run_cli(with_frames=False)
        self.assertEqual(0, r.returncode, r.stderr)
        report = json.loads(self.output.read_text())
        self.assertEqual("NOT_CAPTURED", report["frameEvidence"]["provider"])

    def test_source_drift_is_recorded_without_source_rewrite(self):
        before = self.source.read_bytes()
        self.source.write_text("module Example\npublic class Changed {}\n")
        changed = self.source.read_bytes()
        r = self.run_cli()
        self.assertEqual(0, r.returncode, r.stderr)
        report = json.loads(self.output.read_text())
        self.assertEqual("LINE_OUTSIDE_FILE",
                         report["sourceObservations"][0]["evidence"]["status"])
        self.assertEqual(changed, self.source.read_bytes())
        self.assertNotEqual(before, changed)

    def test_unbalanced_source_is_refused_for_admission(self):
        self.source.write_text("public class Example {\n"
                               " public func Work() -> Void {\n")
        r = self.run_cli()
        self.assertEqual(0, r.returncode, r.stderr)
        report = json.loads(self.output.read_text())
        self.assertEqual("SYNTAX_UNPROVEN",
                         report["sourceObservations"][0]["evidence"]["status"])

    def test_output_must_be_outside_game_and_capture(self):
        for output in (self.game / "result.json",
                       self.capture / "forbidden.json"):
            with self.subTest(output=output):
                r = self.run_cli(output=output)
                self.assertNotEqual(0, r.returncode)
                self.assertFalse(output.exists())

    def test_missing_raw_input_fails_closed(self):
        (self.native / "Data" / "Developer" / "RSP_CallbackTargets.csv").unlink()
        r = self.run_cli()
        self.assertNotEqual(0, r.returncode)
        self.assertIn("RSP_CallbackTargets.csv", r.stderr)


if __name__ == "__main__":
    unittest.main()
