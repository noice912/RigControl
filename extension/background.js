// Passes what the streaming-site tabs report to RigControl (or RichPresence) on this PC (127.0.0.1 only).
// link.js finds which port (47610-47619) this Windows user's RigControl listens on; link-token.js holds the secret
// that lets it check the app really is yours (empty in a copy that RigControl did not hand out).
importScripts('link-token.js', 'link.js');
const latest = {};         // per tab: number of its newest message, so a retried "stopped" never lands after a later report

chrome.runtime.onMessage.addListener((msg) => {
  if (!msg || msg.type !== 'watching' || !msg.info) return;
  const id = `${msg.info.tab || ''}|${msg.info.service || ''}`;
  const n = latest[id] = (latest[id] || 0) + 1;
  send(msg.info, msg.stop ? 3 : 0, id, n);
});

async function post(port, info) {
  const r = await fetch(`http://127.0.0.1:${port}/watching`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-RichPresence': '1' },
    body: JSON.stringify(info)
  });
  if (!r.ok) throw new Error(String(r.status));
}

async function deliver(info) {
  if (rpTrusted()) {
    try { await post(rpPort, info); return; } catch (e) {
      if (e.message === '400') throw e;   // the app is there and didn't want this report
      rpForget();                         // the app restarted, maybe on another port: look again
    }
  }
  const found = await rpFind(false);
  if (!found) throw new Error('not running');
  await post(found.port, info);
}

// a "stopped" that didn't get through is tried again (3 times, 2 s apart); anything else is just dropped
function send(info, retries, id, n) {
  deliver(info).catch(() => {
    // The app isn't running: nothing to do, unless this was a "stopped"
    if (retries > 0) setTimeout(() => { if (latest[id] === n) send(info, retries - 1, id, n); }, 2000);
  });
}
