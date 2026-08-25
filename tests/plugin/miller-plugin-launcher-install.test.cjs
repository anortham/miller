'use strict';

const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const repoRoot = path.resolve(__dirname, '..', '..');
const launcher = require(path.join(repoRoot, 'bin', 'miller-plugin-launcher.cjs'));

function tempHome() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'miller-launcher-home-'));
}

async function listenOn(handler) {
  const server = http.createServer(handler);
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  return { server, origin: `http://127.0.0.1:${server.address().port}` };
}

async function closeServer(server) {
  server.closeAllConnections();
  await new Promise((resolve) => server.close(resolve));
}

function readLog(home, now) {
  return fs.readFileSync(launcher.launcherLogPath({ MILLER_HOME: home }, now), 'utf8');
}

test('launcherLogPath honours MILLER_HOME and stamps the UTC date', () => {
  const home = tempHome();
  const now = new Date('2026-08-25T23:59:59.000Z');

  assert.equal(
    launcher.launcherLogPath({ MILLER_HOME: home }, now),
    path.join(home, '.miller', 'logs', 'launcher-20260825.log'),
  );
});

test('launcherLogPath falls back to the home directory when MILLER_HOME is blank', () => {
  const now = new Date('2026-01-02T00:00:00.000Z');

  assert.equal(
    launcher.launcherLogPath({ MILLER_HOME: '   ' }, now),
    path.join(os.homedir(), '.miller', 'logs', 'launcher-20260102.log'),
  );
});

test('launcherLog creates the logs directory and appends one line per call', () => {
  const home = tempHome();
  const now = new Date('2026-08-25T07:38:40.000Z');

  launcher.launcherLog('archive download begin', { MILLER_HOME: home }, now);
  launcher.launcherLog('archive download end', { MILLER_HOME: home }, now);

  const lines = readLog(home, now).trim().split('\n');
  assert.equal(lines.length, 2);
  assert.match(lines[0], /^2026-08-25T07:38:40\.000Z pid=\d+ archive download begin$/);
  assert.match(lines[1], /archive download end$/);
});

test('launcherLog flattens newlines so one event stays one line', () => {
  const home = tempHome();
  const now = new Date('2026-08-25T07:38:40.000Z');

  launcher.launcherLog('error stage=install Error: boom\n    at one\n    at two', { MILLER_HOME: home }, now);

  assert.equal(readLog(home, now).trim().split('\n').length, 1);
});

test('launcherLog returns normally when the log directory cannot be created', () => {
  const blocker = path.join(tempHome(), 'not-a-directory');
  fs.writeFileSync(blocker, 'x');

  assert.doesNotThrow(() => launcher.launcherLog('start', { MILLER_HOME: blocker }, new Date()));
});

test('sweepLauncherLogs deletes launcher logs past the retention window and keeps recent ones', () => {
  const home = tempHome();
  const now = new Date('2026-08-25T07:00:00.000Z');
  const logsDir = path.dirname(launcher.launcherLogPath({ MILLER_HOME: home }, now));
  fs.mkdirSync(logsDir, { recursive: true });

  const stale = path.join(logsDir, 'launcher-20260805.log');
  const fresh = path.join(logsDir, 'launcher-20260824.log');
  const foreign = path.join(logsDir, 'miller-20260805.log');
  for (const file of [stale, fresh, foreign]) {
    fs.writeFileSync(file, 'x');
  }

  const twentyDaysAgo = now.getTime() - 20 * 24 * 60 * 60 * 1000;
  fs.utimesSync(stale, twentyDaysAgo / 1000, twentyDaysAgo / 1000);
  fs.utimesSync(foreign, twentyDaysAgo / 1000, twentyDaysAgo / 1000);

  launcher.sweepLauncherLogs({ MILLER_HOME: home }, now);

  assert.equal(fs.existsSync(stale), false);
  assert.equal(fs.existsSync(fresh), true);
  assert.equal(fs.existsSync(foreign), true);
});

test('httpClientFor allows https anywhere and http only on loopback', () => {
  assert.equal(launcher.httpClientFor('https://github.com/anortham/miller'), require('node:https'));
  assert.equal(launcher.httpClientFor('http://127.0.0.1:8080/a.zip'), http);
  assert.equal(launcher.httpClientFor('http://localhost:8080/a.zip'), http);

  assert.throws(
    () => launcher.httpClientFor('http://example.com/a.zip'),
    /Refusing to download Miller over http:\/\/example\.com/,
  );
});

test('downloadFile names the HTTP status when the asset is missing', async () => {
  const { server, origin } = await listenOn((request, response) => {
    response.statusCode = 404;
    response.end('nope');
  });
  const destination = path.join(tempHome(), 'asset.bin');

  try {
    await assert.rejects(
      () => launcher.downloadFile(`${origin}/asset.bin`, destination),
      /HTTP 404/,
    );
    assert.equal(fs.existsSync(destination), false);
  } finally {
    await closeServer(server);
  }
});

test('downloadFile rejects and removes the partial file when the body is cut short', async () => {
  const { server, origin } = await listenOn((request, response) => {
    response.writeHead(200, { 'content-length': '1024' });
    response.write(Buffer.alloc(64));
    response.socket.destroy();
  });
  const downloadDir = tempHome();
  const destination = path.join(downloadDir, 'asset.bin');

  try {
    await assert.rejects(() => launcher.downloadFile(`${origin}/asset.bin`, destination));
    assert.equal(fs.existsSync(destination), false);
    assert.deepEqual(fs.readdirSync(downloadDir).filter((name) => name.includes('.tmp-')), []);
  } finally {
    await closeServer(server);
  }
});

test('downloadFile gives up when the server accepts the connection and never answers', async () => {
  const { server, origin } = await listenOn(() => {});
  const destination = path.join(tempHome(), 'asset.bin');

  try {
    await assert.rejects(
      () => launcher.downloadFile(`${origin}/asset.bin`, destination, { connectTimeoutMs: 200 }),
      /No response within 200ms/,
    );
  } finally {
    await closeServer(server);
  }
});

test('downloadFile gives up when the body stalls mid-transfer', async () => {
  const { server, origin } = await listenOn((request, response) => {
    response.writeHead(200, { 'content-length': '1048576' });
    response.write(Buffer.alloc(16));
  });
  const destination = path.join(tempHome(), 'asset.bin');

  try {
    await assert.rejects(
      () => launcher.downloadFile(`${origin}/asset.bin`, destination, { idleTimeoutMs: 200 }),
      /Download stalled for 200ms after 16 of 1048576 bytes/,
    );
  } finally {
    await closeServer(server);
  }
});

test('downloadFile reports transferred bytes and progress while the body streams', async () => {
  const payload = Buffer.alloc(4096, 7);
  const { server, origin } = await listenOn((request, response) => {
    response.writeHead(200, { 'content-length': String(payload.length) });
    response.end(payload);
  });
  const destination = path.join(tempHome(), 'asset.bin');
  const seen = [];

  try {
    const received = await launcher.downloadFile(`${origin}/asset.bin`, destination, {
      onProgress: (bytes, total) => seen.push([bytes, total]),
    });

    assert.equal(received, payload.length);
    assert.deepEqual(fs.readFileSync(destination), payload);
    assert.ok(seen.length > 0);
    assert.equal(seen.at(-1)[0], payload.length);
    assert.equal(seen.at(-1)[1], payload.length);
  } finally {
    await closeServer(server);
  }
});

test('a stalled download is retried and the next attempt succeeds', async () => {
  const payload = Buffer.alloc(4096, 9);
  let served = 0;
  const { server, origin } = await listenOn((request, response) => {
    served += 1;
    if (served === 1) {
      response.writeHead(200, { 'content-length': String(payload.length) });
      response.write(payload.subarray(0, 16));
      return;
    }

    response.writeHead(200, { 'content-length': String(payload.length) });
    response.end(payload);
  });

  const home = tempHome();
  const digest = crypto.createHash('sha256').update(payload).digest('hex');
  const originalHome = process.env.MILLER_HOME;
  process.env.MILLER_HOME = home;

  try {
    const stalling = http.createServer();
    stalling.close();

    const destination = path.join(home, 'asset.bin');
    await assert.rejects(
      () => launcher.downloadFile(`${origin}/asset.bin`, destination, { idleTimeoutMs: 150 }),
      /Download stalled/,
    );

    const received = await launcher.downloadFile(`${origin}/asset.bin`, destination, { idleTimeoutMs: 150 });
    assert.equal(received, payload.length);
    assert.equal(crypto.createHash('sha256').update(fs.readFileSync(destination)).digest('hex'), digest);
  } finally {
    if (originalHome === undefined) {
      delete process.env.MILLER_HOME;
    } else {
      process.env.MILLER_HOME = originalHome;
    }
    await closeServer(server);
  }
});

test('ensureDownloadedArchive retries past a stalled first response', async () => {
  const archive = Buffer.alloc(8192, 5);
  const digest = crypto.createHash('sha256').update(archive).digest('hex');
  let archiveRequests = 0;
  const { server, origin } = await listenOn((request, response) => {
    if (request.url.endsWith('.sha256')) {
      response.writeHead(200, { 'content-length': String(digest.length) });
      response.end(digest);
      return;
    }

    archiveRequests += 1;
    response.writeHead(200, { 'content-length': String(archive.length) });
    if (archiveRequests === 1) {
      response.write(archive.subarray(0, 32));
      return;
    }

    response.end(archive);
  });

  const home = tempHome();
  const originalHome = process.env.MILLER_HOME;
  process.env.MILLER_HOME = home;

  try {
    const result = await launcher.ensureDownloadedArchive(
      { version: '9.9.9', repository: 'anortham/miller' },
      launcher.detectPlatform(),
      path.join(home, 'downloads'),
      { baseUrl: origin, idleTimeoutMs: 150 },
    );

    assert.equal(archiveRequests, 2);
    assert.equal(
      crypto.createHash('sha256').update(fs.readFileSync(result.archivePath)).digest('hex'),
      digest);
  } finally {
    if (originalHome === undefined) {
      delete process.env.MILLER_HOME;
    } else {
      process.env.MILLER_HOME = originalHome;
    }
    await closeServer(server);
  }
});

test('sweepStaleInstallLeftovers removes a killed attempt stage directory and partial download', () => {
  const home = tempHome();
  const targetRoot = path.join(home, 'cache', '1.22.0', 'x86_64-pc-windows-msvc');
  const downloads = path.join(targetRoot, 'downloads');
  fs.mkdirSync(downloads, { recursive: true });

  const dead = 1787661513135;
  const live = Date.now();
  const deadStage = path.join(targetRoot, `stage-29240-${dead}`);
  const liveStage = path.join(targetRoot, `stage-31000-${live}`);
  const deadTmp = path.join(downloads, `miller.zip.tmp-29240-${dead}`);
  const liveTmp = path.join(downloads, `miller.zip.tmp-31000-${live}`);
  const keeper = path.join(downloads, 'miller.zip');

  fs.mkdirSync(deadStage);
  fs.mkdirSync(liveStage);
  for (const file of [deadTmp, liveTmp, keeper]) {
    fs.writeFileSync(file, 'x');
  }

  launcher.sweepStaleInstallLeftovers(targetRoot);

  assert.equal(fs.existsSync(deadStage), false);
  assert.equal(fs.existsSync(deadTmp), false);
  assert.equal(fs.existsSync(liveStage), true);
  assert.equal(fs.existsSync(liveTmp), true);
  assert.equal(fs.existsSync(keeper), true);
});

function seedVersion(cacheRoot, version, target, usedAt) {
  const targetRoot = path.join(cacheRoot, version, target);
  fs.mkdirSync(path.join(targetRoot, 'package'), { recursive: true });
  fs.mkdirSync(path.join(targetRoot, 'downloads'), { recursive: true });
  fs.writeFileSync(path.join(targetRoot, 'package', 'miller'), 'x');
  if (usedAt !== undefined) {
    launcher.markVersionUsed(targetRoot, usedAt);
    fs.utimesSync(path.join(targetRoot, '.last-used'), usedAt / 1000, usedAt / 1000);
  }

  return targetRoot;
}

test('pruneOldCachedVersions keeps the current version and the most recently used other one', () => {
  const cacheRoot = path.join(tempHome(), 'cache');
  const target = 'x86_64-pc-windows-msvc';
  const day = 24 * 60 * 60 * 1000;
  const now = Date.now();

  seedVersion(cacheRoot, '1.22.0', target, now);
  seedVersion(cacheRoot, '1.21.0', target, now - day);
  seedVersion(cacheRoot, '1.20.1', target, now - 10 * day);
  seedVersion(cacheRoot, '1.16.0', target, now - 30 * day);

  const removed = launcher.pruneOldCachedVersions(cacheRoot, '1.22.0', { target });

  assert.deepEqual(removed.sort(), ['1.16.0', '1.20.1']);
  assert.equal(fs.existsSync(path.join(cacheRoot, '1.22.0')), true);
  assert.equal(fs.existsSync(path.join(cacheRoot, '1.21.0')), true);
  assert.equal(fs.existsSync(path.join(cacheRoot, '1.20.1')), false);
  assert.equal(fs.existsSync(path.join(cacheRoot, '1.16.0')), false);
});

test('pruneOldCachedVersions never removes the version being installed', () => {
  const cacheRoot = path.join(tempHome(), 'cache');
  const target = 'x86_64-unknown-linux-gnu';
  seedVersion(cacheRoot, '1.22.0', target, Date.now());

  assert.deepEqual(launcher.pruneOldCachedVersions(cacheRoot, '1.22.0', { target }, 0), []);
  assert.equal(fs.existsSync(path.join(cacheRoot, '1.22.0')), true);
});

test('pruneOldCachedVersions falls back to the directory time when no marker exists', () => {
  const cacheRoot = path.join(tempHome(), 'cache');
  const target = 'x86_64-unknown-linux-gnu';
  const old = Date.now() - 30 * 24 * 60 * 60 * 1000;
  seedVersion(cacheRoot, '1.21.0', target, Date.now());
  const unmarked = seedVersion(cacheRoot, '1.19.0', target, undefined);
  fs.utimesSync(unmarked, old / 1000, old / 1000);

  const removed = launcher.pruneOldCachedVersions(cacheRoot, '1.22.0', { target });

  assert.deepEqual(removed, ['1.19.0']);
  assert.equal(fs.existsSync(path.join(cacheRoot, '1.21.0')), true);
});

test('pruneOldCachedVersions returns nothing when the cache root does not exist', () => {
  assert.deepEqual(
    launcher.pruneOldCachedVersions(path.join(tempHome(), 'missing'), '1.22.0', { target: 'x' }),
    []);
});

function archiveFixture(binaryName, extension) {
  const source = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-launcher-pkg-'));
  fs.writeFileSync(path.join(source, binaryName), '#!/bin/sh\nexit 0\n');
  fs.writeFileSync(path.join(source, 'README.txt'), 'fixture');

  const archivePath = path.join(source, `package${extension}`);
  const args = extension === '.zip'
    ? ['-a', '-cf', archivePath, binaryName, 'README.txt']
    : ['-czf', archivePath, binaryName, 'README.txt'];
  const created = childProcess.spawnSync('tar', args, { cwd: source, encoding: 'utf8' });
  assert.equal(created.status, 0, `tar failed: ${created.stderr || created.error?.message}`);

  return fs.readFileSync(archivePath);
}

test('ensureMillerPackage installs from a served archive and records every stage in the launcher log', async () => {
  const platformInfo = launcher.detectPlatform();
  const archive = archiveFixture(platformInfo.binaryName, platformInfo.archiveExtension);
  const digest = crypto.createHash('sha256').update(archive).digest('hex');

  const { server, origin } = await listenOn((request, response) => {
    if (request.url.endsWith('.sha256')) {
      response.writeHead(200, { 'content-length': String(digest.length) });
      response.end(digest);
      return;
    }

    response.writeHead(200, { 'content-length': String(archive.length) });
    response.end(archive);
  });

  const home = tempHome();
  const cacheRoot = path.join(home, 'cache');
  const now = new Date();
  const config = { version: '9.9.9', repository: `127.0.0.1:${server.address().port}` };
  const originalHome = process.env.MILLER_HOME;
  process.env.MILLER_HOME = home;

  try {
    const binaryPath = await launcher.ensureMillerPackage(
      { ...config, repository: 'anortham/miller' },
      platformInfo,
      cacheRoot,
      { baseUrl: origin },
    );

    assert.equal(binaryPath, path.join(cacheRoot, '9.9.9', platformInfo.target, 'package', platformInfo.binaryName));
    assert.equal(fs.existsSync(binaryPath), true);

    const log = readLog(home, now);
    for (const stage of ['cache miss', 'sidecar download begin', 'archive download end', 'list end', 'extract end', 'promote end']) {
      assert.ok(log.includes(stage), `launcher log is missing "${stage}":\n${log}`);
    }

    const warm = await launcher.ensureMillerPackage(
      { ...config, repository: 'anortham/miller' },
      platformInfo,
      cacheRoot,
      { baseUrl: origin },
    );
    assert.equal(warm, binaryPath);
    assert.ok(readLog(home, now).includes('cache hit'));
    assert.equal(fs.existsSync(path.join(cacheRoot, '9.9.9', platformInfo.target, '.last-used')), true);
  } finally {
    if (originalHome === undefined) {
      delete process.env.MILLER_HOME;
    } else {
      process.env.MILLER_HOME = originalHome;
    }
    await closeServer(server);
  }
});

test('ensureDownloadedArchive names both hashes when the sidecar does not match the archive', async () => {
  const archive = Buffer.alloc(2048, 3);
  const wrongDigest = crypto.createHash('sha256').update('different').digest('hex');
  const { server, origin } = await listenOn((request, response) => {
    if (request.url.endsWith('.sha256')) {
      response.writeHead(200, { 'content-length': String(wrongDigest.length) });
      response.end(wrongDigest);
      return;
    }

    response.writeHead(200, { 'content-length': String(archive.length) });
    response.end(archive);
  });

  const home = tempHome();
  const originalHome = process.env.MILLER_HOME;
  process.env.MILLER_HOME = home;

  try {
    await assert.rejects(
      () => launcher.ensureDownloadedArchive(
        { version: '9.9.9', repository: 'anortham/miller' },
        launcher.detectPlatform(),
        path.join(home, 'downloads'),
        { baseUrl: origin },
      ),
      new RegExp(`expected ${wrongDigest}, got [a-f0-9]{64}`),
    );
  } finally {
    if (originalHome === undefined) {
      delete process.env.MILLER_HOME;
    } else {
      process.env.MILLER_HOME = originalHome;
    }
    await closeServer(server);
  }
});
