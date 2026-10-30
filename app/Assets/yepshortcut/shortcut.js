/*
 * PocketIris's "Move to trash" shortcut inside the chat page. The app injects this file into Yep's page. It adds one entry to
 * Yep's session menu (found by the attribute Yep puts on it) and shows an Undo message after a move. If Yep's page ever changes so
 * that the menu cannot be found, nothing happens and the Trash screen of the PocketIris tab still works.
 * The only thing it can ask of the app is to move a session to the trash or to restore it (window.PocketTrash).
 */
(function () {
  'use strict';
  //  Run only once per page, and only inside the app.
  if (window.__pcShortcut || !window.PocketTrash) return;
  window.__pcShortcut = true;
  const trash = window.PocketTrash;

  //  Remember what was touched last, because the session of a list entry is found from the entry that held the touched button.
  let lastTouched = null;
  document.addEventListener('pointerdown', (event) => { lastTouched = event.target; }, true);

  /** Finds { projectId, sessionId } in an address such as /projects/ID/sessions/ID. */
  function fromPath(path) {
    const match = /\/projects\/([^/]+)\/sessions\/([^/?#]+)/.exec(path || '');
    return match ? { projectId: match[1], sessionId: match[2] } : null;
  }

  /** Finds the session a menu belongs to: the list entry that was touched, or else the session that is open. */
  function sessionOfMenu() {
    //  Climb from the touched button to the nearest list entry that links to a session.
    for (let node = lastTouched, i = 0; node && i < 8; node = node.parentElement, i++) {
      const link = node.querySelector && node.querySelector('a[href*="/projects/"][href*="/sessions/"]');
      const found = link && fromPath(link.getAttribute('href'));
      if (found) return found;
    }
    return fromPath(location.pathname);
  }

  /** Shows the message with an Undo button for ten seconds. */
  function toast(text, onUndo) {
    document.querySelectorAll('.pc-toast').forEach((old) => old.remove());
    const box = document.createElement('div');
    box.className = 'pc-toast';
    const label = document.createElement('span');
    label.textContent = text;
    box.appendChild(label);

    //  Offer Undo only when there is something to undo.
    if (onUndo) {
      const undo = document.createElement('button');
      undo.type = 'button';
      undo.textContent = 'Undo';
      undo.addEventListener('click', () => { box.remove(); onUndo(); });
      box.appendChild(undo);
    }
    document.body.appendChild(box);
    setTimeout(() => box.remove(), 10000);
  }

  /** The app answers each request through this function. */
  window.__pcTrashDone = (action, projectId, sessionId, status) => {
    const ok = Number(status) === 200;
    //  Tell the truth about what happened.
    if (action === 'move') {
      toast(ok ? 'Moved to trash. The list updates in a moment.' : 'Could not move it to the trash.', ok ? () => trash.restore(projectId, sessionId) : null);
    } else {
      toast(ok ? 'Restored. It reappears in the list within about a minute.' : 'Could not restore it.', null);
    }
  };

  /** Adds the entry to one open session menu. */
  function addEntry(menu) {
    if (menu.dataset.pcTrash) return;
    menu.dataset.pcTrash = '1';
    const session = sessionOfMenu();
    //  Without a known session there is nothing to move, so leave the menu alone.
    if (!session) return;
    const buttons = [...menu.querySelectorAll('button')];
    const archive = buttons.find((b) => /archive/i.test(b.textContent || ''));
    //  Copy Yep's own Archive entry, so the new entry has the same look, spacing and icon size; then swap the icon and the words.
    const entry = archive ? archive.cloneNode(true) : document.createElement('button');
    entry.type = 'button';
    entry.removeAttribute('aria-label');
    const icon = entry.querySelector('svg');

    //  Draw a small trash can in place of the Archive icon.
    if (icon) icon.innerHTML = '<polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6M14 11v6"/><path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/>';
    const words = document.createTreeWalker(entry, NodeFilter.SHOW_TEXT);
    let replaced = false;

    //  Put "Move to trash" in the first piece of text that has words.
    for (let node = words.nextNode(); node; node = words.nextNode()) {
      if (!replaced && node.nodeValue.trim()) { node.nodeValue = 'Move to trash'; replaced = true; } else if (replaced) node.nodeValue = '';
    }
    if (!replaced) entry.append('Move to trash');
    entry.addEventListener('click', (event) => {
      event.stopPropagation();
      //  Close Yep's menu, then move the session.
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      for (const type of ['pointerdown', 'mousedown', 'mouseup', 'click']) document.body.dispatchEvent(new MouseEvent(type, { bubbles: true }));
      trash.move(session.projectId, session.sessionId);
    });
    if (archive && archive.parentNode) archive.parentNode.insertBefore(entry, archive.nextSibling); else menu.appendChild(entry);
  }

  //  Watch the page for Yep's session menu opening.
  new MutationObserver(() => document.querySelectorAll('[data-session-menu-open]').forEach(addEntry))
    .observe(document.body, { childList: true, subtree: true });
})();
