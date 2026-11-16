"""Source-owned proxy evidence integration regression: no active source rewrites."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


class ProxyAttributionContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.native = (ROOT / "src" / "lib.rs").read_text(encoding="utf-8")
        cls.proxy = (ROOT / "src" / "proxy_attribution.rs").read_text(encoding="utf-8")
        cls.workflow = (
            ROOT / ".github" / "workflows" / "native-instrumentation.yml"
        ).read_text(encoding="utf-8")

    def test_f11_developer_output_uses_existing_owner_native_writer(self):
        result_body = self.native.split("fn dump_results()", 1)[1].split(
            "#[derive", 1
        )[0]
        self.assertIn("mod proxy_attribution;", self.native)
        self.assertIn("proxy_attribution::export(&developer_dir)?;", result_body)
        self.assertGreater(
            result_body.index("proxy_attribution::export(&developer_dir)?;"),
            result_body.index("callback_pressure::export(&developer_dir)?;"),
        )
        self.assertIn("RSP_ProxyAttribution.csv", self.proxy)

    def test_no_extra_vm_hook_and_unresolved_receiver_stays_unknown(self):
        self.assertNotIn("hooks!", self.proxy)
        self.assertNotIn("attach_hook(", self.proxy)
        self.assertIn("let callsites = CALLSITES.read();", self.proxy)
        self.assertIn("let hot_paths = HOT_PATHS.read();", self.proxy)
        self.assertIn('UNRESOLVED_VIRTUAL_RECEIVER', self.proxy)
        for field in ("receiver_proven", "callback_entries_known",
                      "causality_proven"):
            self.assertIn(field, self.proxy)
        self.assertIn("false, false, false", self.proxy)
        self.assertIn("HOT_PATHS_DROPPED", self.proxy)

    def test_sparse_hot_path_is_aggregated_once_not_per_callsite(self):
        self.assertIn("for event in hot_paths.iter()", self.proxy)
        self.assertIn("path_context.get(&key)", self.proxy)
        self.assertLess(
            self.proxy.index("for event in hot_paths.iter()"),
            self.proxy.index("for (key, stat, meta, target) in rows"),
        )

    def test_native_ci_builds_and_validates_exact_owned_output(self):
        self.assertIn("'src/proxy_attribution.rs'", self.workflow)
        self.assertIn("'tests/test_proxy_attribution_contract.py'", self.workflow)
        self.assertIn("'RSP_ProxyAttribution.csv'", self.workflow)
        self.assertIn("cargo test --lib --locked", self.workflow)
        self.assertIn("cargo build --release --locked", self.workflow)


if __name__ == "__main__":
    unittest.main()
