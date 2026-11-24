// Shows whether RigControl (or RichPresence) is running and what it last heard from the streaming sites.
// link.js (loaded first) finds which port this Windows user's RigControl listens on.
const conn = document.getElementById('conn');
const connText = document.getElementById('connText');
const now = document.getElementById('now');

function line(cls, text) { const d = document.createElement('div'); d.className = cls; d.textContent = text; return d; }

rpFind(true)
  .then(found => {
    if (!found) throw new Error('not running');
    const s = found.status;
    conn.className = 'on';
    connText.textContent = `Connected to ${s.app || 'RigControl'} ${s.version || ''}`.trim();
    // Without the secret this copy can't tell your RigControl from another program answering on this PC.
    if (!s.checked) now.appendChild(line('dim card', "This copy can't check that the app is your own RigControl. On a PC that other people use too, use the copy from Get the browser extension on RigControl's Games page."));
    const list = (s.watching || []).filter(w => w.title || w.playing);
    if (!list.length) { now.appendChild(line('dim card', 'Play something on Netflix, Hulu, Disney+, Prime Video, Max, Crunchyroll, Paramount+, Peacock, Apple TV+, Plex, YouTube or Twitch.')); return; }
    for (const w of list) {
      const c = line('card', '');
      c.appendChild(line('svc', `${w.service} - ${w.playing ? 'playing' : 'paused'}`));
      c.appendChild(line('title', w.title || "Couldn't read the title yet"));
      if (w.episode) c.appendChild(line('dim', w.episode));
      now.appendChild(c);
    }
  })
  .catch(() => {
    conn.className = 'off';
    connText.textContent = "RigControl isn't running for you on this PC.";
    now.appendChild(line('dim card', 'Open RigControl in your own Windows account and turn on Show on Discord (Games page). RichPresence 1.6.0 or newer works too.'));
  });
