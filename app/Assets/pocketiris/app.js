/*
 * The PocketIris page: a thin view over the app. Everything it shows is read from the app (window.PocketIris, the native bridge)
 * and every change is sent back to the app; it keeps no rules or state of its own (the app and the sidecar own them).
 */
(function () {
  'use strict';
  const bridge = window.PocketIris;
  const $ = (id) => document.getElementById(id);

  //  Without the bridge this page is not running inside the app, so there is nothing to show.
  if (!bridge) { document.body.textContent = 'Open this page inside the PocketIris app.'; return; }

  // ---- talking to the sidecar (through the app, which holds the key) ----
  let nextCall = 1;
  const waiting = new Map();

  /** Sends one request to the sidecar and resolves with { status, data }. */
  function sidecar(method, path, body) {
    return new Promise((resolve) => {
      const id = String(nextCall++);
      waiting.set(id, resolve);
      bridge.sidecar(id, method, path, body ? JSON.stringify(body) : '');
    });
  }
  window.__pcSidecar = (id, status, text) => {
    const done = waiting.get(id);
    waiting.delete(id);
    let data = null;
    try { data = JSON.parse(text); } catch (e) { data = null; }
    if (done) done({ status: Number(status), data });
  };

  // ---- safety ----
  /** Shows the trusted session, the two switches and the reasons some controls are unavailable. */
  function renderSafety() {
    const [active, text] = splitOnce(bridge.trustStatus());
    $('trustText').textContent = text;
    $('trustButton').textContent = active === '1' ? 'End' : 'Start';
    setSwitch($('switchControl'), bridge.getFlag('control'));
    setSwitch($('switchFingerprint'), bridge.getFlag('fingerprint'));
    setSwitch($('switchDebug'), bridge.getFlag('debug'));

    //  Phone control cannot be switched on while Android's accessibility switch is off, so say why.
    const why = bridge.reason('control');
    $('controlReason').hidden = !why;
    $('controlReason').textContent = why;
    $('switchControl').disabled = !!why && !bridge.getFlag('control');
  }

  /** Splits "a|b|c" into ["a", "b|c"]. */
  function splitOnce(text) {
    const cut = text.indexOf('|');
    return [text.slice(0, cut), text.slice(cut + 1)];
  }

  /** Shows a switch as on or off. */
  function setSwitch(button, on) { button.setAttribute('aria-checked', on ? 'true' : 'false'); }

  $('trustButton').addEventListener('click', () => {
    //  Ending is immediate; starting asks in the app first.
    if (bridge.trustStatus().startsWith('1|')) { bridge.trustEnd(); renderSafety(); } else bridge.trustStart();
  });
  for (const [id, name] of [['switchControl', 'control'], ['switchFingerprint', 'fingerprint'], ['switchDebug', 'debug']]) {
    $(id).addEventListener('click', () => {
      //  Turning one off is immediate; turning one on asks in the app first, and the page then reads the real state.
      bridge.setFlag(name, $(id).getAttribute('aria-checked') !== 'true');
      renderSafety();
    });
  }
  window.__pcFlags = renderSafety;
  window.__pcTrust = renderSafety;

  // ---- appearance ----
  /** Reads the appearance choices from the app and shows them. */
  function renderAppearance() {
    const a = JSON.parse(bridge.getAppearance());
    document.body.className = `pc-root ui-layout-${a.layout} ui-effective-${a.effective} text-scale-${a.text}`;
    mark($('segNav'), a.nav);
    mark($('segLayout'), a.layout);
    mark($('segText'), a.text);
    $('layoutText').textContent = a.layout === 'auto' ? `Auto (${a.effective}): chosen from the width of this view.` : `Fixed to ${a.layout}.`;
  }

  /** Marks the chosen button of a segmented control. */
  function mark(group, value) {
    for (const b of group.querySelectorAll('button')) b.setAttribute('aria-pressed', b.dataset.value === value ? 'true' : 'false');
  }

  for (const [id, key] of [['segNav', 'nav'], ['segLayout', 'layout'], ['segText', 'text']]) {
    $(id).addEventListener('click', (event) => {
      const button = event.target.closest('button');
      //  Ignore taps beside the buttons.
      if (!button) return;
      bridge.setAppearance(key, button.dataset.value);
      renderAppearance();
    });
  }
  window.__pcAppearance = renderAppearance;

  // ---- sessions and trash ----
  let mode = 'live';
  let live = [];
  let trash = [];
  let lastMoved = [];
  let undoTimer = null;
  const selected = new Set();
  const restoredKeys = [];
  const recentlyRestored = [];

  /** The identity of a session in the lists. */
  function keyOf(s) { return `${s.dir}/${s.sessionId}`; }

  /** Moves one session between the two lists on screen right away, without waiting for the helper to answer again. */
  function moveOnScreen(session, toTrash) {
    const from = toTrash ? live : trash;
    const to = toTrash ? trash : live;
    const index = from.findIndex((x) => keyOf(x) === keyOf(session));
    const item = index >= 0 ? from.splice(index, 1)[0] : session;
    //  Remember restored sessions so a late, stale chat list cannot hide them again.
    if (!toTrash) { restoredKeys.push(keyOf(item)); recentlyRestored.push(item); } else { const at = restoredKeys.indexOf(keyOf(item)); if (at >= 0) { restoredKeys.splice(at, 1); recentlyRestored.splice(at, 1); } }
    to.unshift({ ...item, [toTrash ? 'trashedAt' : 'updatedAt']: new Date().toISOString() });
    $('trashTab').textContent = `In trash (${trash.length})`;
    drawSessions();
  }

  /** Loads both lists from the sidecar and draws the current one. */
  async function loadSessions() {
    const [a, b] = await Promise.all([sidecar('GET', '/sessions'), sidecar('GET', '/trash')]);
    live = a.status === 200 ? a.data.items : [];
    trash = b.status === 200 ? b.data.items : [];
    //  The chat's own list can be a minute behind, so anything already in the trash is hidden from the live list.
    const inTrash = new Set(trash.map(keyOf));
    const back = new Set(restoredKeys);
    live = live.filter((s) => !inTrash.has(keyOf(s)));
    //  A session that was just restored shows in the live list even before the chat's list has caught up.
    for (const s of recentlyRestored) if (!live.some((x) => keyOf(x) === keyOf(s)) && back.has(keyOf(s))) live.unshift(s);
    $('trashTab').textContent = `In trash (${trash.length})`;
    const down = a.status !== 200 && b.status !== 200;
    $('sessionsHelp').textContent = down
      ? 'The helper that moves sessions is not answering. Open the Terminal tab and run server-status in Scripts to check it.'
      : mode === 'live'
        ? 'Tick the sessions to move to the trash. A running session is stopped first.'
        : 'Sessions in the trash. Restore puts one back in the chat list.';
    drawSessions();
  }

  /** Draws the list of the current view. */
  function drawSessions() {
    mark($('segSessions'), mode);
    const list = $('sessionsList');
    list.textContent = '';
    const items = mode === 'live' ? live : trash;

    //  Say so when there is nothing to show.
    if (!items.length) {
      const empty = document.createElement('p');
      empty.className = 'muted';
      empty.textContent = mode === 'live' ? 'No sessions found.' : 'The trash is empty.';
      list.appendChild(empty);
    }

    for (const s of items) {
      const row = document.createElement('div');
      row.className = 'item';
      const key = keyOf(s);

      //  In the live view each row has a checkbox; in the trash view a Restore button.
      if (mode === 'live') {
        const box = document.createElement('input');
        box.type = 'checkbox';
        box.checked = selected.has(key);
        box.setAttribute('aria-label', `Select ${s.title}`);
        box.addEventListener('change', () => { if (box.checked) selected.add(key); else selected.delete(key); updateMove(); });
        row.appendChild(box);
      }
      const what = document.createElement('div');
      what.className = 'what';
      const title = document.createElement('div');
      title.className = 'title';
      title.textContent = s.title;
      const meta = document.createElement('div');
      meta.className = 'meta';
      const when = (s.updatedAt || s.trashedAt || '').slice(0, 10);
      meta.textContent = [s.project, when, s.running ? 'running (will be stopped first)' : ''].filter(Boolean).join(' · ');
      what.append(title, meta);
      row.appendChild(what);
      if (mode === 'trash') {
        const restore = document.createElement('button');
        restore.className = 'btn';
        restore.type = 'button';
        restore.textContent = 'Restore';
        restore.addEventListener('click', () => restoreOne(s, restore));
        row.appendChild(restore);
      }
      list.appendChild(row);
    }
    $('moveButton').hidden = mode !== 'live';
    updateMove();
  }

  /** Enables the Move button only when something is ticked, and says why otherwise. */
  function updateMove() {
    const n = selected.size;
    $('moveButton').disabled = n === 0;
    $('moveButton').textContent = n === 0 ? 'Move to trash' : `Move ${n} to trash`;
    $('moveReason').textContent = mode === 'live' && n === 0 ? 'Tick at least one session first.' : '';
  }

  $('segSessions').addEventListener('click', (event) => {
    const button = event.target.closest('button');
    //  Ignore taps beside the buttons.
    if (!button) return;
    mode = button.dataset.value;
    //  Draw what is already known straight away, then refresh from the helper.
    drawSessions();
    loadSessions();
  });

  $('moveButton').addEventListener('click', async () => {
    const keys = [...selected];
    const moved = [];
    $('moveButton').disabled = true;

    //  Move the ticked sessions one after another and remember which worked, for Undo.
    for (const key of keys) {
      const [dir, sessionId] = key.split('/');
      const answer = await sidecar('POST', '/trash/move', { dir, sessionId });
      if (answer.status === 200) { moved.push({ dir, sessionId }); moveOnScreen((live.find((x) => keyOf(x) === key)) || { dir, sessionId, title: sessionId }, true); }
    }
    selected.clear();
    lastMoved = moved;
    drawSessions();
    showUndo(moved.length === keys.length
      ? `Moved ${moved.length} session${moved.length === 1 ? '' : 's'} to the trash.`
      : `Moved ${moved.length} of ${keys.length} sessions; the others could not be moved.`);
    await loadSessions();
  });

  /** Puts one session back from the trash. */
  async function restoreOne(session, button) {
    button.disabled = true;
    const answer = await sidecar('POST', '/trash/restore', { dir: session.dir, sessionId: session.sessionId });
    $('sessionsHelp').textContent = answer.status === 200 ? 'Restored. It reappears in the chat list within about a minute.' : `Could not restore: ${answer.data?.error || 'no answer'}`;
    //  Show the result at once, then confirm with the helper.
    if (answer.status === 200) moveOnScreen(session, false);
    await loadSessions();
  }

  /** Shows the Undo message for ten seconds. */
  function showUndo(text) {
    $('undoText').textContent = text;
    $('undoBar').hidden = lastMoved.length === 0 && !text;
    //  Offer Undo only when something was really moved; a message that nothing moved has nothing to undo.
    $('undoButton').hidden = lastMoved.length === 0;
    clearTimeout(undoTimer);
    undoTimer = setTimeout(() => { $('undoBar').hidden = true; lastMoved = []; }, 10000);
  }

  $('undoButton').addEventListener('click', async () => {
    const back = lastMoved;
    lastMoved = [];
    $('undoBar').hidden = true;

    //  Put everything that was just moved back.
    for (const s of back) {
      const answer = await sidecar('POST', '/trash/restore', s);
      //  Show each restored session at once.
      if (answer.status === 200) moveOnScreen(trash.find((x) => keyOf(x) === keyOf(s)) || s, false);
    }
    await loadSessions();
  });

  // ---- developer ----
  /** Shows the debugging link choice and its sentence. */
  function renderLink() {
    const [mode, text] = splitOnce(bridge.linkStatus());
    $('linkSelect').value = mode;
    $('linkText').textContent = text;
    const why = bridge.reason('link');
    $('linkReason').hidden = !why;
    $('linkReason').textContent = why;
    $('linkSelect').disabled = !!why && mode === 'off';
  }
  /** Shows the Cable switch and its sentence. */
  function renderCable() {
    const [on, text] = splitOnce(bridge.cableStatus());
    setSwitch($('switchCable'), on === '1');
    $('cableText').textContent = text;
  }
  $('switchCable').addEventListener('click', () => {
    //  Ask the app; it may ask the user first, and then calls back so the page shows the real state.
    bridge.cableSet($('switchCable').getAttribute('aria-checked') !== 'true');
  });
  window.__pcCable = renderCable;
  $('linkSelect').addEventListener('change', () => { $('linkSelect').disabled = true; bridge.linkSet($('linkSelect').value); });
  window.__pcLink = () => { $('linkSelect').disabled = false; renderLink(); };
  $('settingsSelect').addEventListener('change', () => {
    const value = $('settingsSelect').value;
    //  Open the chosen Android settings page, then reset the choice.
    if (value === 'accessibility') bridge.openAccessibility(); else if (value) bridge.openSettings(value);
    $('settingsSelect').value = '';
  });

  const TESTS = [
    ['info', 'Phone and fingerprint info'], ['dialog', 'Mock approval dialog'], ['fingerprint', 'Fingerprint: wrong finger, then right finger'],
    ['slip', '"Let Claude decide" slips'], ['network', 'Network addresses and debugging port'], ['guards', 'Address checks'],
    ['termuxinstall', 'Reinstall Termux:Boot through the update path (Android asks you to confirm)'],
  ];
  for (const [id, label] of TESTS) {
    const row = document.createElement('div');
    row.className = 'item';
    const what = document.createElement('div');
    what.className = 'what';
    const title = document.createElement('div');
    title.className = 'title';
    title.textContent = label;
    const result = document.createElement('div');
    result.className = 'test-result';
    result.id = `test-${id}`;
    what.append(title, result);
    const run = document.createElement('button');
    run.className = 'btn';
    run.type = 'button';
    run.textContent = 'Run';
    run.addEventListener('click', () => {
      run.disabled = true;
      result.textContent = 'Running…';
      result.dataset.started = String(Date.now());
      bridge.runTest(id);
    });
    row.append(what, run);
    $('tests').appendChild(row);
  }
  window.__pcTestResult = (name, report) => {
    const result = $(`test-${name}`);
    //  Show the report (after "Running…" has been on screen long enough to notice) and let the test run again.
    if (result) {
      const wait = Math.max(0, 400 - (Date.now() - Number(result.dataset.started || 0)));
      setTimeout(() => {
        result.textContent = `${new Date().toLocaleTimeString()}  ${report}`;
        result.closest('.item').querySelector('button').disabled = false;
        result.scrollIntoView({ block: 'nearest' });
      }, wait);
    }
  };

  // ---- cards ----
  /** Every card opens and closes from its title row; the app remembers which ones are open between visits and app restarts. */
  function setupCards() {
    const saved = new Map(bridge.getCards().split(',').filter(Boolean).map((pair) => pair.split('=')));
    for (const card of document.querySelectorAll('details.card')) {
      //  A card the user never touched keeps the page's default (open, or closed for Developer).
      if (saved.has(card.id)) card.open = saved.get(card.id) === '1';
      card.addEventListener('toggle', () => bridge.setCard(card.id, card.open));
    }
  }

  // ---- versions ----
  /** Shows the quiet version line only while something installed differs from the tested set; otherwise nothing. */
  function renderVersions() {
    const [state, text] = splitOnce(bridge.versionStatus());
    $('versionBanner').hidden = state !== 'mismatch';
    $('versionBanner').textContent = state === 'mismatch' ? text : '';
  }

  // ---- updates ----
  const PARTS = ['claude', 'yep', 'pkgs'];
  const checking = {};
  const FRESH = 24 * 3600;

  /** Compares two versions such as 2.1.312 and 2.1.313: positive when a is newer. */
  function cmp(a, b) {
    const x = String(a).split('.').map(Number); const y = String(b).split('.').map(Number);
    for (let i = 0; i < 3; i++) if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) - (y[i] || 0);
    return 0;
  }

  /** Says when something was checked, in the phone's own date format. */
  function when(t) { return t ? new Date(Number(t) * 1000).toLocaleDateString() : ''; }

  /**
   * Sets a row's one button: "Updating…" or "Checking…" while that runs; "Update to …" (brand colour) when something newer is known;
   * "Up to date" after a check within the last day found nothing newer (pressing it checks again); otherwise "Check now".
   */
  function setButton(row, state) {
    const b = row.querySelector('[data-act]');
    b.disabled = state.busy;
    b.classList.toggle('update', state.mode === 'update');
    b.dataset.mode = state.mode;
    b.dataset.summary = state.summary || '';
    b.textContent = state.label;
  }

  /** Works out one row's button from what is known: running, checking, newer, freshly up to date, or unknown. */
  function buttonFor(running, isChecking, newer, checkedAt, updateLabel) {
    if (running) return { mode: 'busy', busy: true, label: 'Updating…' };
    if (isChecking) return { mode: 'busy', busy: true, label: 'Checking…' };
    if (newer) return { mode: 'update', busy: false, label: updateLabel };
    const fresh = checkedAt && Date.now() / 1000 - Number(checkedAt) < FRESH;
    return fresh ? { mode: 'check', busy: false, label: 'Up to date' } : { mode: 'check', busy: false, label: 'Check now' };
  }

  /** Shows what Termux and the app know about updates: one line and one button per row, a running update, and the last result. */
  function renderUpdates() {
    let s = {};
    //  A missing or half-written state file just means "not checked yet".
    try { s = JSON.parse(bridge.updatesStatus()); } catch (e) { s = {}; }
    const running = s.running || '';
    for (const what of PARTS) {
      const r = s[what] || {};
      const row = $(`upd-${what}`);
      //  A check started from this page lasts until Termux writes a new time.
      if (checking[what] !== undefined && checking[what] !== String(r.checkedAt || r.triedAt || '')) delete checking[what];
      let line; let newer; let summary; let label;
      //  Packages are counted; the two programs are compared by version.
      if (what === 'pkgs') {
        const n = Number(r.waiting || 0);
        line = r.checkedAt ? `${n} update${n === 1 ? '' : 's'} waiting${n ? ` (${String(r.names || '').trim()})` : ''}. Checked ${when(r.checkedAt)}.` : 'Not checked yet.';
        //  Claude Code runs on Termux's glibc packages, so say when they are part of the batch.
        if (Number(r.glibc)) line += ' Includes glibc packages, which Claude Code depends on.';
        newer = n > 0; summary = `${n} package${n === 1 ? '' : 's'} to upgrade`; label = `Update ${n}`;
      } else {
        newer = !!(r.available && r.installed && cmp(r.available, r.installed) > 0);
        const kind = what === 'claude' ? 'newest stable release' : 'newest release';
        line = r.installed ? `${r.installed} installed; ${kind}: ${r.available || 'unknown'}. Checked ${when(r.checkedAt) || 'never'}.` : 'Not checked yet.';
        summary = `from ${r.installed} to ${r.available}`; label = `Update to ${r.available}`;
      }
      row.querySelector('[data-line]').textContent = line;
      setButton(row, { ...buttonFor(running === what || (running && newer), what in checking, newer, r.checkedAt, label), summary });
    }

    let a = {};
    //  The Termux apps' state lives in the app: installed versions from Android, the last GitHub check and install message.
    try { a = JSON.parse(bridge.termuxAppsStatus()); } catch (e) { a = {}; }
    const st = a.state || {};
    const apps = st.apps || {};
    const names = { 'com.termux': 'Termux', 'com.termux.api': 'API', 'com.termux.boot': 'Boot', 'com.termux.widget': 'Widget' };
    const behind = Object.values(apps).filter((x) => x.behind);
    $('upd-apps').querySelector('[data-line]').textContent = Object.entries(names)
      .map(([id, name]) => `${name} ${(a.installed || {})[id] || 'not installed'}${apps[id] && apps[id].behind ? ` (newest ${apps[id].latest})` : ''}`).join(' · ')
      + (st.checkedAt ? `. Checked ${when(st.checkedAt)}.` : '. Not checked yet.') + (st.last ? ` ${st.last}` : '');
    setButton($('upd-apps'), {
      ...buttonFor(a.busy === 'installing', a.busy === 'checking', behind.length > 0, st.checkedAt, `Update ${behind.length}`),
      summary: behind.map((x) => `${x.name} ${x.installed} to ${x.latest}`).join(', '),
    });

    //  The last result stays visible, so the user always knows how the last update went.
    const last = s.last || {};
    $('updResult').hidden = !last.text && !running;
    $('updResult').textContent = running ? `Updating ${running}… this page follows it.` : last.text ? `${last.ok === 'true' ? 'Done' : 'Not done'} (${when(last.at)}): ${last.text}` : '';
  }

  /** Remembers the time of the last check of one part, so its "Checking…" ends when Termux writes a new one. */
  function startChecking(what) {
    let s = {};
    //  A missing state file means there was no earlier check.
    try { s = JSON.parse(bridge.updatesStatus()); } catch (e) { s = {}; }
    checking[what] = String((s[what] || {}).checkedAt || (s[what] || {}).triedAt || '');
  }

  for (const what of [...PARTS, 'apps']) {
    const row = $(`upd-${what}`);
    row.querySelector('[data-project]').addEventListener('click', () => bridge.openProject(what));
    row.querySelector('[data-act]').addEventListener('click', (event) => {
      const b = event.currentTarget;
      //  One button: install when an update is known, otherwise check (again).
      if (b.dataset.mode === 'update') {
        if (what === 'apps') bridge.installTermuxApps(b.dataset.summary); else bridge.runUpdate(what, b.dataset.summary);
      } else if (what === 'apps') {
        bridge.checkTermuxApps();
      } else {
        startChecking(what);
        bridge.checkUpdate(what);
      }
      renderUpdates();
    });
  }
  window.__pcUpdates = renderUpdates;

  // ---- start ----
  $('aboutLine').textContent = `App version ${bridge.appVersion()}`;
  renderAppearance();
  renderSafety();
  renderLink();
  renderCable();
  renderVersions();
  renderUpdates();
  loadSessions();
  setupCards();

  //  Keep the live parts fresh: a trusted session ends by itself, the debugging link finds its port a moment after switching on, a version check
  //  may finish, and an update reports its progress.
  setInterval(() => { renderSafety(); renderLink(); renderAppearance(); renderCable(); renderVersions(); renderUpdates(); }, 2000);

  //  Reload the lists whenever the app shows this page again.
  window.__pcShown = loadSessions;
})();
