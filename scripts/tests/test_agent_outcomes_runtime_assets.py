import importlib.util
from pathlib import Path

MODULE_PATH = (
    Path(__file__).resolve().parents[1]
    / "agent-outcomes-runtime"
    / "test_runtime_assets.py"
)
SPEC = importlib.util.spec_from_file_location(
    "agent_outcomes_runtime_assets", MODULE_PATH
)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("runtime asset test module cannot be loaded")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

RuntimeAssetsTests = MODULE.RuntimeAssetsTests
