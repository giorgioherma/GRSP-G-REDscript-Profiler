"""Source-level regression guard for GRSP's independent callback evidence writer.

Rust unit tests in src/callback_pressure.rs test classification and burst
statistics on real compiled code; this test protects integration boundaries.
"""
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent

class CallbackInstrumentationContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.native = (ROOT / "src" / "lib.rs").read_text(encoding="utf-8")
        cls.callback = (ROOT / "src" / "callback_pressure.rs").read_text(encoding="utf-8")
        cls.workflow = (ROOT / ".github" / "workflows" / "native-instrumentation.yml").read_text(encoding="utf-8")

    def test_callback_csv_written_by_native_after_stop(self):
        body = self.native.split("fn dump_results()", 1)[1].split("#[derive", 1)[0]
        self.assertIn('callback_pressure::export(&developer_dir)?', body)
        self.assertGreater(body.index('callback_pressure::export(&developer_dir)?'),
                           body.index('dump_work_map_csv(&developer_dir.join("RSP_WorkMap.csv"))?'))
        self.assertIn('mod callback_pressure;', self.native)
        self.assertIn('not(feature = "gco_explicit_f11")', self.native)
        self.assertIn('if dump_ok && play_stop_signal', self.native)
        self.assertIn('GCO-F11-Complete.flag', self.native)
        self.assertIn('RSP_CallbackPressure.csv', self.callback)
        self.assertIn('RSP_CallbackTargets.csv', self.callback)

    def test_no_new_hooks_or_false_callback_entry_claims(self):
        self.assertNotIn('hooks!', self.callback)
        self.assertNotIn('attach_hook(', self.callback)
        self.assertIn('OUTGOING_EDGES_OBSERVED_CALLBACK_ENTRIES_UNKNOWN', self.callback)
        self.assertIn('NO_OUTGOING_EDGES_OBSERVED_CALLBACK_ENTRIES_UNKNOWN', self.callback)
        self.assertIn('UNRESOLVED_TARGET_NO_CAUSAL_ATTRIBUTION', self.callback)
        self.assertIn('callback_frame_distribution', self.callback.replace('fn frame_pressure', 'callback_frame_distribution'))

    def test_native_package_requires_manual_dispatch(self):
        self.assertIn('workflow_dispatch:', self.workflow)
        self.assertIn("github.event_name == 'workflow_dispatch'", self.workflow)
        self.assertIn("cargo test --lib --locked", self.workflow)
        self.assertIn("cargo build --release --locked", self.workflow)
        self.assertIn("SHA256.txt", self.workflow)
        self.assertIn("--features gco_explicit_f11", self.workflow)
        self.assertIn("strictF11StopOnly=$true", self.workflow)
        self.assertIn("provenance.json", self.workflow)
        self.assertIn("GRSP-native-callback-instrumentation-", self.workflow)
        self.assertNotIn('GCO_', self.workflow)

if __name__ == "__main__":
    unittest.main()
