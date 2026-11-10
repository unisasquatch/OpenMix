// Big-screen mode UI: mode select, Clash lobby / scoreboard / wheel / winner.
(function () {
  const $ = s => document.querySelector(s);
  const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const post = (url, body) => fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body || {}) })
    .then(async r => { if (!r.ok) { const d = await r.json().catch(() => ({})); flash(d.error || 'Not now'); } return r; });

  const MODES = [
    { id: 'freestyle', title: 'Freestyle', icon: 'sys_studio' },
    { id: 'clash', title: 'Clash', icon: 'sys_clash' },
    { id: 'lanes', title: 'Puzzle', icon: 'icon_lanes' },
    { id: 'party', title: 'Party', icon: 'sys_party' },
    { id: 'mixgrid', title: 'MixGrid', icon: 'icon_lanes' },
  ];

  document.head.insertAdjacentHTML('beforeend', `<style>
    #modebar { position: fixed; right: 2.4vw; bottom: 2.2vh; display: flex; gap: 1vw; z-index: 20; }
    #modebar button { display: flex; flex-direction: column; align-items: center; gap: .4vh; padding: .8vh .9vw; border-radius: 1.2vh; background: #000b; border: 1px solid #222; color: var(--dim); font-weight: 800; font-size: 1.3vh; letter-spacing: .1em; text-transform: uppercase; }
    #modebar button img { height: 3.6vh; filter: invert(1) brightness(.8); }
    #modebar button.on { color: #fff; border-color: #fff; box-shadow: 0 0 1.5vh #fff4; }
    #modebar button.on img { filter: invert(1); }
    #modebar button.soon { opacity: .35; cursor: default; }

    #hud .score { display: flex; flex-direction: column; align-items: center; min-width: 14vw; padding: .6vh 1.5vw; border-radius: 1.6vh; background: #000a; border: .25vh solid #222; }
    #hud .score .n { font-family: var(--font-num); font-size: 8vh; line-height: 1; font-weight: 700; color: var(--team); text-shadow: 0 0 2.5vh var(--team); }
    #hud .score .lbl { font-weight: 900; letter-spacing: .2em; font-size: 1.7vh; color: var(--team); }
    #hud .score .names { font-size: 1.6vh; color: #ccc; margin-top: .3vh; }
    #hud .score.turn { border-color: var(--team); box-shadow: 0 0 3vh -0.5vh var(--team), inset 0 0 2vh -1vh var(--team); }
    #hud .mid { display: flex; flex-direction: column; align-items: center; gap: .8vh; min-width: 22vw; }
    #hud .turnlbl { font-weight: 900; letter-spacing: .14em; font-size: 2.6vh; color: var(--team); text-shadow: 0 0 2vh var(--team); }
    #hud .pips { display: flex; gap: .8vw; }
    #hud .pips i { width: 2.2vh; height: 2.2vh; border-radius: 50%; border: .3vh solid var(--team); box-shadow: 0 0 1vh var(--team); }
    #hud .pips i.full { background: var(--team); }
    #hud .target { color: var(--dim); font-size: 1.6vh; letter-spacing: .14em; font-weight: 800; text-transform: uppercase; }
    #hud .free { font-size: 1.8vh; font-weight: 800; color: #fff; background: #ffffff18; padding: .3vh 1vw; border-radius: 99px; }
    #hud .timer { font-family: var(--font-num); font-size: 2.6vh; font-weight: 700; }
    #hud .timer.low { color: var(--orange); animation: blink .5s steps(2) infinite; }

    .overlay { position: fixed; inset: 0; z-index: 30; display: grid; place-items: center; background: radial-gradient(ellipse at center, #000d 30%, #000f 80%); }
    .panel { display: flex; flex-direction: column; align-items: center; gap: 3vh; }
    .panel h1 { margin: 0; font-size: 6vh; letter-spacing: .08em; }
    .teams { display: flex; gap: 4vw; }
    .team { width: 26vw; min-height: 26vh; border-radius: 2vh; border: .35vh solid var(--team); box-shadow: 0 0 4vh -1vh var(--team); padding: 2vh 1.5vw; background: #000; }
    .team h2 { margin: 0 0 1.5vh; color: var(--team); letter-spacing: .25em; font-size: 3vh; text-align: center; }
    .team .pl { font-size: 2.6vh; font-weight: 800; text-align: center; padding: .6vh 0; }
    .team .empty { color: #555; text-align: center; font-size: 2vh; }
    .opts { display: flex; gap: 3vw; align-items: center; }
    .opt { display: flex; align-items: center; gap: .8vw; color: var(--dim); font-weight: 800; letter-spacing: .1em; font-size: 1.8vh; text-transform: uppercase; }
    .opt button { padding: .8vh 1.2vw; border-radius: 99px; border: 1px solid #333; font-weight: 800; font-size: 1.8vh; color: #aaa; }
    .opt button.on { background: #fff; color: #000; border-color: #fff; }
    .bigpill { font-size: 2.4vh; padding: 1.6vh 4vw; }
    .hint { color: var(--dim); font-size: 2vh; }

    #wheelOv { pointer-events: none; background: #000b; }
    #wheelOv .wbox { position: relative; width: 52vh; height: 52vh; }
    #wheelOv svg { width: 100%; height: 100%; filter: drop-shadow(0 0 3vh #fff3); }
    #wheelOv .ptr { position: absolute; left: 50%; top: -2.5vh; transform: translateX(-50%); width: 0; height: 0;
      border-left: 2vh solid transparent; border-right: 2vh solid transparent; border-top: 4vh solid #fff; filter: drop-shadow(0 0 1vh #fff); }
    #wheelOv .res { position: absolute; inset: 0; display: grid; place-items: center; font-size: 10vh; font-weight: 900; text-shadow: 0 0 4vh #fff; }
    #wheelOv .cap { margin-top: 3vh; font-size: 3vh; font-weight: 800; text-align: center; min-height: 4vh; }

    #overOv h1 { font-size: 11vh; color: var(--team); text-shadow: 0 0 5vh var(--team); }
    #overOv .final { font-family: var(--font-num); font-size: 6vh; }
    .t-none { --team: #fff; }
    .lanes-panel { gap: 2vh; max-height: 94vh; overflow-y: auto; }
    .plgrid { display: grid; grid-template-columns: repeat(8, 9vw); gap: 1vh .8vw; }
    .plgrid .pl { display: flex; flex-direction: column; align-items: center; gap: .4vh; padding: 1vh .4vw; border-radius: 1.2vh; border: 1px solid #333; background: #000; color: #aaa; font-weight: 800; font-size: 1.5vh; }
    .plgrid .pl img { height: 4.5vh; filter: invert(1) brightness(.7); }
    .plgrid .pl.on { border-color: #fff; color: #fff; box-shadow: 0 0 2vh #fff5; } .plgrid .pl.on img { filter: invert(1); }
    .lboard { min-width: 30vw; } .lboard h3 { margin: 0 0 .6vh; color: var(--dim); letter-spacing: .2em; font-size: 1.6vh; text-transform: uppercase; text-align: center; }
    .lbrow { display: grid; grid-template-columns: 3vw 1fr auto; gap: 1vw; font-size: 2vh; padding: .3vh 0; border-bottom: 1px solid #222; }
    .lbrow em { color: var(--orange); font-style: normal; font-size: 1.3vh; font-weight: 900; }
    .lanes-mid .tbar { width: 22vw; height: 1vh; border-radius: 99px; background: #ffffff18; overflow: hidden; }
    .lanes-mid .tbar i { display: block; height: 100%; background: #fff; transition: width .1s linear; }
    .lanes-mid.finale .turnlbl { color: #f5c542; text-shadow: 0 0 2vh #f5c542; } .lanes-mid.finale .tbar i { background: #f5c542; }
    table.recap { font-size: 2.6vh; border-spacing: 2vw .8vh; } table.recap td:nth-child(n+2) { font-family: var(--font-num); text-align: right; }
    .blk { display: inline-block; width: 2vh; height: 2vh; border-radius: .4vh; margin-right: .8vw; vertical-align: middle; }
    .blk.w { background: #ddd; } .blk.g { background: #f5c542; } .blk.r { background: conic-gradient(#EB0C6D, #FFF100, #50B048, #00BFF3, #EB0C6D); }
    .unlock { font-size: 4vh; font-weight: 900; color: var(--orange); text-shadow: 0 0 3vh var(--orange); }
    .party-mid { min-width: 56vw; gap: 1.2vh; }
    #hud.party { min-height: 19vh; }
    #hud.party ~ #boardWrap .well { width: 78%; }
    #partyMsg h1 { font-size: 9vh; text-shadow: 0 0 4vh #fff8; }
    #modebar .sep { width: 1px; background: #333; margin: .4vh .2vw; }
    #decksOv .panel { gap: 1.6vh; max-height: 94vh; width: 86vw; }
    #decksOv .dhead { display: flex; align-items: center; gap: 2vw; width: 100%; justify-content: space-between; }
    #decksOv .dhead h1 { font-size: 5vh; }
    #decksOv .dcount { font-family: var(--font-num); font-size: 2.4vh; color: #fff; }
    #decksOv .dscroll { overflow-y: auto; width: 100%; display: flex; flex-direction: column; gap: 1.4vh; padding-right: 1vw; }
    #decksOv .grp h3 { margin: 0 0 .8vh; display: flex; gap: 1vw; align-items: center; color: var(--dim); letter-spacing: .2em; font-size: 1.6vh; text-transform: uppercase; }
    #decksOv .grp h3 button { font-size: 1.3vh; padding: .3vh .8vw; border-radius: 99px; border: 1px solid #333; color: #aaa; letter-spacing: .1em; }
    #decksOv .dgrid { display: grid; grid-template-columns: repeat(auto-fill, minmax(9.5vw, 1fr)); gap: 1vh .8vw; }
    #decksOv .dk { display: flex; flex-direction: column; align-items: center; gap: .3vh; padding: 1vh .4vw; border-radius: 1.2vh; border: 1px solid #2a2a2a; background: #000; color: #666; font-weight: 800; font-size: 1.6vh; }
    #decksOv .dk img { height: 5vh; filter: invert(1) brightness(.35); }
    #decksOv .dk small { font-size: 1.2vh; color: #555; font-weight: 700; }
    #decksOv .dk.on { border-color: #fff; color: #fff; box-shadow: 0 0 2vh #fff4; } #decksOv .dk.on img { filter: invert(1); } #decksOv .dk.on small { color: var(--dim); }
    #decksOv .presets { display: flex; gap: .8vw; flex-wrap: wrap; }
    #decksOv .presets button { padding: .8vh 1.2vw; border-radius: 99px; border: 1px solid #333; font-weight: 800; font-size: 1.6vh; color: #aaa; }
    body.mgmode #boardWrap { position: fixed; left: 2vw; bottom: 9vh; width: 30vw; margin: 0; transform: scale(.5); transform-origin: bottom left; width: 60vw; }
    body.mgmode #hud { padding-top: 0; }
    #flash { position: fixed; left: 50%; top: 18vh; transform: translateX(-50%); z-index: 40; background: #fff; color: #000; font-weight: 900; padding: 1.2vh 2vw; border-radius: 99px; font-size: 2.4vh; opacity: 0; transition: opacity .25s; }
    #flash.show { opacity: 1; }
  </style>`);
  document.body.insertAdjacentHTML('beforeend', `<div id="modebar"></div><div id="flash"></div>`);

  let lastNotice = 0, lastSpin = -1, s = null;

  // ------------------------------------------------------------------ Deck manager
  let decksOpen = false;
  async function openDecks() { decksOpen = true; renderDecks(await (await fetch('/api/decks')).json()); }
  async function deckPost(body) { const r = await post('/api/decks', body); if (r.ok) renderDecks(await r.json()); }
  function renderDecks(d) {
    if (!decksOpen) return overlay('decksOv');
    const kinds = [...new Set(d.decks.map(x => x.kind))];
    const blurb = { 'Starter': 'Starter set', 'Playlist': 'Playlists', 'Discovery': 'Discovery packs', 'Season 2': 'Season 2 playlist packs', 'Bonus': 'Baffler bonus cards (one per playlist pack)', 'Promo': 'Promo cards' };
    const o = overlay('decksOv', `<div class="panel"><div class="dhead"><h1 class="heavy">Decks</h1>
        <div class="dcount">${d.activeCount} of ${d.decks.length} decks · ${d.cardCount} cards</div>
        <button class="pill solid" id="dkDone">Done</button></div>
      <div class="presets">${[['all', 'Everything'], ['starter', 'Starter only'], ['season1', 'Season 1'], ['season2', 'Season 2 + starter']].map(([k, l]) => `<button data-p="${k}">${l}</button>`).join('')}</div>
      <div class="dscroll">${kinds.map(k => { const g = d.decks.filter(x => x.kind === k), allOn = g.every(x => x.active);
        return `<div class="grp"><h3>${blurb[k] || k}<button data-g="${k}" data-on="${allOn ? 0 : 1}">${allOn ? 'None' : 'All'}</button></h3>
          <div class="dgrid">${g.map(x => `<button class="dk${x.active ? ' on' : ''}" data-i="${x.icon}"><img src="/ui/i_${x.icon}" alt="">${esc(x.name)}<small>${x.count} cards</small></button>`).join('')}</div></div>`; }).join('')}</div>
      <div class="hint">Virtual hands draw only from the lit decks. Cards on the physical board always count.</div></div>`);
    o.querySelector('#dkDone').onclick = () => { decksOpen = false; overlay('decksOv'); };
    o.querySelectorAll('[data-i]').forEach(b => b.onclick = () => deckPost({ toggle: [b.dataset.i] }));
    o.querySelectorAll('[data-p]').forEach(b => b.onclick = () => deckPost({ preset: b.dataset.p }));
    o.querySelectorAll('[data-g]').forEach(b => b.onclick = () => {
      const on = b.dataset.on === '1', grp = new Set(d.decks.filter(x => x.kind === b.dataset.g).map(x => x.icon));
      deckPost({ active: d.decks.filter(x => grp.has(x.icon) ? on : x.active).map(x => x.icon) });
    });
  }
  function flash(t) { const f = $('#flash'); f.textContent = t; f.classList.add('show'); clearTimeout(f._t); f._t = setTimeout(() => f.classList.remove('show'), 2600); }
  const overlay = (id, html) => { let o = document.getElementById(id); if (!html) { o?.remove(); return null; }
    if (!o) { o = document.createElement('div'); o.id = id; o.className = 'overlay'; document.body.appendChild(o); } if (o._html !== html) { o.innerHTML = html; o._html = html; } return o; };

  window.renderMode = function (state) {
    s = state; const m = s.mode || {};
    $('#modeTitle .name').textContent = m.title || 'DropMix';
    $('#modeTitle .sub').textContent = m.subtitle || '';
    const bar = MODES.map(x => `<button class="${m.id === x.id ? 'on' : ''}${x.soon ? ' soon' : ''}" data-m="${x.id}"><img src="/ui/${x.icon}" alt="">${x.title}${x.soon ? ' (soon)' : ''}</button>`).join('')
      + `<i class="sep"></i><button data-d="1"><img src="/ui/i_sweets" alt="">Decks</button>`;
    if ($('#modebar')._html !== bar) {
      $('#modebar').innerHTML = $('#modebar')._html = bar;
      $('#modebar').querySelectorAll('[data-m]').forEach(b => b.onclick = () => { const x = MODES.find(y => y.id === b.dataset.m); if (!x.soon && x.id !== s.mode.id) post('/api/mode', { mode: x.id }); });
      $('#modebar').querySelector('[data-d]').onclick = openDecks;
    }
    if (m.notice && m.notice.id !== lastNotice) { if (lastNotice) flash(m.notice.text); lastNotice = m.notice.id; }
    // Modes rebuilt in the original app's look draw themselves on the 1920x1080 stage (orig.js).
    if (window.OrigUI && window.OrigUI.render(s)) { overlay('lobbyOv'); overlay('overOv'); overlay('wheelOv'); overlay('partyMsg'); overlay('mgLobby'); return; }

    if (m.id === 'clash') clash(m);
    else if (m.id === 'lanes') lanes(m);
    else if (m.id === 'party') party(m);
    else if (m.id === 'mixgrid') mixgrid(m);
    else { $('#hud').innerHTML = ''; overlay('lobbyOv'); overlay('overOv'); overlay('wheelOv'); }
    if (m.id !== 'party') { overlay('partyMsg'); $('#hud').classList.remove('party'); }
    document.body.classList.toggle('mgmode', m.id === 'mixgrid' && !['lobby', 'over'].includes(m.phase));
    if (m.id !== 'mixgrid') { overlay('mgLobby'); $('#hud')._html = null; }
  };

  // ------------------------------------------------------------------ MixGrid
  const mgAct = (action, arg) => post('/api/host', { action, args: arg == null ? {} : { arg: String(arg) } });
  function mixgrid(m) {
    overlay('overOv'); overlay('wheelOv'); overlay('lobbyOv');
    if (m.phase === 'lobby' || m.phase === 'over') {
      $('#hud').innerHTML = ''; $('#hud')._html = null;
      const o = overlay('mgLobby', '<div class="panel lanes-panel" style="width:84vw"><h1 class="heavy">MixGrid</h1><div id="mgL" style="width:100%"></div></div>');
      MixGrid.render(o.querySelector('#mgL'), m, mgAct);
      return;
    }
    overlay('mgLobby');
    if (!$('#hud').querySelector('#mgT')) { $('#hud').innerHTML = '<div id="mgT" style="width:100%"></div>'; $('#hud').querySelector('#mgT').addEventListener('mgmsg', e => flash(e.detail)); }
    MixGrid.render($('#mgT'), m, mgAct);
  }

  // ------------------------------------------------------------------ Clash
  function clash(m) {
    const side = x => x.toLowerCase();
    if (m.phase === 'lobby') {
      $('#hud').innerHTML = '';
      const team = (name, t) => `<div class="team t-${name}"><h2>${name.toUpperCase()}</h2>${t.players.length ? t.players.map(p => `<div class="pl">${esc(p)}</div>`).join('') : '<div class="empty">Join on your phone<br>and pick this team</div>'}</div>`;
      const o = overlay('lobbyOv', `<div class="panel"><img class="logo" src="/ui/logo_dropmix_no_tm" style="height:10vh;width:40vh;object-fit:cover">
        <h1 class="heavy">Clash</h1>
        <div class="teams">${team('blue', m.blue)}${team('red', m.red)}</div>
        <div class="opts">
          <div class="opt">Play to ${[15, 21].map(n => `<button data-o="score" data-v="${n}" class="${m.target === n ? 'on' : ''}">${n}</button>`).join('')}</div>
          <div class="opt">Timer ${[['0', 'Off'], ['15', '15s'], ['30', '30s'], ['60', '60s']].map(([v, l]) => `<button data-o="timer" data-v="${v}" class="${String(m.timerLimit || 0) === v ? 'on' : ''}">${l}</button>`).join('')}</div>
        </div>
        <button class="pill solid bigpill" id="startBtn">Start</button>
        <div class="hint">1v1, 2v2 or 1v2 · each team gets 2 actions a turn · the DropMix button spins the wheel</div></div>`);
      o.querySelectorAll('[data-o]').forEach(b => b.onclick = () => post('/api/host', { action: 'options', args: { [b.dataset.o]: b.dataset.v } }));
      o.querySelector('#startBtn').onclick = () => post('/api/host', { action: 'start' });
      overlay('overOv'); overlay('wheelOv');
      return;
    }
    overlay('lobbyOv');

    const cur = side(m.current);
    const sc = (name, t) => `<div class="score t-${name}${m.phase === 'play' && cur === name ? ' turn' : ''}"><span class="lbl">${name.toUpperCase()}</span><span class="n">${t.score}</span><span class="names">${t.players.map(esc).join(' & ')}</span></div>`;
    const tl = m.timerLeft != null ? `<div class="timer${m.timerLeft <= 5 ? ' low' : ''}">${Math.ceil(m.timerLeft)}s</div>` : '';
    $('#hud').innerHTML = `${sc('blue', m.blue)}<div class="mid t-${cur}">
        <div class="turnlbl">${m.phase === 'over' ? 'GAME OVER' : `${m.current.toUpperCase()}'S TURN`}</div>
        <div class="pips">${[0, 1].map(i => `<i class="${i < m.actionsLeft ? 'full' : ''}"></i>`).join('')}</div>
        ${m.free ? `<div class="free">Free action: ${esc(m.free)}</div>` : m.freeSpin ? `<div class="free">Free spin!</div>` : m.turnEnding ? `<div class="free">Turn ending…</div>` : ''}
        ${tl}<div class="target">First to ${m.target}</div></div>${sc('red', m.red)}`;

    wheel(m);

    if (m.phase === 'over' && m.winner) {
      const w = side(m.winner), lose = w === 'blue' ? 'red' : 'blue';
      const o = overlay('overOv', `<div class="panel t-${w}"><h1 class="heavy">${m.winner.toUpperCase()} WINS</h1>
        <div class="final">${m[w].score} – ${m[lose].score}</div><div class="hint">${m[w].players.map(esc).join(' & ')}</div>
        <div style="display:flex;gap:2vw"><button class="pill solid bigpill" id="rematch">Rematch</button><button class="pill bigpill" id="toLobby">Teams</button></div></div>`);
      o.querySelector('#rematch').onclick = () => post('/api/host', { action: 'start' });
      o.querySelector('#toLobby').onclick = () => post('/api/host', { action: 'lobby' });
    } else overlay('overOv');
  }

  // ------------------------------------------------------------------ Party
  let lastFoul = 0, lastBumper = 0;
  function partyBoard(m) {
    return `<div class="lboard"><h3>${esc(OMLoc.t('GameOver_Leaderboard', 'Best scores'))}</h3>${(m.leaderboard || []).slice(0, 8).map((e, i) =>
      `<div class="lbrow"><span>${i + 1}</span><span>${esc(e.name)} <em>${esc(e.difficulty).toUpperCase()} · ${e.players}P</em></span><b class="num">${e.score.toLocaleString()}</b></div>`).join('')}</div>`;
  }
  function party(m) {
    const diff = () => `<div class="opts"><div class="opt">Difficulty ${['Easy', 'Normal', 'Hard'].map(d =>
      `<button data-d="${d}" class="${m.difficulty === d ? 'on' : ''}">${d}</button>`).join('')}</div></div>`;
    const wire = o => {
      o.querySelectorAll('[data-d]').forEach(b => b.onclick = () => post('/api/host', { action: 'difficulty', args: { value: b.dataset.d } }));
      const sb = o.querySelector('#startBtn'); if (sb) sb.onclick = () => post('/api/host', { action: 'start' });
    };
    if (m.phase === 'lobby') {
      $('#hud').innerHTML = ''; $('#hud').classList.remove('party');
      const players = (s.players || []).map(p => esc(p.name));
      const o = overlay('lobbyOv', `<div class="panel lanes-panel"><img src="/ui/sys_party" style="height:9vh;filter:invert(1)" alt="">
        <h1 class="heavy">Party</h1>
        <div class="hint">Everyone plays together. Meet the crowd's requests before time runs out.<br>Clear every request in a round for bonus requests — no misses all game earns the Encore.</div>
        <div class="hint" style="color:#fff">${players.length ? `${players.length} player${players.length === 1 ? '' : 's'}: ${players.join(', ')}` : 'Join on your phone'}</div>
        ${diff()}<button class="pill solid bigpill" id="startBtn">Start the party</button>${partyBoard(m)}</div>`);
      wire(o); overlay('overOv'); overlay('wheelOv'); overlay('partyMsg');
      return;
    }
    overlay('lobbyOv');
    const hud = $('#hud');
    if (!hud.querySelector('.party-mid')) hud.innerHTML = `<div class="score t-none"><span class="lbl">SCORE</span><span class="n" id="pScore"></span><span class="names" id="pBest"></span></div>
      <div class="mid party-mid"><div class="turnlbl" id="pRound"></div><div id="pPips"></div><div class="ptiles" id="pTiles"></div></div>
      <div class="score t-none"><span class="lbl">ENCORE</span><span class="n" style="font-size:4vh" id="pEnc"></span><span class="names">no misses all game</span></div>`;
    hud.classList.add('party');
    $('#pScore').textContent = m.score.toLocaleString();
    $('#pBest').textContent = `${m.difficulty} · best ${m.highScore.toLocaleString()}`;
    $('#pRound').textContent = m.roundLabel + (m.bonusPhase ? ' · BONUS' : '');
    const pips = window.renderPips(m); if ($('#pPips')._h !== pips) { $('#pPips').innerHTML = pips; $('#pPips')._h = pips; }
    $('#pEnc').textContent = m.encoreAlive ? 'ALIVE' : '—';
    window.updateTiles($('#pTiles'), m);
    wheel(m);

    // full-screen moments: intro, round label, between rounds, banners
    let msg = null;
    if (m.phase === 'intro') msg = `<h1 class="heavy">${OMLoc.html('Party_Start', 'Here we go!')}</h1><div class="final">${OMLoc.html('Party_DrawCards', 'Take {0} cards', m.handSize)}</div><div class="hint">${OMLoc.html('Party_HighScoreToBeat', 'Best so far:')} ${m.highScore.toLocaleString()} · starting in ${Math.ceil(m.introLeft || 0)}</div>`;
    else if (m.phase === 'roundIntro') msg = `<h1 class="heavy">${esc(m.roundLabel)}</h1>`;
    else if (m.phase === 'break') msg = `<h1 class="heavy">${OMLoc.html('Party_RoundComplete', '{0} done', m.roundLabel)}</h1><div class="final">${m.score.toLocaleString()}</div>
        ${m.nextLabel ? `<div class="hint">Refill your hand to ${m.handSize} cards · ${esc(m.nextLabel)} in ${Math.ceil(m.breakLeft || 0)}</div>` : ''}`;
    if (m.foul && m.foul.id !== lastFoul) { lastFoul = m.foul.id; flash(`${OMLoc.text('Party_PartyFoul', 'Foul!')} ${m.foul.text} (−${m.foul.penalty})`); }
    if (m.bumper && m.bumper.id !== lastBumper) { lastBumper = m.bumper.id; flash(m.bumper.text); }
    const o = overlay('partyMsg', msg ? `<div class="panel">${msg}</div>` : null);
    if (o) o.style.background = 'radial-gradient(ellipse at center, #000c 30%, #000e 80%)';

    if (m.phase === 'over' && m.results) {
      const r = m.results;
      const ov = overlay('overOv', `<div class="panel lanes-panel"><h1 class="heavy" style="color:#fff">${m.score.toLocaleString()}</h1>
        ${r.newHigh ? `<div class="unlock">${OMLoc.html('Party_NewHighScore', 'A new best!')}</div>` : r.rank > 0 ? `<div class="hint">Rank #${r.rank}</div>` : ''}
        <table class="recap"><tr><td>Perfect requests</td><td>${r.perfect}</td><td>+${r.perfectPts.toLocaleString()}</td></tr>
          <tr><td>Other requests met</td><td>${r.good}</td><td>+${r.goodPts.toLocaleString()}</td></tr>
          <tr><td>Missed</td><td>${r.failed}</td><td></td></tr><tr><td>Party fouls</td><td>${r.fouls}</td><td></td></tr></table>
        ${diff()}<div style="display:flex;gap:2vw"><button class="pill solid bigpill" id="startBtn">Party again</button></div>${partyBoard(m)}</div>`);
      wire(ov);
    } else overlay('overOv');
  }

  // ------------------------------------------------------------------ Puzzle (Lanes)
  const pretty = x => x ? x[0].toUpperCase() + x.slice(1) : x;
  function leaderboard(m) {
    return `<div class="lboard"><h3>${esc(OMLoc.t('GameOver_Leaderboard', 'Best scores'))}</h3>${(m.leaderboard || []).slice(0, 8).map((e, i) =>
      `<div class="lbrow"><span>${i + 1}</span><span>${esc(pretty(e.name))}${e.hard ? ' <em>FEVER</em>' : ''}</span><b class="num">${e.score.toLocaleString()}</b></div>`).join('')}</div>`;
  }
  function lanes(m) {
    const playlistPicker = () => `<div class="plgrid">${(m.allPlaylists || []).map(pl =>
        `<button class="pl${m.playlists.includes(pl) ? ' on' : ''}" data-pl="${pl}"><img src="/ui/i_${pl}" alt=""><span>${pretty(pl)}</span></button>`).join('')}</div>
      <div class="opts"><div class="opt">Difficulty <button data-d="normal" class="${m.hard ? '' : 'on'}">Normal</button><button data-d="hard" class="${m.hard ? 'on' : ''}"${m.hardUnlocked ? '' : ' title="Score 1,000 to unlock"'}>Fever${m.hardUnlocked ? '' : ' (locked)'}</button></div></div>`;
    const wire = o => {
      o.querySelectorAll('[data-pl]').forEach(b => b.onclick = () => post('/api/host', { action: 'playlist', args: { value: b.dataset.pl } }));
      o.querySelectorAll('[data-d]').forEach(b => b.onclick = () => post('/api/host', { action: 'difficulty', args: { value: b.dataset.d } }));
      o.querySelector('#startBtn').onclick = () => post('/api/host', { action: 'start' });
    };
    if (m.phase === 'lobby') {
      $('#hud').innerHTML = '';
      const o = overlay('lobbyOv', `<div class="panel lanes-panel"><img src="/ui/icon_lanes" style="height:9vh;filter:invert(1)" alt="">
        <h1 class="heavy">Puzzle</h1><div class="hint">Pick two playlists (or leave it to chance). Their 30 cards make the deck.</div>
        ${playlistPicker()}<button class="pill solid bigpill" id="startBtn">Start</button>
        <div class="hint">Clear requests in each slot's row · match big blocks · the DropMix button spins the EQ wheel</div>${leaderboard(m)}</div>`);
      wire(o); overlay('overOv'); overlay('wheelOv');
      return;
    }
    overlay('lobbyOv');
    const pct = m.timerLeft != null && m.timerLen ? Math.max(0, m.timerLeft / m.timerLen) * 100 : 0;
    const label = { shuffle: 'SHUFFLING', spawning: 'INCOMING', play: 'YOUR MOVE', spinning: 'EQUALIZER', finale: 'FINALE', over: 'GAME OVER' }[m.phase] || '';
    $('#hud').innerHTML = `<div class="score t-none"><span class="lbl">SCORE</span><span class="n">${m.score.toLocaleString()}</span><span class="names">${m.playlists.map(pretty).join(' + ')}</span></div>
      <div class="mid lanes-mid${m.phase === 'finale' ? ' finale' : ''}"><div class="turnlbl">${label}${m.hard ? ' · FEVER' : ''}</div>
        <div class="tbar"><i style="width:${pct}%"></i></div>
        <div class="target">${m.phase === 'finale' ? 'Free plays until the timer runs out' : 'Biggest block ' + m.biggestBlock}</div></div>
      <div class="score t-none"><span class="lbl">ACTIONS</span><span class="n">${m.actionsLeft}</span><span class="names">of ${m.actions}</span></div>`;
    wheel(Object.assign({}, m, { wheel: Object.assign({ side: 'none' }, m.wheel) }));
    if (m.phase === 'over' && m.recap) {
      const r = m.recap;
      const o = overlay('overOv', `<div class="panel lanes-panel"><h1 class="heavy" style="color:#fff">${m.score.toLocaleString()}</h1>
        <table class="recap"><tr><td><i class="blk w"></i>White blocks</td><td>${r.white}</td><td>+${r.whitePts}</td></tr>
          <tr><td><i class="blk g"></i>Gold blocks</td><td>${r.gold}</td><td>+${r.goldPts}</td></tr>
          <tr><td><i class="blk r"></i>Rainbow blocks</td><td>${r.rainbow}</td><td>+${r.rainbowPts}</td></tr>
          <tr><td>Full clears</td><td>${r.fullClears}</td><td>+${r.fullClearPts}</td></tr>
          <tr><td>Biggest block × ${r.bonusPerBlock}</td><td>${r.biggest}</td><td>+${r.bonus}</td></tr></table>
        ${r.unlocked ? '<div class="unlock">FEVER UNLOCKED</div>' : ''}
        ${playlistPicker()}<div style="display:flex;gap:2vw"><button class="pill solid bigpill" id="startBtn">Play again</button></div>${leaderboard(m)}</div>`);
      wire(o);
    } else overlay('overOv');
  }

  // Wheel of Misfortune: the slices and their odds come from the user's game (/api/tables); the look is OpenMix's
  const SLICE_LOOK = { 0: { l: 'X', c: '#EB0C6D' }, 1: { l: '1', c: '#FFF100' }, 2: { l: '2', c: '#50B048' }, 3: { l: '3', c: '#00BFF3' } };
  let SLICES = [];
  fetch('/api/tables').then(r => r.json()).then(d => {
    SLICES = (d.wheel || []).filter(s => s.weight > 0).map(s => ({ v: s.value, w: s.weight, ...(SLICE_LOOK[s.value] || { l: String(s.value), c: '#fff' }) }));
  }).catch(() => {});
  const sliceTotal = () => SLICES.reduce((a, s) => a + s.w, 0) || 1;
  function wheelSvg(bonus) {
    let a = 0, out = '';
    for (const sl of SLICES) {
      const ang = sl.w / sliceTotal() * 360, a0 = (a - 90) * Math.PI / 180, a1 = (a + ang - 90) * Math.PI / 180, mid = (a + ang / 2 - 90) * Math.PI / 180;
      const p = (r, t) => `${50 + r * Math.cos(t)},${50 + r * Math.sin(t)}`;
      out += `<path d="M50,50 L${p(48, a0)} A48,48 0 ${ang > 180 ? 1 : 0} 1 ${p(48, a1)} Z" fill="${sl.v === 0 && bonus ? '#fff' : sl.c}" stroke="#000" stroke-width=".8"/>`;
      out += `<text x="${50 + 32 * Math.cos(mid)}" y="${50 + 32 * Math.sin(mid) + 3.5}" text-anchor="middle" font-size="11" font-weight="900" fill="#000">${sl.v === 0 && bonus ? '+1' : sl.l}</text>`;
      a += ang;
    }
    return `<svg viewBox="0 0 100 100"><g id="wheelRot">${out}<circle cx="50" cy="50" r="7" fill="#000" stroke="#fff" stroke-width="1"/></g></svg>`;
  }
  function wheel(m) {
    const w = m.wheel;
    if (!w || w.state === 'idle') { overlay('wheelOv'); return; }
    const o = overlay('wheelOv', `<div class="panel"><div class="wbox t-${m.wheel.side.toLowerCase()}"><div class="ptr"></div>${wheelSvg(w.bonus)}<div class="res"></div></div><div class="cap"></div></div>`);
    const rot = o.querySelector('#wheelRot');
    if (w.spin !== lastSpin) {
      lastSpin = w.spin;
      // land the chosen slice's centre under the pointer (with a little jitter inside the slice)
      let a = 0, centre = 0; for (const sl of SLICES) { const ang = sl.w / sliceTotal() * 360; if (sl.v === w.resultValue) centre = a + ang * (0.3 + Math.random() * 0.4); a += ang; }
      const final = 360 * 6 - centre;
      rot.style.transformOrigin = '50px 50px'; rot.style.transition = 'none'; rot.style.transform = 'rotate(0deg)';
      requestAnimationFrame(() => requestAnimationFrame(() => { rot.style.transition = `transform ${w.ms}ms cubic-bezier(.12,.6,.1,1)`; rot.style.transform = `rotate(${final}deg)`; }));
      o.querySelector('.cap').textContent = `${m.wheel.side.toUpperCase()} SPINS`;
    }
    if (w.state === 'landed') { o.querySelector('.res').textContent = w.result; o.querySelector('.cap').textContent = m.notice?.text || ''; }
  }
})();
