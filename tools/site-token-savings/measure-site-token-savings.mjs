#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptDir, "..", "..");
const sourceRoot = resolveSampleSourceRoot();
const defaultMiller = join(repoRoot, "src", "Miller.Server", "bin", "Release", "net10.0", "miller");
const miller = process.env.MILLER_BINARY || defaultMiller;

const workflows = [
  {
    repo: "flask",
    label: "Flask",
    language: "Python",
    file: "src/flask/app.py",
    command: ["inspect", "--workspace-id", "flask", "src/flask/app.py", "--limit", "20"],
  },
  {
    repo: "express",
    label: "Express",
    language: "JavaScript",
    file: "lib/application.js",
    command: ["inspect", "--workspace-id", "express", "lib/application.js", "--limit", "20"],
  },
  {
    repo: "zod",
    label: "Zod",
    language: "TypeScript",
    file: "packages/zod/src/v4/classic/schemas.ts",
    command: ["inspect", "--workspace-id", "zod", "packages/zod/src/v4/classic/schemas.ts", "--limit", "20"],
  },
  {
    repo: "Newtonsoft.Json",
    label: "Newtonsoft.Json",
    language: "C#",
    file: "Src/Newtonsoft.Json/JsonConvert.cs",
    command: ["inspect", "--workspace-id", "Newtonsoft.Json", "Src/Newtonsoft.Json/JsonConvert.cs", "--limit", "20"],
  },
  {
    repo: "gson",
    label: "Gson",
    language: "Java",
    file: "gson/src/main/java/com/google/gson/Gson.java",
    command: ["inspect", "--workspace-id", "gson", "gson/src/main/java/com/google/gson/Gson.java", "--limit", "20"],
  },
  {
    repo: "jq",
    label: "jq",
    language: "C",
    file: "src/jv_parse.c",
    command: ["inspect", "--workspace-id", "jq", "src/jv_parse.c", "--limit", "20"],
  },
];

function run(command, args) {
  const result = spawnSync(command, args, {
    cwd: repoRoot,
    encoding: "utf8",
    maxBuffer: 10 * 1024 * 1024,
  });

  if (result.status !== 0) {
    throw new Error(
      `${command} ${args.join(" ")} failed with ${result.status}\n${result.stderr || result.stdout}`,
    );
  }

  return result.stdout;
}

function resolveSampleSourceRoot() {
  if (process.env.MILLER_SITE_METRICS_SOURCE_ROOT) {
    return resolve(process.env.MILLER_SITE_METRICS_SOURCE_ROOT);
  }

  const git = spawnSync("git", ["rev-parse", "--git-common-dir"], {
    cwd: repoRoot,
    encoding: "utf8",
  });

  if (git.status === 0) {
    const commonDir = git.stdout.trim();
    const commonPath = resolve(repoRoot, commonDir);
    if (basename(commonPath) === ".git") {
      return resolve(dirname(commonPath), "..");
    }
  }

  return resolve(repoRoot, "..");
}

function bytes(text) {
  return Buffer.byteLength(text, "utf8");
}

function approxTokens(byteCount) {
  return Math.ceil(byteCount / 4);
}

function pctSaved(rawBytes, millerBytes) {
  return rawBytes === 0 ? 0 : (1 - millerBytes / rawBytes) * 100;
}

if (!existsSync(miller)) {
  throw new Error(`Miller binary not found: ${miller}. Build Release first or set MILLER_BINARY.`);
}

const rows = workflows.map((workflow) => {
  const root = join(sourceRoot, workflow.repo);
  const sourcePath = join(root, workflow.file);
  if (!existsSync(sourcePath)) {
    throw new Error(
      `Source file not found for ${workflow.repo}: ${sourcePath}. Set MILLER_SITE_METRICS_SOURCE_ROOT to the directory containing the sampled repos.`,
    );
  }

  const output = run(miller, workflow.command);
  const sourceBytes = readFileSync(sourcePath).length;
  const millerBytes = bytes(output);

  return {
    repo: workflow.label,
    language: workflow.language,
    command: `miller ${workflow.command.join(" ")}`,
    file: workflow.file,
    sourceBytes,
    sourceTokens: approxTokens(sourceBytes),
    millerBytes,
    millerTokens: approxTokens(millerBytes),
    savedBytes: sourceBytes - millerBytes,
    savedTokens: approxTokens(sourceBytes) - approxTokens(millerBytes),
    savingsPercent: pctSaved(sourceBytes, millerBytes),
  };
});

const totals = rows.reduce(
  (acc, row) => {
    acc.sourceBytes += row.sourceBytes;
    acc.millerBytes += row.millerBytes;
    acc.savedBytes += row.savedBytes;
    acc.sourceTokens += row.sourceTokens;
    acc.millerTokens += row.millerTokens;
    acc.savedTokens += row.savedTokens;
    return acc;
  },
  { sourceBytes: 0, millerBytes: 0, savedBytes: 0, sourceTokens: 0, millerTokens: 0, savedTokens: 0 },
);

totals.savingsPercent = pctSaved(totals.sourceBytes, totals.millerBytes);

const markdown = [
  "| repo | language | file | source bytes | Miller bytes | est. tokens saved | savings |",
  "|---|---|---|---:|---:|---:|---:|",
  ...rows.map((row) =>
    `| ${row.repo} | ${row.language} | \`${row.file}\` | ${row.sourceBytes.toLocaleString("en-US")} | ${row.millerBytes.toLocaleString("en-US")} | ${row.savedTokens.toLocaleString("en-US")} | ${row.savingsPercent.toFixed(1)}% |`,
  ),
  `| **total** |  |  | **${totals.sourceBytes.toLocaleString("en-US")}** | **${totals.millerBytes.toLocaleString("en-US")}** | **${totals.savedTokens.toLocaleString("en-US")}** | **${totals.savingsPercent.toFixed(1)}%** |`,
].join("\n");

console.log(JSON.stringify({ measuredAt: new Date().toISOString(), miller, sourceRoot, rows, totals }, null, 2));
console.log("\n--- markdown ---\n");
console.log(markdown);
