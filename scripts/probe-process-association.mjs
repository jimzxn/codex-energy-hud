// Bounded, metadata-only audit. Does not load/resume tasks or connect to the desktop's stdin.
// The only Codex process this script controls is its exclusively owned read-only probe.
import fs from 'node:fs';
import path from 'node:path';
import { spawn, execFileSync } from 'node:child_process';
import { DatabaseSync } from 'node:sqlite';

function argument(name) {
  const i = process.argv.indexOf(name);
  if (i < 0 || !process.argv[i + 1]) throw new Error(`Missing ${name}`);
  return process.argv[i + 1];
}
const home = argument('--codex-home');
const executable = argument('--codex-exe');
const threadId = argument('--thread-id');
const rollout = argument('--rollout');
const output = path.resolve(argument('--output'));
const evidence = {
  observedAt: new Date().toISOString(),
  scope: 'current task only; metadata and schema; no prompts, commands, outputs, or credentials retained',
  threadId,
  restrictions: {
    taskMutations: false, desktopConnectionAccess: false, credentialInspection: false,
    settingsChanges: false, readOnlySqliteConnections: true,
    supportedProcessInspectionToolExposed: false,
  },
};

function inspectSqlite() {
  const state = new DatabaseSync(path.join(home, 'state_5.sqlite'), { readOnly: true });
  const history = new DatabaseSync(path.join(home, 'thread_history_1.sqlite'), { readOnly: true });
  const logs = new DatabaseSync(path.join(home, 'logs_2.sqlite'), { readOnly: true });
  try {
    evidence.schemas = Object.fromEntries([
      ['threads', state], ['thread_turns', history], ['thread_items', history], ['logs', logs],
    ].map(([table, db]) => [table, db.prepare(`PRAGMA table_info(${table})`).all().map(c => c.name)]));
    const rows = history.prepare('SELECT item_type,item_json FROM thread_items WHERE thread_id=? ORDER BY rollout_ordinal DESC LIMIT 350').all(threadId);
    const commands = rows.filter(r => r.item_type === 'commandExecution').map(r => JSON.parse(r.item_json));
    evidence.history = {
      rowLimit: 350, rows: rows.length, commandExecutionItems: commands.length,
      keys: [...new Set(commands.flatMap(c => Object.keys(c)))].sort(),
      nonNullProcessId: commands.filter(c => c.processId != null).length,
      nonNullOsPid: commands.filter(c => c.osPid != null || c.os_pid != null).length,
      creationTimeFields: commands.filter(c => Object.keys(c).some(k => /creation.?time|process.?start.?time/i.test(k))).length,
    };
    const high = logs.prepare('SELECT MAX(id) AS id FROM logs').get().id;
    const recent = logs.prepare('SELECT thread_id,process_uuid FROM logs WHERE id>? AND thread_id IS NOT NULL').all(high - 5000);
    const groups = new Map();
    for (const row of recent) {
      if (!groups.has(row.process_uuid)) groups.set(row.process_uuid, new Set());
      groups.get(row.process_uuid).add(row.thread_id);
    }
    evidence.logs = {
      boundedLastIds: 5000, taskTaggedRows: recent.length,
      groups: [...groups].map(([processUuid, ids]) => ({ processUuid, distinctThreads: ids.size, includesCurrentThread: ids.has(threadId) })),
    };
  } finally { state.close(); history.close(); logs.close(); }
}

function inspectRollout() {
  const stat = fs.statSync(rollout), limit = 6 * 1024 * 1024;
  const bytes = Math.min(stat.size, limit), start = Math.max(0, stat.size - bytes);
  const buffer = Buffer.alloc(bytes), handle = fs.openSync(rollout, 'r');
  try { fs.readSync(handle, buffer, 0, bytes, start); } finally { fs.closeSync(handle); }
  let lines = buffer.toString('utf8').split('\n');
  if (start > 0) lines.shift();
  const tally = { byteLimit: limit, bytesRead: bytes, fileSize: stat.size, parsedRows: 0, commandExecutionItems: 0, nonNullProcessId: 0, nonNullOsPid: 0, explicitPidCreationMappings: 0, schemas: [] };
  const schemas = new Set();
  for (const line of lines) {
    let row; try { row = JSON.parse(line); } catch { continue; }
    tally.parsedRows++;
    const item = row.payload?.item;
    if (item?.type !== 'CommandExecution') continue;
    tally.commandExecutionItems++;
    if (item.process_id != null) tally.nonNullProcessId++;
    if (item.os_pid != null || item.osPid != null) tally.nonNullOsPid++;
    if ((item.os_pid != null || item.osPid != null) && Object.keys(item).some(k => /creation.?time|process.?start.?time/i.test(k))) tally.explicitPidCreationMappings++;
    schemas.add(JSON.stringify(Object.keys(item).sort()));
  }
  tally.schemas = [...schemas].map(s => JSON.parse(s));
  evidence.rollout = tally;
}

function errorCategory(error) {
  const s = String(error?.message ?? '');
  if (/thread.*not found|thread.*not loaded|conversation.*not found/i.test(s)) return 'threadNotLoadedOrNotFoundInThisServer';
  if (/unknown variant|method not found|unknown method/i.test(s)) return 'methodUnsupported';
  if (/experimental/i.test(s)) return 'experimentalCapabilityRequired';
  if (/timed out/i.test(s)) return 'timeout';
  return 'otherErrorDetailsOmitted';
}

async function inspectOwnedServer() {
  const version = execFileSync(executable, ['--version'], { encoding: 'utf8', windowsHide: true, timeout: 10000 }).trim();
  evidence.cliVersion = /^codex-cli [a-zA-Z0-9.\-+]+$/.test(version) ? version : 'unrecognized-version-format';
  const child = spawn(executable, ['app-server', '--listen', 'stdio://'], {
    windowsHide: true, cwd: process.cwd(), env: { ...process.env, CODEX_HOME: home }, stdio: ['pipe', 'pipe', 'pipe'],
  });
  const probe = { ownedProcessId: child.pid, outboundMethods: [], notificationCount: 0, stderrBytes: 0, exited: false };
  evidence.sidecar = probe;
  let buffer = '', nextId = 1;
  const pending = new Map();
  let exitedResolve;
  const exited = new Promise(resolve => { exitedResolve = resolve; });
  child.stderr.on('data', chunk => { probe.stderrBytes += chunk.length; });
  child.on('exit', (code, signal) => {
    probe.exited = true; probe.exitCode = code; probe.exitSignal = signal;
    for (const [id, p] of pending) { clearTimeout(p.timer); p.reject(new Error('Probe exited')); pending.delete(id); }
    exitedResolve();
  });
  child.on('error', error => {
    probe.launchErrorCode = error.code ?? 'unknown';
    for (const p of pending.values()) { clearTimeout(p.timer); p.reject(error); }
    pending.clear(); exitedResolve();
  });
  child.stdout.setEncoding('utf8');
  child.stdout.on('data', chunk => {
    buffer += chunk;
    if (buffer.length > 8 * 1024 * 1024) { buffer = ''; return; }
    for (let end; (end = buffer.indexOf('\n')) >= 0;) {
      const line = buffer.slice(0, end); buffer = buffer.slice(end + 1);
      let message; try { message = JSON.parse(line); } catch { continue; }
      const waiter = pending.get(message.id);
      if (waiter) {
        pending.delete(message.id); clearTimeout(waiter.timer); waiter.resolve(message);
      } else probe.notificationCount++;
    }
  });
  function request(method, params) {
    if (!['initialize', 'thread/loaded/list', 'thread/backgroundTerminals/list'].includes(method)) throw new Error('RPC not allowed');
    probe.outboundMethods.push(method);
    return new Promise((resolve, reject) => {
      const id = nextId++;
      const timer = setTimeout(() => { pending.delete(id); reject(new Error('RPC timed out')); }, 10000);
      pending.set(id, { resolve, reject, timer });
      child.stdin.write(JSON.stringify({ id, method, params }) + '\n');
    });
  }
  try {
    const initialized = await request('initialize', { clientInfo: { name: 'codex_hud_process_audit', version: '1.2.0' }, capabilities: { experimentalApi: true } });
    probe.initialized = !initialized.error;
    if (initialized.error) { probe.initializeError = { code: initialized.error.code, category: errorCategory(initialized.error) }; return; }
    child.stdin.write(JSON.stringify({ method: 'initialized' }) + '\n');
    probe.outboundMethods.push('initialized');
    const loaded = await request('thread/loaded/list', { limit: 20 });
    probe.loaded = loaded.error ? { errorCode: loaded.error.code, category: errorCategory(loaded.error) } : { count: loaded.result?.data?.length ?? null, hasNextCursor: loaded.result?.nextCursor != null, containsCurrentThread: (loaded.result?.data ?? []).includes(threadId) };
    const terminals = await request('thread/backgroundTerminals/list', { threadId, limit: 20 });
    probe.backgroundTerminals = terminals.error ? { errorCode: terminals.error.code, category: errorCategory(terminals.error) } : {
      count: terminals.result?.data?.length ?? null,
      nonNullOsPid: (terminals.result?.data ?? []).filter(t => t.osPid != null).length,
      keys: [...new Set((terminals.result?.data ?? []).flatMap(t => Object.keys(t)))].sort(),
    };
  } catch (error) { probe.error = errorCategory(error); }
  finally {
    if (!probe.exited) child.stdin.end();
    await Promise.race([exited, new Promise(resolve => setTimeout(resolve, 1500))]);
    if (!probe.exited && child.pid) { probe.ownedProcessTerminationRequested = true; child.kill(); await Promise.race([exited, new Promise(resolve => setTimeout(resolve, 1500))]); }
  }
}

inspectSqlite();
inspectRollout();
await inspectOwnedServer();
evidence.conclusion = {
  reliableTaskToOsProcessMappingEstablished: false,
  resourceFeatureEnabled: false,
  reason: 'Persisted processId is an app-server execution handle; host process is shared; independent sidecar does not expose desktop-owned loaded-thread processes.',
  unknownIsNotIdle: true,
};
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, JSON.stringify(evidence, null, 2) + '\n');
console.log(JSON.stringify({ output, evidence }, null, 2));
