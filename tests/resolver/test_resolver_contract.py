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
        write_csv(runtime / "GRSP_Markers.csv",
                  ["event", "qpc", "unix_ms"], [
            {"event": "START", "qpc": "10", "unix_ms": "1794754551000"},
            {"event": "STOP_REQUEST", "qpc": "30",
             "unix_ms": "1794754553000"}])
        mon = self.capture / "PresentMon" / "PresentMon.csv"
        write_csv(mon, ["Application", "CPUStartQPC", "TimeInQPC",
                        "MsBetweenPresents", "MsCPUBusy", "MsGPUBusy",
                        "MsGPUTime"], [
            {"Application": "Cyberpunk2077.exe", "CPUStartQPC": "12",
             "TimeInQPC": "13", "MsBetweenPresents": "100",
             "MsCPUBusy": "90", "MsGPUBusy": "25", "MsGPUTime": "30"},
            {"Application": "Cyberpunk2077.exe", "CPUStartQPC": "22",
             "TimeInQPC": "23", "MsBetweenPresents": "100",
             "MsCPUBusy": "90", "MsGPUBusy": "25", "MsGPUTime": "30"},
        ])
        self.mon = mon
        chronology = {
            "schemaVersion": "gco.presentmon.chronology.v1",
            "producer": "GCO_PRESENTMON_2_6",
            "clock": "WINDOWS_QPC_INTERPOLATED_FROM_GRSP_F11_UNIX_ANCHORS",
            "intervalSemantics":
                "MILLISECONDS_BETWEEN_CONSECUTIVE_PRESENT_EVENTS_NOT_DISPLAY_LATENCY",
            "correlation": "CET_GRSP_F11_INTERSECTION_VERIFIED",
            "cetStartUnixMs": 1794754551000,
            "overlapStartUnixMs": 1794754551000,
            "overlapEndUnixMs": 1794754553000,
            "sourceFile": "PresentMon/PresentMon.csv",
            "sourceSha256": hashlib.sha256(mon.read_bytes()).hexdigest(),
            "sourceFrameCount": 2,
            "frames": [
                {"sourceRow": 1, "cpuStartQpc": 12, "presentQpc": 13,
                 "relativeStartMs": 200.0, "intervalMs": 100.0,
                 "cpuBusyMs": 90.0, "gpuBusyMs": 25.0, "gpuTimeMs": 30.0},
                {"sourceRow": 2, "cpuStartQpc": 22, "presentQpc": 23,
                 "relativeStartMs": 1200.0, "intervalMs": 100.0,
                 "cpuBusyMs": 90.0, "gpuBusyMs": 25.0, "gpuTimeMs": 30.0},
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

    def write_gco_index(self):
        def record(path, relative):
            raw = path.read_bytes()
            return {
                "Path": relative,
                "Bytes": len(raw),
                "Sha256": hashlib.sha256(raw).hexdigest().upper(),
            }

        redscript = [
            record(file, file.relative_to(self.native).as_posix())
            for file in self.native.rglob("*") if file.is_file()
        ]
        index = {
            "SessionId": "17b44e540df4401c8eb72c198dca5c7e",
            "Redscript": {"Files": redscript},
            "FrameTimeEvidence": record(
                self.mon, self.mon.relative_to(self.capture).as_posix()),
        }
        (self.capture / "collection.json").write_text(
            json.dumps(index), encoding="utf-8")
        return index

    def run_source_audit(self, output=None, *extra):
        return subprocess.run(
            ["dotnet", str(DLL), "source-audit",
             "--game", str(self.game), "--out", str(output or self.output)] +
            list(extra),
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

    def test_module_qualified_free_function_is_mapped_read_only(self):
        self.source.write_text(
            'module Example\n'
            'public func Standalone() -> Int32 {\n'
            '  return 3;\n'
            '}\n', encoding='utf-8')
        callsites = (self.native / 'Data' / 'Developer' /
                     'RSP_CallSites.csv')
        with callsites.open(newline='', encoding='utf-8') as handle:
            rows = list(csv.DictReader(handle))
        for row in rows:
            row['source_function'] = 'Example.Standalone'
            row['source_line'] = '3'
        write_csv(callsites, list(rows[0].keys()), rows)
        result = self.run_cli()
        self.assertEqual(0, result.returncode, result.stderr)
        report = json.loads(self.output.read_text(encoding='utf-8'))
        for observation in report['sourceObservations']:
            self.assertEqual(
                'LINE_METHOD_NAME_MATCH_RECEIVER_UNVERIFIED',
                observation['evidence']['status'])
            self.assertEqual('UNSAFE_OR_UNPROVEN',
                             observation['evidence']['transformationState'])
        self.assertFalse(report['generationAuthorized'])

    def test_source_stack_fingerprint_is_stable_and_rejects_source_drift(self):
        before = self.source.read_bytes()
        first = self.run_source_audit()
        self.assertEqual(0, first.returncode, first.stderr)
        audit = json.loads(self.output.read_text())
        identity = audit["sourceStackSha256"]
        self.assertEqual(64, len(identity))
        self.output.unlink()
        second = self.run_source_audit(
            None, "--expected-source-sha256", identity)
        self.assertEqual(0, second.returncode, second.stderr)
        self.assertEqual(identity,
                         json.loads(self.output.read_text())["sourceStackSha256"])
        self.assertEqual(before, self.source.read_bytes())
        self.output.unlink()
        self.source.write_bytes(before + b"// changed after admission\n")
        rejected = self.run_source_audit(
            None, "--expected-source-sha256", identity)
        self.assertNotEqual(0, rejected.returncode)
        self.assertIn("SOURCE_DRIFT_DETECTED", rejected.stderr)
        self.assertFalse(self.output.exists())
        self.source.write_bytes(before)
        restored = self.run_source_audit(
            None, "--expected-source-sha256", identity)
        self.assertEqual(0, restored.returncode, restored.stderr)

    def test_grsp_analysis_requires_admitted_live_stack_when_pinned(self):
        first = self.run_source_audit()
        self.assertEqual(0, first.returncode, first.stderr)
        admitted = json.loads(self.output.read_text())["sourceStackSha256"]
        self.output.unlink()
        accepted = self.run_cli("--expected-source-sha256", admitted)
        self.assertEqual(0, accepted.returncode, accepted.stderr)
        analysis = json.loads(self.output.read_text())
        self.assertEqual(admitted, analysis["source"]["sourceStackSha256"])
        self.assertFalse(analysis["generationAuthorized"])
        self.output.unlink()
        self.source.write_text(self.source.read_text() + "// change\n")
        rejected = self.run_cli("--expected-source-sha256", admitted)
        self.assertNotEqual(0, rejected.returncode)
        self.assertIn("SOURCE_DRIFT_DETECTED", rejected.stderr)
        self.assertFalse(self.output.exists())

    def test_source_stack_fingerprint_detects_added_file(self):
        first = self.run_source_audit()
        self.assertEqual(0, first.returncode, first.stderr)
        admitted = json.loads(self.output.read_text())["sourceStackSha256"]
        self.output.unlink()
        added = self.source.parent / "NewDependency.reds"
        added.write_text("public class NewDependency {}\n")
        rejected = self.run_source_audit(
            None, "--expected-source-sha256", admitted)
        self.assertNotEqual(0, rejected.returncode)
        self.assertIn("SOURCE_DRIFT_DETECTED", rejected.stderr)
        self.assertFalse(self.output.exists())

    def test_source_audit_records_unproven_syntax_without_assuming_proof(self):
        self.source.write_text("public class Broken {\n")
        r = self.run_source_audit()
        self.assertEqual(0, r.returncode, r.stderr)
        payload = json.loads(self.output.read_text())
        self.assertEqual(1, payload["sourceFiles"])
        self.assertEqual(1, payload["structurallyUnprovenFiles"])
        self.assertEqual(0, payload["recognizedFunctionSpans"])
        self.assertFalse(payload["compilerValidated"])

    def test_real_redscript_declaration_forms_are_indexed_without_rewrite(self):
        self.source.write_text(
            'module Example\n'
            'public native func External() -> Bool\n'
            'public abstract class Base {\n'
            '  public func Missing() -> Bool;\n'
            '  public static func Size() -> Int32 = 42\n'
            '  public static func Palette() -> array<Int32> = [\n'
            '    1, 2\n'
            '  ]\n'
            '  public func Live() -> Int32 { return 42; }\n'
            '}\n', encoding='utf-8')
        before = self.source.read_bytes()
        result = self.run_source_audit()
        self.assertEqual(0, result.returncode, result.stderr)
        audit = json.loads(self.output.read_text(encoding='utf-8'))
        self.assertEqual(1, audit['lexicallyIndexedFiles'])
        self.assertEqual(0, audit['structurallyUnprovenFiles'])
        self.assertEqual(3, audit['recognizedFunctionSpans'])
        self.assertEqual(1, audit['nativeExternalDeclarations'])
        self.assertEqual(1, audit['declarationOnlySignatures'])
        self.assertEqual(2, audit['expressionBodiedFunctions'])
        self.assertFalse(audit['codeRewritten'])
        self.assertEqual(before, self.source.read_bytes())

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

    def test_indexed_gco_capture_proves_all_raw_evidence_hashes(self):
        self.write_gco_index()
        result = self.run_cli()
        self.assertEqual(0, result.returncode, result.stderr)
        report = json.loads(self.output.read_text(encoding="utf-8"))
        self.assertEqual("GCO_INDEXED_COLLECTED_BYTES_VERIFIED",
                         report["capture"]["collectionProvenance"])
        self.assertEqual(2, report["frameEvidence"]
                         ["sourceRowsVerifiedAgainstOriginalCsv"])
        self.assertTrue(report["frameEvidence"]
                        ["nativeF11QpcUnixAnchorsVerified"])

    def test_tampered_indexed_native_callsites_refuses_before_analysis(self):
        self.write_gco_index()
        file = self.native / "Data" / "Developer" / "RSP_CallSites.csv"
        with file.open("a", encoding="utf-8") as handle:
            handle.write("# unsealed extra callsite\n")
        result = self.run_cli()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("indexed collected file hash/size mismatch", result.stderr)
        self.assertFalse(self.output.exists())

    def test_missing_indexed_callback_evidence_refuses(self):
        index = self.write_gco_index()
        index["Redscript"]["Files"] = [
            item for item in index["Redscript"]["Files"]
            if not item["Path"].endswith("RSP_CallbackTargets.csv")
        ]
        (self.capture / "collection.json").write_text(json.dumps(index))
        result = self.run_cli()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("omits required GRSP evidence", result.stderr)
        self.assertFalse(self.output.exists())

    def test_malformed_index_refuses_instead_of_standalone_fallback(self):
        (self.capture / "collection.json").write_text("{bad-index")
        result = self.run_cli()
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(self.output.exists())

    def prepare_measured_membership(self, altered=None):
        original = (
            'module Example\n'
            'public class ExampleController {\n'
            '  public let names: array<CName>;\n'
            '  protected cb func Work() -> Void {\n'
            '    let matched: Bool = false;\n'
            '    let actionName: CName = n"alpha";\n'
            '    for entry in this.names {\n'
            '      if Equals(entry, actionName) {\n'
            '        matched = true;\n'
            '        break;\n'
            '      }\n'
            '    }\n'
            '    if matched { let result: Bool = true; }\n'
            '  }\n'
            '}\n'
        )
        self.source.write_text(
            altered(original) if altered else original, encoding='utf-8')
        native = self.native / 'Data' / 'Developer' / 'RSP_CallSites.csv'
        with native.open(newline='', encoding='utf-8') as handle:
            rows = list(csv.DictReader(handle))
        for i, row in enumerate(rows):
            row['source_line'] = '7'
            row['source_function'] = 'Example.ExampleController::Work'
            row['target'] = ['OperatorLess', 'OperatorAssignAdd'][i]
            row['calls'] = ['100', '200'][i]
            row['stable_callsite_id'] = 'RSPC-' + str(i)
        write_csv(native, list(rows[0].keys()), rows)
        self.write_gco_index()

    def preview_generic(self, analysis=None, target=None):
        root = target or self.root / 'preview'
        result = subprocess.run(
            ['dotnet', str(DLL), 'preview-generic',
             '--game', str(self.game), '--analysis', str(analysis or self.output),
             '--out', str(root)],
            capture_output=True, text=True, timeout=90)
        return result, root

    def scan_generic(self, analysis=None, target=None):
        report = target or self.root / 'scan' / 'opportunities.json'
        cmd = ['dotnet', str(DLL), 'scan-generic',
               '--game', str(self.game), '--out', str(report)]
        if analysis is not None:
            cmd += ['--analysis', str(analysis)]
        return subprocess.run(
            cmd, capture_output=True, text=True, timeout=120), report

    def test_source_wide_scanner_identifies_measured_proven_shape_read_only(self):
        self.prepare_measured_membership()
        before = self.source.read_bytes()
        analyzed = self.run_cli()
        self.assertEqual(0, analyzed.returncode, analyzed.stderr)
        result, path = self.scan_generic(analysis=self.output)
        self.assertEqual(0, result.returncode, result.stderr)
        scan = json.loads(path.read_text())
        self.assertEqual('grsp.generic.opportunities.v1', scan['schemaVersion'])
        self.assertEqual('CNAME_ARRAY_MEMBERSHIP_V1', scan['rule'])
        self.assertEqual(1, scan['sourceFiles'])
        self.assertEqual(1, scan['candidateLoopsExamined'])
        self.assertEqual(1, scan['eligibleMeasured'])
        self.assertEqual(0, scan['eligibleUnmeasured'])
        self.assertEqual('ELIGIBLE_MEASURED', scan['candidates'][0]['state'])
        self.assertEqual(300, scan['candidates'][0]['measuredLoopOperatorEdges'])
        self.assertFalse(scan['candidates'][0]['rewriteAuthorized'])
        self.assertFalse(scan['generationAuthorized'])
        self.assertFalse(scan['deployable'])
        self.assertEqual(0, scan['generatedFiles'])
        self.assertTrue(scan['pathsOnlyInPrivateLocalReport'])
        self.assertNotIn('Example.reds', result.stdout)
        self.assertEqual(before, self.source.read_bytes())
        self.assertFalse((self.root / 'scan' / 'overlay').exists())

    def test_source_wide_scanner_finds_multiple_unmeasured_mods(self):
        self.prepare_measured_membership()
        another = self.game / 'r6/scripts/AnotherOwner/Other.reds'
        another.parent.mkdir(parents=True)
        another.write_bytes(self.source.read_bytes())
        initial = (self.source.read_bytes(), another.read_bytes())
        result, path = self.scan_generic()
        self.assertEqual(0, result.returncode, result.stderr)
        scan = json.loads(path.read_text())
        self.assertEqual(2, scan['sourceFiles'])
        self.assertEqual(2, scan['candidateLoopsExamined'])
        self.assertEqual(2, scan['eligibleUnmeasured'])
        self.assertEqual(0, scan['eligibleMeasured'])
        self.assertEqual({'AnotherOwner', 'Example'},
                         {x['owner'] for x in scan['candidates']})
        self.assertEqual('NOT_CAPTURED', scan['evidenceKind'])
        self.assertEqual(initial, (self.source.read_bytes(), another.read_bytes()))

    def test_source_wide_scanner_refuses_side_effects_and_fake_text_hits(self):
        self.prepare_measured_membership(
            lambda s: s.replace('matched = true;',
                                'DoSomething();\n        matched = true;'))
        self.source.write_text(
            self.source.read_text() + '\n// for entry in this.names { if Equals(x, y) {} }\n'
            '// "for entry in this.names { if Equals(x, y) {} }"\n')
        result, path = self.scan_generic()
        self.assertEqual(0, result.returncode, result.stderr)
        scan = json.loads(path.read_text())
        self.assertEqual(1, scan['candidateLoopsExamined'])
        self.assertEqual(1, scan['rejectedUnproven'])
        self.assertEqual(0, scan['structuralMatches'])
        self.assertEqual('REJECTED_UNPROVEN', scan['candidates'][0]['state'])

    def test_source_wide_scanner_refuses_stale_capture_and_unsafe_output(self):
        self.prepare_measured_membership()
        self.assertEqual(0, self.run_cli().returncode)
        self.source.write_text(self.source.read_text() + '// source drift\n')
        refused, path = self.scan_generic(analysis=self.output)
        self.assertNotEqual(0, refused.returncode)
        self.assertIn('SOURCE_DRIFT_DETECTED', refused.stderr)
        self.assertFalse(path.exists())
        original = self.source.read_bytes()
        refused, _ = self.scan_generic(target=self.game / 'bad.json')
        self.assertNotEqual(0, refused.returncode)
        self.assertIn('SCAN_OUTPUT_NOT_ISOLATED', refused.stderr)
        self.assertFalse((self.game / 'bad.json').exists())
        sentinel = self.root / 'existing.json'
        sentinel.write_text('keep')
        refused, _ = self.scan_generic(target=sentinel)
        self.assertNotEqual(0, refused.returncode)
        self.assertEqual('keep', sentinel.read_text())
        self.assertEqual(original, self.source.read_bytes())

    def test_measured_generic_membership_is_staged_only_and_idempotent(self):
        self.prepare_measured_membership()
        original = self.source.read_bytes()
        analyzed = self.run_cli()
        self.assertEqual(0, analyzed.returncode, analyzed.stderr)
        preview, folder = self.preview_generic()
        self.assertEqual(0, preview.returncode, preview.stderr)
        plan = json.loads((folder / 'preview.json').read_text())
        self.assertEqual('grsp.generic.preview.v1', plan['schemaVersion'])
        self.assertEqual('CNAME_ARRAY_MEMBERSHIP_V1', plan['rule'])
        self.assertEqual(1, plan['stagedFiles'])
        self.assertEqual(1, plan['applicableFileCount'])
        self.assertEqual(300, plan['measuredOperatorEdgesInApplicableLoops'])
        self.assertEqual('PRISTINE_APPLICABLE',
                         plan['candidates'][0]['state'])
        self.assertFalse(plan['deployable'])
        self.assertFalse(plan['generationAuthorized'])
        self.assertFalse(plan['compilerValidated'])
        staged = folder / 'overlay/r6/scripts/Example/Example.reds'
        transformed = staged.read_text()
        self.assertIn('matched = ArrayContains(this.names, actionName);',
                      transformed)
        self.assertIn('GRSP-GENERIC:CNAME_ARRAY_MEMBERSHIP_V1', transformed)
        self.assertNotIn('for entry in this.names', transformed)
        self.assertEqual(original, self.source.read_bytes())

        # Simulate a partially/fully optimized installed stack. A fresh
        # authoritative evidence analysis of that current source must see
        # verified completed structure and propose zero additional files.
        self.source.write_bytes(staged.read_bytes())
        self.output.unlink()
        analyzed = self.run_cli()
        self.assertEqual(0, analyzed.returncode, analyzed.stderr)
        second, secondFolder = self.preview_generic(
            target=self.root / 'second')
        self.assertEqual(0, second.returncode, second.stderr)
        report = json.loads((secondFolder / 'preview.json').read_text())
        self.assertEqual('ALREADY_COMPLETE', report['candidates'][0]['state'])
        self.assertEqual(0, report['stagedFiles'])
        self.assertEqual(1, report['alreadyCompleteFileCount'])

    def test_generic_membership_partial_source_generates_only_missing_work(self):
        def partial(source):
            return source.replace(
                '    if matched {',
                '    let other: Bool = false;\n'
                '    let otherAction: CName = n"beta";\n'
                '    // GRSP-GENERIC:CNAME_ARRAY_MEMBERSHIP_V1\n'
                '    other = ArrayContains(this.names, otherAction);\n'
                '    if matched {')
        self.prepare_measured_membership(partial)
        baseline = self.source.read_bytes()
        analyzed = self.run_cli()
        self.assertEqual(0, analyzed.returncode, analyzed.stderr)
        preview, folder = self.preview_generic()
        self.assertEqual(0, preview.returncode, preview.stderr)
        plan = json.loads((folder / 'preview.json').read_text())
        self.assertEqual('PARTIALLY_APPLICABLE',
                         plan['candidates'][0]['state'])
        self.assertEqual(1, plan['stagedFiles'])
        staged = (folder / 'overlay/r6/scripts/Example/Example.reds').read_text()
        self.assertEqual(2, staged.count('GRSP-GENERIC:CNAME_ARRAY_MEMBERSHIP_V1'))
        self.assertNotIn('for entry in this.names', staged)
        self.assertEqual(baseline, self.source.read_bytes())

    def test_generic_membership_refuses_corrupt_partial_marker(self):
        def partial(source):
            return source.replace(
                '    if matched {',
                '    let other: Bool = false;\n'
                '    // GRSP-GENERIC:CNAME_ARRAY_MEMBERSHIP_V1\n'
                '    other = false;\n'
                '    if matched {')
        self.prepare_measured_membership(partial)
        analyzed = self.run_cli()
        self.assertEqual(0, analyzed.returncode, analyzed.stderr)
        preview, folder = self.preview_generic()
        self.assertEqual(0, preview.returncode, preview.stderr)
        plan = json.loads((folder / 'preview.json').read_text())
        self.assertEqual('UNPROVEN_OR_DRIFTED',
                         plan['candidates'][0]['state'])
        self.assertEqual(0, plan['stagedFiles'])

    def test_generic_membership_requires_cname_needle_type(self):
        self.prepare_measured_membership(lambda text: text.replace(
            'let actionName: CName = n"alpha";',
            'let actionName: String = "alpha";'))
        analyzed = self.run_cli()
        self.assertEqual(0, analyzed.returncode, analyzed.stderr)
        preview, folder = self.preview_generic()
        self.assertEqual(0, preview.returncode, preview.stderr)
        plan = json.loads((folder / 'preview.json').read_text())
        self.assertEqual('UNPROVEN_OR_DRIFTED',
                         plan['candidates'][0]['state'])
        self.assertEqual(0, plan['stagedFiles'])

    def test_generic_membership_refuses_side_effects_in_loop(self):
        self.prepare_measured_membership(
            lambda text: text.replace('matched = true;',
                                      'DoSomething();\n        matched = true;'))
        analyzed = self.run_cli()
        self.assertEqual(0, analyzed.returncode, analyzed.stderr)
        preview, folder = self.preview_generic()
        self.assertEqual(0, preview.returncode, preview.stderr)
        plan = json.loads((folder / 'preview.json').read_text())
        self.assertEqual(0, plan['stagedFiles'])
        self.assertEqual('UNPROVEN_OR_DRIFTED',
                         plan['candidates'][0]['state'])

    def test_generic_membership_refuses_nonfalse_initial_flag(self):
        self.prepare_measured_membership(
            lambda text: text.replace('let matched: Bool = false;',
                                      'let matched: Bool = true;'))
        analyzed = self.run_cli()
        self.assertEqual(0, analyzed.returncode, analyzed.stderr)
        preview, folder = self.preview_generic()
        self.assertEqual(0, preview.returncode, preview.stderr)
        plan = json.loads((folder / 'preview.json').read_text())
        self.assertEqual(0, plan['stagedFiles'])
        self.assertEqual('UNPROVEN_OR_DRIFTED',
                         plan['candidates'][0]['state'])

    def test_generic_membership_refuses_analysis_to_source_drift(self):
        self.prepare_measured_membership()
        analyzed = self.run_cli()
        self.assertEqual(0, analyzed.returncode, analyzed.stderr)
        self.source.write_text(self.source.read_text() + '// changed\n')
        preview, folder = self.preview_generic()
        self.assertNotEqual(0, preview.returncode)
        self.assertIn('SOURCE_DRIFT_DETECTED', preview.stderr)
        self.assertFalse(folder.exists())

    def test_headless_scc_refuses_unmarked_live_game_root(self):
        cache = self.game / 'r6' / 'cache' / 'final.redscripts'
        cache.parent.mkdir(parents=True)
        cache.write_bytes(b'not a real cache')
        source_before = self.source.read_bytes()
        cache_before = cache.read_bytes()
        report = self.root / 'compiled-report.json'
        cmd = [
            'dotnet', str(DLL), 'scc-headless',
            '--workspace', str(self.game),
            '--r6', str(self.game / 'r6'),
            '--cache', str(cache),
            '--compiled', str(cache) + '.modded',
            '--scc-native', str(cache),
            '--out', str(report),
            '--log', str(self.root / 'compile-errors.log')
        ]
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('explicitly isolated', result.stderr)
        self.assertFalse(report.exists())
        self.assertFalse(Path(str(cache) + '.modded').exists())
        self.assertEqual(cache_before, cache.read_bytes())
        self.assertEqual(source_before, self.source.read_bytes())

    def test_headless_scc_refuses_overwriting_vanilla_cache(self):
        cache = self.game / 'r6' / 'cache' / 'final.redscripts'
        cache.parent.mkdir(parents=True)
        cache.write_bytes(b'not a real cache')
        (self.game / '.grsp-r6-workspace').write_text(
            'GRSP_R6_ISOLATED_WORKSPACE')
        cmd = [
            'dotnet', str(DLL), 'scc-headless',
            '--workspace', str(self.game),
            '--r6', str(self.game / 'r6'),
            '--cache', str(cache),
            '--compiled', str(cache),
            '--scc-native', str(cache),
            '--out', str(self.root / 'compiler-result.json'),
            '--log', str(self.root / 'compiler-diagnostic.log')
        ]
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('explicitly isolated', result.stderr)
        self.assertEqual(b'not a real cache', cache.read_bytes())

    def test_generic_preview_refuses_writes_to_live_game(self):
        self.prepare_measured_membership()
        analyzed = self.run_cli()
        self.assertEqual(0, analyzed.returncode, analyzed.stderr)
        original = self.source.read_bytes()
        preview, _ = self.preview_generic(target=self.game / 'bad-preview')
        self.assertNotEqual(0, preview.returncode)
        self.assertEqual(original, self.source.read_bytes())

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

    def test_monotonic_but_fabricated_frame_qpc_refuses(self):
        chronology = json.loads(self.chronology.read_text())
        chronology["frames"][0]["cpuStartQpc"] = 11
        self.chronology.write_text(json.dumps(chronology))
        result = self.run_cli()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("original CSV row", result.stderr)
        self.assertFalse(self.output.exists())

    def test_changed_present_interval_refuses(self):
        chronology = json.loads(self.chronology.read_text())
        chronology["frames"][1]["intervalMs"] = 99.0
        self.chronology.write_text(json.dumps(chronology))
        result = self.run_cli()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("original CSV row", result.stderr)

    def test_fabricated_optional_cpu_busy_refuses(self):
        chronology = json.loads(self.chronology.read_text())
        chronology["frames"][1]["cpuBusyMs"] = 0.1
        self.chronology.write_text(json.dumps(chronology))
        result = self.run_cli()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("optional metric disagrees", result.stderr)

    def test_changed_native_f11_clock_refuses(self):
        path = (self.native / "Data" / "Runtime" / "GRSP_Markers.csv")
        write_csv(path, ["event", "qpc", "unix_ms"], [
            {"event": "START", "qpc": "9", "unix_ms": "1794754551000"},
            {"event": "STOP_REQUEST", "qpc": "30",
             "unix_ms": "1794754553000"},
        ])
        result = self.run_cli()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("QPC/Unix anchors", result.stderr)

    def test_dropped_original_frame_refuses_even_with_valid_hash(self):
        with self.mon.open("a", encoding="utf-8") as handle:
            handle.write("Cyberpunk2077.exe,25,26,100,90,25,30\n")
        chronology = json.loads(self.chronology.read_text())
        chronology["sourceSha256"] = hashlib.sha256(self.mon.read_bytes()).hexdigest()
        self.chronology.write_text(json.dumps(chronology))
        result = self.run_cli()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("omits original game frames", result.stderr)

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
