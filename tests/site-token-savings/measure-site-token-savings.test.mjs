import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";

const repoRoot = resolve(import.meta.dirname, "..", "..");
const scriptPath = join(repoRoot, "tools", "site-token-savings", "measure-site-token-savings.mjs");

const fixtureFiles = [
  ["flask", "src/flask/app.py"],
  ["express", "lib/application.js"],
  ["zod", "packages/zod/src/v4/classic/schemas.ts"],
  ["Newtonsoft.Json", "Src/Newtonsoft.Json/JsonConvert.cs"],
  ["gson", "gson/src/main/java/com/google/gson/Gson.java"],
  ["jq", "src/jv_parse.c"],
];

test("measure-site-token-savings can use an explicit sample source root", () => {
  const tempRoot = mkdtempSync(join(tmpdir(), "miller-site-savings-"));
  const sampleSourceRoot = join(tempRoot, "source");
  const fakeMiller = join(tempRoot, "fake-miller.js");

  for (const [repo, relativePath] of fixtureFiles) {
    const filePath = join(sampleSourceRoot, repo, relativePath);
    mkdirSync(resolve(filePath, ".."), { recursive: true });
    writeFileSync(filePath, `// ${repo} ${relativePath}\nfunction sample() { return 42; }\n`);
  }

  writeFileSync(
    fakeMiller,
    "#!/usr/bin/env node\nconsole.log('compact inspect output');\n",
  );
  chmodSync(fakeMiller, 0o755);

  const result = spawnSync(process.execPath, [scriptPath], {
    cwd: repoRoot,
    encoding: "utf8",
    env: {
      ...process.env,
      MILLER_BINARY: fakeMiller,
      MILLER_SITE_METRICS_SOURCE_ROOT: sampleSourceRoot,
    },
  });

  assert.equal(result.status, 0, result.stderr || result.stdout);
  const json = JSON.parse(result.stdout.split("\n--- markdown ---\n")[0]);
  assert.equal(json.rows.length, fixtureFiles.length);
  assert.equal(json.sourceRoot, sampleSourceRoot);
});
