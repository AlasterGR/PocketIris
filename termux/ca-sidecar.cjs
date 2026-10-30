'use strict';
/*
 * PocketIris sidecar: the part of PocketIris's features that used to live inside Yep Anywhere's code, now a separate small
 * program that talks to Yep only through its public HTTP API (127.0.0.1:3400). Yep itself stays exactly as released.
 *
 *   1. Notifications: every few seconds it looks at Yep's list of running sessions and, when a session starts waiting for the user,
 *      finishes a turn, or ends mid-work, it shows an Android notification (through Termux:API). Tapping it opens that session.
 *   2. Trash: moves a Claude session's files into ~/.claude/trash (never erases anything), lists what is there, and puts a session
 *      back (restore). A session that is still running is stopped first, through Yep's own API.
 *
 * The trash and status functions are offered to the PocketIris app on 127.0.0.1:3405. Every request must carry the secret key the
 * app gave this program (CA_SIDECAR_TOKEN), must not come from a web page (no Origin header), and must be addressed to this server.
 */

const http = require('node:http');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFile } = require('node:child_process');

//  The port, the address of Yep and the polling pace can be overridden: only the self-test does that.
const PORT = Number(process.env.CA_SIDECAR_PORT) || 3405;
const YEP = process.env.CA_YEP_URL || 'http://127.0.0.1:3400';
const TOKEN = process.env.CA_SIDECAR_TOKEN || '';
const HOME = os.homedir();
const CLAUDE = path.join(HOME, '.claude');
const PROJECTS = path.join(CLAUDE, 'projects');
const TRASH = path.join(CLAUDE, 'trash');
const APP_ACTIVITY = 'io.github.alastergr.pocketiris/io.github.alastergr.pocketiris.MainActivity';
const POLL_MS = Number(process.env.CA_POLL_MS) || 3000;
const MAX_BODY = 64 * 1024;

/** Session ids and folder names must be plain names: never a path. */
const SAFE_NAME = /^[A-Za-z0-9._-]{1,200}$/;

/** Writes one line to the log (the log file is trimmed by ca-trim-logs). */
function log(...parts) {
  process.stdout.write(`[${new Date().toISOString()}] ${parts.join(' ')}\n`);
}

/** Asks Yep's API for JSON; resolves to null when Yep does not answer or answers with an error. Yep demands this header on every change. */
async function yep(pathname, options) {
  try {
    const headers = { 'x-yep-anywhere': 'true', ...(options && options.headers) };
    const response = await fetch(YEP + pathname, { signal: AbortSignal.timeout(5000), ...options, headers });
    if (!response.ok) return null;
    return await response.json();
  } catch {
    return null;
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// 1. Notifications
// ---------------------------------------------------------------------------------------------------------------------

/** What was last seen of each running session: its state, and whether a notification is showing for it. */
const seen = new Map();

/** Shows (or replaces) one notification for a session; a tap opens that session in the app. */
function notify(session, title, content, ongoing) {
  const tap = `am start -n ${APP_ACTIVITY} --es path '/projects/${session.projectId}/sessions/${session.sessionId}'`;
  const args = ['--id', `ca-${session.sessionId}`, '--group', 'pocketiris', '--title', title, '--content', content,
    '--priority', ongoing ? 'high' : 'default', '--action', tap];
  //  A question that is waiting should alert once, not every time it is refreshed.
  if (ongoing) args.push('--alert-once');
  execFile('termux-notification', args, { timeout: 10000 }, () => { /* A missing Termux:API must never affect anything else. */ });
}

/** Removes the notification of a session, if one was shown. */
function clearNotification(sessionId) {
  execFile('termux-notification-remove', [`ca-${sessionId}`], { timeout: 10000 }, () => {});
}

/** The words for a session in a notification: its title, or else its project's name. */
function label(proc) {
  return proc.sessionTitle || proc.projectName || 'a session';
}

/** Looks at Yep's running sessions once and shows or removes notifications for what changed. */
async function checkSessions() {
  const answer = await yep('/api/processes');
  //  When Yep does not answer there is nothing to compare; try again next time.
  if (!answer || !Array.isArray(answer.processes)) return;
  const now = new Map(answer.processes.map((p) => [p.sessionId, p]));

  //  Go through every session that is running now and compare it with what was seen before.
  for (const [sessionId, proc] of now) {
    const before = seen.get(sessionId);
    const state = proc.state;

    //  A session that has just started waiting for the user.
    if (state === 'waiting-input' && before?.state !== 'waiting-input') {
      const inbox = await yep('/api/inbox');
      const entry = inbox?.needsAttention?.find((s) => s.id === sessionId || s.sessionId === sessionId);
      const kind = entry?.pendingInputType === 'user-question' ? 'Claude has a question' : 'Claude needs your approval';
      notify(proc, kind, label(proc), true);
      seen.set(sessionId, { state, shown: true, active: true, proc });
      continue;
    }

    //  A session that stopped waiting: the question was answered, so remove the notice.
    if (before?.state === 'waiting-input' && state !== 'waiting-input') clearNotification(sessionId);

    //  A session that finished its work and is now idle.
    if (state === 'idle' && before?.active) notify(proc, 'Claude finished', proc.projectName || label(proc), false);
    seen.set(sessionId, { state, shown: false, active: state === 'in-turn' || state === 'waiting-input', proc });
  }

  //  A session that disappeared while it was working (not one that was idle) ended unexpectedly.
  for (const [sessionId, before] of seen) {
    if (now.has(sessionId)) continue;
    if (before.active) notify(before.proc, 'A session ended unexpectedly', before.proc.projectName || label(before.proc), false);
    if (before.shown) clearNotification(sessionId);
    seen.delete(sessionId);
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// 2. Trash
// ---------------------------------------------------------------------------------------------------------------------

/** Reads the first user message of a transcript as a short title ("Untitled" when none is found). */
async function titleOf(file) {
  try {
    const handle = await fsp.open(file, 'r');
    try {
      const buffer = Buffer.alloc(65536);
      const { bytesRead } = await handle.read(buffer, 0, buffer.length, 0);
      //  Look at each line until a user message with text is found.
      for (const line of buffer.subarray(0, bytesRead).toString('utf8').split('\n')) {
        let entry;
        try { entry = JSON.parse(line); } catch { continue; }
        const content = entry?.message?.content;
        const text = typeof content === 'string' ? content : Array.isArray(content) ? (content.find((c) => c.type === 'text') || {}).text : '';
        if (entry?.type === 'user' && text) return text.replace(/\s+/g, ' ').trim().slice(0, 90);
      }
    } finally { await handle.close(); }
  } catch { /* An unreadable file simply has no title. */ }
  return 'Untitled';
}

/** Turns a project folder name such as -data-data-com-termux-files-home into a short readable name. */
function projectLabel(dir) {
  const parts = dir.split('-').filter(Boolean);
  return parts.length ? parts[parts.length - 1] : dir;
}

/** Lists every session in the trash, newest first. */
async function listTrash() {
  const items = [];
  let dirs = [];
  try { dirs = await fsp.readdir(TRASH); } catch { return items; }

  //  Look into each project's trash folder for transcripts.
  for (const dir of dirs) {
    let files = [];
    try { files = await fsp.readdir(path.join(TRASH, dir)); } catch { continue; }
    for (const name of files.filter((f) => f.endsWith('.jsonl'))) {
      const file = path.join(TRASH, dir, name);
      const info = await fsp.stat(file);
      items.push({
        dir, sessionId: name.slice(0, -'.jsonl'.length), project: projectLabel(dir),
        title: await titleOf(file), bytes: info.size, trashedAt: info.mtime.toISOString(),
      });
    }
  }
  return items.sort((a, b) => b.trashedAt.localeCompare(a.trashedAt));
}

/** Moves a file and its sibling folder (subagent and tool-result files) from one place to another; the folder is optional. */
async function moveSession(fromDir, toDir, sessionId) {
  await fsp.mkdir(toDir, { recursive: true });
  const transcript = `${sessionId}.jsonl`;
  await fsp.stat(path.join(fromDir, transcript));
  //  Never overwrite something that is already there.
  try { await fsp.stat(path.join(toDir, transcript)); throw Object.assign(new Error('A session with this id is already there.'), { code: 'EEXIST' }); }
  catch (error) { if (error.code === 'EEXIST') throw error; }
  await fsp.rename(path.join(fromDir, transcript), path.join(toDir, transcript));
  try { await fsp.rename(path.join(fromDir, sessionId), path.join(toDir, sessionId)); } catch { /* No sibling folder: nothing more to move. */ }
}

/** Lists the sessions that can be moved to the trash: every session Yep knows, newest first, with its project's folder name. */
async function listSessions() {
  const projects = await yep('/api/projects');
  const running = new Set(((await yep('/api/processes'))?.processes || []).map((p) => p.sessionId));
  const items = [];

  //  Ask Yep for the sessions of each Claude project.
  for (const project of projects?.projects || []) {
    if (project.provider && project.provider !== 'claude') continue;
    const dir = project.sessionDir ? path.basename(project.sessionDir) : '';
    //  Skip projects whose session folder is unknown, because nothing could be moved from them.
    if (!SAFE_NAME.test(dir)) continue;
    const answer = await yep(`/api/projects/${project.id}/sessions`);
    for (const s of answer?.sessions || []) {
      items.push({
        dir, project: project.name || projectLabel(dir), sessionId: s.id, title: s.title || 'Untitled',
        updatedAt: s.updatedAt || '', messages: s.messageCount || 0, running: running.has(s.id),
      });
    }
  }
  return items.sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
}

/** Stops the session's running process through Yep's own API, so it cannot keep writing to a transcript that was moved. */
async function stopSession(sessionId) {
  const answer = await yep('/api/processes');
  const proc = answer?.processes?.find((p) => p.sessionId === sessionId);
  //  Nothing to stop when the session is not running.
  if (!proc) return true;
  await yep(`/api/processes/${proc.id}/abort`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' });

  //  Wait up to five seconds for it to really be gone.
  for (let i = 0; i < 10; i++) {
    await new Promise((r) => setTimeout(r, 500));
    const again = await yep('/api/processes');
    if (!again?.processes?.some((p) => p.sessionId === sessionId)) return true;
  }
  return false;
}

/** Hides or shows a session in Yep's own lists at once, the way Yep's own Archive button does (best effort). */
async function setArchived(sessionId, archived) {
  await yep(`/api/sessions/${sessionId}/metadata`, { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ archived }) });
}

/** The file in the trash that remembers which trashed sessions the user had archived before, so a restore puts that state back. */
const ARCHIVED_BEFORE = path.join(TRASH, '.pocketiris-archived.json');

/** Reads the remembered archived states ({ sessionId: true }); an empty list when there is none yet. */
async function readArchivedBefore() {
  try { return JSON.parse(await fsp.readFile(ARCHIVED_BEFORE, 'utf8')); } catch { return {}; }
}

/** Remembers (or forgets, with false) that a trashed session had been archived by the user. */
async function rememberArchived(sessionId, archived) {
  const states = await readArchivedBefore();
  //  Only archived sessions are listed, so the file stays small.
  if (archived) states[sessionId] = true; else delete states[sessionId];
  await fsp.mkdir(TRASH, { recursive: true });
  await fsp.writeFile(ARCHIVED_BEFORE + '.new', JSON.stringify(states));
  await fsp.rename(ARCHIVED_BEFORE + '.new', ARCHIVED_BEFORE);
}

/** True when Yep shows the session as archived right now (false when Yep does not answer). */
async function isArchivedNow(dir, sessionId) {
  const projects = await yep('/api/projects');
  const project = projects?.projects?.find((p) => p.sessionDir && path.basename(p.sessionDir) === dir);
  //  Without the project there is no way to ask, so treat the session as not archived (the old behaviour).
  if (!project) return false;
  const answer = await yep(`/api/projects/${project.id}/sessions/${sessionId}/metadata`);
  return answer?.session?.isArchived === true;
}

/** Finds the project's folder name under ~/.claude/projects from Yep's own project id (what Yep's page puts in its addresses). */
async function dirOfProject(projectId) {
  const projects = await yep('/api/projects');
  const project = projects?.projects?.find((p) => p.id === projectId);
  return project?.sessionDir ? path.basename(project.sessionDir) : null;
}

/** Moves a session to the trash: { dir, sessionId } (dir is the project's folder name) or { projectId, sessionId } (Yep's project id). */
async function trashSession(request) {
  const { sessionId } = request;
  const dir = request.dir || (request.projectId ? await dirOfProject(request.projectId) : null);
  if (!SAFE_NAME.test(dir || '') || !SAFE_NAME.test(sessionId || '')) return [400, { error: 'Invalid project or session name.' }];
  if (!(await stopSession(sessionId))) return [409, { error: 'Could not stop the running session first.' }];
  try {
    const wasArchived = await isArchivedNow(dir, sessionId);
    await moveSession(path.join(PROJECTS, dir), path.join(TRASH, dir), sessionId);
    await rememberArchived(sessionId, wasArchived);
    //  Yep only notices a moved file after a while, so tell it now that the session is gone from the list.
    await setArchived(sessionId, true);
    return [200, { trashed: true, dir, sessionId }];
  } catch (error) {
    return [error.code === 'ENOENT' ? 404 : 409, { error: error.code === 'ENOENT' ? 'Session not found.' : error.message }];
  }
}

/** Puts a session back from the trash: { dir, sessionId } or { projectId, sessionId }. */
async function restoreSession(request) {
  const { sessionId } = request;
  const dir = request.dir || (request.projectId ? await dirOfProject(request.projectId) : null);
  if (!SAFE_NAME.test(dir || '') || !SAFE_NAME.test(sessionId || '')) return [400, { error: 'Invalid project or session name.' }];
  try {
    await moveSession(path.join(TRASH, dir), path.join(PROJECTS, dir), sessionId);
    //  Put back the state it had before the trash: shown again, or still archived if the user had archived it.
    const wasArchived = (await readArchivedBefore())[sessionId] === true;
    await setArchived(sessionId, wasArchived);
    await rememberArchived(sessionId, false);
    return [200, { restored: true, dir, sessionId }];
  } catch (error) {
    return [error.code === 'ENOENT' ? 404 : 409, { error: error.code === 'ENOENT' ? 'Session not found in the trash.' : error.message }];
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// The small server for the app
// ---------------------------------------------------------------------------------------------------------------------

/** True when the Authorization header carries exactly the secret key (compared in constant time). */
function keyMatches(header) {
  if (!TOKEN || !header || !/^Bearer /i.test(header)) return false;
  const given = Buffer.from(header.slice(7).trim());
  const wanted = Buffer.from(TOKEN);
  return given.length === wanted.length && crypto.timingSafeEqual(given, wanted);
}

/** Reads a JSON body of at most MAX_BODY bytes. */
function readJson(request) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    request.on('data', (chunk) => {
      size += chunk.length;
      //  A body over the limit is not a real request.
      if (size > MAX_BODY) { reject(Object.assign(new Error('too large'), { status: 413 })); request.destroy(); return; }
      chunks.push(chunk);
    });
    request.on('end', () => {
      try { resolve(chunks.length ? JSON.parse(Buffer.concat(chunks).toString('utf8')) : {}); }
      catch { reject(Object.assign(new Error('not json'), { status: 400 })); }
    });
    request.on('error', reject);
  });
}

/** Answers one request. */
async function handle(request, response) {
  /** Sends a JSON answer. */
  const send = (status, body) => {
    const text = JSON.stringify(body);
    response.writeHead(status, { 'content-type': 'application/json', 'content-length': Buffer.byteLength(text) });
    response.end(text);
  };

  //  Websites always send an Origin header; the app never does. Refuse them.
  if (request.headers.origin) return send(403, { error: 'Forbidden' });

  //  The request must be addressed to this server by its own name.
  const host = request.headers.host;
  if (host !== `127.0.0.1:${PORT}` && host !== `localhost:${PORT}`) return send(403, { error: 'Forbidden' });

  //  Only the holder of the secret key may continue.
  if (!keyMatches(request.headers.authorization)) return send(401, { error: 'Unauthorized' });
  try {
    const url = new URL(request.url, 'http://127.0.0.1');

    //  Answer the request that was made.
    if (request.method === 'GET' && url.pathname === '/status') {
      return send(200, { ok: true, watching: seen.size, yepAnswers: (await yep('/api/processes')) !== null });
    }
    if (request.method === 'GET' && url.pathname === '/trash') return send(200, { items: await listTrash() });
    if (request.method === 'GET' && url.pathname === '/sessions') return send(200, { items: await listSessions() });
    if (request.method === 'POST' && (url.pathname === '/trash/move' || url.pathname === '/trash/restore')) {
      //  Tool requests must be sent as JSON.
      if (!String(request.headers['content-type'] || '').startsWith('application/json')) return send(415, { error: 'Send application/json' });
      const body = await readJson(request);
      const [status, result] = url.pathname === '/trash/move' ? await trashSession(body) : await restoreSession(body);
      return send(status, result);
    }
    return send(404, { error: 'Not found' });
  } catch (error) {
    return send(error.status || 500, { error: error.status ? error.message : 'Server error' });
  }
}

//  Start only when the app gave this program its key; without it nothing could be served safely.
if (!TOKEN) {
  log('CA_SIDECAR_TOKEN is not set: the sidecar serves nothing, but still sends notifications');
}
const server = http.createServer((request, response) => { handle(request, response).catch(() => { try { response.end(); } catch { /* gone */ } }); });
server.maxConnections = 16;
server.headersTimeout = 10000;
server.requestTimeout = 15000;
server.on('error', (error) => { log('server error:', error.code || error.message); });
//  Serve the app only when a key exists; notifications run either way.
if (TOKEN) server.listen(PORT, '127.0.0.1', () => log(`sidecar listening on 127.0.0.1:${PORT}`));

//  Watch Yep's sessions for as long as this program runs; one check at a time.
let checking = false;
setInterval(async () => {
  if (checking) return;
  checking = true;
  try { await checkSessions(); } catch (error) { log('check failed:', error.message); } finally { checking = false; }
}, POLL_MS);
log('sidecar started');

/** Daily upkeep while the phone runs: keep the Claude launcher from updating by itself, and run the monthly Yep and package checks when due (notices only). */
function upkeep() {
  const line = `"${path.join(HOME, 'bin')}/ca-updates" check due`;
  execFile('bash', ['-c', line], { timeout: 15 * 60 * 1000 }, (error) => { if (error) log('upkeep:', error.message.split('\n')[0]); });
}
const UPKEEP_HOURS = Number(process.env.CA_UPKEEP_HOURS ?? 24);
//  A value of 0 switches the upkeep off (the self-test does that); the first run waits 10 minutes, so the start-up checks go first.
if (UPKEEP_HOURS > 0) {
  setTimeout(upkeep, 10 * 60 * 1000);
  setInterval(upkeep, UPKEEP_HOURS * 3600 * 1000);
}

process.on('SIGTERM', () => { server.close(); process.exit(0); });
