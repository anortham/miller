import json, os, subprocess, time, shutil, sqlite3
from pathlib import Path
JULIE="/Users/murphy/source/miller/.tools/julie-extract"
SCRATCH=Path(os.environ.get("TMPDIR","/tmp"))/"miller-ph1-task2"
OUT=Path("/tmp/ph1-task2/out")
fixture=SCRATCH/"fixture"; pristine=SCRATCH/"pristine"; base=SCRATCH/"base.db"
def clone(work):
    for s in ("","-wal","-shm"):
        t=Path(str(work)+s)
        if t.exists(): t.unlink()
    if subprocess.run(["cp","-c",str(base),str(work)],capture_output=True).returncode!=0:
        shutil.copyfile(base,work)
def kind(rep):
    rr=(rep.get("languages") or {}).get("reference_resolution")
    if rr is None: return "none",0
    return ("Full" if rr.get("by_language") else "Delta"), rr["counts"]["identifier_resolutions"]
c=sqlite3.connect(f"file:{base}?mode=ro",uri=True)
tot=c.execute("SELECT COUNT(*) FROM identifiers").fetchone()[0]; c.close()
res=[]
for i,tgt in enumerate(["src/Miller.Server/Tools/SearchTool.cs","src/Miller.Indexing/JulieExtractRunner.cs"]):
    work=SCRATCH/f"w{i}.db"; clone(work)
    with open(fixture/tgt,"ab") as h: h.write(b"\n")
    argv=[JULIE,"update","--root",str(fixture.resolve()),"--db",str(work.resolve()),
          "--file",str((fixture/tgt).resolve()),"--strict-schema","--json"]
    t=time.monotonic(); p=subprocess.run(argv,capture_output=True,text=True)
    ms=int((time.monotonic()-t)*1000)
    shutil.copyfile(pristine/tgt, fixture/tgt)
    if p.returncode!=0: print("FAIL",tgt,p.returncode,p.stderr[:500]); continue
    rep=json.loads(p.stdout); (OUT/f"D_update_{Path(tgt).name}.json").write_text(json.dumps(rep,indent=1))
    k,rows=kind(rep)
    r={"target":tgt,"wall_ms":ms,"pass":k,"identifier_resolutions":rows,"share":round(rows/tot*100,1)}
    res.append(r); print(json.dumps(r))
(OUT/"results2.json").write_text(json.dumps({"corpus_identifiers":tot,"runs":res},indent=1))
