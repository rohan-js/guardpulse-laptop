// Taps the SSE stream of devices/{id}/control/v2 exactly like the agent does,
// logging every raw frame so we can see Firebase's real put/patch shapes.
const fs = require('fs');
const path = require('path');
const s = JSON.parse(fs.readFileSync(path.join(__dirname, 'e2e-session.json'), 'utf8'));
const dev = process.argv[2] || '4f2dde5e337243c68c66d33d6982dfaa';
const url = 'https://guardpulse-laptop-control-default-rtdb.firebaseio.com/devices/' + dev + '/control/v2.json?auth=' + s.idToken;

(async () => {
  const res = await fetch(url, { headers: { Accept: 'text/event-stream' } });
  console.log('[tap] connected', res.status);
  const decoder = new TextDecoder();
  let buf = '';
  for await (const chunk of res.body) {
    buf += decoder.decode(chunk, { stream: true });
    let idx;
    while ((idx = buf.indexOf('\n\n')) >= 0) {
      const frame = buf.slice(0, idx);
      buf = buf.slice(idx + 2);
      if (frame.trim()) console.log('[frame] ' + frame.replace(/\n/g, ' | '));
    }
  }
  console.log('[tap] stream ended');
})().catch(e => { console.error('[tap] error', e.message); process.exitCode = 1; });
