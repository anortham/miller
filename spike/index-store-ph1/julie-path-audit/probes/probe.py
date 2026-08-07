import json, os, sqlite3, subprocess, sys, time, shutil
from pathlib import Path

JULIE = "/Users/murphy/source/miller/.tools/julie-extract"
REPO  = "/Users/murphy/source/miller/.claude/worktrees/index-store-ph1"
REV   = "0ec78eec"
SCRATCH = Path(os.environ.get("TMPDIR", "/tmp")) / "miller-ph1-task2"
OUT = Path("/tmp/ph1-task2/out"); OUT.mkdir(parents=True, exist_ok=True)

def sh(argv, **kw):
    t = time.monotonic()
    p = subprocess.run(argv, capture_output=True, text=True, **kw)
    return p, int((time.monotonic()-t)*1000)

def extract_tree(dest):
    if dest.exists(): shutil.rmtree(dest)
    dest.mkdir(parents=True)
    a = subprocess.Popen(["git","-C",REPO,"archive",REV], stdout=subprocess.PIPE)
    subprocess.run(["tar","-x","-C",str(dest)], stdin=a.stdout, check=True); a.wait()

def clone_db(base, work):
    for s in ("","-wal","-shm"):
        t = Path(str(work)+s)
        if t.exists(): t.unlink()
    if subprocess.run(["cp","-c",str(base),str(work)],capture_output=True).returncode != 0:
        shutil.copyfile(base, work)

def pass_kind(rep):
    langs = rep.get("languages") or {}
    rr = langs.get("reference_resolution") if isinstance(langs, dict) else None
    if rr is None: return "none", 0
    return ("Full" if rr.get("by_language") else "Delta"), rr["counts"]["identifier_resolutions"]

def facts(db):
    c = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        f = c.execute("SELECT COUNT(*) FROM files").fetchone()[0]
        i = c.execute("SELECT COUNT(*) FROM identifiers").fetchone()[0]
        m = dict(c.execute("SELECT key, value FROM artifact_metadata").fetchall())
    finally: c.close()
    return {"files": f, "identifiers": i,
            "last_full_revision": m.get("reference_resolution_last_full_revision"),
            "resolution_status": m.get("reference_resolution_status")}

SCRATCH.mkdir(parents=True, exist_ok=True)
fixture = SCRATCH / "fixture"
print("[1] extracting fixture", flush=True); extract_tree(fixture)
pristine = SCRATCH / "pristine"
if pristine.exists(): shutil.rmtree(pristine)
shutil.copytree(fixture, pristine)

base_db = SCRATCH / "base.db"
for s in ("","-wal","-shm"):
    t = Path(str(base_db)+s)
    if t.exists(): t.unlink()
print("[2] base scan", flush=True)
p, ms = sh([JULIE,"scan","--root",str(fixture),"--db",str(base_db),"--jobs","4","--json"])
if p.returncode != 0: sys.exit("base scan failed: "+p.stderr[:2000])
base_rep = json.loads(p.stdout)
(OUT/"base_scan.json").write_text(json.dumps(base_rep, indent=1))
bf = facts(base_db)
print("   base:", bf, "wall_ms", ms, flush=True)

c = sqlite3.connect(f"file:{base_db}?mode=ro", uri=True)
paths = [r[0] for r in c.execute("SELECT path FROM files ORDER BY path").fetchall()]
c.close()
cs = [x for x in paths if x.endswith(".cs")]
target = cs[0]
print("   target .cs:", target, flush=True)

results = {"base_facts": bf, "target": target, "julie_version": subprocess.run([JULIE,"--version"],capture_output=True,text=True).stdout.strip(), "rev": REV, "runs": []}

def touch(rel):
    with open(fixture/rel, "ab") as h: h.write(b"\n")
def restore(rel):
    shutil.copyfile(pristine/rel, fixture/rel)

# RUN A: Ph0 replication — whole-repo scan, 1 changed .cs file
work = SCRATCH/"work.db"; clone_db(base_db, work); touch(target)
print("[3] RUN A: scan (Ph0 replication)", flush=True)
p, ms = sh([JULIE,"scan","--root",str(fixture),"--db",str(work),"--jobs","4","--json"])
restore(target)
if p.returncode != 0: sys.exit("A failed: "+p.stderr[:2000])
rep = json.loads(p.stdout); (OUT/"A_scan_1cs.json").write_text(json.dumps(rep, indent=1))
kind, rows = pass_kind(rep)
results["runs"].append({"label":"A_scan_1cs","argv":["scan","--root","--db","--jobs 4","--json"],
    "wall_ms":ms,"pass":kind,"identifier_resolutions":rows,
    "share_of_corpus": round(rows/bf["identifiers"]*100,1),
    "resolution_ms": rep.get("profile",{}).get("phases",{}).get("resolution"),
    "files_changed": rep["counts"]["files_changed"],
    "post_facts": facts(work)})
print("   A:", results["runs"][-1], flush=True)

# RUN B: Miller's REAL single-file save argv — update --file
work2 = SCRATCH/"work2.db"; clone_db(base_db, work2); touch(target)
abs_target = str((fixture/target).resolve())
argvB = [JULIE,"update","--root",str(fixture.resolve()),"--db",str(work2.resolve()),
         "--file",abs_target,"--strict-schema","--json"]
print("[4] RUN B: update --file (Miller's watcher argv)", flush=True)
p, ms = sh(argvB)
restore(target)
if p.returncode != 0: sys.exit("B failed rc=%d: %s" % (p.returncode, p.stderr[:2000]))
rep = json.loads(p.stdout); (OUT/"B_update_1cs.json").write_text(json.dumps(rep, indent=1))
kind, rows = pass_kind(rep)
results["runs"].append({"label":"B_update_1cs","argv":argvB[1:],
    "wall_ms":ms,"pass":kind,"identifier_resolutions":rows,
    "share_of_corpus": round(rows/bf["identifiers"]*100,1),
    "resolution_ms": rep.get("profile",{}).get("phases",{}).get("resolution"),
    "operation": rep.get("operation"), "mode": rep.get("mode"),
    "post_facts": facts(work2)})
print("   B:", results["runs"][-1], flush=True)

# RUN C: markdown control through update --file
md = [x for x in paths if x.endswith(".md")]
if md:
    tmd = md[0]
    work3 = SCRATCH/"work3.db"; clone_db(base_db, work3); touch(tmd)
    argvC = [JULIE,"update","--root",str(fixture.resolve()),"--db",str(work3.resolve()),
             "--file",str((fixture/tmd).resolve()),"--strict-schema","--json"]
    print("[5] RUN C: update --file markdown control", flush=True)
    p, ms = sh(argvC); restore(tmd)
    if p.returncode == 0:
        rep = json.loads(p.stdout); (OUT/"C_update_1md.json").write_text(json.dumps(rep, indent=1))
        kind, rows = pass_kind(rep)
        results["runs"].append({"label":"C_update_1md","target":tmd,"wall_ms":ms,"pass":kind,
            "identifier_resolutions":rows,"share_of_corpus":round(rows/bf["identifiers"]*100,1),
            "resolution_ms": rep.get("profile",{}).get("phases",{}).get("resolution")})
        print("   C:", results["runs"][-1], flush=True)

(OUT/"results.json").write_text(json.dumps(results, indent=1))
print(json.dumps(results, indent=1))
