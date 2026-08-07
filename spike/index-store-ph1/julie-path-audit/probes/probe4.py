import json, os, subprocess, time, shutil, sqlite3
from pathlib import Path

OLD = "/Users/murphy/source/miller/.tools/julie-extract"
NEW = "/Users/murphy/source/julie-extractors/target/release/julie-extract"
SCRATCH = Path(os.environ.get("TMPDIR", "/tmp")) / "miller-ph1-task2"
OUT = Path(os.environ["SP"]) / "probe3-out"
OUT.mkdir(parents=True, exist_ok=True)
fixture = SCRATCH / "fixture"
pristine = SCRATCH / "pristine"
base = SCRATCH / "base.db"

cs_files = sorted(p for p in fixture.rglob("*.cs"))
print(f"changed set: {len(cs_files)} .cs files")


def clone(work):
    for s in ("", "-wal", "-shm"):
        t = Path(str(work) + s)
        if t.exists():
            t.unlink()
    if subprocess.run(["cp", "-c", str(base), str(work)], capture_output=True).returncode != 0:
        shutil.copyfile(base, work)


def touch_all():
    for p in cs_files:
        with open(p, "ab") as h:
            h.write(b"\n")


def restore_all():
    for p in cs_files:
        rel = p.relative_to(fixture)
        shutil.copyfile(pristine / rel, p)


def rr_facts(rep):
    rr = (rep.get("languages") or {}).get("reference_resolution") or {}
    kind = "Full" if rr.get("by_language") else "Delta"
    rows = (rr.get("counts") or {}).get("identifier_resolutions", 0)
    phases = rep.get("profile", {}).get("phases", {})
    res_ms = None
    for k, v in phases.items():
        if "resolution" in k:
            res_ms = v
    return kind, rows, res_ms, phases


res = []
for label, julie in (("old-2.27.0", OLD), ("new-fixed", NEW)):
    work = SCRATCH / f"scan-{label}.db"
    clone(work)
    touch_all()
    argv = [julie, "scan", "--root", str(fixture.resolve()), "--db", str(work.resolve()),
            "--strict-schema", "--json", "--jobs", "4"]
    t = time.monotonic()
    p = subprocess.run(argv, capture_output=True, text=True)
    ms = int((time.monotonic() - t) * 1000)
    restore_all()
    if p.returncode != 0:
        print("FAIL", label, p.returncode, p.stderr[:400])
        continue
    rep = json.loads(p.stdout)
    kind, rows, res_ms, phases = rr_facts(rep)
    r = {"binary": label, "changed_cs_files": len(cs_files), "wall_ms": ms,
         "pass": kind, "identifier_resolutions": rows, "resolution_ms": res_ms}
    res.append(r)
    print(json.dumps(r))
    (OUT / f"scan-{label}.json").write_text(json.dumps(rep, indent=1))
    for s in ("", "-wal", "-shm"):
        t2 = Path(str(work) + s)
        if t2.exists():
            t2.unlink()

(OUT / "results4.json").write_text(json.dumps(res, indent=1))
