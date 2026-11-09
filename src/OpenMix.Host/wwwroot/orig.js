// The original app's screens on a 1920x1080 stage (see orig.css, docs/ORIGINAL_UI.md).
// OrigUI.render(state) draws the screen for modes rebuilt so far (Party) and returns true;
// it returns false for modes that still use the older table view.
(function () {
  const $ = s => document.querySelector(s);
  const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const post = (url, body) => fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body || {}) });
  const host = (action, args) => post('/api/host', { action, args: args || {} });
  // game text by key from the imported strings table (board.js OMLoc); the fallback is OpenMix's own wording for a
  // missing key. LT = text with {0}, {1}… filled in; LH = the same as HTML (rich-text tags rendered); LU = LH in capitals
  const LT = (k, f, ...a) => OMLoc.fmt(OMLoc.t(k, f), ...a);
  const LH = (k, f, ...a) => OMLoc.rich(LT(k, f, ...a)), LU = (k, f, ...a) => OMLoc.rich(LT(k, f, ...a), { upper: true });
  OMLoc.onLoad(() => { if (window.OrigUI && lastState) window.OrigUI.render(lastState); });
  const COL = { Lead: 'var(--o-yellow)', Loop: 'var(--o-magenta)', Beat: 'var(--o-blue)', Bass: 'var(--o-green)', Wild: '#fff', Fx: '#cfcfcf' };
  // Values imported from the user's game at setup (/api/tables): Wheel of Misfortune odds and tints, the coin-flip
  // curve, VUMeterTriangle bloom per instrument, deck-builder rules, saved-mix rules, the master loop length.
  let TB = { wheel: [], wheelColours: {}, coinCurve: null, vuBloom: {}, deckRules: null, snapshotMinCards: null, loopBars: 0 };
  let SLICE_EDGES = [0, 1];                                                      // cumulative slice fractions, by slice
  fetch('/api/tables').then(r => r.json()).then(d => {
    TB = d;
    const tot = (d.wheel || []).reduce((a, s) => a + Math.max(0, s.weight), 0);
    if (tot > 0) { let a = 0; SLICE_EDGES = [0, ...d.wheel.map(s => (a += Math.max(0, s.weight)) / tot)]; }
  }).catch(() => {});
  // a Wild (no bloom of its own in the meter) and unknown types use a middle bloom
  const vuBloom = t => (TB.vuBloom || {})[String(t).toLowerCase()] ?? .5;
  // a WheelOfMisfortune colour field as [r, g, b] (0..1); null when not imported
  const wcol = k => (TB.wheelColours || {})[k] || null;
  const rgb = c => c ? `rgb(${c.map(v => Math.round(v * 255)).join(',')})` : '';
  const GLOW = [['#FFF100', '#EB0C6D'], ['#EB0C6D', '#EB0C6D'], ['#EB0C6D', '#00BFF3'], ['#00BFF3', '#00BFF3'], ['#00BFF3', '#50B048']];

  // ---- stage ----
  document.body.insertAdjacentHTML('beforeend', '<div id="ostage-wrap"><div id="ostage"></div></div>');
  const stage = $('#ostage');
  function fit() {
    const k = Math.min(innerWidth / 1920, innerHeight / 1080);
    stage.style.transform = `translate(-50%, -50%) scale(${k})`;
  }
  addEventListener('resize', fit); fit();

  // Persistent layers so animations aren't restarted on every state update.
  const layer = (id, cls) => { let el = stage.querySelector('#' + id); if (!el) { el = document.createElement('div'); el.id = id; if (cls) el.className = cls; stage.appendChild(el); } return el; };
  const setHtml = (el, html) => { if (el._h !== html) { el.innerHTML = html; el._h = html; } return el; };

  // ---- the slot strip ----
  function slotsHtml(state, opts = {}) {
    return state.pads.map(p => {
      const c = p.top, k = p.pad, [g1, g2] = GLOW[k];
      if (opts.label && opts.label(k)) return `<div class="o-slot player" style="--g1:${g1};--g2:${g2}"><img class="frame" src="/ui/shell_slot_0${4 - k}" alt="" style="filter:none;opacity:0"><div class="lbl" style="border:6px solid #fff;border-radius:12px;box-shadow:0 0 22px #fff8, inset 0 0 22px #fff4">${opts.label(k)}</div></div>`;
      if (!c) return `<div class="o-slot" style="--g1:${g1};--g2:${g2}"><img class="frame" src="/ui/shell_slot_0${4 - k}" alt="">${opts.join && opts.join(k) ? `<div class="lbl">JOIN</div>` : ''}</div>`;
      const n = Math.max(0, Math.min(3, c.power || 0));
      // tint/VU colour: the card's colour; a Wild takes its slot's colour (white washed the art out under the
      // studio volume tint)
      const cc = c.type === 'Wild' ? g1 : (COL[c.type] || '#fff');
      return `<div class="o-slot" data-pad="${k}" data-ty="${c.type}" style="--c:${cc};--bloom:${vuBloom(c.type)}"><div class="vu">${[0, 1, 2].map(i => `<i class="${i < n ? 'on' : ''}"></i>`).join('')}</div>
        <div class="art"><img src="/art/${encodeURIComponent(c.image)}" alt="" draggable="false"></div></div>`;
    }).join('');
  }

  // ======================================================================================
  // Party
  // ======================================================================================
  // request visuals (OBB Resources/PartyRequestVisuals: power1..3, lead.., remove_*, instruments, equalizer)
  const SHADOW_CARD = { Lead: 'shadow_yellow_card', Loop: 'shadow_red_card', Beat: 'shadow_blue_card', Bass: 'shadow_green_card' };
  const SHADOW_INST = { synth: 'shadow_synth', vocal: 'shadow_microphone', guitar: 'shadow_guitar', drums: 'shadow_drum', horns: 'shadow_horns', strings: 'shadow_strings', sampler: 'shadow_sampler' };
  const TILE_ICON = t => {
    const ty = t.type, lvl = /^Level(\d)/.exec(ty);
    if (lvl) return { img: `<img class="pow" src="/ui/shadow_power_${lvl[1]}" alt="">`, label: LU('Party_LevelRequest', 'ANY TYPE') };
    if (SHADOW_CARD[ty]) return { img: `<img class="card" src="/ui/${SHADOW_CARD[ty]}" alt="">` };
    const rm = /^Remove(\w+)/.exec(ty);
    if (rm && SHADOW_CARD[rm[1]]) return { img: `<img class="card" src="/ui/${SHADOW_CARD[rm[1]].replace('shadow_', 'shadow_remove_')}" alt="">`, label: 'REMOVE' };
    if (ty === 'Equalizer') return { img: `<img class="inst" src="/ui/shadow_dm_button" alt="">` };
    if (ty === 'FX') return { img: `<img class="card" src="/ui/sys_fx_card" alt="">` };
    if (ty === 'Wild') return { img: `<img class="card" src="/ui/sys_wild_card" alt="">` };
    const inst = (t.instrument || ty.replace(/^Add/, '')).toLowerCase();
    if (SHADOW_INST[inst]) return { img: `<img class="inst" src="/ui/${SHADOW_INST[inst]}" alt="">` };
    if (/^Slot(\d)/.test(ty)) return { img: '', label: `SLOT ${+ty.slice(4) + 1}` };
    return { img: '', label: esc(t.prompt || ty).toUpperCase() };
  };

  document.head.insertAdjacentHTML('beforeend', `<style>
    .p-tiles { position: absolute; left: 0; right: 0; top: 189px; height: 591px; display: flex; justify-content: center; gap: 201px; }
    .p-tile { position: relative; width: 474px; height: 591px; border-radius: 12px; background: #fff; color: #000; overflow: visible; transition: background .15s; }
    .p-tile .drain { position: absolute; left: 0; right: 0; top: 0; border-radius: 12px 12px 0 0; background: #5b5b5b; }
    .p-tile .line { position: absolute; left: -30px; right: -30px; height: 22px; margin-top: -11px; background: url('/ui/request_fillline') center / 100% 100% no-repeat;
      filter: drop-shadow(0 0 8px #fff); }
    .p-tile .ico { position: absolute; inset: 0; display: grid; place-items: center; }
    .p-tile .card { width: 400px; margin-top: -20px; }
    .p-tile .pow { width: 400px; margin-top: -70px; }
    .p-tile .inst { width: 400px; filter: brightness(0); margin-top: -30px; }
    .p-tile .eq { width: 360px; filter: invert(1); margin-top: -20px; }
    .p-tile .rmx { position: absolute; width: 300px; left: 87px; top: 120px; }
    .p-tile .lab { position: absolute; left: 0; right: 0; bottom: 40px; text-align: center; font-weight: 800; font-size: 46px; letter-spacing: .1em; }
    .p-tile.with-lab .ico { bottom: 70px; }
    .p-tile .res { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; }
    .p-tile .res img { width: 330px; filter: brightness(0); margin-top: -40px; }
    .p-tile .res b { font-family: var(--o-din); font-size: 100px; line-height: 1; letter-spacing: .02em; margin-top: -10px; }
    .p-tile.perfect { background: var(--om-PartyRequest-perfectBackgroundColor); box-shadow: 0 0 60px 10px #fff8; }
    .p-tile.met.fresh, .p-tile.perfect.fresh { animation: p-pop .5s ease-out; }
    .p-tile.failed { background: var(--om-PartyRequest-failedBackgroundColor); }
    .p-tile.failed .res img { width: 300px; margin: 0; }
    .p-tile.bonus { box-shadow: 0 0 0 8px transparent, 0 0 40px 6px #fff6; }
    .p-tile.bonus::before { content: ""; position: absolute; inset: -10px; border-radius: 18px; padding: 8px; z-index: -1;
      background: linear-gradient(90deg, #50B048, #00BFF3, #EB0C6D, #FFF100); -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0); -webkit-mask-composite: xor; mask-composite: exclude; }
    .p-tile.enter { animation: p-enter .35s cubic-bezier(.2,.9,.3,1.2) both; }
    @keyframes p-enter { from { transform: translateY(-140px) scale(.85); opacity: 0; } }
    @keyframes p-pop { 0% { transform: scale(1.12); filter: brightness(1.6); } 100% { transform: scale(1); } }
    .p-msg h1 { margin: 0; font-weight: 700; letter-spacing: .07em; text-transform: uppercase; line-height: 1.05; }
    .p-foul { position: absolute; inset: 0; background: #000b; }
    .p-badge { position: absolute; left: 50%; bottom: 120px; transform: translateX(-50%); text-align: center; font-weight: 800; letter-spacing: .14em; font-size: 34px; }
    .p-badge img { display: block; margin: 0 auto 10px; width: 170px; filter: drop-shadow(0 0 18px #fff8); }
    .p-wheel { position: absolute; left: 50%; top: 150px; width: 560px; height: 560px; margin-left: -280px; border-radius: 50%;
      background: conic-gradient(#d9d9d9 0 25%, #1c1c1c 25% 50%, #9a9a9a 50% 75%, #2c2c2c 75% 100%); box-shadow: 0 0 0 10px #000, 0 0 0 16px #fff, 0 0 60px 20px #c06cff88; }
    .p-wheel b { position: absolute; left: 50%; top: -40px; margin-left: -26px; width: 0; height: 0; border-left: 26px solid transparent; border-right: 26px solid transparent; border-top: 44px solid #FFC800; }
    .p-wheel span { position: absolute; inset: 0; display: grid; place-items: center; font-family: var(--o-din); font-size: 200px; color: #fff; text-shadow: 0 0 30px #000; }
  </style>`);

  let lastTiles = '', tileSeen = new Set(), lastState = {}, menuHook = null;

  function partyLobby(state, m) {
    const players = (state.players || []).map(p => p.name);
    const label = k => { const i = k - 1; return k >= 1 && k <= 3 && players[i] ? `<div><img src="/ui/sys_player" style="height:120px;filter:brightness(0) invert(1)"><small>${esc(players[i]).toUpperCase()}</small></div>` : null; };
    const join = k => k >= 1 && k <= 3 && !players[k - 1];
    const best = m.highScore || 0;
    const url = (window.__joinUrl || '').replace(/^https?:\/\//, '').replace(/\/$/, '');
    setHtml(layer('p-base'), `<div class="o-dots"></div>
      <div class="o-topbar"><div class="l"><img data-a="mn-go" data-to="modes" src="/ui/sys_back" alt="back" style="filter:invert(0)"></div>${LU('Menu_PartySetup', 'PARTY')}<div class="r"><img data-a="scores" src="/ui/sys_leaderboard_icon" alt=""><span data-a="connect" class="cn-ic" title="Connect a phone" aria-label="Connect a phone">${WIFI_SVG}</span></div></div>
      <div class="o-center" style="justify-content:flex-start;top:195px"><div class="o-heavy" style="font-size:84px;letter-spacing:.09em">${players.length ? LU('IconSelect_AddIcon', 'PLACE A DECK TO JOIN') : 'JOIN ON YOUR PHONE'}</div></div>
      <div class="o-slots" style="top:375px">${slotsHtml(state, { label, join })}</div>
      <div class="o-center" style="justify-content:flex-start;top:735px">
        <div class="o-heavy o-click" data-a="start" style="font-size:80px;pointer-events:auto">${LU('Party_HitButton', 'BEGIN: <sprite name="dropmix">')}</div>
        <div class="o-heavy" style="font-size:32px;letter-spacing:.14em;margin-top:6px;line-height:1.45">${LU('IconSelect_CardsParty', '1-2 players: 30 each\n3 or more: 15 each')}</div>
        <div style="font-size:24px;letter-spacing:.14em;margin-top:8px;color:#999">${url ? `PHONES: JOIN AT ${esc(url).toUpperCase()}` : '&nbsp;'}</div></div>
      ${drawer('party', esc(m.difficulty || 'Normal').toUpperCase(), LU('PartySettings_HighScore', 'BEST: {0}', best.toLocaleString()), true,
        [{ a: 'p-diff', stops: ['EASY', 'NORMAL', 'HARD'], i: ['Easy', 'Normal', 'Hard'].indexOf(m.difficulty || 'Normal') }])}`);
  }

  function partyGame(state, m) {
    const base = layer('p-base');
    if (!base.querySelector('.o-crowd')) setHtml(base, `<div class="o-crowd"><div class="ph"></div><div class="tint"></div><div class="lights"></div><div class="shade"></div></div>`);
    const showPips = m.phase === 'play' || m.phase === 'roundIntro';
    const pips = (m.pips || []).map(p => `<i class="${p === 'bonus' ? (m.bonusPhase ? 'bonus live' : 'bonus') : p}"></i>`).join('');
    const results = m.phase === 'over';
    setHtml(layer('p-top'), `<div class="o-icon" data-a="pause" style="left:60px;top:45px"><img src="/ui/sys_pause" alt=""></div>
      ${showPips ? `<div class="o-pips">${pips}</div>` : ''}
      ${results ? `<div class="o-icon" data-a="scores" style="left:1617px;top:45px"><img src="/ui/sys_leaderboard_icon" alt=""></div>` : ''}
      <div class="o-icon mx-save" data-a="mix-save" style="left:1788px;top:45px"><img src="/ui/sys_mixes_add" alt=""></div>`);

    // slots: peeking at the bottom edge during play, raised between rounds
    const raised = m.phase === 'break';
    const slots = layer('p-slots'); setHtml(slots, `<div class="o-slots" style="top:${raised ? 675 : 1008}px">${slotsHtml(state)}</div>`);
    slots.firstChild && (slots.firstChild.style.top = `${raised ? 675 : 1008}px`);

    // request tiles (not during round breaks / intro / results)
    const tiles = layer('p-tiles', 'p-tiles');
    const live = ['play'].includes(m.phase) ? (m.tiles || []) : [];
    const html = live.map(t => {
      const fill = Math.max(0, Math.min(1, t.fill));
      let inner, cls = `p-tile ${t.state}${t.bonus ? ' bonus' : ''}`;
      if (t.state === 'active') {
        const ic = TILE_ICON(t);
        if (ic.label) cls += ' with-lab';
        const h = Math.round((1 - fill) * 591);
        inner = `<div class="drain" style="height:${h}px"></div><div class="line" style="top:${h}px"></div><div class="ico">${ic.img}</div>${ic.label ? `<div class="lab">${ic.label}</div>` : ''}`;
      } else if (t.state === 'failed') inner = `<div class="res"><img src="/ui/sys_close" alt=""></div>`;
      else if (t.state === 'dismissed') inner = `<div class="res"><img src="/ui/sys_check_mark" alt=""></div>`;
      else inner = `<div class="res"><img src="/ui/sys_check_mark" alt=""><b>${(t.points || 0).toLocaleString()}</b></div>`;
      const key = t.id + t.state;
      const fresh = !tileSeen.has(key); tileSeen.add(key);
      if (fresh && t.state === 'active') cls += ' enter'; else if (fresh) cls += ' fresh';
      return `<div class="${cls}" data-id="${t.id}">${inner}</div>`;
    });
    // update in place so the drain animates smoothly and entrance animations aren't replayed
    const ids = live.map(t => t.id + t.state).join(',');
    if (tiles.dataset.ids !== ids) { tiles.dataset.ids = ids; tiles.innerHTML = html.join(''); }
    else live.forEach((t, i) => {
      const el = tiles.children[i]; if (!el || t.state !== 'active') return;
      const h = Math.round((1 - Math.max(0, Math.min(1, t.fill))) * 591);
      const d = el.querySelector('.drain'), l = el.querySelector('.line'); if (d) d.style.height = h + 'px'; if (l) l.style.top = h + 'px';
    });

    // the EQ wheel
    const w = m.wheel || {};
    setHtml(layer('p-wheel'), w.state && w.state !== 'idle'
      ? `<div class="p-wheel" style="${w.state === 'spinning' ? 'animation:p-spin .5s linear infinite' : ''}"><b></b>${w.result ? `<span>${w.result === 'X' ? '✕' : esc(w.result)}</span>` : ''}</div><style>@keyframes p-spin{to{transform:rotate(360deg)}}</style>` : '');

    // full-screen moments
    let msg = '';
    if (m.phase === 'intro') msg = `<div class="o-center p-msg o-fade"><h1 style="font-size:150px">${LU('Party_Start', 'HERE WE GO!')}</h1><h1 style="font-size:84px">${LU('Party_DrawCards', 'TAKE {0} CARDS', m.handSize)}</h1>
        <h1 style="font-size:66px;margin-top:70px">${LU('Party_HighScoreToBeat', 'BEST SO FAR:')}</h1><h1 style="font-size:84px" class="din">${(m.highScore || 0).toLocaleString()}</h1></div>`;
    else if (m.phase === 'roundIntro') msg = `<div class="o-center p-msg o-fade"><h1 style="font-size:128px">${esc(m.roundLabel)}</h1></div>`;
    else if (m.phase === 'break') msg = `<div class="o-center p-msg" style="justify-content:flex-start;top:20px">
        <div style="font-family:var(--o-din);font-weight:700;font-size:150px;line-height:1">${m.score.toLocaleString()}</div>
        <h1 class="o-wipe" style="font-size:150px;margin-top:6px">${LU('Party_RoundComplete', '{0} DONE', m.nextLabel ? m.roundLabel : LT('Party_FinalRound', 'LAST ROUND'))}</h1>
        ${m.nextLabel ? `<h1 style="font-size:76px;color:#9a9a9a;margin-top:18px">${LU('Party_RefillCards', 'DRAW UP TO {0}', m.handSize)}</h1>` : ''}</div>`;
    else if (results) {
      const r = m.results || {};
      const name = r.name || window.__partyName || 'The Party';
      msg = `<div class="o-center p-msg o-fade" style="background:#0008">
        <div style="font-size:84px;font-weight:600;letter-spacing:.06em;pointer-events:auto">${r.rank > 0 ? `<input class="p-name" value="${esc(name)}" maxlength="30" spellcheck="false">` : esc(name)}${r.rank > 0 ? ' <span style="font-size:.6em">✎</span>' : ''}</div>
        <div style="font-size:52px;font-weight:600;letter-spacing:.12em;margin-top:30px">${LU('Party_TotalScore', 'SCORE')}</div>
        <div style="display:flex;align-items:flex-start;gap:30px"><span style="font-family:var(--o-din);font-size:84px;margin-top:70px">${r.rank > 0 ? `${r.rank}<sup style="font-size:.5em">${['th', 'st', 'nd', 'rd'][(r.rank % 100 > 10 && r.rank % 100 < 14) ? 0 : Math.min(r.rank % 10, 4) % 4] || 'th'}</sup>` : ''}</span>
        <span class="p-count" data-to="${m.score}" style="font-family:var(--o-din);font-weight:700;font-size:260px;line-height:1.1">${m.score.toLocaleString()}</span></div>
        ${r.newHigh ? `<div class="o-heavy" style="font-size:56px;color:#FFC800">${LU('Party_NewHighScore', 'A NEW BEST!')}</div>` : ''}
        <div class="o-heavy o-click" data-a="lobby" style="font-size:34px;letter-spacing:.14em;margin-top:60px;pointer-events:auto">${LU('Menu_TapToContinue', 'NEXT')}</div></div>`;
    }
    setHtml(layer('p-msg'), msg);
    // results: the score counts up once (PartyResultsScreen), and the name is editable
    const pc = stage.querySelector('.p-count');
    if (pc && !pc._done) {
      pc._done = true; const to = +pc.dataset.to, t0 = performance.now();
      const step = now => { const f = Math.min(1, (now - t0) / 1800); pc.textContent = Math.round(to * (1 - Math.pow(1 - f, 3))).toLocaleString(); if (f < 1) requestAnimationFrame(step); };
      requestAnimationFrame(step);
    }
    const pn = stage.querySelector('.p-name');
    if (pn && !pn._bound) {
      pn._bound = true;
      pn.onkeydown = e => { if (e.key === 'Enter') pn.blur(); };
      pn.onblur = () => host('rename', { value: pn.value });
    }

    // party foul + badges (bumpers)
    const f = m.foul;
    setHtml(layer('p-foul'), f ? `<div class="p-foul"></div><div class="o-center p-msg o-fade"><h1 style="font-size:76px">${LU('Party_PartyFoul', 'FOUL!')}</h1>
        <h1 style="font-size:150px;margin:6px 0">${esc(f.text).toUpperCase()}</h1><div style="font-family:var(--o-din);font-weight:700;font-size:150px;color:#f00;line-height:1">-${f.penalty}</div></div>` : '');
    const b = m.bumper;
    setHtml(layer('p-bump'), b ? `<div class="p-badge o-fade"><img src="/ui/badge_frame" alt="">${esc(b.text).toUpperCase()}</div>` : '');
  }

  // ======================================================================================
  // Freestyle (docs/ORIGINAL_UI.md "Freestyle")
  // ======================================================================================
  document.head.insertAdjacentHTML('beforeend', `<style>
    .f-ticker { position: absolute; left: 0; right: 0; top: 808px; text-align: center; font-size: 54px; letter-spacing: .14em; white-space: nowrap; }
    .f-ticker b { font-weight: 800; } .f-ticker span { font-weight: 400; }
    .f-ticker i { display: inline-block; width: 52px; height: 52px; vertical-align: -6px; margin-left: 18px; background: var(--ic);
      -webkit-mask: center / contain no-repeat; mask: center / contain no-repeat; }
    .f-bar { position: absolute; left: -3000px; right: -3000px; bottom: 0; padding: 0 3000px; background: #141414; border-top: 1px solid #ffffff14; transition: height .35s cubic-bezier(.2,.8,.2,1); overflow: hidden; }
    .f-row { position: absolute; left: 3000px; right: 3000px; display: grid; grid-template-columns: 1fr 120px 1fr; align-items: center; }
    .f-row > div { text-align: center; font-size: 54px; letter-spacing: .14em; }
    .f-chev { justify-self: center; width: 72px; height: 72px; border-radius: 50%; background: #fff; color: #000; display: grid; place-items: center; font-size: 46px; font-weight: 900; cursor: pointer; line-height: 1; }
    /* major/minor toggle: the white "change" icon right after the key name (25_studio); the sprite is already white */
    .f-mode { display: inline-block; width: 58px; height: 58px; margin-left: 20px; vertical-align: -10px; background: url('/ui/sys_change%20mode') center / contain no-repeat; cursor: pointer; }
    .f-slider { display: grid; grid-template-columns: 70px 1fr 70px; align-items: center; gap: 30px; padding: 0 30px; }
    .f-slider .arr { font-size: 70px; font-weight: 200; cursor: pointer; text-align: center; line-height: 1; }
    .f-track { position: relative; height: 6px; background: #4a4a4a; border-radius: 3px; cursor: pointer; }
    .f-track b { position: absolute; top: 50%; width: 6px; height: 54px; margin: -27px 0 0 -3px; background: #fff; border-radius: 3px; pointer-events: none; }
    .d-track { position: relative; height: 6px; margin: 0 30px; background: #4a4a4a; border-radius: 3px; top: -20px; }
    .d-track b { position: absolute; top: -30px; width: 120px; height: 100px; margin-left: -60px; cursor: pointer; text-align: center; }
    .d-track b i { position: absolute; left: 50%; top: 27px; width: 12px; height: 12px; margin-left: -6px; border-radius: 50%; background: #777; }
    .d-track b.on i { top: 6px; width: 6px; height: 54px; margin-left: -3px; border-radius: 3px; background: #fff; }
    .d-track b span { position: absolute; left: 0; right: 0; top: 74px; font-size: 34px; letter-spacing: .1em; color: #ccc; }
    .d-track b.on span { color: #fff; }
    .o-swipe { position: absolute; inset: -3000px; z-index: 80; pointer-events: none; overflow: hidden; }
    .o-swipe .band { position: absolute; left: 50%; top: 50%; width: 900px; height: 6000px; margin: -3000px 0 0 -450px; display: flex; transform: rotate(32deg) translateX(-4000px); animation: o-swipe 1.4s cubic-bezier(.5,0,.5,1) forwards; }
    .o-swipe .band i { flex: 1; }
    @keyframes o-swipe { to { transform: rotate(32deg) translateX(4000px); } }
    .o-swipe .ring { position: absolute; left: 50%; top: 50%; width: 260px; height: 260px; margin: -130px 0 0 -130px; border-radius: 50%; background: #fff; display: grid; place-items: center;
      box-shadow: 0 0 0 14px #000, 0 0 0 22px #fff; opacity: 0; animation: o-ring 1.4s ease forwards; }
    .o-swipe .ring i { width: 170px; height: 170px; background: #000; -webkit-mask: center / contain no-repeat; mask: center / contain no-repeat; }
    @keyframes o-ring { 30%, 70% { opacity: 1; transform: scale(1); } 0% { opacity: 0; transform: scale(.6); } 100% { opacity: 0; transform: scale(1.3); } }
    .f-time { position: absolute; left: 3063px; right: 3063px; height: 40px; display: flex; justify-content: space-between; align-items: center; }
    .f-time i { width: 6px; height: 6px; border-radius: 50%; background: #666; }
    .f-time i.tall { width: 4px; height: 26px; border-radius: 2px; background: #888; }
    .f-time i.on { background: #fff; box-shadow: 0 0 12px #fff; width: 12px; height: 12px; }
    .f-time i.tall.on { width: 6px; height: 34px; }
    .o-slot .vol { position: absolute; inset: 0; pointer-events: none; }
    .o-slot .vol .shade { position: absolute; left: 0; right: 0; top: 0; background: #000a; border-radius: 10px 10px 0 0; }
    .o-slot .vol .tint { position: absolute; left: 0; right: 0; bottom: 0; mix-blend-mode: screen; opacity: .45; background: var(--c); border-radius: 0 0 10px 10px; }
    .o-slot .vol .ln { position: absolute; left: -8px; right: -8px; height: 6px; margin-top: -3px; background: var(--c); box-shadow: 0 0 18px 4px var(--c); border-radius: 3px; }
    .o-slot.dragv { cursor: ns-resize; touch-action: none; }
    .f-info { position: absolute; left: 300px; top: 190px; width: 1320px; display: flex; gap: 60px; padding: 50px; box-sizing: border-box; background: #1c1c1c; border: 4px solid #fff; border-radius: 10px; cursor: pointer; }
    .f-info .art { width: 520px; height: 520px; object-fit: cover; border-radius: 12px; }
    .f-info .tx { display: flex; flex-direction: column; gap: 22px; min-width: 0; }
    .f-info b { font-size: 64px; font-weight: 800; letter-spacing: .06em; line-height: 1.1; } .f-info span { font-size: 40px; letter-spacing: .1em; color: #bbb; }
    .f-info .row { display: flex; align-items: center; gap: 16px; font-size: 36px; letter-spacing: .12em; } .f-info .row img { height: 60px; }
    .f-info .row i { width: 60px; height: 60px; background: #fff; -webkit-mask: center / contain no-repeat; mask: center / contain no-repeat; }
    .f-info .fxr { font-size: 34px; text-transform: uppercase; } .f-info .sm { font-size: 24px; color: #888; }
  </style>`);

  let tickerCard = null, lastTops = null, volDrag = null, fInfo = null;

  // ---- setup option drawer (GameModeSettingsControls): a bottom bar with the ︽ chevron; open, it shows
  // stepped sliders (‹ track with labelled stops ›) under each label. Locked = the lock icon instead. ----
  const drawerOpen = {};
  function drawer(id, left, right, unlocked, sliders) {
    const open = unlocked && drawerOpen[id];
    const chev = unlocked ? `<div class="f-chev" data-a="drawer" data-id="${id}">${open ? '︾' : '︽'}</div>`
                          : `<div class="f-chev" style="cursor:default"><img src="/ui/sys_lock" style="height:44px"></div>`;
    const sl = sl => sl ? `<div class="f-slider"><span class="arr" data-a="${sl.a}" data-v="${Math.max(0, sl.i - 1)}">‹</span>
        <div class="d-track">${sl.stops.map((t, k) => `<b class="${k === sl.i ? 'on' : ''}" data-a="${sl.a}" data-v="${k}" style="left:${sl.stops.length < 2 ? 50 : 4 + k / (sl.stops.length - 1) * 92}%"><i></i><span>${t}</span></b>`).join('')}</div>
        <span class="arr" data-a="${sl.a}" data-v="${Math.min(sl.stops.length - 1, sl.i + 1)}">›</span></div>` : '<div></div>';
    return `<div class="f-bar" style="height:${open ? 300 : 123}px;bottom:0"><div class="f-row" style="top:0;height:123px"><div>${left}</div>${chev}<div>${right}</div></div>
      ${open ? `<div class="f-row" style="top:118px;height:150px">${sl(sliders[0])}<div></div>${sl(sliders[1])}</div>` : ''}</div>`;
  }

  // ---- game start: the diagonal yellow/magenta/blue/green stripe wipe with the mode's icon (p012) ----
  let wipeSeen = {};
  function stripeWipe(icon) {
    const el = document.createElement('div'); el.className = 'o-swipe';
    el.innerHTML = `<div class="band"><i style="background:#FFF100"></i><i style="background:#EB0C6D"></i><i style="background:#00BFF3"></i><i style="background:#50B048"></i></div>
      <div class="ring"><i style="-webkit-mask-image:url('/ui/${icon}');mask-image:url('/ui/${icon}')"></i></div>`;
    requestAnimationFrame(() => stage.appendChild(el)); setTimeout(() => el.remove(), 1600);   // after the screen switch clears the stage
  }
  function wipeOnStart(m, icon) {
    const key = m.id, prev = wipeSeen[key];
    wipeSeen[key] = m.phase;
    if (prev === 'lobby' && m.phase !== 'lobby') stripeWipe(icon);
  }
  const INSTR = ['synth', 'vocal', 'guitar', 'drums', 'horns', 'strings', 'sampler'];
  const KEYS = ['C', 'D♭', 'D', 'E♭', 'E', 'F', 'G♭', 'G', 'A♭', 'A', 'B♭', 'B'];
  const circlePos = key => { const ki = KEYS.indexOf(key); return ki < 0 ? 0 : (ki + (ki % 2 === 1 ? 6 : 0)) % 12; };

  function freestyle(state, m) {
    const mix = state.mix || {}, open = !!mix.studioOpen, vols = mix.volumes || [];
    // the ticker names the card most recently added to the mix
    const tops = state.pads.map(p => p.top?.cid || null);
    if (lastTops) tops.forEach((cid, i) => { if (cid && cid !== lastTops[i]) tickerCard = state.pads[i].top; });
    if (!tickerCard || !tops.includes(tickerCard.cid)) tickerCard = state.pads.map(p => p.top).filter(Boolean).pop() || null;
    lastTops = tops;

    setHtml(layer('f-base'), `<div class="o-dots"></div>
      <div class="o-topbar"><div class="l"><span data-a="menu" style="font-size:64px;cursor:pointer;line-height:1">︿</span></div>FREESTYLE<div class="r"><div class="o-icon" data-a="connect" style="position:static;color:#000" title="Connect a phone">${WIFI_SVG}</div><div class="o-icon mx-save" data-a="mix-save" style="position:static"><img src="/ui/sys_mixes_add" alt=""></div></div></div>`);

    // slots; with the studio panel open, a volume line across each card (drag the card up/down)
    const slots = layer('f-slots');
    if (!volDrag) {
      setHtml(slots, `<div class="o-slots" style="top:387px">${slotsHtml(state)}</div>`);
      slots.querySelectorAll('.o-slot').forEach((el, k) => {
        const c = state.pads[k].top;
        el.classList.toggle('dragv', open && !!c);
        // setHtml leaves unchanged slots in place, so drop any overlay from the last redraw (they stacked every
        // 400 ms; with a Wild's white tint the card turned solid white)
        el.querySelectorAll('.vol').forEach(v => v.remove());
        if (!open || !c) {
          el.onpointerdown = el.onpointermove = el.onpointerup = el.onpointercancel = null;
          el.onclick = c ? () => { fInfo = { card: c, slot: k }; uiSfx('card_inspect'); window.OrigUI.render(lastState); } : null;   // card info on tap
          return;
        }
        el.onclick = null;
        const lv = vols[k] ?? 0.683, y = Math.round((1 - lv) * 333);
        el.insertAdjacentHTML('beforeend', `<div class="vol"><div class="shade" style="height:${y}px"></div><div class="tint" style="top:${y}px"></div><div class="ln" style="top:${y}px"></div></div>`);
        el.onpointerdown = e => { e.preventDefault(); volDrag = { k, y0: e.clientY, l0: lv }; el.setPointerCapture(e.pointerId); };   // no native image drag
        el.onpointermove = e => {
          if (!volDrag || volDrag.k !== k) return;
          const scale = stage.getBoundingClientRect().height / 1080;
          const lvl = Math.max(0, Math.min(1, volDrag.l0 - (e.clientY - volDrag.y0) / scale / 300));   // level += dy/300 (SlotVolumeControl)
          const yy = Math.round((1 - lvl) * 333);
          el.querySelector('.ln').style.top = yy + 'px'; el.querySelector('.shade').style.height = yy + 'px'; el.querySelector('.tint').style.top = yy + 'px';
          clearTimeout(volDrag.t); volDrag.t = setTimeout(() => post('/api/studio', { action: 'volume', slot: k, value: lvl }), 60);
        };
        el.onpointerup = el.onpointercancel = () => { volDrag = null; };
      });
    }

    // card info (tap a card): art, song, artist, colour/level, key, tempo, instruments, FX rule
    const fi = fInfo && state.pads[fInfo.slot]?.top?.cid === fInfo.card.cid ? fInfo.card : null;
    if (!fi) fInfo = null;
    const KEYN = { C: 'C', Cs: 'D♭', D: 'D', Ds: 'E♭', E: 'E', F: 'F', Fs: 'G♭', G: 'G', Gs: 'A♭', A: 'A', As: 'B♭', B: 'B' };
    setHtml(layer('f-info'), fi ? `<div class="pz" style="z-index:60"><div class="dim" data-a="f-info-x"></div><div class="f-info" data-a="f-info-x">
        <img class="art" src="/art/${encodeURIComponent(fi.image)}" alt=""><div class="tx"><b>${esc(fi.name).toUpperCase()}</b><span>${esc(fi.artist).toUpperCase()}</span>
        <div class="row"><img src="/ui/${CARD_SPR[fi.type] || 'sys_wild_card'}" alt=""> ${esc(fi.type === 'Fx' ? 'FX' : fi.type).toUpperCase()} · LEVEL ${fi.power || '—'}</div>
        ${fi.key && fi.key !== 'None' ? `<div class="row">${KEYN[fi.key] || fi.key} ${fi.mode === 'Minor' ? 'MINOR' : 'MAJOR'} · ${Math.round(fi.bpm || 0)} BPM</div>` : ''}
        <div class="row">${(fi.instruments || []).filter((x, i, a) => a.indexOf(x) === i).map(x => `<i style="-webkit-mask-image:url('/ui/instrument_${String(x).toLowerCase()}');mask-image:url('/ui/instrument_${String(x).toLowerCase()}')"></i>`).join('')}</div>
        ${fi.fx ? `<div class="row fxr">${fxTags(fi.fx)}</div>` : ''}
        ${(fi.genres || []).length || fi.year ? `<div class="row sm" style="font-size:28px;color:#bbb">${(fi.genres || []).map(g => esc(g).toUpperCase()).join(' · ')}${fi.year ? ` · ${esc(fi.year)}` : ''}</div>` : ''}
        <div class="row sm">${esc(fi.print || '')} ${esc(fi.num || '')}${fi.illustrator ? ` · ART: ${esc(fi.illustrator).toUpperCase()}` : ''}</div></div></div></div>` : '');

    const t = tickerCard, tk = layer('f-ticker', 'f-ticker');
    const ins = t ? (t.instruments || []).map(x => String(x).toLowerCase()).find(x => INSTR.includes(x)) : null;
    setHtml(tk, t ? `<b>${esc(t.artist || t.name).toUpperCase()}</b><span> — ${esc(t.name).toUpperCase()}</span>${ins ? `<i style="--ic:${COL[t.type] || '#fff'};-webkit-mask-image:url('/ui/instrument_${ins}');mask-image:url('/ui/instrument_${ins}')"></i>` : ''}` : '');
    tk.style.display = open ? 'none' : '';

    const bpm = Math.round(mix.tempo || 0), key = mix.key ? `${mix.key.replace('♭', 'b')} ${mix.minor ? 'MINOR' : 'MAJOR'}` : '—';
    const lo = mix.minTempo, hi = mix.maxTempo;
    const tempoPos = hi > lo ? Math.max(0, Math.min(1, (bpm - lo) / (hi - lo))) : 0;
    const bar = mix.bar ?? -1;
    const bar32 = Array.from({ length: TB.loopBars || 0 }, (_, i) => `<i class="${i % 4 === 0 ? 'tall' : ''}"></i>`).join('');
    const barEl = layer('f-bar', 'f-bar');
    barEl.style.height = open ? '300px' : '123px';
    setHtml(barEl, `<div class="f-row" style="top:0;height:123px"><div>${bpm ? bpm + ' BPM' : '—'}</div>
        <div class="f-chev" data-a="studio">${open ? '︾' : '︽'}</div><div>${key}${open ? '<span class="f-mode" data-a="kmode"></span>' : ''}</div></div>
      <div class="f-row" style="top:118px;height:90px">
        <div class="f-slider"><span class="arr" data-a="t-">‹</span><div class="f-track" data-a="tset"><b style="left:${(tempoPos * 100).toFixed(1)}%"></b></div><span class="arr" data-a="t+">›</span></div><div></div>
        <div class="f-slider"><span class="arr" data-a="k-">‹</span><div class="f-track" data-a="kset"><b style="left:${(circlePos(mix.key) / 11 * 100).toFixed(1)}%"></b></div><span class="arr" data-a="k+">›</span></div></div>
      <div class="f-time" style="top:210px">${bar32}</div>`);
  }

  // ======================================================================================
  // Clash (recorded on the device: docs/ORIGINAL_UI.md "Clash")
  // ======================================================================================
  const TEAM_ICON = { Red: 'fever', Blue: 'ouroboros' };      // team deck icons: refreshed from Icon Select each render
  const SLOT_COL = [['#FFF100', '#EB0C6D'], ['#EB0C6D'], ['#EB0C6D', '#00BFF3'], ['#00BFF3'], ['#00BFF3', '#50B048']];
  const CARD_SPR = { Bass: 'sys_green_card', Beat: 'sys_blue%20card', Loop: 'sys_red_card', Lead: 'sys_yellow_card', Wild: 'sys_wild_card', Fx: 'sys_fx_card' };
  document.head.insertAdjacentHTML('beforeend', `<style>
    .c-slot.wt .c-bars i { background: #ff2a2a !important; opacity: var(--wa, 1); }
    .c-foul .art { opacity: .45; filter: grayscale(.6); }
    .c-foul .bad { position: absolute; inset: 0; border-radius: 10px; background: url('/ui/sys_blocked_icon') center / 58% no-repeat; box-shadow: 0 0 0 6px #ff2a2a, 0 0 30px 6px #ff2a2a; animation: c-foul .5s steps(2) infinite; }
    @keyframes c-foul { 50% { box-shadow: 0 0 0 6px #ff2a2a33, 0 0 0 0 #0000; } }
    .c-scrim { position: absolute; top: 0; bottom: 0; background: #000d; z-index: 20; }
    .c-errtx { position: absolute; top: 400px; width: 760px; transform: translateX(-50%); z-index: 21; text-align: center; display: flex; flex-direction: column; gap: 34px; }
    .c-errtx b { font-size: 76px; font-weight: 800; letter-spacing: .1em; color: #ff3b3b; line-height: 1.15; }
    .c-errtx span { font-size: 46px; letter-spacing: .16em; }
    .c-circle { position: absolute; width: 524px; height: 524px; margin: -262px 0 0 -262px; border-radius: 50%; display: grid; place-items: center; transition: background .3s, box-shadow .3s; }
    .c-circle svg { position: absolute; inset: -14px; width: 552px; height: 552px; transform: rotate(-90deg); }
    .c-circle .n { font-family: var(--o-din); font-weight: 700; font-size: 300px; line-height: 1; letter-spacing: -.02em; }
    .c-circle.off { background: #1c1c1e; box-shadow: inset 0 0 0 4px #ffffff10; }
    .c-circle.off .n { color: #111; -webkit-text-stroke: 7px #fff; text-shadow: 0 0 22px #fff8; }
    .c-circle.on { background: radial-gradient(#fff 60%, #e9e9e9 100%); box-shadow: 0 0 0 6px #fff, 0 0 70px 18px #fff9; }
    .c-circle.on .n { color: #555; }
    .c-glow { position: absolute; width: 900px; height: 900px; margin: -450px 0 0 -450px; border-radius: 50%; filter: blur(60px); opacity: .55;
      background: conic-gradient(from 0deg, #FFF100, #EB0C6D, #7665B0, #00BFF3, #50B048, #FFF100); animation: c-rot 9s linear infinite; pointer-events: none; }
    @keyframes c-rot { to { transform: rotate(360deg); } }
    .c-col { position: absolute; left: 873px; top: 45px; width: 177px; }
    .c-slot { position: absolute; left: 0; width: 177px; height: 177px; }
    .c-slot .frame { position: absolute; inset: 0; width: 100%; height: 100%; transform: scaleX(-1); filter: drop-shadow(0 0 8px var(--g1)) drop-shadow(0 0 8px var(--g2)); }
    .c-slot .art { position: absolute; inset: 0; border-radius: 6px; overflow: hidden; } .c-slot .art img { width: 100%; height: 100%; object-fit: cover; }
    .c-slot .x { position: absolute; inset: -10px; background: url('/ui/sys_remove_card_icon') center / 90% no-repeat; }
    .c-bars { position: absolute; top: 14px; width: 150px; height: 150px; display: flex; flex-direction: column; gap: 9px; }
    .c-bars.r { left: calc(100% + 8px); } .c-bars.l { right: calc(100% + 8px); align-items: flex-end; }
    .c-bars i { height: 42px; background: #ffffff1c; }
    .c-bars i:nth-child(1) { width: 100%; } .c-bars i:nth-child(2) { width: 80%; } .c-bars i:nth-child(3) { width: 60%; }
    .c-bars.r i { clip-path: polygon(0 0, 100% 0, calc(100% - 42px) 100%, 0 100%); } .c-bars.l i { clip-path: polygon(0 0, 100% 0, 100% 100%, 42px 100%); }
    .c-pips { position: absolute; width: 300px; margin-left: -150px; top: 760px; display: flex; justify-content: center; gap: 30px; }
    .c-pips div { width: 120px; display: flex; flex-direction: column; align-items: center; gap: 12px; }
    .c-pips b { width: 120px; height: 14px; border-radius: 7px; background: #fff; } .c-pips b.used { background: #ffffff40; }
    .c-pips img { height: 64px; }
    .c-team { position: absolute; width: 130px; height: 130px; margin: -65px 0 0 -65px; background: #fff; -webkit-mask: center / contain no-repeat; mask: center / contain no-repeat; }
    .c-hand { position: absolute; margin-left: -50px; width: 100px; top: 740px; height: 110px; display: grid; place-items: center; font-family: var(--o-din); font-weight: 700; font-size: 64px; color: #000;
      background: url('/ui/icon_cardcount') center / contain no-repeat; filter: invert(1); }
    /* SlotScoreIndicator: "+N" over the slot (text/background colours imported: slot/bounty, FX, mix fill),
       with its score-burst particles; CardEnterBurst: the card's colours flaring out of the slot (centre/left/right) */
    .c-pop { position: absolute; width: 200px; height: 200px; margin: -100px 0 0 -100px; border-radius: 24px; background: #000c; color: var(--om-SlotScoreIndicator-slotScoreColor);
      display: grid; place-items: center; animation: c-pop 1.5s cubic-bezier(.2,.8,.2,1) forwards; pointer-events: none; z-index: 6; opacity: 0; }
    .c-pop .ring { position: absolute; inset: 18px; border-radius: 50%; border: 7px solid currentColor; }
    .c-pop b { position: relative; font-family: var(--o-din); font-weight: 700; font-size: 104px; letter-spacing: -.04em; line-height: 1; }
    .c-pop.fx { background: var(--om-SlotScoreIndicator-fxScoreBackgroundColor); color: var(--om-SlotScoreIndicator-fxScoreColor); } .c-pop.mix { color: var(--om-SlotScoreIndicator-mixFillScoreColor); } .c-pop.neg { color: #f33; }
    .c-pop .burst { position: absolute; left: 50%; top: 50%; width: 460px; height: 460px; margin: -230px 0 0 -230px; z-index: -1;
      background: currentColor; -webkit-mask: url('/ui/glow_burst') center / contain no-repeat; mask: url('/ui/glow_burst') center / contain no-repeat; animation: c-burst .8s ease-out forwards; }
    @keyframes c-pop { 0% { opacity: 0; transform: scale(.3); } 12% { opacity: 1; transform: scale(1.12); } 22% { transform: scale(1); } 78% { opacity: 1; } 100% { opacity: 0; transform: scale(.96); } }
    @keyframes c-burst { 0% { opacity: .9; transform: scale(.25); } 100% { opacity: 0; transform: scale(1.5); } }
    .c-enter { position: absolute; pointer-events: none; mix-blend-mode: screen; z-index: 4; }
    .c-enter .core { position: absolute; left: -330px; top: -330px; width: 660px; height: 660px; background: var(--c1);
      -webkit-mask: url('/ui/glow_burst') center / contain no-repeat; mask: url('/ui/glow_burst') center / contain no-repeat; animation: c-flare 1.1s ease-out forwards; }
    .c-enter .band { position: absolute; left: -1100px; width: 2200px; top: -110px; height: 220px; filter: blur(40px);
      background: linear-gradient(90deg, transparent, var(--c2) 30%, var(--c1) 50%, var(--c2) 70%, transparent); animation: c-band 1.1s ease-out forwards; }
    .c-enter .col { position: absolute; top: -700px; height: 1400px; left: -120px; width: 240px; filter: blur(50px);
      background: linear-gradient(180deg, transparent, var(--c1) 50%, transparent); animation: c-band 1.1s ease-out forwards; }
    @keyframes c-flare { 0% { opacity: 1; transform: scale(.3); } 25% { opacity: 1; } 100% { opacity: 0; transform: scale(1.3); } }
    @keyframes c-band { 0% { opacity: 0; transform: scaleX(.3); } 15% { opacity: .95; } 100% { opacity: 0; transform: scaleX(1.1); } }
    .c-wheel { position: absolute; width: 560px; height: 560px; margin: -280px 0 0 -280px; border-radius: 50%; overflow: visible; }
    .c-wheel .pie { position: absolute; inset: 0; border-radius: 50%; box-shadow: 0 0 0 12px #000, 0 0 0 20px transparent; }
    .c-wheel .ring { position: absolute; inset: -26px; border-radius: 50%; background: conic-gradient(#FFF100, #EB0C6D, #7665B0, #00BFF3, #50B048, #FFF100);
      -webkit-mask: radial-gradient(closest-side, transparent 92%, #000 93%); mask: radial-gradient(closest-side, transparent 92%, #000 93%); filter: drop-shadow(0 0 20px #fff8); }
    .c-wheel .ptr { position: absolute; left: 50%; top: -46px; margin-left: -24px; border-left: 24px solid transparent; border-right: 24px solid transparent; border-top: 40px solid #FFC800; filter: drop-shadow(0 0 6px #000); }
    .c-wheel .glyph { position: absolute; width: 110px; height: 110px; margin: -55px 0 0 -55px; background: center / contain no-repeat; }
    .c-cancel { position: absolute; font-size: 40px; font-weight: 700; letter-spacing: .12em; }
    .c-fx { position: absolute; left: 1100px; right: 0; top: 0; bottom: 0; background: #111e; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 30px; text-align: center; padding: 0 60px; }
    .c-fx h2 { margin: 0; font-size: 92px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
    .c-fx .pics { display: flex; align-items: center; gap: 20px; } .c-fx .pics .pp { height: 90px; } .c-fx .pics .pinst { filter: invert(1); }
    .c-fx .pics .pnum { position: relative; padding-left: 34px; } .c-fx .pics .pnum .sign { position: absolute; left: 0; top: 50%; height: 56px; transform: translateY(-50%); background: #111; border-radius: 50%; }
    .c-fx .pics .pdot { width: 18px; height: 18px; border-radius: 50%; background: #fff; }
    .c-fx p { margin: 0; font-size: 50px; font-weight: 700; letter-spacing: .1em; text-transform: uppercase; line-height: 1.25; }
    .c-win { position: absolute; inset: 0; }
    .c-win .sparks { position: absolute; inset: 0; background-image: radial-gradient(#fff 1px, transparent 1.5px), radial-gradient(#f5c542 1px, transparent 1.5px);
      background-size: 37px 41px, 53px 47px; opacity: .35; animation: c-twinkle 3s linear infinite; }
    @keyframes c-twinkle { to { background-position: 0 40px, 30px -50px; } }
    .c-win .row { position: absolute; left: 63px; right: 63px; bottom: 50px; display: grid; grid-template-columns: auto 1fr auto; align-items: center; gap: 40px; font-family: var(--o-din); font-weight: 700; font-size: 100px; }
    .c-win .row .mid { display: flex; align-items: center; gap: 30px; } .c-win .row .mid i { flex: 1; height: 3px; background: #fff6; }
    .c-win .side { display: flex; align-items: center; gap: 30px; }
    .c-coin { position: absolute; left: 50%; top: 520px; width: 520px; height: 520px; margin: -260px 0 0 -260px; }
  </style>`);

  // ---- WheelOfMisfortune, rebuilt from the original sprites and transforms (assets_raw/obb_ui/wheel_*) ----
  // Units: wheel local frame 623.3 px/unit (root 0.653 × 6.364 × 150 px); coin 783.6 px/unit; spinner 97.95 px/unit.
  // Disc rotation = −(pos·360 + 90)°; slice i covers pos ∈ [cumweight(i−1)/total, cumweight(i)/total) (imported slice weights).
  // Spin-up −10 rev/s until the next bar (+4 beats rules), spin-down v0 = −4 rev/s decaying v' = −0.9999·BPM/128·v for
  // exactly 8 beats, no overshoot. Slice tints (WheelOfMisfortune colours, imported): slice normal / highlight
  // while spinning / selected when stopped; glyphs volume dimmed (untargeted) / normal (targeted) / highlight (current,
  // spinning) / pulsing normal-to-selected when stopped (bonus colours on the bonus slice) at half-beat period.
  document.head.insertAdjacentHTML('beforeend', `<style>
    .wh { position: absolute; width: 0; height: 0; z-index: 8; pointer-events: none; }
    .wh .s { position: absolute; transform-origin: 50% 50%; }
    .wh .m { -webkit-mask-repeat: no-repeat; mask-repeat: no-repeat; -webkit-mask-size: 100% 100%; mask-size: 100% 100%; }
    .wh .glow { -webkit-mask-mode: luminance; mask-mode: luminance; mix-blend-mode: plus-lighter; opacity: .5;
      background: url('/ui/wheel_rainbow_blurred') center / 100% 100%; }
    .wh .echo { mix-blend-mode: plus-lighter; }
    .wh-scrim { position: absolute; inset: -3000px; pointer-events: none; z-index: 7; }
    .wh-dim { position: absolute; width: 177px; height: 177px; border-radius: 6px; background: #000000a8; z-index: 7; pointer-events: none; }
    .wh-cancel { position: absolute; z-index: 9; transform: translateX(-50%); display: flex; align-items: center; gap: 14px;
      font-weight: 600; font-size: 50px; letter-spacing: .1em; white-space: nowrap; }
    .wh-cancel img { height: 64px; }
    .wh-first { position: absolute; left: 0; right: 0; text-align: center; font-size: 56px; letter-spacing: .2em; z-index: 9; }
  </style>`);
  const frac = x => x - Math.floor(x);
  const hashRand = id => frac(Math.sin(Number(id) * 12.9898 + 78.233) * 43758.5453);
  const sliceAt = pos => { const f = frac(pos); const i = SLICE_EDGES.findIndex((e, k) => k > 0 && f < e); return i > 0 ? i - 1 : SLICE_EDGES.length - 2; };
  // sprite helper: centre (x, y) px in the parent frame (y down), box w×h, rotation (css deg), tint (mask) or plain image
  const spr = (img, x, y, w, h, rot, tint, extra = '') => tint
    ? `<div class="s m ${extra}" style="left:${x - w / 2}px;top:${y - h / 2}px;width:${w}px;height:${h}px;transform:rotate(${rot}deg);background:${tint};-webkit-mask-image:url('/ui/${img}');mask-image:url('/ui/${img}')"></div>`
    : `<img class="s ${extra}" src="/ui/${img}" alt="" style="left:${x - w / 2}px;top:${y - h / 2}px;width:${w}px;height:${h}px;transform:rotate(${rot}deg)">`;
  const appearScale = u => { u = Math.max(0, Math.min(1, u)); return -1.8272 * u ** 3 + 2.6545 * u ** 2 + 0.1727 * u; };
  const disappearScale = u => { u = Math.max(0, Math.min(1, u)); return 2.1727 * u ** 3 - 3.1727 * u ** 2 + 1; };

  let WH = null;                                   // the live wheel: { id, el, t0, ... }
  function wheelPos(W, tNow) {
    const t = (tNow - W.t0) / 1000, up = W.up, k = Math.max(1e-6, 0.9999 * W.bpm / 128);   // the original's decay constants (WheelOfMisfortune.Update)
    const travelDown = 4 * (1 - Math.exp(-k * W.down)) / k;
    const p0 = W.target + 10 * up + travelDown;                                     // so the stop lands on target
    if (t <= 0) return p0;
    if (t < up) return p0 - 10 * t;
    const tau = Math.min(t - up, W.down);
    return p0 - 10 * up - 4 * (1 - Math.exp(-k * tau)) / k;
  }
  function buildWheel(W) {
    const U = 623.3, g = '#ffffff';
    const slices = [['wheel_slice_x_bg', 0.2288, -0.11, 512, 256, 0.101], ['wheel_slice_1_bg', 0.25, -0.25, 512, 512, 0.101],
                    ['wheel_slice_2_bg', -0.25, -0.25, 512, 512, 0.1], ['wheel_slice_3_bg', 0, 0.25, 1024, 512, 0.1]]
      .map(([f, x, y, w, h, sc], i) => spr(f, x * U, -y * U, w / 100 * sc * U, h / 100 * sc * U, 0, rgb(wcol('sliceNormalColor')), 'bg' + i)).join('');
    const glyphs = [['wheel_num_1', 0.152, -0.207, -7.5, 1], ['wheel_num_2', -0.19, -0.19, 0, 2], ['wheel_num_3', 0, 0.23, 0, 3]]
      .map(([f, x, y, r, i]) => spr(f, x * U, -y * U, 191.5, 191.5, r, rgb(wcol('volumeDimmedColor')), 'gl' + i)).join('')
      + spr(W.bonus ? 'wheel_bonus_plus1' : 'wheel_bomb_x', 0.288 * U, 0.075 * U, 86.8, 86.8, 105, rgb(wcol('volumeDimmedColor')), 'gl0');
    const glow = `<div class="s m glow" style="left:-378.4px;top:-378.4px;width:756.8px;height:756.8px;transform:rotate(-90deg);-webkit-mask-image:url('/ui/wheel_glow_circle');mask-image:url('/ui/wheel_glow_circle')"></div>`;
    W.el.innerHTML = `${spr('wheel_slot_glow', 0, 0, 1167, 1185, 0)}<div class="echoes"></div>
      <div class="s spin" style="left:0;top:0;width:0;height:0">${glow}${slices}${spr('wheel_stroke', 0, 0, 625.5, 625.5, 0, null, 'stroke')}${glyphs}</div>
      ${spr('wheel_pointer_yellow', 0, -263.5, 196, 196, 0)}`;
    W.el.querySelector('.stroke').style.filter = 'brightness(1.65)';
    W.spin = W.el.querySelector('.spin');
    W.bg = [0, 1, 2, 3].map(i => W.el.querySelector('.bg' + i)); W.gl = [0, 1, 2, 3].map(i => W.el.querySelector('.gl' + i));
  }
  function buildCoin(W) {
    const C = 783.6, S = 97.95, half = 1024 / 85.3333 * 0.08 * C;               // 752 px sprite box
    const icon = side => `wheel_coin_icon_${TEAM_ICON[side]}`;
    W.el.innerHTML = `${spr('wheel_slot_glow', 0, 0, 1167, 1185, 0)}<div class="echoes"></div>
      <div class="s spin" style="left:0;top:0;width:0;height:0">
        <div class="s" style="left:0;top:0;width:0;height:0;transform:rotate(-90deg)">
          <div class="s m glow" style="left:-477.9px;top:-477.9px;width:955.8px;height:955.8px;-webkit-mask-image:url('/ui/wheel_glow_circle');mask-image:url('/ui/wheel_glow_circle')"></div>
          ${spr('wheel_coin_half_1', 0, 0, half, half, 0, rgb(wcol('sliceNormalColor')), 'bg0')}${spr('wheel_coin_half_2', 0, 0, half, half, 0, rgb(wcol('sliceNormalColor')), 'bg1')}
          ${spr('wheel_coin_stroke', 0, 0, half, half, 0, '#000')}</div>
        ${spr(icon('Blue'), 0, -1.62 * S, 188, 188, 0)}${spr(icon('Red'), 0, 1.62 * S, 188, 188, 180)}</div>
      ${spr('wheel_pointer_yellow', 0, -3.53 * S, 196, 196, 0)}<div class="wh-first" style="top:-${427 + 40}px;left:-400px;right:-400px">${LU('HeadToHead_FirstUp', 'STARTING:')}</div>`;
    W.spin = W.el.querySelector('.spin'); W.bg = [W.el.querySelector('.bg0'), W.el.querySelector('.bg1')];
  }
  function echo(W) {                                                               // one ring per slice change
    const big = W.coin, d = document.createElement('div'), sz = big ? 6 * 97.95 * 1.6 : 741;
    d.innerHTML = spr('wheel_echo_ring', 0, 0, sz, sz, 20, null, 'echo');
    const img = d.firstChild; W.el.querySelector('.echoes').appendChild(img);
    const t0 = performance.now(), life = big ? 1000 : 600;
    const step = now => {
      const u = (now - t0) / life; if (u >= 1) { img.remove(); return; }
      img.style.transform = `rotate(${20 + 90 * (now - t0) / 1000}deg) scale(${1.008 + 0.72 * u})`;
      img.style.opacity = u < 0.39 ? 1 : 1 - (u - 0.39) / 0.61;
      requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
  }
  function wheelFrame(now) {
    const W = WH; if (!W) return;
    let scale;
    if (W.closing) {
      scale = disappearScale((now - W.closing) / 500);
      if (now - W.closing >= 500) { W.el.remove(); W.extra?.remove(); WH = null; return; }
    } else scale = appearScale((now - W.shown) / 500);
    W.el.style.transform = `scale(${scale})`;
    if (W.coin) {
      const CC = TB.coinCurve || [], n = CC.length;                              // imported coin-flip curve
      const beats = (now - W.t0) / 1000 * W.bpm / 60, p = Math.min(1, Math.max(0, beats / 8)), f = p * n;
      const i = Math.min(n - 1, Math.floor(f)), j = Math.min(n - 1, i + 1);
      let a = n ? CC[i] : 0, b = n ? CC[j] : 0; if (b - a > 0.5) b -= 1; else if (a - b > 0.5) b += 1;   // unwrap the seams
      const pos = a + (b - a) * (f - i) + W.target;
      W.spin.style.transform = `rotate(${-(pos * 360 + 90)}deg)`;
      const cur = frac(pos) >= 0.5 ? 1 : 0;
      W.bg.forEach((e, k) => e.style.background = rgb(wcol(k === cur ? 'sliceSelectedColor' : 'sliceNormalColor')));
      if (cur !== W.last) { if (W.last != null) echo(W); W.last = cur; }
      const sc = W.extra; if (sc) sc.style.background = `rgba(0,0,0,${p < 1 ? 0.784 : Math.max(0, 0.784 * (1 - (beats - 8) * 60 / W.bpm))})`;
    } else {
      const pos = W.landed ? W.target : wheelPos(W, now), cur = sliceAt(pos);
      W.spin.style.transform = `rotate(${-(frac(pos) * 360 + 90)}deg)`;
      const pulse = (Math.cos(2 * Math.PI * ((now - W.t0) / 1000) * W.bpm / 30) + 1) / 2;    // half-beat period
      W.bg.forEach((e, k) => e.style.background = rgb(wcol(k === cur ? (W.landed ? 'sliceSelectedColor' : 'sliceHighlightColor') : 'sliceNormalColor')));
      W.gl.forEach((e, k) => {
        const bonus = W.bonus && k === 0, tgt = k === 0 || W.targets.has(k);
        let c;
        if (k === cur && W.landed) {                                             // pulse between the normal and selected tint
          const hi = wcol(bonus ? 'bonusSelectedColor' : 'volumeSelectedColor'), lo = wcol(bonus ? 'bonusNormalColor' : 'volumeNormalColor');
          c = hi && lo ? rgb(lo.map((l, q) => l + (hi[q] - l) * pulse)) : '';
        } else if (k === cur) c = rgb(wcol(bonus ? 'bonusHighlightColor' : 'volumeHighlightColor'));
        else c = rgb(wcol(bonus ? 'bonusNormalColor' : tgt ? 'volumeNormalColor' : 'volumeDimmedColor'));
        e.style.background = c;
      });
      if (cur !== W.last) { if (W.last != null) echo(W); W.last = cur; }
    }
    requestAnimationFrame(wheelFrame);
  }
  function showWheel(w, cx, state, cur) {
    const host = layer('c-wheel');
    const live = w.state === 'spinning' || w.state === 'landed';
    if (!live) { if (WH && !WH.coin && !WH.closing) WH.closing = performance.now(); return; }
    if (WH && WH.coin) { WH.el.remove(); WH.extra?.remove(); WH = null; }
    if (!WH || WH.id !== w.spin) {
      if (WH) { WH.el.remove(); WH.extra?.remove(); }
      const el = document.createElement('div'); el.className = 'wh'; Object.assign(el.style, { left: cx + 'px', top: '390px' });
      const target = w.side === 'Red' ? 'Blue' : 'Red';                           // the spinner's opponent is targeted
      const targets = new Set(state.pads.filter(p => p.top && !p.silenced && p.owner === target).map(p => Math.max(0, Math.min(3, p.top.power || 0))));
      const si = Math.max(0, (TB.wheel || []).findIndex(s => s.value === (w.resultValue ?? 0)));
      const lo = SLICE_EDGES[si] ?? 0, hi = SLICE_EDGES[si + 1] ?? 1;
      WH = { id: w.spin, el, coin: false, bonus: !!w.bonus, targets, bpm: w.bpm || TB.startTempo || 0,
             up: Math.max(0, ((w.ms || 4000) - (w.downMs || 4000)) / 1000), down: (w.downMs || 4000) / 1000,
             target: lo + (hi - lo) * (0.15 + 0.7 * hashRand(w.spin)), t0: performance.now() - (w.elapsedMs || 0), shown: performance.now(), last: null };
      buildWheel(WH); host.appendChild(el);
      // clashFade: black over the wheel's half of the screen; non-vulnerable slots dimmed
      const ex = document.createElement('div'); ex.style.cssText = 'position:absolute;inset:0;pointer-events:none;z-index:7';
      const left = cx < 960;
      ex.innerHTML = `<div class="wh-scrim" style="background:linear-gradient(${left ? '90deg' : '270deg'}, rgba(0,0,0,.961) 0, rgba(0,0,0,.961) ${left ? 3720 : 3720}px, transparent ${3720 + 300}px)"></div>`
        + state.pads.map((p, k) => (p.top && !p.silenced && p.owner === target) ? '' : `<div class="wh-dim" style="left:873px;top:${45 + k * 202.5}px"></div>`).join('')
        + `<div class="wh-cancel" style="left:${cx}px;top:${390 + 558 - 32}px;display:none"><img src="/ui/wheel_cancel_dm_button" alt="">${OMLoc.rich(LT('Spinner_Cancel', 'Stop').replace(/<sprite[^>]*>\s*/, ''), { upper: true })}</div>`;
      host.insertBefore(ex, el); WH.extra = ex;
      requestAnimationFrame(wheelFrame);
    }
    WH.landed = w.state === 'landed';
    const cancel = WH.extra.querySelector('.wh-cancel');
    if (cancel) cancel.style.display = w.state === 'spinning' && w.canCancel !== false && (performance.now() - WH.t0) / 1000 < WH.up + 2 * 60 / WH.bpm ? 'flex' : 'none';
  }
  function showCoin(flip, bpm) {
    const host = layer('c-coin');
    if (!flip) return;
    const dur = 8 * 60 / bpm * 1000;
    if (performance.now() - flip.t > dur + 1500) { if (WH && WH.coin && !WH.closing) WH.closing = performance.now(); return; }
    if (WH && WH.coin && WH.id === flip.id) return;
    if (WH) { WH.el.remove(); WH.extra?.remove(); }
    const el = document.createElement('div'); el.className = 'wh'; Object.assign(el.style, { left: '960px', top: '540px' });
    const sideOne = flip.side === 'Blue';                                           // side 1 = Blue, 2 = Red
    const r = hashRand(flip.id);
    WH = { id: flip.id, el, coin: true, bpm, t0: flip.t, shown: flip.t, last: null,
           target: sideOne ? 0.55 + 0.4 * r : 0.05 + 0.4 * r };
    buildCoin(WH);
    const ex = document.createElement('div'); ex.className = 'wh-scrim'; ex.style.zIndex = 7; WH.extra = ex;
    host.appendChild(ex); host.appendChild(el);
    requestAnimationFrame(wheelFrame);
  }

  // {beat}/{guitar}/... in FX rule texts → the colour card or instrument icon, as on the original's FX screen
  const fxTags = t => esc(t || '').replace(/\{(\w+)\}/g, (m, k) => CARD_SPR[{ beat: 'Beat', bass: 'Bass', loop: 'Loop', lead: 'Lead', fx: 'Fx', wild: 'Wild' }[k]]
    ? `<img src="/ui/${CARD_SPR[{ beat: 'Beat', bass: 'Bass', loop: 'Loop', lead: 'Lead', fx: 'Fx', wild: 'Wild' }[k]]}" style="height:1.3em;vertical-align:-.3em" alt="">`
    : `<i style="display:inline-block;width:1.2em;height:1.2em;vertical-align:-.2em;background:#fff;-webkit-mask:url('/ui/instrument_${k === 'drum' ? 'drums' : k}') center/contain no-repeat;mask:url('/ui/instrument_${k === 'drum' ? 'drums' : k}') center/contain no-repeat"></i>`);
  let cPrev = null, cTops = null, cPlays = [], cPopSeen = null, cFlip = null, cFx = null, cFxSeen = null, cHandShow = 0;
  function clashLobby(state, m) {
    const red = m.red?.players || [], blue = m.blue?.players || [];
    const named = (side, list) => list.length
      ? `<div><div class="c-team" data-a="c-reroll" data-side="${side}" title="Tap for another icon" style="cursor:pointer;position:relative;margin:0 auto;width:150px;height:150px;-webkit-mask-image:url('/ui/${TEAM_ICON[side]}');mask-image:url('/ui/${TEAM_ICON[side]}')"></div><small>${list.map(esc).join(' & ').toUpperCase()}</small></div>` : null;
    const label = k => k === 0 ? named('Red', red) : k === 4 ? named('Blue', blue) : null;
    const join = k => (k === 1 && !red.length) || (k === 3 && !blue.length);
    const ready = red.length && blue.length;
    setHtml(layer('c-base'), `<div class="o-dots"></div>
      <div class="o-topbar"><div class="l"><img data-a="mn-go" data-to="modes" src="/ui/sys_back" alt="back"></div>${LU('Menu_IconSelect', 'CLASH')}<div class="r"><span data-a="connect" class="cn-ic" title="Connect a phone" aria-label="Connect a phone">${WIFI_SVG}</span></div></div>
      <div class="o-center" style="justify-content:flex-start;top:195px"><div class="o-heavy" style="font-size:84px;letter-spacing:.09em">${ready ? LU('IconSelect_HitButton', 'BEGIN: <sprite name="dropmix">') : 'JOIN A TEAM ON YOUR PHONE'}</div></div>
      <div class="o-slots" style="top:375px">${slotsHtml(state, { label, join })}</div>
      <img src="/ui/shadow_clash_color" alt="" style="position:absolute;left:810px;top:350px;width:300px;pointer-events:none" onerror="this.remove()">
      <div class="o-center" style="justify-content:flex-start;top:760px">
        ${ready ? `<div class="o-heavy o-click" data-a="start" style="font-size:40px;letter-spacing:.14em;pointer-events:auto">TAP TO START</div>` : ''}
        <div class="o-heavy" style="font-size:32px;letter-spacing:.14em;margin-top:12px;line-height:1.5">${LU('IconSelect_CardsClash', 'Solo team: 30\nTeam of two: 15 each')}</div></div>
      ${drawer('clash', `${m.target || 21} POINTS`, m.timerLimit ? `${`0:${String(m.timerLimit).padStart(2, '0')}`.replace('0:60', '1:00')} TIMER` : LU('ClashSettings_NoTimerTitle', 'UNTIMED'), true,
        [{ a: 'c-score', stops: ['15', '21'], i: (m.target || 21) === 15 ? 0 : 1 }, { a: 'c-timer', stops: ['OFF', '0:15', '0:30', '1:00'], i: [0, 15, 30, 60].indexOf(m.timerLimit || 0) }])}`);
  }

  function clashGame(state, m) {
    const cur = m.current, phase = m.phase;
    // track the colours played this turn (action pips) and turn changes (hand-count badges)
    if (!cPrev || cPrev.current !== cur) { cPlays = []; cHandShow = Date.now(); }
    if (cPrev) state.pads.forEach((p, i) => { const was = cPrev.tops[i]; if (p.top && p.valid !== false && p.top.cid !== was && (p.owner || '') === cur) cPlays.push(p.top.type); });
    if (cPrev && cPrev.phase === 'lobby' && phase === 'play') cFlip = { at: Date.now(), side: cur };
    // a newly played FX card shows its rule full-screen for a few seconds
    state.pads.forEach((p, i) => { if (p.top && p.valid !== false && p.top.type === 'Fx' && cPrev && p.top.cid !== cPrev.tops[i]) cFx = { card: p.top, at: Date.now() }; });
    cPrev = { current: cur, phase, tops: state.pads.map(p => p.top?.cid) };

    const base = layer('c-base');
    if (!base.querySelector('.o-crowd')) setHtml(base, `<div class="o-crowd"><div class="ph"></div><div class="tint"></div><div class="lights"></div><div class="shade" style="background:linear-gradient(#000c,#0006 40%,#000a)"></div></div>`);
    setHtml(layer('c-top'), `<div class="o-icon" data-a="pause" style="left:60px;top:45px"><img src="/ui/sys_pause" alt=""></div><div class="o-icon mx-save" data-a="mix-save" style="left:1788px;top:45px"><img src="/ui/sys_mixes_add" alt=""></div>`);

    const target = m.target || 21;
    const circle = (side, cx) => {
      const sc = m[side.toLowerCase()]?.score ?? 0, on = phase === 'play' && cur === side;
      const f = Math.max(0, Math.min(1, sc / target)), C = 2 * Math.PI * 266;
      return `${on ? `<div class="c-glow" style="left:${cx}px;top:387px"></div>` : ''}<div class="c-circle ${on ? 'on' : 'off'}" style="left:${cx}px;top:387px">
        <svg viewBox="0 0 552 552"><circle cx="276" cy="276" r="266" fill="none" stroke="${on ? '#0004' : '#ffffff26'}" stroke-width="10"/>
          <circle cx="276" cy="276" r="266" fill="none" stroke="${on ? '#222' : '#fff'}" stroke-width="10" stroke-linecap="round" stroke-dasharray="${(f * C).toFixed(1)} ${C.toFixed(1)}"/></svg>
        <div class="n">${sc}</div></div>`;
    };
    const handCount = side => (state.players || []).filter(p => p.team === side).reduce((a, p) => a + p.handCount, 0);
    const showHands = Date.now() - cHandShow < 3500;
    const pips = side => {
      if (phase !== 'play') return '';
      const cx = side === 'Red' ? 393 : 1530;
      if (showHands) return `<div class="c-hand" style="left:${cx}px">${handCount(side)}</div>`;
      if (cur !== side) return '';
      const used = cPlays.length;
      return `<div class="c-pips" style="left:${cx}px">${[0, 1].map(i => `<div><b class="${i < used ? '' : ''}${i >= (m.actionsLeft + used) ? ' used' : ''}"></b>${cPlays[i] ? `<img src="/ui/${CARD_SPR[cPlays[i]]}" alt="">` : ''}</div>`).join('')}</div>`;
    };
    setHtml(layer('c-score'), `${circle('Red', 393)}${circle('Blue', 1530)}${pips('Red')}${pips('Blue')}
      <div class="c-team" style="left:393px;top:945px;-webkit-mask-image:url('/ui/${TEAM_ICON.Red}');mask-image:url('/ui/${TEAM_ICON.Red}')"></div>
      <div class="c-team" style="left:1530px;top:945px;-webkit-mask-image:url('/ui/${TEAM_ICON.Blue}');mask-image:url('/ui/${TEAM_ICON.Blue}')"></div>
      ${m.timerLeft != null && phase === 'play' ? `<div style="position:absolute;left:${cur === 'Red' ? 393 : 1530}px;top:690px;transform:translateX(-50%);font-family:var(--o-din);font-size:54px;font-weight:700">${Math.ceil(m.timerLeft)}</div>` : ''}`);

    // the slot column, top = slot 1; power bars point toward the owning team
    const col = state.pads.map((p, k) => {
      const c = p.top, cols = SLOT_COL[k], top = k * 202.5;
      const err = (m.errors || []).find(x => x.slot === k);
      if (c && err) return `<div class="c-slot c-foul" style="top:${top}px"><div class="art">${c.image ? `<img src="/art/${encodeURIComponent(c.image)}" alt="">` : ''}</div><div class="bad"></div></div>`;
      if (!c) return `<div class="c-slot" style="top:${top}px;--g1:${cols[0]};--g2:${cols[cols.length - 1]}"><img class="frame" src="/ui/shell_slot_0${4 - k}" alt=""></div>`;
      const n = Math.max(0, Math.min(3, c.power || 0)), side = p.owner === 'Red' ? 'l' : 'r';
      const colours = c.type === 'Wild' ? cols : [COL[c.type] || '#fff'];
      const bars = [0, 1, 2].map(i => { const lit = (2 - i) < n; return `<i style="${lit ? `background:${colours[Math.min(colours.length - 1, 2 - i)] || colours[0]}` : ''}"></i>`; }).join('');
      return `<div class="c-slot" style="top:${top}px"><div class="c-bars ${side}">${bars}</div><div class="art"><img src="/art/${encodeURIComponent(c.image)}" alt=""></div>${p.silenced ? '<div class="x"></div>' : ''}</div>`;
    }).join('');
    setHtml(layer('c-col', 'c-col'), col);
    const errs = m.errors || [];
    setHtml(layer('c-err'), ['Red', 'Blue'].map(side => {
      const e = errs.find(x => x.side === side) || (side === cur ? errs.find(x => x.side !== 'Red' && x.side !== 'Blue') : null);
      if (!e) return '';
      return `<div class="c-scrim" style="${side === 'Red' ? 'left:-3000px;right:1047px' : 'left:1047px;right:-3000px'}"></div>
        <div class="c-errtx" style="left:${side === 'Red' ? 393 : 1530}px"><b>${esc(e.text)}</b><span>${esc(OMLoc.text('Error_ReturnToHand', 'TAKE THE CARD BACK').toUpperCase())}</span></div>`;
    }).join(''));

    // CardEnterBurst: a new card in a slot flares its colours out of the slot (centre + left/right bands)
    const fxL = layer('c-pops');
    const slotXY = k => [873 + 88.5, 45 + Math.max(0, Math.min(4, k)) * 202.5 + 88.5];
    const tops = state.pads.map(p => p.top && p.valid !== false ? p.top.cid + ':' + p.stack : null);
    if (cTops) tops.forEach((t, k) => {
      if (!t || t === cTops[k]) return;
      const c = state.pads[k].top, cols = c.type === 'Wild' ? SLOT_COL[k] : [COL[c.type] || '#fff'];
      const [x, y] = slotXY(k), d = document.createElement('div');
      d.className = 'c-enter'; Object.assign(d.style, { left: x + 'px', top: y + 'px' });
      d.style.setProperty('--c1', cols[0]); d.style.setProperty('--c2', cols[cols.length - 1]);
      d.innerHTML = '<div class="band"></div><div class="col"></div><div class="core"></div>';
      fxL.appendChild(d); setTimeout(() => d.remove(), 1200);
    });
    cTops = tops;

    // SlotScoreIndicator: "+N" over the slot; several events from one play follow each other (BumpSecondary)
    const evs = m.events || [];
    if (cPopSeen === null) cPopSeen = evs.length ? evs[evs.length - 1].id : 0;
    let stagger = 0;
    for (const e of evs) {
      if (e.id <= cPopSeen) continue; cPopSeen = e.id;
      if (!e.points) continue;
      const kind = e.points < 0 ? ' neg' : e.kind === 'FX' ? ' fx' : e.kind === 'MixFill' ? ' mix' : '';
      const d = document.createElement('div'); d.className = 'c-pop' + kind;
      d.innerHTML = `<div class="burst"></div><div class="ring"></div><b>${e.points > 0 ? '+' : ''}${e.points}</b>`;
      const [x, y] = slotXY(e.slot);
      Object.assign(d.style, { left: x + 'px', top: y + 'px', animationDelay: stagger + 's' });
      d.querySelector('.burst').style.animationDelay = stagger + 's';
      const delay = stagger; stagger += 0.45;
      fxL.appendChild(d); setTimeout(() => d.remove(), (1.6 + delay) * 1000);
    }

    // the EQ wheel (WheelOfMisfortune, original sprites) over the spinning team's score circle, Spinner_Cancel
    const w = m.wheel || {};
    showWheel(w, w.side === 'Red' || (w.side !== 'Blue' && cur === 'Red') ? 393 : 1530, state, cur);

    // HeadToHead_FirstUp: the coin flip when the game starts (original coin, 8 beats on the landing curve)
    if (cFlip && !cFlip.id) { cFlip.id = Date.now(); cFlip.t = performance.now(); }
    showCoin(cFlip, (state.mix && state.mix.tempo) || TB.startTempo || 0);

    // FX card info panel
    const fx = layer('c-fx');
    if (cFx && Date.now() - cFx.at < 4000 && cFx.card.fx) {
      const c = cFx.card;
      setHtml(fx, `<div class="c-fx o-fade"><h2>${esc(c.name)}</h2><div class="pics">${c.picto ? `<img src="/ui/${encodeURIComponent(c.picto)}" style="height:120px" alt="">` : window.abilityPics ? window.abilityPics(c.ability) : ''}</div><p>${fxTags(c.fx)}</p></div>`);
    } else setHtml(fx, '');

    // the winner
    const win = layer('c-win');
    if (phase === 'over' && m.winner) {
      const ws = m.winner, rs = m.red?.score ?? 0, bs = m.blue?.score ?? 0;
      const ic = side => `<span class="c-team" style="position:relative;display:inline-block;margin:0;width:110px;height:110px;-webkit-mask-image:url('/ui/${TEAM_ICON[side]}');mask-image:url('/ui/${TEAM_ICON[side]}')"></span>`;
      setHtml(win, `<div class="c-win o-fade"><div style="position:absolute;inset:0;background:#000b"></div><div class="sparks"></div>
        <div style="position:absolute;left:0;right:0;top:200px;display:flex;flex-direction:column;align-items:center;gap:30px">
          <span class="c-team" style="position:relative;margin:0;width:330px;height:330px;-webkit-mask-image:url('/ui/${TEAM_ICON[ws]}');mask-image:url('/ui/${TEAM_ICON[ws]}')"></span>
          <div style="font-size:150px;font-weight:700;letter-spacing:.1em">WINS</div></div>
        <div style="position:absolute;left:0;right:0;top:120px;display:flex;justify-content:center;gap:200px;opacity:0;animation:o-fadein .5s 4s forwards">
        </div>
        <div style="position:absolute;right:80px;top:60px;display:flex;gap:120px;font-size:30px;font-weight:600;letter-spacing:.12em;text-align:center">
          <div class="o-click" data-a="to-main"><img src="/ui/sys_home" style="height:120px;filter:invert(1);display:block;margin:0 auto 10px">${LU('Menu_MainMenu', 'MENU')}</div>
          <div class="o-click" data-a="start"><img src="/ui/sys_restart_game" style="height:120px;filter:invert(1);display:block;margin:0 auto 10px">REMATCH</div></div>
        <div class="row"><div class="side">${rs}${ic('Red')}</div><div class="mid"><i></i><img src="/ui/shadow_clash_color" style="height:90px" onerror="this.outerHTML='<b>VS</b>'"><i></i></div><div class="side">${ic('Blue')}${bs}</div></div></div>`);
    } else setHtml(win, '');
  }

  // ======================================================================================
  // Puzzle / Lanes (recorded on the device: docs/ORIGINAL_UI.md "Puzzle")
  // ======================================================================================
  const LANE_COL = ['#FFF100', '#EB0C6D', '#EB0C6D', '#00BFF3', '#50B048'];
  const REQ_SPR = { Lead: 'shadow_yellow_card', Loop: 'shadow_red_card', Beat: 'shadow_blue_card', Bass: 'shadow_green_card' };
  document.head.insertAdjacentHTML('beforeend', `<style>
    .l-head { position: absolute; left: 63px; top: 30px; display: flex; align-items: center; gap: 26px; }
    .l-head b { font-family: var(--o-din); font-weight: 700; font-size: 110px; line-height: 1; text-shadow: 0 0 20px #fff8; }
    .l-head span { font-size: 40px; font-weight: 700; letter-spacing: .1em; line-height: 1.05; }
    .l-score { position: absolute; left: 0; right: 0; top: 28px; text-align: center; font-family: var(--o-din); font-weight: 700; font-size: 96px; line-height: 1; }
    .l-score em { font-style: normal; position: absolute; margin-left: 40px; font-size: 64px; top: 8px; animation: l-up 1.6s ease-out forwards; }
    @keyframes l-up { 0% { opacity: 0; } 15% { opacity: 1; } 75% { opacity: 1; } 100% { opacity: 0; } }
    .l-slot { position: absolute; left: 288px; width: 170px; height: 170px; }
    .l-slot .frame { position: absolute; inset: 0; width: 100%; height: 100%; transform: scaleX(-1); filter: drop-shadow(0 0 8px var(--g1)) drop-shadow(0 0 8px var(--g2)); }
    .l-slot .art { position: absolute; inset: 0; border-radius: 4px; overflow: hidden; } .l-slot .art img { width: 100%; height: 100%; object-fit: cover; }
    .l-slot .x { position: absolute; inset: 0; background: url('/ui/sys_blocked_icon') center / 70% no-repeat; }
    .l-slot .bad { position: absolute; inset: 0; background: #000a url('/ui/sys_blocked_icon') center / 60% no-repeat; }
    .l-bars { position: absolute; right: calc(100% + 4px); top: 12px; width: 145px; height: 146px; display: flex; flex-direction: column; gap: 8px; align-items: flex-end; }
    .l-bars i { height: 43px; background: #ffffff1a; clip-path: polygon(0 0, 100% 0, 100% 100%, 43px 100%); }
    .l-bars i:nth-child(1) { width: 100%; } .l-bars i:nth-child(2) { width: 82%; } .l-bars i:nth-child(3) { width: 64%; }
    .l-lane { position: absolute; left: 465px; width: 1215px; height: 170px; }
    .l-err { position: absolute; left: 465px; width: 1215px; height: 170px; z-index: 5; background: #000d; border: 4px solid #ff2a2a; border-radius: 12px; box-sizing: border-box;
      display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 10px; animation: c-foul .5s steps(2) infinite; }
    .l-err b { font-size: 54px; font-weight: 800; letter-spacing: .1em; color: #ff3b3b; } .l-err span { font-size: 34px; letter-spacing: .16em; }
    .l-lane .stripes { position: absolute; inset: 0; background: repeating-linear-gradient(180deg, var(--lc) 0 46px, #0000 46px 62px); opacity: .32; }
    .l-lane.danger .stripes { animation: l-danger .6s steps(2) infinite; } @keyframes l-danger { 50% { opacity: .65; } }
    .l-q { position: absolute; -webkit-mask: 0 0 / 100% 100% no-repeat; mask: 0 0 / 100% 100% no-repeat; }
    .l-icon { position: absolute; width: 128px; height: 154px; display: grid; place-items: center; pointer-events: none; }
    .l-icon img { height: 140px; }
    .l-timer { position: absolute; left: 1700px; top: 120px; width: 142px; height: 910px; background: center / 100% 100% no-repeat; }
    .l-timer .base { position: absolute; inset: 0; background: #262626; -webkit-mask: url('/ui/lanes_turn_timer') center / 100% 100% no-repeat; mask: url('/ui/lanes_turn_timer') center / 100% 100% no-repeat; }
    .l-timer .fill { position: absolute; left: 50%; width: 24px; margin-left: -12px; background: #f5f5f5; box-shadow: 0 0 22px 4px #fff8;
      -webkit-mask: url('/ui/lanes_turn_timer_fill') center / 100% 100% no-repeat; mask: url('/ui/lanes_turn_timer_fill') center / 100% 100% no-repeat; }
    .l-timer .ring { position: absolute; left: 50%; top: 50%; width: 142px; height: 142px; margin: -71px 0 0 -71px; background: url('/ui/icon_empty') center / contain no-repeat; }
    .l-msg { position: absolute; left: 465px; width: 1215px; top: 138px; height: 890px; background: #000e; display: flex; flex-direction: column; align-items: center; justify-content: center; text-align: center; gap: 40px; }
    .l-msg h1 { margin: 0; font-size: 110px; font-weight: 700; letter-spacing: .08em; line-height: 1.1; }
    .l-msg p { margin: 0; font-size: 44px; font-weight: 700; letter-spacing: .14em; }
    .l-msg p img { height: 44px; vertical-align: -6px; }
    .l-recap { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 30px; text-align: center; }
    .l-recap .boxes { display: flex; gap: 120px; }
    .l-recap .box small { display: block; font-size: 36px; font-weight: 700; letter-spacing: .12em; margin-bottom: 14px; }
    .l-recap .box b { display: grid; place-items: center; width: 260px; height: 260px; border-radius: 22px; font-family: var(--o-din); font-weight: 700; font-size: 150px; }
    .l-recap .w b { background: #222; border: 8px solid #888; } .l-recap .g b { background: #3a3530; border: 8px solid #b88a2a; color: #f5c542; }
    .l-recap .r b { background: #ddd; border: 8px solid transparent; background-clip: padding-box; box-shadow: 0 0 0 8px #EB0C6D, 0 0 40px #fff8;
      color: transparent; -webkit-text-stroke: 0; background-image: linear-gradient(#ddd, #ddd); }
    .l-recap .r b span { background: linear-gradient(90deg, #50B048, #00BFF3, #EB0C6D, #FFF100); -webkit-background-clip: text; background-clip: text; color: transparent; }
    .l-recap .line { font-size: 40px; font-weight: 700; letter-spacing: .12em; }
    .l-recap .big { font-family: var(--o-din); font-weight: 700; font-size: 110px; line-height: 1; }
    .l-pl { position: absolute; left: 0; right: 0; top: 790px; display: flex; justify-content: center; gap: 22px; flex-wrap: wrap; padding: 0 200px; }
    .l-pl button { width: 92px; height: 92px; border-radius: 14px; border: 3px solid #333; background: #000 center / 70% no-repeat; cursor: pointer; opacity: .55; }
    .l-pl button.on { border-color: #fff; opacity: 1; box-shadow: 0 0 22px #fff8; }
    .l-ticker { position: absolute; left: 0; right: 0; bottom: 30px; text-align: center; font-size: 44px; letter-spacing: .14em; font-weight: 600; }
    .l-art { position: absolute; left: 470px; width: 330px; height: 330px; border-radius: 10px; overflow: hidden; box-shadow: 0 0 40px #000; animation: o-fadein .25s ease-out; }
    .l-art img { width: 100%; height: 100%; object-fit: cover; }
  </style>`);

  let lPrev = null, lScore = 0, lDelta = null, lMsg = null, lCard = null, lNotice = null, lFinale = null;
  const pretty = x => x ? x[0].toUpperCase() + x.slice(1) : x;
  function lanesLobby(state, m) {
    const pls = (m.allPlaylists || []).map(pl => `<button class="${(m.playlists || []).includes(pl) ? 'on' : ''}" data-a="l-pl" data-pl="${pl}" title="${pretty(pl)}" style="background-image:url('/ui/i_${pl}')"></button>`).join('');
    const chosen = (m.playlists || []);
    const label = k => k === 2 && chosen.length ? `<div style="display:flex;gap:10px;justify-content:center">${chosen.map(pl => `<img src="/ui/i_${pl}" style="width:70px;height:70px;border-radius:10px">`).join('')}</div>` : null;
    const best = (m.leaderboard || [])[0]?.score || 0;
    setHtml(layer('l-base'), `<div class="o-dots"></div>
      <div class="o-topbar"><div class="l"><img data-a="mn-go" data-to="modes" src="/ui/sys_back" alt="back"></div>${LU('Menu_PuzzleSetup', 'PUZZLE')}<div class="r"><img data-a="scores" src="/ui/sys_leaderboard_icon" alt=""><span data-a="connect" class="cn-ic" title="Connect a phone" aria-label="Connect a phone">${WIFI_SVG}</span></div></div>
      <div class="o-center" style="justify-content:flex-start;top:195px"><div class="o-heavy o-click" data-a="start" style="font-size:84px;letter-spacing:.09em;pointer-events:auto">${LU('IconSelect_HitButton', 'BEGIN: <sprite name="dropmix">')}</div></div>
      <div class="o-slots" style="top:375px">${slotsHtml(state, { label: k => label(k) })}</div>
      <style>#l-base .o-slot .frame { filter: none; opacity: .35; } #l-base .o-slot:nth-child(3) .frame { opacity: 1; filter: drop-shadow(0 0 10px #EB0C6D) drop-shadow(0 0 10px #00BFF3); }</style>
      <div class="o-heavy" style="position:absolute;left:0;right:0;top:735px;text-align:center;font-size:32px;letter-spacing:.14em">PICK ${chosen.length >= 2 ? '' : 'TWO '}PLAYLISTS · ${esc(OMLoc.text('Lanes_SetupCards', '30 CARDS').toUpperCase())}</div>
      <div class="l-pl">${pls}</div>
      ${drawer('lanes', m.hard ? 'FEVER' : 'NORMAL', LU('PartySettings_HighScore', 'BEST: {0}', best.toLocaleString()), !!m.hardUnlocked,
        [{ a: 'l-diff', stops: ['NORMAL', 'FEVER'], i: m.hard ? 1 : 0 }])}`);
  }

  function lanesGame(state, m) {
    const ph = m.phase;
    // score delta, newly played card (art + ticker), notices, finale banner
    if (lPrev && m.score > lScore) lDelta = { n: m.score - lScore, at: Date.now() };
    lScore = m.score;
    const tops = state.pads.map(p => p.top?.cid);
    if (lPrev) state.pads.forEach((p, i) => { if (p.top && p.top.cid !== lPrev.tops[i] && p.valid !== false) lCard = { card: p.top, slot: i, at: Date.now() }; });
    if (m.notice && (!lNotice || m.notice.id !== lNotice.id)) { if (lNotice) lMsg = { text: m.notice.text, at: Date.now() }; lNotice = { id: m.notice.id }; }
    if (lPrev && lPrev.phase !== 'finale' && ph === 'finale') lFinale = Date.now();
    lPrev = { phase: ph, tops };

    const base = layer('l-base');
    if (!base.querySelector('.o-crowd')) setHtml(base, `<div class="o-crowd"><div class="ph"></div><div class="tint"></div><div class="lights"></div><div class="shade" style="background:#0007"></div></div>`);
    const finale = ph === 'finale';
    setHtml(layer('l-top'), `<div class="l-head">${finale ? `<span style="font-size:52px">★ ${LU('Lanes_FinaleTitle', 'FINALE')}</span>` : `<b>${m.actionsLeft}</b><span>${LU(m.actionsLeft === 1 ? 'Lanes_ActionsRemainingSingle' : 'Lanes_ActionsRemaining', 'Plays\nleft')}</span>`}</div>
      <div class="l-score">${m.score.toLocaleString()}${lDelta && Date.now() - lDelta.at < 1600 ? `<em>+${lDelta.n}</em>` : ''}</div>
      <div class="o-icon" data-a="pause" style="left:1617px;top:45px"><img src="/ui/sys_pause" alt=""></div>
      <div class="o-icon mx-save" data-a="mix-save" style="left:1788px;top:45px"><img src="/ui/sys_mixes_add" alt=""></div>`);

    // the slot column with its lane to the right; cells: index 0 is next to the slot
    const lanes = m.lanes || [];
    let html = '';
    for (let k = 0; k < 5; k++) {
      const top = 138 + k * 180, p = state.pads[k], c = p.top, lane = lanes[k] || { cells: [] };
      const g = SLOT_COL[k];
      if (c) {
        const n = Math.max(0, Math.min(3, c.power || 0)), col = c.type === 'Wild' ? g[0] : (COL[c.type] || '#fff');
        html += `<div class="l-slot" style="top:${top}px"><div class="l-bars">${[0, 1, 2].map(i => `<i style="${(2 - i) < n ? `background:${col}` : ''}"></i>`).join('')}</div>
          <div class="art"><img src="/art/${encodeURIComponent(c.image)}" alt=""></div>${p.valid === false ? '<div class="bad"></div>' : p.silenced ? '<div class="x" style="background-image:url(/ui/remove)"></div>' : ''}</div>`;
      } else html += `<div class="l-slot" style="top:${top}px;--g1:${g[0]};--g2:${g[g.length - 1]}"><img class="frame" src="/ui/shell_slot_0${4 - k}" alt=""></div>`;
      // lane cells: each request tile is 4 quarter pieces (block_corner_convex / block_edge / block_edge_top /
      // block_corner_concave / block_fill, drawn as the top-right quarter and mirrored), a white *_border layer
      // under a tinted fill layer; quarters bridge the gap to same-block neighbours (the prefab's "fusers").
      let cells = '';
      const TW = 128, TH = 154, GX = 22, GY = 26, cx = idx => 40 + idx * 150;
      const same = (lk, idx, b) => { const L = lanes[lk]; const r = L && L.cells[idx]; return !!(r && b != null && r.b === b); };
      lane.cells.forEach((r, idx) => {
        if (!r) return;
        const b = r.n > 1 ? r.b : null, merged = b != null;
        const fillCol = merged ? '#a8a5a0' : '#1f1f1f', bordCol = merged ? '#d9d6d0' : '#f2f2f2';
        // quarters: [dx, dy] = horizontal side (+1 right) and vertical side (-1 up = previous lane)
        for (const [dx, dy] of [[1, -1], [-1, -1], [1, 1], [-1, 1]]) {
          const h = same(k, idx + dx, b), v = same(k + dy, idx, b), d = same(k + dy, idx + dx, b);
          const piece = !h && !v ? 'block_corner_convex' : h && !v ? 'block_edge_top' : !h && v ? 'block_edge' : !d ? 'block_corner_concave' : 'block_fill';
          const w = TW / 2 + 1 + (h ? GX / 2 + 1 : 0), hh = TH / 2 + 1 + (v ? GY / 2 + 1 : 0);   // +1: overlap at the tile centre (no seams)
          const x = cx(idx) + (dx > 0 ? TW / 2 - 1 : TW / 2 + 1 - w), y = 8 + (dy > 0 ? TH / 2 - 1 : TH / 2 + 1 - hh);
          const flip = `scale(${dx > 0 ? 1 : -1},${dy < 0 ? 1 : -1})`;
          const box = `left:${x}px;top:${y}px;width:${w}px;height:${hh}px;transform:${flip}`;
          cells += `<i class="l-q" style="${box};background:${bordCol};-webkit-mask-image:url('/ui/${piece}${piece === 'block_fill' ? '' : '_border'}');mask-image:url('/ui/${piece}${piece === 'block_fill' ? '' : '_border'}')"></i>`;
          const iw = w - (h ? 0 : 5), ih = hh - (v ? 0 : 5), ix = dx > 0 ? x : x + (h ? 0 : 5), iy = dy < 0 ? y + (v ? 0 : 5) : y;
          const inner = `left:${ix}px;top:${iy}px;width:${iw}px;height:${ih}px;transform:${flip}`;
          cells += `<i class="l-q" style="${inner};background:${fillCol};-webkit-mask-image:url('/ui/${piece}');mask-image:url('/ui/${piece}')"></i>`;
        }
        const lvl = r.t.startsWith('Level') ? +r.t.slice(5) : 0;
        cells += `<div class="l-icon" style="left:${cx(idx)}px;top:8px">${lvl ? `<img src="/ui/shadow_power_${lvl}" alt="">` : `<img src="/ui/${REQ_SPR[r.t] || 'sys_wild_card'}" alt="">`}</div>`;
      });
      html += `<div class="l-lane${lane.danger ? ' danger' : ''}" style="top:${top}px;--lc:${LANE_COL[k]}"><div class="stripes"></div>${cells}</div>`;
      const lerr = (m.errors || []).find(x => x.slot === k);
      if (lerr) html += `<div class="l-err" style="top:${top}px"><b>${esc(lerr.text)}</b><span>${esc(OMLoc.text('Error_ReturnToHand', 'TAKE THE CARD BACK').toUpperCase())}</span></div>`;
    }
    setHtml(layer('l-board'), html);

    // the turn timer (turn_timer_meter): fills grow from both ends toward the ring as time runs out
    const tl = m.timerLeft, tlen = m.timerLen || 1, used = tl == null ? 0 : Math.max(0, Math.min(1, 1 - tl / tlen)), fh = Math.round(used * 360);
    setHtml(layer('l-timer'), `<div class="l-timer" data-a="eq" style="cursor:pointer"><div class="base"></div>
        <div class="fill" style="top:20px;height:${fh}px"></div><div class="fill" style="bottom:20px;height:${fh}px"></div><div class="ring"></div></div>`);

    // played card: art beside its slot + "ARTIST — SONG" ticker
    const art = layer('l-art');
    if (lCard && Date.now() - lCard.at < 2200) {
      const c = lCard.card;
      setHtml(art, `<div class="l-art" style="top:${Math.min(700, 138 + lCard.slot * 180 - 80)}px"><img src="/art/${encodeURIComponent(c.image)}" alt=""></div><div class="l-ticker">${esc(c.artist).toUpperCase()} — ${esc(c.name).toUpperCase()}</div>`);
    } else setHtml(art, '');

    // messages: intro, finale, fouls / hints, results
    let msg = '';
    const bad = state.pads.some(p => p.top && p.valid === false);
    if (ph === 'shuffle') msg = `<div class="l-msg"><h1 style="font-size:70px">${LU('Lanes_ShuffleTop', 'Shuffle your deck')}</h1><h1 style="font-size:70px">${LU('Lanes_ShuffleMid', 'Take 6 cards')}</h1><h1 style="font-size:64px">${LU('Lanes_ShuffleBot', 'Take a new card\nafter each play')}</h1></div>`;
    else if (lFinale && Date.now() - lFinale < 2600) msg = `<div class="l-msg"><h1>★ ${LU('Lanes_FinaleTitle', 'FINALE')}</h1><p>${LU('Lanes_FinaleSubtitle', 'Final plays!')}</p></div>`;
    else if (lMsg && Date.now() - lMsg.at < 2600 || bad) {
      const t = (lMsg?.text || '').toLowerCase();
      const head = /level/.test(t) ? LU('Error_LevelTooLow', 'Level not\nhigh enough') : /already in the mix|duplicate/.test(t) ? LU('Error_DuplicateCard', 'Already in\nthe mix') : /don't go in slot/.test(t) ? 'WRONG<br>SLOT'
        : /silenced|clear/.test(t) ? OMLoc.rich(LT('Clash_GameplayReminderRemoveCards', 'Clear the\n<sprite index=34> slots'), { upper: true, img: 'height:90px' })
        : /action|wait|now/.test(t) ? LU('Lanes_ErrorCardSpawning', 'Requests\nare loading') : esc(lMsg?.text || 'RETURN CARDS').toUpperCase();
      msg = `<div class="l-msg"><h1 style="font-size:96px">${head}</h1>${bad ? `<p>${OMLoc.rich(LT('Error_ReturnToHand', 'Take back the <sprite index=41> cards'), { upper: true, img: '' })}</p>` : ''}</div>`;
    }
    if (ph === 'over') {
      const r = m.recap || {};
      msg = `<div class="l-recap o-fade" style="background:#000c">
        <div class="boxes"><div class="box w"><small>${LU('Lanes_RecapSmall', '{0} x1\nblocks', r.white || 0)}</small><b>${r.whitePts || 0}</b></div>
          <div class="box g"><small>${LU('Lanes_RecapMedium', '{0} x2\nblocks', r.gold || 0)}</small><b>${r.goldPts || 0}</b></div>
          <div class="box r"><small>${LU('Lanes_RecapLarge', '{0} big\nblocks', r.rainbow || 0)}</small><b><span>${r.rainbowPts || 0}</span></b></div></div>
        <div class="line">${LU('Lanes_RecapClearBonus', 'Rows cleared: {0}', r.fullClears || 0)}</div><div class="big">${(r.fullClearPts || 0).toLocaleString()}</div>
        <div class="line">${LU('Lanes_LargestBlock', 'Biggest block: {0}', r.biggest || 0)} · +${(r.bonus || 0).toLocaleString()}</div>
        <div class="line" style="margin-top:20px">${esc(pretty((m.playlists || [])[0] || 'Puzzle')).toUpperCase()} · ${LU('Lanes_TotalScore', 'SCORE')}</div><div class="big" style="font-size:180px">${m.score.toLocaleString()}</div>
        ${r.unlocked ? `<div class="line" style="color:#f5c542">${LU('Lanes_DifficultyUnlocked', 'Fever is now open!')}</div>` : ''}
        <div class="line o-click" data-a="lobby" style="font-size:34px">${LU('Menu_TapToContinue', 'NEXT')}</div></div>`;
    }
    setHtml(layer('l-msg'), msg);

    // EQ wheel result
    const w = m.wheel || {};
    setHtml(layer('l-wheel'), w.state && w.state !== 'idle' ? `<div class="c-wheel" style="left:1110px;top:560px"><div class="ring"></div>
        <div class="pie" style="background:conic-gradient(#d8d8d8 0 25%, #7a7a7a 25% 50%, #1a1a1a 50% 75%, #3a3a3a 75% 100%);${w.state === 'spinning' ? 'animation:c-rot .6s linear infinite reverse' : ''}"></div><div class="ptr"></div>
        ${w.result ? `<div style="position:absolute;inset:0;display:grid;place-items:center;font-family:var(--o-din);font-weight:700;font-size:200px;text-shadow:0 0 30px #000">${w.result === 'X' ? '✕' : esc(w.result)}</div>` : ''}</div>` : '');
  }

  // ======================================================================================
  // MixGrid — unreleased; its layout scene is lost. Built from the surviving pieces: portrait 1080x1920
  // (MixGridConstants), MixGridBackground (level card art, zoom/centre), PartyRequest tiles with the
  // PartyRequestVisuals icons, goal_target, the InGame*Goal panels, slot frames and card art.
  // ======================================================================================
  document.head.insertAdjacentHTML('beforeend', `<style>
    .m-bg { position: absolute; inset: -600px -1200px; background: #000 center / cover; filter: brightness(.32) saturate(1.2) blur(2px); }
    .m-port { position: absolute; top: 0; left: 656px; width: 1080px; height: 1920px; transform: scale(.5625); transform-origin: 0 0; }
    .m-port .pbg { position: absolute; inset: 0; background: #000 no-repeat; filter: brightness(.55); }
    .m-port .pshade { position: absolute; inset: 0; background: linear-gradient(#000c, #0004 18%, #0004 66%, #000d); }
    .m-top { position: absolute; left: 40px; right: 40px; top: 36px; height: 200px; display: flex; align-items: center; justify-content: space-between; }
    .m-score small { display: block; font-size: 34px; font-weight: 700; letter-spacing: .14em; }
    .m-score b { font-family: var(--o-din); font-weight: 700; font-size: 110px; line-height: 1; }
    .m-score i { font-style: normal; font-family: var(--o-din); font-size: 64px; color: #f5c542; margin-left: 20px; }
    .m-goal { display: flex; flex-direction: column; gap: 14px; align-items: flex-end; }
    .m-goal .row { display: flex; align-items: center; gap: 18px; }
    .m-goal .box { width: 92px; height: 92px; border: 5px solid #fff; border-radius: 18px; display: grid; place-items: center; }
    .m-goal .box img { width: 78px; } .m-goal .box img.ck { width: 60px; filter: invert(1); }
    .m-goal .n { font-family: var(--o-din); font-weight: 700; font-size: 66px; min-width: 150px; }
    .m-goal .rnd { font-size: 32px; font-weight: 700; letter-spacing: .14em; }
    .m-grid { position: absolute; left: 38px; top: 270px; width: 1004px; height: 1004px; }
    .m-grid .grid { position: relative; width: 100%; height: 100%; touch-action: none; }
    .m-grid .cell { position: absolute; width: 132px; height: 132px; border-radius: 16px; transition: transform .1s; }
    .m-grid .cell.req { background: #fff; box-shadow: 0 6px 16px #0008; display: grid; place-items: center; }
    .m-grid .cell.req img { width: 118px; } .m-grid .cell.req img.inst { filter: brightness(0); }
    .m-grid .cell.req .lab { position: absolute; bottom: 4px; left: 0; right: 0; text-align: center; color: #000; font-weight: 800; font-size: 17px; letter-spacing: .06em; }
    .m-grid .cell.tg::after { content: ""; position: absolute; right: -14px; top: -14px; width: 54px; height: 54px; background: url('/ui/goal_target') center / contain no-repeat; filter: drop-shadow(0 0 6px #000); }
    .m-grid .cell.wall { background: #bdbab4; } .m-grid .cell.wall::before { content: ""; position: absolute; inset: 6px; border-radius: 12px; border: 4px dashed #0006; }
    .m-grid .cell.bump::before { content: ""; position: absolute; left: 14px; right: 14px; bottom: 10px; height: 12px; border-radius: 6px; background: #0005; }
    .m-grid .cell.absorb { box-shadow: 0 0 0 6px #EB0C6D, 0 0 18px #EB0C6D; }
    .m-grid .cell.gcard { overflow: hidden; border: 5px solid var(--c, #fff); background: #000; }
    .m-grid .cell.gcard img { width: 100%; height: 100%; object-fit: cover; }
    .m-grid .cell.gcard .sl { position: absolute; right: 8px; bottom: 2px; font-family: var(--o-din); font-weight: 700; font-size: 40px; text-shadow: 0 0 6px #000, 0 0 6px #000; }
    .m-grid .cell.gcard.mute { opacity: .45; }
    .m-grid .cell.ok { box-shadow: 0 0 0 5px #ffffffaa, 0 6px 16px #0008; }
    .m-grid .cell.on { transform: scale(.9); box-shadow: 0 0 0 8px #fff, 0 0 40px 6px #fff; }
    .m-grid .cell.flash { animation: mgflash .7s ease-out; }
    .m-hand { position: absolute; left: 40px; right: 40px; top: 1330px; height: 400px; display: flex; align-items: flex-end; justify-content: center; gap: 50px; }
    .m-hand .hc { position: relative; width: 275px; height: 385px; border-radius: 18px; overflow: hidden; border: 8px solid var(--c, #fff); background: #000; cursor: pointer; }
    .m-hand .hc img { width: 100%; height: 100%; object-fit: cover; }
    .m-hand .hc.deck { width: 190px; height: 266px; opacity: .8; }
    .m-hand .cap { position: absolute; left: 0; right: 0; bottom: -54px; text-align: center; font-size: 30px; font-weight: 700; letter-spacing: .12em; white-space: nowrap; }
    .m-hand .left { font-family: var(--o-din); font-weight: 700; font-size: 70px; align-self: center; text-align: center; }
    .m-hand .left small { display: block; font-family: var(--o-avenir); font-size: 28px; letter-spacing: .14em; }
    .m-slots { position: absolute; left: 40px; right: 40px; top: 1752px; display: flex; justify-content: space-between; }
    .m-slots .ms { position: relative; width: 180px; height: 140px; }
    .m-slots .ms img.f { width: 140px; height: 140px; margin-left: 20px; transform: scaleX(-1); filter: drop-shadow(0 0 6px var(--g1)) drop-shadow(0 0 6px var(--g2)); }
    .m-slots .ms .art { position: absolute; left: 20px; top: 0; width: 140px; height: 140px; border-radius: 8px; overflow: hidden; } .m-slots .ms .art img { width: 100%; height: 100%; object-fit: cover; }
    .m-banner { position: absolute; left: 0; right: 0; top: 560px; text-align: center; }
    .m-banner div { display: inline-block; background: #000d; padding: 40px 70px; border-radius: 30px; border: 4px solid #fff4; }
    .m-banner h1 { margin: 0; font-size: 84px; font-weight: 700; letter-spacing: .08em; }
    .m-banner p { margin: 14px 0 0; font-size: 40px; font-weight: 700; letter-spacing: .12em; color: #ccc; }
    .m-pop { position: absolute; left: 0; right: 0; top: 700px; text-align: center; font-family: var(--o-din); font-weight: 700; font-size: 150px; text-shadow: 0 0 30px #fff8; pointer-events: none; animation: mgpop 1.3s ease-out forwards; }
    .m-levels { position: absolute; left: 60px; right: 60px; top: 300px; bottom: 260px; overflow-y: auto; display: grid; grid-template-columns: 1fr 1fr; gap: 18px; align-content: start; }
    .m-levels button { text-align: left; display: grid; grid-template-columns: 90px 1fr; align-items: center; gap: 4px 18px; padding: 18px 22px; border-radius: 20px; border: 3px solid #ffffff30; background: #000a; color: #fff; cursor: pointer; }
    .m-levels button b { grid-row: span 2; font-family: var(--o-din); font-size: 64px; text-align: center; }
    .m-levels button span { font-size: 34px; font-weight: 700; letter-spacing: .06em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .m-levels button i { font-style: normal; font-size: 26px; color: #bbb; letter-spacing: .08em; } .m-levels button i em { font-style: normal; color: #f5c542; }
    .m-levels button.sel { border-color: #fff; box-shadow: 0 0 30px #fff6; } .m-levels button.locked { opacity: .3; }
  </style>`);

  let mPrevScore = null, mLastPlay = null;
  function mixgridBg(m) {
    const lv = m.level || {};
    if (!lv.bg) return '';
    const z = lv.zoom || 1;
    // MixGridBackground: texture scale (0.5625/zoom, 1/zoom), offset (cx - 0.28125/zoom, cy - 0.5/zoom)
    const w = 1080 / (0.5625 / z), h = 1920 / (1 / z), ox = ((lv.cx ?? .5) - 0.28125 / z) * w, oy = (1 - (lv.cy ?? .5) - 0.5 / z) * h;
    return `background-image:url('/art/${encodeURIComponent(lv.bg)}');background-size:${w.toFixed(0)}px ${h.toFixed(0)}px;background-position:${(-ox).toFixed(0)}px ${(-oy).toFixed(0)}px`;
  }
  function mixgrid(state, m) {
    const lv = m.level || {}, bgImg = lv.bg ? `background-image:url('/art/${encodeURIComponent(lv.bg)}')` : '';
    const lobby = m.phase === 'lobby' || m.phase === 'over';
    const base = layer('m-base');
    setHtml(base, `<div class="m-bg" style="${bgImg}"></div><div class="o-dots" style="opacity:.25"></div>`);
    const port = layer('m-port', 'm-port');
    if (lobby) {
      const r = m.result;
      const lvls = (m.levels || []).map(l => `<button class="${l.i === m.levelIdx ? 'sel' : ''}${l.unlocked ? '' : ' locked'}" data-a="m-level" data-i="${l.i}"><b>${l.i + 1}</b><span>${esc(l.name)}</span>
        <i>${l.beaten ? `<em>${'★'.repeat(l.stars)}</em> ${l.best.toLocaleString()}` : l.skipped ? 'SKIPPED' : l.unlocked ? (l.rounds > 1 ? '3 ROUNDS' : 'NEW') : 'LOCKED'}</i></button>`).join('');
      setHtml(port, `<div class="pbg" style="${mixgridBg(m)}"></div><div class="pshade"></div>
        <div class="o-topbar" style="left:0;right:0;width:1080px"><div class="l" style="left:50px"><img data-a="mn-go" data-to="modes" src="/ui/sys_back" alt=""></div>MIXGRID<div class="r" style="right:50px"></div></div>
        ${r ? `<div class="o-center" style="top:180px;justify-content:flex-start;gap:20px"><h1 style="margin:0;font-size:96px;font-weight:700;letter-spacing:.06em">${r.passed ? 'LEVEL PASSED' : 'LEVEL FAILED'}</h1>
            <div style="font-size:110px;color:#f5c542;letter-spacing:.2em">${[0, 1, 2].map(i => `<span style="${i < r.stars ? '' : 'color:#333'}">★</span>`).join('')}</div>
            <div style="font-size:40px;font-weight:700;letter-spacing:.14em">SCORE${r.newBest ? ' · NEW BEST' : ''}</div><div style="font-family:var(--o-din);font-weight:700;font-size:150px;line-height:1">${m.score.toLocaleString()}</div>
            <div style="display:flex;gap:30px;margin-top:40px;pointer-events:auto">${r.hasNext ? `<div class="pill solid o-click" data-a="m-next" style="font-size:40px;padding:24px 50px">NEXT LEVEL</div>` : ''}
              <div class="pill o-click" data-a="m-retry" style="font-size:40px;padding:24px 50px">RETRY</div>${r.canSkip ? `<div class="pill o-click" data-a="m-skip" style="font-size:40px;padding:24px 50px">SKIP</div>` : ''}
              <div class="pill o-click" data-a="m-levels" style="font-size:40px;padding:24px 50px">LEVELS</div></div></div>`
          : `<div class="o-center" style="top:150px;justify-content:flex-start;gap:6px"><div style="font-size:64px;font-weight:700;letter-spacing:.06em">${lv.number ? lv.number + '. ' : ''}${esc(lv.name || '')}</div>
            <div style="font-size:34px;font-weight:700;letter-spacing:.12em;color:#bbb">${esc(lv.sub || '')}</div></div>
            <div class="m-levels">${lvls}</div>
            <div class="o-center" style="top:auto;bottom:0;height:250px;gap:16px"><div class="o-heavy o-click" data-a="start" style="font-size:64px;pointer-events:auto">PRESS<span class="o-dmpill"></span>TO PLAY</div>
              <div class="o-heavy o-click" data-a="m-unlock" style="font-size:30px;letter-spacing:.14em;pointer-events:auto;color:#bbb">${m.unlockAll ? 'ALL LEVELS UNLOCKED' : 'UNLOCK ALL'}</div></div>`}`);
      return;
    }
    // ---- in game ----
    const g = m.goal || {};
    const goalHtml = m.encore ? `<div class="row"><div class="n" style="font-size:54px">TIME</div><div class="n">${Math.ceil(m.encoreLeft || 0)}</div></div>`
      : (g.items || []).map(it => {
          const done = it.cur >= it.target;
          const icon = it.type === 'Score' ? '' : it.type === 'Targets' ? `<img src="/ui/goal_target">` : (SHADOW_CARD[it.type] ? `<img src="/ui/${SHADOW_CARD[it.type]}">` : SHADOW_INST[(it.type || '').toLowerCase()] ? `<img src="/ui/${SHADOW_INST[it.type.toLowerCase()]}" style="filter:invert(1)">` : /^Level(\d)/.test(it.type) ? `<img src="/ui/shadow_power_${it.type.slice(5)}">` : '');
          return it.type === 'Score' ? `<div class="row"><div class="box">${done ? '<img class="ck" src="/ui/sys_check_mark">' : ''}</div><div class="n">${it.target.toLocaleString()}</div></div>`
            : `<div class="row"><div class="box">${done ? '<img class="ck" src="/ui/sys_check_mark">' : icon}</div><div class="n">x${Math.max(0, it.target - it.cur)}</div></div>`;
        }).join('');
    const scoreUp = mPrevScore != null && m.score > mPrevScore; mPrevScore = m.score;
    const hand = m.hand || [];
    const handHtml = `<div class="left">${m.deckLeft}<small>DECK</small></div>
      ${hand[0] ? `<div class="hc" style="--c:${COL[hand[0].type] || '#fff'}"><img src="/art/${encodeURIComponent(hand[0].image)}" alt=""><div class="cap" style="bottom:auto;top:395px">${esc(hand[0].name).toUpperCase()}</div></div>` : `<div class="pill o-click" data-a="m-finish" style="font-size:40px;padding:24px 40px;align-self:center">END LEVEL</div>`}
      ${hand[1] ? `<div class="hc deck" data-a="m-swap" style="--c:${COL[hand[1].type] || '#fff'}"><img src="/art/${encodeURIComponent(hand[1].image)}" alt=""></div>` : ''}`;
    const slots = state.pads.map((p, k) => { const c = p.top, gl = SLOT_COL[k];
      return `<div class="ms" style="--g1:${gl[0]};--g2:${gl[gl.length - 1]}">${c ? `<div class="art"><img src="/art/${encodeURIComponent(c.image)}" alt=""></div>` : `<img class="f" src="/ui/shell_slot_0${4 - k}" alt="">`}</div>`; }).join('');
    const banner = m.phase === 'reveal' ? `<div class="m-banner"><div><h1>ROUND ${m.round + 1}/${m.rounds}</h1><p>${esc(g.text || '').toUpperCase()}</p></div></div>`
      : m.phase === 'goal' ? `<div class="m-banner"><div><h1>GOAL COMPLETE!</h1><p>BONUS +${(m.bonus || 0).toLocaleString()}</p></div></div>` : '';
    if (!port.querySelector('.m-grid') || port._lv !== lv.name) {
      port._lv = lv.name;
      port.innerHTML = `<div class="pbg"></div><div class="pshade"></div><div class="m-top"></div><div class="m-grid"></div><div class="m-hand"></div><div class="m-slots"></div><div class="m-ban"></div>`;
    }
    port.querySelector('.pbg').setAttribute('style', mixgridBg(m));
    setHtml(port.querySelector('.m-top'), `<div class="m-score"><small>SCORE</small><b>${m.score.toLocaleString()}</b><i>×${m.mult}</i></div>
      <div class="m-goal"><div class="rnd">${m.encore ? 'ENCORE' : `ROUND ${m.round + 1}/${m.rounds}`}</div>${goalHtml}</div>`);
    setHtml(port.querySelector('.m-hand'), handHtml);
    setHtml(port.querySelector('.m-slots'), slots);
    setHtml(port.querySelector('.m-ban'), banner);
    // the grid (path drawing reuses mixgrid.js)
    const gridBox = port.querySelector('.m-grid');
    if (!gridBox._dragging) {
      const active = hand[0];
      const cells = (m.grid || []).flat().filter(Boolean).map(c => {
        const x = c.x * 145, y = (6 - c.y) * 145, k = `data-x="${c.x}" data-y="${c.y}" style="left:${x}px;top:${y}px`;
        const cls = ['cell']; if (c.tg) cls.push('tg'); if (c.wall && c.noEnd) cls.push('wall'); if (c.mustEnd) cls.push('bump'); if (c.absorb) cls.push('absorb');
        if (c.card) { const cc = c.card.c; return `<div class="${cls.join(' ')} gcard${c.card.slot < 0 ? ' mute' : ''}" ${k};--c:${COL[cc.type] || '#fff'}"><img src="/art/${encodeURIComponent(cc.image)}" alt="" draggable="false"><span class="sl">${c.card.slot >= 0 ? c.card.slot + 1 : ''}</span></div>`; }
        if (!c.t) return `<div class="${cls.join(' ')}" ${k}"></div>`;
        cls.push('req');
        if (active && window.MixGrid && window.MixGrid.options(active).some(o => window.MixGrid.sat(c.t, active, o)) && !c.noEnd) cls.push('ok');
        const ic = TILE_ICON({ type: c.t });
        return `<div class="${cls.join(' ')}" ${k}">${ic.img}${ic.label && ic.label !== 'ANY COLOR' ? `<div class="lab">${ic.label}</div>` : ''}</div>`;
      }).join('');
      const html = `<div class="grid">${cells}</div>`;
      if (gridBox._h !== html) {
        gridBox.innerHTML = html; gridBox._h = html; gridBox._m = m;
        gridBox.addEventListener && !gridBox._wired && (gridBox._wired = true, gridBox.addEventListener('mgmsg', e => flashMsg(e.detail)));
        window.MixGrid.bindGrid(gridBox, () => gridBox._m, (action, arg) => host(action, { arg: String(arg) }));
        gridBox._flush = () => {};
      }
      gridBox._m = m;
    }
    // score pop for each play
    if (m.last && (!mLastPlay || m.last.id !== mLastPlay)) {
      if (mLastPlay != null && m.last.total) { const d = document.createElement('div'); d.className = 'm-pop'; d.textContent = `+${m.last.total.toLocaleString()}${m.last.mult > 1 ? ` ×${m.last.mult}` : ''}`; port.appendChild(d); setTimeout(() => d.remove(), 1400);
        for (const [x, y] of m.last.cells) gridBox.querySelector(`.cell[data-x="${x}"][data-y="${y}"]`)?.classList.add('flash'); }
      mLastPlay = m.last.id;
    }
  }
  function flashMsg(t) { const f = document.getElementById('flash'); if (!f) return; f.textContent = t; f.classList.add('show'); clearTimeout(f._t); f._t = setTimeout(() => f.classList.remove('show'), 2400); }

  // ======================================================================================
  // Menus: main, mode select, top bar, collection
  // Canvas 1280x720 → stage x1.5.
  // ======================================================================================
  document.head.insertAdjacentHTML('beforeend', `<style>
    .mn-btn { position: absolute; display: flex; flex-direction: column; align-items: center; cursor: pointer; }
    .mn-btn .ic { background: #fff; -webkit-mask: center / contain no-repeat; mask: center / contain no-repeat; }
    .mn-btn .lb { position: absolute; left: -100px; right: -100px; text-align: center; font-size: 54px; font-weight: 400; letter-spacing: .16em; white-space: nowrap; }
    .mn-btn .pc { position: absolute; left: -100px; right: -100px; display: flex; justify-content: center; align-items: center; gap: 12px; font-size: 36px; letter-spacing: .1em; }
    .mn-btn .pc i { width: 48px; height: 48px; background: #fff; -webkit-mask: center / contain no-repeat; mask: center / contain no-repeat; }
    .mn-btn.dim { opacity: .35; cursor: default; }
    .mn-play { position: absolute; left: 772px; top: 285px; width: 375px; height: 375px; cursor: pointer; }
    .mn-play .card { position: absolute; inset: 0; -webkit-mask: url('/ui/sys_blank_card') center / contain no-repeat; mask: url('/ui/sys_blank_card') center / contain no-repeat;
      background: linear-gradient(135deg, #ff5b5b, #ffb03b, #fff35c, #5cff9d, #4bd5ff, #b06bff, #ff5bb5); background-size: 400% 400%; animation: mn-grad 9s ease-in-out infinite alternate; }
    @keyframes mn-grad { 0% { background-position: 0% 50%; } 100% { background-position: 100% 50%; } }
    .mn-play img { position: absolute; inset: 0; width: 100%; height: 100%; }
    .mn-play .lb { position: absolute; left: -100px; right: -100px; top: 450px; text-align: center; font-size: 54px; letter-spacing: .16em; }
    .mn-top-ic { height: 54px; cursor: pointer; }
    .cn-ic { display: grid; place-items: center; height: 54px; color: #fff; cursor: pointer; }
    .cn-ic svg { pointer-events: none; }
    .col-wrap { position: absolute; left: 0; right: 0; top: 140px; height: 800px; overflow-x: auto; overflow-y: hidden; scrollbar-width: none; }
    .col-wrap::-webkit-scrollbar { display: none; }
    .col-row { display: flex; gap: 60px; padding: 0 80px; height: 100%; align-items: flex-start; }
    .col-stats { flex: none; width: 520px; padding-top: 10px; }
    .col-stats h3 { margin: 0; font-size: 36px; font-weight: 800; letter-spacing: .1em; } .col-stats h4 { margin: 0 0 30px; font-size: 34px; font-weight: 400; letter-spacing: .1em; }
    .col-stats .st { display: flex; align-items: center; gap: 30px; margin-bottom: 40px; }
    .col-stats .st i { width: 170px; height: 220px; background: #fff; -webkit-mask: center / contain no-repeat; mask: center / contain no-repeat; }
    .col-stats .st b { font-family: var(--o-din); font-size: 110px; font-weight: 700; } .col-stats .st small { font-family: var(--o-din); font-size: 50px; }
    .col-grp { flex: none; padding-top: 10px; }
    .col-grp h3 { margin: 0; font-size: 36px; font-weight: 800; letter-spacing: .1em; } .col-grp h4 { margin: 0 0 30px; font-size: 34px; font-weight: 400; letter-spacing: .1em; }
    .col-tiles { display: flex; gap: 60px; }
    .col-tile { position: relative; width: 360px; cursor: pointer; text-align: center; }
    .col-tile .card { position: relative; width: 360px; height: 470px; background: center / 100% 100% no-repeat; }
    .col-tile .card i { position: absolute; left: 50%; top: 50%; width: 220px; height: 220px; margin: -110px 0 0 -110px; background: #000; -webkit-mask: center / contain no-repeat; mask: center / contain no-repeat; }
    .col-tile .nm { margin-top: 14px; font-size: 36px; font-weight: 800; letter-spacing: .1em; }
    .col-tile .ct { font-size: 28px; letter-spacing: .1em; color: #bbb; }
    .col-tile.on .card { filter: drop-shadow(0 0 22px #fff8); }
    .col-tile:not(.on) { opacity: .55; }
  </style>`);

  document.head.insertAdjacentHTML('beforeend', `<style>
    .bd-pulse { animation: bd-pulse 1.2s ease-in-out infinite; } @keyframes bd-pulse { 50% { opacity: .35; } }
    .bd-batt { position: absolute; left: 1440px; top: 140px; width: 110px; height: 52px; z-index: 40; cursor: pointer; box-sizing: border-box;
      border: 5px solid #fff; border-radius: 9px; padding: 5px; }
    .bd-batt::after { content: ""; position: absolute; right: -14px; top: 13px; width: 8px; height: 16px; background: #fff; border-radius: 0 3px 3px 0; }
    .bd-batt i { display: block; height: 100%; border-radius: 3px; }
    .bd-batt.crit { animation: bd-crit .5s steps(2) infinite; } @keyframes bd-crit { 50% { opacity: .15; } }
    .bd-unk { position: absolute; left: 0; right: 0; top: 150px; z-index: 40; display: flex; justify-content: center; gap: 30px; }
    .bd-unk span { display: flex; align-items: center; gap: 14px; padding: 12px 26px; background: #000d; border: 3px solid #ff3b3b; border-radius: 40px; font-size: 30px; font-weight: 800; letter-spacing: .1em; cursor: pointer; }
    .bd-unk img { height: 40px; }
    .bd-panel { position: absolute; left: 375px; top: 180px; width: 1170px; padding: 70px 60px 60px; box-sizing: border-box; background: #1c1c1c; border: 4px solid #fff; border-radius: 8px; text-align: center; }
    .bd-panel .x { position: absolute; right: -30px; top: -30px; width: 66px; cursor: pointer; }
    .bd-panel .ex { width: 70px; height: 70px; margin: 0 auto 26px; border-radius: 50%; background: #fff; color: #000; font-size: 52px; font-weight: 900; line-height: 70px; }
    .bd-panel .l1 { font-size: 36px; letter-spacing: .14em; margin-bottom: 70px; }
    .bd-panel .l2 { font-size: 82px; font-weight: 800; letter-spacing: .1em; line-height: 1.25; }
    .bd-panel .l3 { font-size: 26px; letter-spacing: .08em; color: #999; margin-top: 50px; }
    .cl-wrap { position: absolute; left: 0; right: 0; top: 150px; bottom: 30px; display: flex; flex-direction: column; align-items: center; text-align: center; }
    .cl-expl { font-size: 30px; line-height: 1.35; letter-spacing: .03em; color: #ccc; max-width: 1300px; }
    .cl-counts { display: flex; gap: 160px; margin-top: 26px; } .cl-counts div { display: flex; flex-direction: column; align-items: center; }
    .cl-counts b { font-family: var(--o-din); font-size: 150px; font-weight: 700; line-height: 1; } .cl-counts span { font-size: 34px; font-weight: 800; letter-spacing: .14em; }
    .cl-add { margin-top: 26px; padding: 18px 110px; border-radius: 60px; background: #fff; color: #000; font-size: 44px; font-weight: 800; letter-spacing: .2em; cursor: pointer; }
    .cl-add.off { opacity: .25; pointer-events: none; }
    .cl-dl { margin-top: 14px; width: auto; padding: 0 40px; }
    .st-list .st-row.sub { font-size: 32px; height: 84px; padding-left: 60px; text-transform: none; }
    .cc-ov { position: absolute; inset: 0; z-index: 80; background: #000c; display: grid; place-items: center; }
    .cc-box { display: flex; flex-direction: column; align-items: center; gap: 22px; padding: 50px 70px; background: #1b1b22; border-radius: 18px; max-width: 1100px; text-align: center; font-size: 34px; letter-spacing: .04em; }
    .cc-box b { font-size: 44px; } .cc-box small { font-size: 26px; opacity: .7; }
    .cc-btns { display: flex; gap: 40px; } .cc-btns i { font-style: normal; padding: 14px 44px; border: 2px solid #fff; border-radius: 40px; cursor: pointer; }
    .cl-gate { position: absolute; left: 0; right: 0; top: 100px; text-align: center; font-size: 26px; line-height: 1.35; color: #ffd84a; letter-spacing: .02em; }
    .cl-gate + .cl-wrap, .cl-gate + .cl-gate + .cl-wrap { top: 200px; }
    .cl-msg { min-height: 90px; margin-top: 18px; font-size: 32px; letter-spacing: .06em; max-width: 1300px; } .cl-msg.err { color: #ff3b3b; }
    .cl-slots { display: flex; gap: 40px; margin-top: 6px; } .cl-slot { width: 230px; height: 150px; display: flex; justify-content: center; }
    .cl-card { width: 96px; height: 134px; margin: 0 -22px; border-radius: 8px; overflow: hidden; background: #333; box-shadow: 0 0 0 4px #555; display: flex; align-items: center; justify-content: center; font-size: 60px; }
    .cl-card img { width: 100%; height: 100%; object-fit: cover; } .cl-card.own { box-shadow: 0 0 0 4px #1ec8a0; }
    .cl-total { margin-top: 10px; font-size: 26px; letter-spacing: .14em; color: #888; }
    .wt-flow { position: absolute; left: 160px; right: 160px; top: 168px; text-align: center; font-size: 50px; font-weight: 800; letter-spacing: .06em; text-shadow: 0 0 18px #000, 0 0 6px #000; z-index: 5; pointer-events: none; }
    .wt-last { position: absolute; left: 0; right: 0; top: 262px; text-align: center; font-size: 34px; letter-spacing: .16em; color: #ddd; z-index: 5; pointer-events: none; }
    @keyframes wt-fade { 0%, var(--hold) { opacity: 1; } 100% { opacity: 0; } }
    .wt-hint { position: absolute; left: 0; right: 0; top: 322px; text-align: center; font-size: 30px; letter-spacing: .06em; color: #ccc; z-index: 5; pointer-events: none; }
    .wt-call { position: absolute; left: 50%; transform: translateX(-50%); top: 745px; padding: 16px 40px 20px; border-radius: 22px; background: #000c; box-shadow: 0 0 0 5px #fff, 0 0 40px #fff6; text-align: center; z-index: 6; animation: wt-pulse 1.2s ease-in-out infinite alternate; pointer-events: none; }
    .wt-call .lb { font-size: 34px; font-weight: 800; letter-spacing: .18em; margin-bottom: 12px; }
    .wt-call .row { display: flex; gap: 36px; justify-content: center; align-items: flex-end; }
    .wt-call .it { display: flex; flex-direction: column; align-items: center; gap: 8px; font-size: 24px; letter-spacing: .1em; }
    .wt-call .it i { width: 90px; height: 90px; background: #fff; -webkit-mask-size: contain; mask-size: contain; -webkit-mask-repeat: no-repeat; mask-repeat: no-repeat; -webkit-mask-position: center; mask-position: center; }
    .wt-call .it img { height: 90px; }
    .wt-modes { position: absolute; left: 0; right: 0; top: 330px; display: flex; flex-direction: column; align-items: center; z-index: 6; pointer-events: none; }
    .wt-modes .lb { font-size: 40px; font-weight: 800; letter-spacing: .2em; margin-bottom: 20px; text-shadow: 0 0 14px #000; }
    .wt-modes .row { display: flex; gap: 90px; padding: 30px 60px; border-radius: 30px; background: #000d; }
    .wt-modes .md { display: flex; flex-direction: column; align-items: center; gap: 14px; font-size: 34px; font-weight: 800; letter-spacing: .14em; opacity: .35; transition: opacity .3s, transform .3s; }
    .wt-modes .md.on { opacity: 1; transform: scale(1.12); }
    .wt-modes .md img { width: 240px; height: 240px; object-fit: contain; }
    .wt-pop { position: absolute; left: 50%; top: 50%; transform: translate(-50%, -50%); width: 1100px; padding: 50px 60px; border-radius: 30px; background: #111; box-shadow: 0 0 0 4px #fff; text-align: center; }
    .wt-pop .hd { font-size: 56px; font-weight: 800; letter-spacing: .2em; } .wt-pop p { font-size: 38px; line-height: 1.4; margin: 30px 0 40px; }
    .wt-pop .bt { display: flex; gap: 40px; justify-content: center; } .wt-pop .bt span { padding: 18px 60px; border-radius: 50px; background: #fff; color: #000; font-size: 36px; font-weight: 800; letter-spacing: .14em; cursor: pointer; }
    .wt-pop .bt span:first-child { background: transparent; color: #fff; box-shadow: inset 0 0 0 4px #fff; }
    .wt-skip { position: absolute; right: 60px; bottom: 40px; padding: 14px 44px; border-radius: 40px; box-shadow: inset 0 0 0 4px #fff; font-size: 32px; font-weight: 800; letter-spacing: .16em; cursor: pointer; z-index: 7; background: #0008; }
    .o-slot.wt-lit { box-shadow: 0 0 0 8px var(--wt), 0 0 60px 10px var(--wt); border-radius: 18px; animation: wt-pulse .9s ease-in-out infinite alternate; }
    .wt-ring { box-shadow: 0 0 0 6px #fff, 0 0 40px 8px #fffa !important; border-radius: 14px; animation: wt-pulse .9s ease-in-out infinite alternate; }
    @keyframes wt-pulse { from { filter: brightness(1); } to { filter: brightness(1.6); } }
    .db-wrap { position: absolute; left: 0; right: 0; top: 170px; text-align: center; }
    .db-hint { font-size: 34px; letter-spacing: .16em; color: #aaa; }
    .db-big { margin-top: 30px; font-family: var(--o-din); } .db-big b { font-size: 190px; font-weight: 700; } .db-big span { font-size: 64px; }
    .db-lvl { font-size: 44px; letter-spacing: .12em; margin-top: 4px; }
    .db-types { display: flex; justify-content: center; gap: 46px; margin-top: 36px; }
    .db-types div { display: flex; flex-direction: column; align-items: center; gap: 8px; } .db-types img { height: 96px; } .db-types b { font-family: var(--o-din); font-size: 54px; }
    .db-status { margin-top: 30px; font-size: 52px; font-weight: 800; letter-spacing: .14em; }
    .db-dup { position: absolute; right: 120px; top: 120px; width: 260px; cursor: pointer; } .db-dup img { width: 260px; border-radius: 10px; box-shadow: 0 0 0 6px #ff3b3b; }
    .db-dup span { display: block; margin-top: 16px; font-size: 28px; font-weight: 800; letter-spacing: .1em; color: #ff3b3b; }
    .tv { position: absolute; left: 300px; top: 90px; width: 1320px; height: 880px; background: #1c1c1c; border: 4px solid #fff; border-radius: 8px; display: flex; flex-direction: column; }
    .tv .x { position: absolute; right: -34px; top: -34px; width: 70px; cursor: pointer; }
    .tv .hd { height: 100px; display: grid; place-items: center; font-size: 50px; letter-spacing: .14em; border-bottom: 2px solid #ffffff22; flex: none; }
    .tv .bd { flex: 1; overflow-y: auto; padding: 30px 50px; font-size: 30px; line-height: 1.45; letter-spacing: .02em; color: #ddd; } .tv .bd b { color: #fff; font-size: 34px; }
    .bdg { position: relative; flex: none; }
    .bdg .art { position: absolute; inset: 8%; background: center / cover no-repeat; -webkit-mask: url('/ui/badge_fill') center / contain no-repeat; mask: url('/ui/badge_fill') center / contain no-repeat; display: grid; place-items: center; }
    .bdg .art i { width: 55%; height: 55%; background: #fff; -webkit-mask: center / contain no-repeat; mask: center / contain no-repeat; }
    .bdg .fr { position: absolute; inset: 0; background: #777; -webkit-mask: url('/ui/badge_frame') center / contain no-repeat; mask: url('/ui/badge_frame') center / contain no-repeat; }
    .bdg.on .fr { background: linear-gradient(135deg, #FFF100, #EB0C6D, #00BFF3, #50B048); }
    .bdg:not(.on) .art { filter: grayscale(1) brightness(.45); }
    .bd-decks { position: absolute; left: 60px; right: 60px; top: 140px; display: flex; gap: 14px; overflow-x: auto; scrollbar-width: none; }
    .bd-decks div { flex: none; width: 96px; text-align: center; cursor: pointer; opacity: .45; } .bd-decks div.on { opacity: 1; }
    .bd-decks i { display: block; width: 84px; height: 84px; margin: 0 auto; background: #fff; -webkit-mask: center / contain no-repeat; mask: center / contain no-repeat; }
    .bd-decks small { font-size: 22px; letter-spacing: .08em; }
    .bd-grid { position: absolute; left: 90px; right: 90px; top: 300px; display: grid; grid-template-columns: 1fr 1fr; gap: 26px 60px; }
    .bd-cell { display: flex; align-items: center; gap: 26px; } .bd-cell b { display: block; font-size: 32px; letter-spacing: .1em; }
    .bd-cell span { display: block; font-size: 24px; color: #bbb; letter-spacing: .04em; margin-top: 6px; } .bd-cell em { font-style: normal; font-size: 22px; color: #FFC800; letter-spacing: .12em; }
    .bd-pop { position: absolute; left: 50%; bottom: 150px; transform: translateX(-50%); z-index: 70; display: flex; align-items: center; gap: 30px; padding: 24px 40px; background: #000d; border-radius: 20px; box-shadow: 0 0 0 4px #fff3; animation: bd-pop 6s ease forwards; pointer-events: none; }
    .bd-pop small { display: block; font-size: 22px; letter-spacing: .2em; color: #FFC800; } .bd-pop b { display: block; font-size: 52px; letter-spacing: .1em; } .bd-pop span { display: block; font-size: 26px; color: #ccc; max-width: 760px; }
    @keyframes bd-pop { 0% { opacity: 0; transform: translateX(-50%) translateY(40px); } 8%, 85% { opacity: 1; transform: translateX(-50%) translateY(0); } 100% { opacity: 0; } }
    .gd-list { position: absolute; left: 40px; top: 140px; bottom: 30px; width: 520px; overflow-y: auto; scrollbar-width: thin; }
    .gd-h { font-size: 30px; font-weight: 800; letter-spacing: .14em; color: #aaa; margin: 24px 0 10px; }
    .gd-i { font-size: 32px; letter-spacing: .06em; padding: 14px 18px; border-radius: 10px; cursor: pointer; } .gd-i.on { background: #ffffff1a; }
    .gd-view { position: absolute; left: 600px; right: 40px; top: 140px; bottom: 30px; display: flex; flex-direction: column; align-items: center; }
    .gd-imgs { flex: 1; min-height: 0; display: flex; align-items: center; justify-content: center; gap: 30px; }
    .gd-imgs img { max-height: 470px; max-width: 100%; object-fit: contain; }
    .gd-txt { font-size: 34px; line-height: 1.4; letter-spacing: .03em; text-align: center; max-width: 1150px; } .gd-txt p { margin: 10px 0; }
    .gd-nav { display: flex; align-items: center; gap: 40px; margin-top: 16px; } .gd-nav span { font-size: 80px; cursor: pointer; line-height: 1; } .gd-nav span.off { opacity: .2; pointer-events: none; }
    .gd-nav .dots { display: flex; gap: 14px; } .gd-nav .dots i { width: 16px; height: 16px; border-radius: 50%; background: #555; } .gd-nav .dots i.on { background: #fff; }
    .hs { position: absolute; left: 400px; top: 110px; width: 1120px; height: 830px; box-sizing: border-box; padding: 6px; border-radius: 12px;
      background: linear-gradient(135deg, #FFF100, #EB0C6D 40%, #9b4dff 60%, #00BFF3 80%, #50B048); }
    .hs::before { content: ""; position: absolute; inset: 6px; background: #1c1c1c; border-radius: 8px; }
    .hs > * { position: relative; }
    .hs .x { position: absolute; right: -34px; top: -34px; width: 70px; cursor: pointer; z-index: 2; }
    .hs .hd { height: 110px; display: flex; align-items: center; justify-content: center; font-size: 56px; letter-spacing: .14em; border-bottom: 2px solid #ffffff22; }
    .hs .hd img { position: absolute; left: 34px; height: 56px; cursor: pointer; }
    .hs .ls { height: 700px; overflow-y: auto; padding: 0 40px; }
    .hs-row { display: grid; grid-template-columns: 130px 330px 1fr; align-items: center; height: 140px; border-bottom: 2px solid #ffffff22; }
    .hs-row .pl { font-family: var(--o-din); font-size: 56px; font-weight: 700; } .hs-row sup { font-size: .5em; }
    .hs-row b { font-family: var(--o-din); font-size: 96px; } .hs-row .nm { font-size: 42px; letter-spacing: .14em; display: flex; flex-direction: column; } .hs-row small { font-size: 28px; color: #bbb; }
    .mx-save:not(.can) { background: #6a6a6a; cursor: default; }
    .mx-msg { position: absolute; transform: translateX(-50%); z-index: 60; padding: 14px 26px; background: #fff; color: #000; border-radius: 10px; font-size: 30px; font-weight: 800; letter-spacing: .1em; white-space: nowrap; pointer-events: none; animation: mx-msg 1.6s ease forwards; }
    .mx-msg::before { content: ""; position: absolute; left: 50%; top: -12px; margin-left: -12px; border: 12px solid transparent; border-top: 0; border-bottom-color: #fff; }
    @keyframes mx-msg { 0% { opacity: 0; } 10%, 80% { opacity: 1; } 100% { opacity: 0; } }
    .mx-flash { position: absolute; inset: -3000px; z-index: 59; background: #fff; pointer-events: none; animation: mx-flash .6s ease-out forwards; }
    @keyframes mx-flash { from { opacity: 1; } to { opacity: 0; } }
    .mx-list { position: absolute; left: 160px; right: 120px; top: 150px; bottom: 30px; overflow-y: auto; scrollbar-width: thin; scrollbar-color: #fff3 transparent; }
    .mx-none { margin-top: 300px; text-align: center; font-size: 54px; letter-spacing: .16em; color: #888; }
    .mx-row { position: relative; height: 210px; border-bottom: 2px solid #ffffff1a; overflow: hidden; }
    .mx-slide { position: absolute; inset: 0; display: flex; align-items: center; gap: 36px; background: #050505; cursor: pointer; z-index: 1; transition: transform .25s cubic-bezier(.2,.8,.2,1); touch-action: pan-y; }
    .mx-row.open .mx-slide { transform: translateX(-260px); }
    .mx-th { display: flex; gap: 8px; flex: none; }
    .mx-th img, .mx-th i { width: 172px; height: 172px; object-fit: cover; border-radius: 4px; display: block; }
    .mx-th i { box-sizing: border-box; border: 5px solid var(--c); box-shadow: 0 0 10px var(--c), inset 0 0 10px var(--c); }
    .mx-tx { display: flex; flex-direction: column; gap: 10px; min-width: 0; }
    .mx-tx b { font-size: 50px; font-weight: 800; letter-spacing: .1em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .mx-tx span { font-size: 36px; letter-spacing: .1em; color: #9a9a9a; line-height: 1.25; white-space: nowrap; }
    .mx-del { position: absolute; right: 0; top: 19px; width: 240px; height: 172px; background: #f00; border-radius: 10px; display: grid; place-items: center; font-size: 48px; font-weight: 800; letter-spacing: .1em; cursor: pointer; }
    .mx-name { width: 1100px; background: none; border: 0; outline: 0; color: #fff; font: inherit; letter-spacing: inherit; text-align: center; text-transform: uppercase; cursor: text; }
    .mx-name:focus { text-transform: none; border-bottom: 3px solid #fff8; }
    .mx-exp { display: grid; place-items: center; height: 54px; cursor: pointer; }
    .mx-exp svg { pointer-events: none; }
    .mx-exp.busy { animation: mx-busy 1s ease-in-out infinite; cursor: progress; }
    @keyframes mx-busy { 50% { opacity: .3; } }
    .mx-pop { position: absolute; left: 460px; top: 330px; width: 1000px; padding: 60px; box-sizing: border-box; background: #1c1c1c; border-radius: 24px; text-align: center; box-shadow: 0 0 0 3px #fff2; }
    .mx-pop p { margin: 0 0 60px; font-size: 44px; letter-spacing: .1em; line-height: 1.4; }
    .mx-pop div { display: flex; gap: 60px; justify-content: center; }
    .mx-pop span { min-width: 260px; padding: 26px 0; border-radius: 60px; border: 4px solid #fff; font-size: 42px; font-weight: 800; letter-spacing: .14em; cursor: pointer; }
    .mx-pop span:first-child { background: #fff; color: #000; }
    /* Connect-a-phone popover (address + port + QR) */
    .cn-pop { top: 150px; }
    .cn-pop > div { display: block; }
    .cn-h { font-size: 46px; font-weight: 800; letter-spacing: .12em; margin-bottom: 20px; }
    .cn-sub { font-size: 30px; color: #bbb; letter-spacing: .03em; line-height: 1.4; margin-bottom: 24px; }
    .cn-url { font-size: 58px; font-weight: 800; letter-spacing: .03em; color: #fff; background: #000; border-radius: 16px; padding: 24px 20px; margin-bottom: 34px; word-break: break-all; }
    .cn-qr { margin: 0 auto 26px; width: fit-content; background: #fff; padding: 20px; border-radius: 16px; }
    .cn-qr img { display: block; width: 300px; height: 300px; image-rendering: pixelated; }
    .cn-alt:empty { display: none; }
    .cn-alt { font-size: 24px; color: #888; margin-bottom: 34px; }
    .cn-pop > div:last-child { display: flex; justify-content: center; }
  </style>`);
  window.__menu = null;
  let decksCache = null, decksAt = 0, mixesCache = null, mixesAt = 0;
  // slide a row left to show its DELETE button (SnapshotListElement); a short tap still opens the mix
  function bindMixRows(root) {
    root.querySelectorAll('.mx-row').forEach(row => {
      if (row._bound || !row.querySelector('.mx-del')) return; row._bound = true;
      const sl = row.querySelector('.mx-slide'); let d = null;
      sl.onpointerdown = e => { d = { x0: e.clientX, off: row.classList.contains('open') ? -260 : 0, moved: false }; sl.setPointerCapture(e.pointerId); };
      sl.onpointermove = e => {
        if (!d) return; const scale = stage.getBoundingClientRect().width / 1920, dx = (e.clientX - d.x0) / scale;
        if (Math.abs(dx) > 12) d.moved = true;
        if (d.moved) { sl.style.transition = 'none'; sl.style.transform = `translateX(${Math.max(-260, Math.min(0, d.off + dx))}px)`; }
      };
      sl.onpointerup = sl.onpointercancel = e => {
        if (!d) return; const scale = stage.getBoundingClientRect().width / 1920, dx = (e.clientX - d.x0) / scale;
        sl.style.transition = ''; sl.style.transform = '';
        if (d.moved) { row.classList.toggle('open', d.off + dx < -130); sl._swallow = true; setTimeout(() => sl._swallow = false, 50); }
        d = null;
      };
    });
  }
  // the viewer (Menu_SnapshotViewer): name at the top (tap to rename your own), slots + VU, credits, timeline
  let mxConfirm = false;
  function mixView(state, m) {
    const x = m.mix || {};
    setHtml(layer('mv-base'), `<div class="o-dots"></div>`);
    const top = layer('mv-top');
    if (!top.contains(document.activeElement)) {
      setHtml(top, topbar(x.isDefault ? esc(x.name).toUpperCase() : `<input class="mx-name" value="${esc(x.name)}" maxlength="40" spellcheck="false" data-id="${x.id}">`,
        `<img data-a="mx-back" src="/ui/sys_back" alt="back" class="mn-top-ic">`,
        `<span data-a="mx-export" class="mx-exp" title="Export to Documents/OpenMix Exports (.wav + mix file)">${EXPORT_SVG}</span>${x.isDefault ? '' : `<img data-a="mx-trash" src="/ui/sys_delete" class="mn-top-ic" alt="delete">`}`));
      const inp = top.querySelector('.mx-name');
      if (inp) {
        const commit = () => { const v = inp.value.trim(); if (v && v !== x.name) post('/api/mixes/rename', { id: inp.dataset.id, name: v }).then(() => { mixesCache = null; }); else inp.value = x.name; };
        inp.onkeydown = e => { if (e.key === 'Enter') inp.blur(); if (e.key === 'Escape') { inp.value = x.name; inp.blur(); } };
        inp.onblur = commit;
      }
    }
    setHtml(layer('mv-slots'), `<div class="o-slots" style="top:387px">${slotsHtml(state)}</div>`);
    setHtml(layer('f-ticker', 'f-ticker'), x.credit ? `<b>${esc(x.credit).toUpperCase()}</b>` : '');
    const bar = layer('f-bar', 'f-bar'); bar.style.height = '123px';
    setHtml(bar, `<div class="f-time" style="top:42px">${Array.from({ length: TB.loopBars || 0 }, (_, i) => `<i class="${i % 4 === 0 ? 'tall' : ''}"></i>`).join('')}</div>`);
    setHtml(layer('mv-pop'), mxConfirm ? `<div class="pz"><div class="dim" data-a="mx-no"></div><div class="mx-pop"><p>${LU('Snapshot_DeleteConfirm', 'Remove this mix?')}</p>
        <div><span data-a="mx-yes">YES</span><span data-a="mx-no">NO</span></div></div></div>` : '');
  }
  // SnapshotButton feedback: Snapshot_Captured / Snapshot_NotEnoughCards under the button, white snapshot_flash on success
  // export glyph (white, like the original's top-bar icons): arrow down into a tray
  const EXPORT_SVG = `<svg viewBox="0 0 48 48" width="54" height="54" fill="none" stroke="#fff" stroke-width="4.5" stroke-linecap="round" stroke-linejoin="round"><path d="M24 6v24M14 21l10 10 10-10"/><path d="M8 30v10h32V30"/></svg>`;
  // Wi-Fi glyph for the "connect a phone" button (currentColor: white on the dark top bars, black in the round o-icon)
  const WIFI_SVG = `<svg viewBox="0 0 48 48" width="54" height="54" fill="none" stroke="currentColor" stroke-width="4.5" stroke-linecap="round" stroke-linejoin="round"><path d="M7 20Q24 2 41 20"/><path d="M14 27Q24 14 34 27"/><path d="M20 33Q24 27 28 33"/><circle cx="24" cy="38.5" r="2" fill="currentColor" stroke="none"/></svg>`;
  function mixSaved(ok, text, btn, flash = true) {
    if (ok && flash) { const f = document.createElement('div'); f.className = 'mx-flash'; stage.appendChild(f); setTimeout(() => f.remove(), 700); mixesCache = null; }
    const r = btn.getBoundingClientRect(), sr = stage.getBoundingClientRect(), sc = sr.width / 1920;
    const t = document.createElement('div'); t.className = 'mx-msg'; t.textContent = text.toUpperCase();
    t.style.left = ((r.left + r.width / 2 - sr.left) / sc) + 'px'; t.style.top = ((r.bottom - sr.top) / sc + 16) + 'px';
    stage.appendChild(t); setTimeout(() => t.remove(), 1600);
  }
  const MODE_LIST = [
    { id: 'clash', icon: 'sys_clash', label: 'CLASH', x: 309, count: '2-4' },
    { id: 'party', icon: 'sys_party', label: 'PARTY', x: 744, count: '1-5' },
    { id: 'lanes', icon: 'icon_lanes', label: 'PUZZLE', x: 1176, count: '1' },
    { id: 'freestyle', icon: 'sys_studio', label: 'FREESTYLE', x: 1611, count: null },
  ];
  function topbar(title, left, right) {
    // the ? on the right of every top bar opens "Connect a phone" (address + port + QR)
    const connect = `<span data-a="connect" class="cn-ic" title="Connect a phone" aria-label="Connect a phone">${WIFI_SVG}</span>`;
    return `<div class="o-topbar">${left ? `<div class="l">${left}</div>` : ''}${title}<div class="r">${(right || '') + connect}</div></div>`;
  }
  const backBtn = to => `<img data-a="mn-go" data-to="${to}" src="/ui/sys_back" alt="back" class="mn-top-ic">`;
  function menuSlots(state) { return `<div class="o-slots" style="top:1012px">${slotsHtml(state)}</div>`; }

  // ---- Pause popup and Settings ----
  document.head.insertAdjacentHTML('beforeend', `<style>
    .pz { position: absolute; inset: 0; z-index: 50; }
    .pz .dim { position: absolute; inset: -3000px; background: #000000c9; }
    .pz .frame { position: absolute; left: 123px; right: 123px; top: 123px; bottom: 123px; border-radius: 30px; background: #fff; }
    .pz .bg { position: absolute; left: 129px; right: 129px; top: 129px; bottom: 129px; border-radius: 26px; background: #1a1a1a; }
    .pz h1 { position: absolute; left: 0; right: 0; top: 186px; margin: 0; text-align: center; font-size: 54px; font-weight: 400; letter-spacing: .2em; }
    .pz .b { position: absolute; top: 327px; width: 378px; height: 378px; cursor: pointer; }
    .pz .b i { position: absolute; inset: 0; background: #fff; -webkit-mask: center / contain no-repeat; mask: center / contain no-repeat; }
    .pz .b span { position: absolute; left: -60px; right: -60px; top: 440px; text-align: center; font-size: 54px; letter-spacing: .16em; white-space: nowrap; }
    .pz .b.dim2 { opacity: .35; cursor: default; }
    .pz .x { position: absolute; left: 1755px; top: 93px; width: 72px; height: 72px; border-radius: 50%; background: #fff; display: grid; place-items: center; cursor: pointer; }
    .pz .x img { width: 54px; filter: brightness(0); }
    .st-list { position: absolute; left: 240px; width: 1434px; top: 120px; bottom: 0; overflow-y: auto; scrollbar-width: none; }
    .st-list::-webkit-scrollbar { display: none; }
    .st-row { position: relative; height: 120px; display: flex; align-items: center; border-bottom: 3px solid #ffffff40; font-size: 54px; letter-spacing: .14em; }
    .st-row.off { opacity: .35; }
    .st-row.link { cursor: pointer; }
    .st-row .rt { position: absolute; right: 0; display: flex; align-items: center; gap: 24px; font-size: 44px; }
    .st-row .ck { width: 72px; height: 72px; border-radius: 50%; background: #fff; }
    .st-sl { position: relative; width: 594px; height: 120px; cursor: pointer; touch-action: none; }
    .st-sl .tr { position: absolute; left: 0; right: 0; top: 57px; height: 6px; border-radius: 3px; background: #4a4a4a; }
    .st-sl .fl { position: absolute; left: 0; top: 57px; height: 6px; border-radius: 3px; background: #fff; }
    .st-sl .hd { position: absolute; top: 33px; width: 54px; height: 54px; margin-left: -27px; border-radius: 50%; background: #fff; box-shadow: 0 0 16px #0008; }
    .st-ver { padding: 40px 0 80px; font-size: 27px; letter-spacing: .14em; color: #aaa; }
  </style>`);
  let settingsCache = null, pauseOpen = false;
  function pauseHtml() {
    return `<div class="pz"><div class="dim" data-a="pz-close"></div><div class="frame"></div><div class="bg"></div><h1>PAUSED</h1>
      <div class="b" data-a="pz-menu" style="left:291px"><i style="-webkit-mask-image:url('/ui/sys_home');mask-image:url('/ui/sys_home')"></i><span>${LU('Menu_MainMenu', 'MENU')}</span></div>
      <div class="b" data-a="pz-restart" style="left:771px"><i style="-webkit-mask-image:url('/ui/sys_restart_game');mask-image:url('/ui/sys_restart_game')"></i><span>RESTART</span></div>
      <div class="b" data-a="pz-guide" style="left:1251px"><i style="-webkit-mask-image:url('/ui/sys_text_guide');mask-image:url('/ui/sys_text_guide')"></i><span>GUIDE</span></div>
      <div class="x" data-a="pz-close"><img src="/ui/sys_close" alt="close"></div></div>`;
  }
  // ---- card content (as the original's Settings → Menu_Settings_CardCache): downloads are opt-in ----
  let ccInfo = null, ccAt = 0, ccPrompted = null;
  function ccDetails() {
    if (Date.now() - ccAt > 3000) { ccAt = Date.now(); fetch('/api/cardcontent').then(r => r.json()).then(d => { ccInfo = d; window.OrigUI.render(lastState); }).catch(() => {}); }
    return ccInfo || { owned: 0, downloaded: 0, storedMB: 0 };
  }
  function cardContentRows() {
    locLoad(); const d = ccDetails();
    const arrow = `<img src="/ui/sys_back" style="height:44px;transform:scaleX(-1)">`;
    return `<div class="st-row">${esc(clText('Menu_Settings_CardCache', 'Card art and music').toUpperCase())}</div>
        <div class="st-row sub">${esc(clText('Menu_Settings_CardCacheSize', 'On disk: {0} MB').replace('{0}', d.storedMB))}</div>
        <div class="st-row sub">${esc(clText('Menu_Settings_CardCacheDownloaded', 'Ready: {0} of {1} cards').replace('{0}', d.downloaded).replace('{1}', d.owned))}</div>
        <div class="st-row link sub${d.owned > d.downloaded ? '' : ' off'}" data-a="cc-dl">${esc(clText('Menu_Settings_CardCacheDownloadCollection', 'Get my cards'))}<div class="rt">${arrow}</div></div>
        <div class="st-row link sub${d.storedMB > 0 ? '' : ' off'}" data-a="cc-del">${esc(clText('Menu_Settings_CardCacheDeleteAll', 'Remove card art and music'))}<div class="rt">${arrow}</div></div>`;
  }
  // the download size prompt, progress overlay and errors, driven by state.gate.cards
  function cardContentFx(state) {
    const c = (state.gate && state.gate.cards) || {};
    if (c.state === 'confirm' && ccPrompted !== c.sizeMB) {
      ccPrompted = c.sizeMB; locLoad();
      confirmBox(clText('Menu_Settings_CardCacheDownloadCollectionConfirm', 'Fetch art and music for your cards ({0} MB)?').replace('{0}', c.sizeMB),
        () => post('/api/cardcontent/confirm', {}));
      const no = document.querySelectorAll('#cf-layer [data-a="cf-no"]'); no.forEach(n => n.addEventListener('click', () => post('/api/cardcontent/cancel', {})));
    }
    if (c.state !== 'confirm') ccPrompted = null;
    if (c.state === 'downloading' || c.state === 'estimating')
      return `<div class="cc-ov" data-a="cc-cancel"><div class="cc-box"><b>${esc(clText('CardCacheOverlay_DownloadingCards', 'Fetching card files…'))}</b>
        ${c.state === 'downloading' && c.totalMB > 0 ? `<span>${esc(clText('CardCacheOverlay_DownloadProgress', '{0}/{1} MB').replace('{0}', c.doneMB).replace('{1}', c.totalMB))}</span>` : ''}
        <small>${esc(clText('CardCacheOverlay_TapToCancel', 'Tap to stop'))}</small></div></div>`;
    if (c.state === 'error')
      return `<div class="cc-ov"><div class="cc-box"><span>${esc(clText(c.error, 'The card files could not be fetched.'))}</span>
        <div class="cc-btns"><i data-a="cc-dl">${esc(clText('CardCacheError_Retry', 'Retry'))}</i><i data-a="cc-cancel">OK</i></div></div></div>`;
    return '';
  }
  function settingsHtml() {
    if (!settingsCache) fetch('/api/settings').then(r => r.json()).then(d => { settingsCache = d; window.OrigUI.render(lastState); }).catch(() => {});
    const st = settingsCache || { music: 1, effects: 1, audio: true };
    const slider = (key, v, off) => `<div class="st-sl" ${off ? '' : `data-sl="${key}"`}><div class="tr"></div><div class="fl" style="width:${(v * 100).toFixed(1)}%"></div><div class="hd" style="left:${(v * 100).toFixed(1)}%"></div></div>`;
    return `<div class="o-dots"></div>${topbar('SETTINGS', backBtn('main'), '')}
      <div class="st-list">
        <div class="st-row${st.audio ? '' : ' off'}">${LU('Menu_Settings_MusicVolume', 'Music')}:<div class="rt">${slider('music', st.music, !st.audio)}</div></div>
        <div class="st-row${st.audio ? '' : ' off'}">${LU('Menu_Settings_EffectsVolume', 'Sound effects')}:<div class="rt">${slider('effects', st.effects, !st.audio)}</div></div>
        <div class="st-row${st.audio ? '' : ' off'}">${LU('Menu_Settings_CrowdVolume', 'Crowd')}:<div class="rt">${slider('crowd', st.crowd ?? 1, !st.audio)}</div></div>
        <div class="st-row link" data-a="st-lang">LANGUAGE<div class="rt">${({ en: 'ENGLISH', fr: 'FRANÇAIS', de: 'DEUTSCH', it: 'ITALIANO', 'es-es': 'ESPAÑOL (ES)', 'es-mx': 'ESPAÑOL (MX)', nl: 'NEDERLANDS', fi: 'SUOMI', no: 'NORSK', pl: 'POLSKI', 'pt-pt': 'PORTUGUÊS (PT)', 'pt-br': 'PORTUGUÊS (BR)', sv: 'SVENSKA' })[st.language || 'en'] || st.language}</div></div>
        <div class="st-row link" data-a="st-rem">${LU('Menu_Settings_GameplayReminders', 'Hints')}<div class="rt">${st.reminders === false ? 'OFF' : 'ON'}</div></div>
        <div class="st-row link" data-a="st-text" data-t="openmix-about">ABOUT OPENMIX<div class="rt"><img src="/ui/sys_back" style="height:44px;transform:scaleX(-1)"></div></div>
        <div class="st-row link" data-a="st-text" data-t="openmix-privacy">OPENMIX PRIVACY<div class="rt"><img src="/ui/sys_back" style="height:44px;transform:scaleX(-1)"></div></div>
        <div class="st-row link" data-a="st-text" data-t="openmix-licenses">OPENMIX LICENCES<div class="rt"><img src="/ui/sys_back" style="height:44px;transform:scaleX(-1)"></div></div>
        <div class="st-row link" data-a="st-text" data-t="credits">DROPMIX CREDITS (ORIGINAL GAME)<div class="rt"><img src="/ui/sys_back" style="height:44px;transform:scaleX(-1)"></div></div>
        <div class="st-row link" data-a="st-text" data-t="eula">DROPMIX EULA (ORIGINAL GAME, HISTORICAL)<div class="rt"><img src="/ui/sys_back" style="height:44px;transform:scaleX(-1)"></div></div>
        <div class="st-row link" data-a="st-text" data-t="privacy">DROPMIX PRIVACY POLICY (ORIGINAL GAME, HISTORICAL)<div class="rt"><img src="/ui/sys_back" style="height:44px;transform:scaleX(-1)"></div></div>
        ${(() => { const b = lastState.board || {}; return b.enabled ? `<div class="st-row${b.state === 'connected' ? '' : ' link'}" ${b.state === 'connected' ? 'data-a="bd-batt"' : 'data-a="bd-open"'}>${LU('Menu_Settings_BoardSettings', 'Board')}<div class="rt">${b.state === 'connected' ? `BATTERY ${b.battery >= 0 ? b.battery + '%' : '—'}` : 'NOT CONNECTED'}</div></div>` : ''; })()}
        <div class="st-row link" data-a="mn-go" data-to="decks">DECKS<div class="rt">COLLECTION <img src="/ui/sys_back" style="height:44px;transform:scaleX(-1)"></div></div>
        <div class="st-row off">${LU('Menu_Settings_DisconnectBoard', 'Unpair board')}<div class="rt"><div class="ck"></div></div></div>
        <div class="st-row off">${LU('Menu_BoardBatteryLevel', 'Board battery')}<div class="rt"><img src="/ui/sys_battery" style="height:60px;filter:invert(1)"></div></div>
        <div class="st-row off">FIRMWARE UPDATE</div>
        <div class="st-row off">${LU('Menu_Settings_GameplayReminders', 'Hints')}<div class="rt"><div class="ck"></div></div></div>
        <div class="st-row off">ACCOUNT</div>
${cardContentRows()}
        <div class="st-row off" style="justify-content:space-around">CREDITS<i style="width:3px;height:70px;background:#ffffff40"></i>EULA</div>
        <div class="st-row off">${LU('Menu_Settings_PrivacyPolicy', 'Privacy')}</div>
        <div class="st-ver">${LU('Menu_Settings_VersionApp', 'Version:')} ${esc(st.version || 'OpenMix').toUpperCase()}</div>
      </div>`;
  }
  function bindSliders() {
    stage.querySelectorAll('[data-sl]').forEach(el => {
      const key = el.dataset.sl;
      const set = (clientX, send) => {
        const r = el.getBoundingClientRect(), v = Math.max(0, Math.min(1, (clientX - r.left) / r.width));
        el.querySelector('.fl').style.width = (v * 100) + '%'; el.querySelector('.hd').style.left = (v * 100) + '%';
        if (settingsCache) settingsCache[key] = v;
        clearTimeout(el._t); el._t = setTimeout(() => post('/api/settings', { [key]: v }).then(r => r.json()).then(d => { settingsCache = d; }), send ? 0 : 80);
      };
      el.onpointerdown = e => { el.setPointerCapture(e.pointerId); el._drag = true; set(e.clientX); };
      el.onpointermove = e => { if (el._drag) set(e.clientX); };
      el.onpointerup = e => { el._drag = false; set(e.clientX, true); uiSfx('slider_'); };
    });
  }

  function renderMenu(state) {
    const scr = window.__menu, m = state.mode || {};
    let html = `<div class="o-dots"></div>`;
    if (scr === 'main' && !wtOfferChecked) {
      wtOfferChecked = true;
      fetch('/api/walkthrough/offer').then(r => r.json()).then(d => {
        if (d.offered) return;
        post('/api/walkthrough/offer', {});
        fetch('/api/loc/' + ((settingsCache && settingsCache.language) || 'current')).then(r => r.json()).catch(() => ({})).then(L =>
          confirmBox((L && L.Walkthrough_Offer) || 'New here? Take the guided tour?', startWalk));
      }).catch(() => {});
    }
    if (scr === 'main') {
      html += topbar('', `<img data-a="mn-close" src="/ui/sys_back" alt="back" class="mn-top-ic" title="Back to ${esc(m.title || 'the game')}">`,
        `${boardIcon(state)}<img data-a="mn-go" data-to="guide" src="/ui/sys_text_guide" class="mn-top-ic" alt="guide"><img data-a="mn-go" data-to="settings" src="/ui/sys_settings" class="mn-top-ic" alt="">`);
      // cards_button (-340,40) 260; play_button (0,45) 250; mixes_button (340,40) 220
      html += `<div class="mn-btn" data-a="mn-go" data-to="cards" style="left:255px;top:285px;width:390px;height:390px"><div class="ic" style="width:390px;height:390px;-webkit-mask-image:url('/ui/sys_card_hand');mask-image:url('/ui/sys_card_hand')"></div><div class="lb" style="top:418px">CARDS</div></div>
        <div class="mn-play" data-a="mn-go" data-to="modes"><div class="card"></div><img src="/ui/shadow_dm" alt=""><div class="lb">PLAY</div></div>
        <div class="mn-btn" data-a="mn-go" data-to="mixes" style="left:1305px;top:315px;width:330px;height:330px"><div class="ic" style="width:330px;height:330px;-webkit-mask-image:url('/ui/sys_mixes');mask-image:url('/ui/sys_mixes')"></div><div class="lb" style="top:388px">MIXES</div></div>`;
      html += menuSlots(state);
    } else if (scr === 'modes') {
      html += topbar(LT('Menu_ModeSelect', 'Modes').toUpperCase(), backBtn('main'), `<img src="/ui/sys_text_guide" class="mn-top-ic" alt="">`);
      html += MODE_LIST.map(x => `<div class="mn-btn" data-a="mn-mode" data-mode="${x.id}" style="left:${x.x - 187}px;top:292px;width:375px;height:375px">
          <div class="ic" style="width:375px;height:375px;-webkit-mask-image:url('/ui/${x.icon}');mask-image:url('/ui/${x.icon}')"></div>
          <div class="lb" style="top:400px">${x.label}</div>
          <div class="pc" style="top:466px"><i style="-webkit-mask-image:url('/ui/sys_player');mask-image:url('/ui/sys_player')"></i>${x.count ? x.count : `<i style="width:60px;-webkit-mask-image:url('/ui/sys_infinity_icon');mask-image:url('/ui/sys_infinity_icon')"></i>`}</div></div>`).join('');
      html += `<div class="o-heavy o-click" data-a="mn-mode" data-mode="mixgrid" style="position:absolute;left:0;right:0;top:860px;text-align:center;font-size:30px;letter-spacing:.2em;color:#888">MIXGRID · UNRELEASED</div>`;
      html += menuSlots(state);
    } else if (scr === 'cards') {
      html += topbar('CARDS', backBtn('main'), `<img src="/ui/sys_text_guide" class="mn-top-ic" alt=""><img src="/ui/sys_settings" class="mn-top-ic" alt="">`);
      const b = (x, icon, label, to) => `<div class="mn-btn${to ? '' : ' dim'}" ${to ? `data-a="mn-go" data-to="${to}"` : ''} style="left:${x - 165}px;top:300px;width:330px;height:330px">
          <div class="ic" style="width:330px;height:330px;-webkit-mask-image:url('/ui/${icon}');mask-image:url('/ui/${icon}')"></div><div class="lb" style="top:370px;line-height:1.2">${label}</div></div>`;
      html += b(510, 'sys_deckedit', 'DECK<br>BUILDER', 'deckbuilder') + b(960, 'sys_collection', 'COLLECTION', 'decks') + b(1410, 'sys_register', 'ADD TO<br>COLLECTION', 'claim');
      html += menuSlots(state);
    } else if (scr === 'mixes') {
      // Menu_SnapshotBrowser: newest first; 5 thumbnails at their slots (empty = the slot's neon outline), name,
      // long date + time (none for the shipped mixes); tap to play, slide left for DELETE (no confirm)
      if (!mixesCache || Date.now() - mixesAt > 5000) { mixesAt = Date.now(); fetch('/api/mixes').then(r => r.json()).then(d => { mixesCache = d; window.OrigUI.render(lastState); }).catch(() => {}); }
      html += topbar('MIXES', backBtn('main'), '');
      const list = mixesCache || [];
      const fmtDate = t => { const d = new Date(t); return `${d.toLocaleDateString('en-US', { weekday: 'long', month: 'long', day: 'numeric', year: 'numeric' })}<br>${d.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })}`; };
      html += `<div class="mx-list">${mixesCache && !list.length ? '<div class="mx-none">NO SAVED MIXES</div>' : ''}${list.map(x => `<div class="mx-row" data-id="${x.id}">
          <div class="mx-slide" data-a="mn-mix" data-id="${x.id}">
            <div class="mx-th">${x.slots.map((c, k) => c ? `<img src="/art/${encodeURIComponent(c.image)}" alt="" draggable="false">` : `<i style="--c:${GLOW[k][0]}"></i>`).join('')}</div>
            <div class="mx-tx"><b>${esc(x.name).toUpperCase()}</b>${x.time ? `<span>${fmtDate(x.time).toUpperCase()}</span>` : `<span>${esc(x.tempo)} BPM • ${esc(x.key)}</span>`}</div></div>
          ${x.isDefault ? '' : `<div class="mx-del" data-a="mx-del" data-id="${x.id}">DELETE</div>`}</div>`).join('')}</div>`;
    } else if (scr === 'guide') {
      html += guideHtml();
    } else if (scr === 'badges') {
      html += badgesHtml();
    } else if (scr === 'deckbuilder') {
      html += deckBuilderHtml();
    } else if (scr === 'claim') {
      html += claimHtml();
    } else if (scr === 'settings') {
      html = settingsHtml();
    } else if (scr === 'decks') {
      // Collection, used as the deck manager: lit decks are the ones virtual hands draw from
      if (!decksCache || Date.now() - decksAt > 8000) { decksAt = Date.now(); fetch('/api/decks').then(r => r.json()).then(d => { decksCache = d; window.OrigUI.render(lastState); }).catch(() => {}); }
      const d = decksCache;
      html += topbar('COLLECTION', backBtn('cards'), `<img src="/ui/sys_text_guide" class="mn-top-ic" alt="">`);
      if (d) {
        const total = d.decks.reduce((a, x) => a + x.count, 0);
        const s1 = LT('Season_1', 'Series 1').toUpperCase(), s2 = LT('Season_2', 'Series 2').toUpperCase(), packs = LT('Item_Playlist', 'Playlists').toUpperCase();
        const KIND = { Starter: [s1, LT('Item_Starter', 'Starter').toUpperCase()], Playlist: [s1, packs], Discovery: [s1, 'DISCOVERY PACKS'], 'Season 2': [s2, packs], Bonus: ['BAFFLER', 'BONUS CARDS'], Promo: ['PROMO', 'CARDS'] };
        const kinds = [...new Set(d.decks.map(x => x.kind))];
        const groups = kinds.map(k => {
          const g = d.decks.filter(x => x.kind === k), [h3, h4] = KIND[k] || [k.toUpperCase(), ''];
          return `<div class="col-grp"><h3>${esc(h3)}</h3><h4>${esc(h4)}</h4><div class="col-tiles">${g.map(x => `<div class="col-tile${x.active ? ' on' : ''}" data-a="mn-deck" data-icon="${x.icon}">
              <div class="card" style="background-image:url('/ui/${x.active ? 'icon_collection_deck_full' : 'icon_collection_deck_empty'}');${x.active ? 'filter:brightness(.78)' : ''}"><i style="-webkit-mask-image:url('/ui/${x.icon}');mask-image:url('/ui/${x.icon}');${x.active ? '' : 'background:#000'}"></i></div>
              <div class="nm">${esc(x.name).toUpperCase()}</div><div class="ct">${x.owned ? `${x.owned}/` : ''}${x.count} CARDS</div></div>`).join('')}</div></div>`;
        }).join('');
        html += `<div class="col-wrap"><div class="col-row"><div class="col-stats"><h3>MY DECKS</h3><h4>IN THE VIRTUAL DECK</h4>
            <div class="st"><i style="-webkit-mask-image:url('/ui/sys_collection_card_outline');mask-image:url('/ui/sys_collection_card_outline')"></i><div><b>${d.cardCount}</b><small>/${total}</small></div></div>
            <div class="st o-click" data-a="mn-go" data-to="claim" title="${esc(LT('Collection_InCollection', 'Owned'))}"><i style="-webkit-mask-image:url('/ui/sys_register');mask-image:url('/ui/sys_register')"></i><div><b>${d.ownedCount || 0}</b><small>/${d.totalCards || 0}</small></div></div>
            <div class="st o-click" data-a="mn-go" data-to="badges" title="Badges"><i style="width:150px;height:170px;-webkit-mask-image:url('/ui/icon_badgecount');mask-image:url('/ui/icon_badgecount')"></i><div><b>${badgesCache ? badgesCache.reduce((a, g) => a + g.unlocked, 0) : '…'}</b><small>/${badgesCache ? badgesCache.reduce((a, g) => a + g.badges.length, 0) : 224}</small></div></div>
            <div style="font-size:28px;letter-spacing:.1em;color:#bbb;line-height:1.5">TAP A DECK TO TURN IT ON OR OFF.<br>CARDS ON THE BOARD ALWAYS COUNT.</div>
            <div class="o-click" data-a="mn-decks-all" style="margin-top:30px;font-size:30px;font-weight:700;letter-spacing:.14em;text-decoration:underline">USE EVERY DECK</div></div>${groups}</div></div>`;
      }
    }
    const root = layer('mn-root'), sx = root.querySelector('.col-wrap')?.scrollLeft || 0;
    if (scr === 'decks') html += menuSlots(state);
    setHtml(root, html);
    const cw = root.querySelector('.col-wrap'); if (cw && sx) cw.scrollLeft = sx;
    if (scr === 'settings' && !stage.querySelector('[data-sl]')?._drag) bindSliders();
    if (scr === 'mixes') bindMixRows(root);
    if (cw && !cw._wheel) { cw._wheel = true; cw.addEventListener('wheel', e => { cw.scrollLeft += e.deltaY + e.deltaX; e.preventDefault(); }, { passive: false }); }
  }

  // ---- Menu_DeckVerification (Menu_DeckBuilder): stack your deck on the board; the five stacks count as one deck ----
  // ClashConfig deck rules (imported): the full deck (duplicates allowed) or the half deck (no duplicates), each with
  // its card count, level budget and copies; Wild and FX count as level 4. dbFull: which of the two is chosen.
  let dbState = null, dbAt = 0, dbFull = true, dbDupes = true, dbOpenSent = false;
  function deckBuilderHtml() {
    if (Date.now() - dbAt > 500) { dbAt = Date.now(); fetch('/api/deckbuilder').then(r => r.json()).then(d => { dbState = d; window.OrigUI.render(lastState); }).catch(() => {}); }
    if (!dbOpenSent) { dbOpenSent = true; post('/api/deckbuilder', { open: true }); }
    const R = TB.deckRules || { size: [0, 0], budget: [0, 0], maxCopies: [0, 0] }, ri = dbFull ? 0 : 1;
    const dbSize = R.size[ri], budget = R.budget[ri], maxCopies = R.maxCopies[dbDupes ? 0 : 1];
    const cards = (dbState?.cards || []).filter(c => c.known);
    const lv = c => c.type === 'Wild' || c.type === 'Fx' ? 4 : Math.max(1, c.power || 1);
    const level = cards.reduce((a, c) => a + lv(c), 0), over = level > budget;
    const counts = {}; cards.forEach(c => counts[c.cid] = (counts[c.cid] || 0) + 1);
    const dup = cards.find(c => counts[c.cid] > maxCopies);
    const types = ['Lead', 'Loop', 'Beat', 'Bass', 'Wild', 'Fx'].map(t => [t, cards.filter(c => c.type === t).length]);
    const status = dup || over ? ['OVER BUDGET', '#ff3b3b'] : cards.length < dbSize ? ['ADD MORE CARDS', '#fff'] : ['DECK READY', '#7dff8a'];
    const noBoard = dbState && dbState.board !== 'connected' && dbState.board !== 'off';
    return topbar(LT('Menu_DeckVerification', 'Deck check').toUpperCase(), `<img data-a="db-back" src="/ui/sys_back" alt="back" class="mn-top-ic">`, '') + `
      <div class="db-wrap">
        <div class="db-hint">${noBoard ? 'CONNECT THE DROPMIX BOARD, THEN ' : ''}STACK YOUR DECK ON THE BOARD</div>
        <div class="db-big"><b>${cards.length}</b><span>/${dbSize} CARDS</span></div>
        <div class="db-lvl" style="color:${over ? '#ff3b3b' : '#fff'}">${LU('IconSelect_StatsLevel', 'Levels: {0} of {1}', level, budget)}</div>
        <div class="db-types">${types.map(([t, n]) => `<div><img src="/ui/${CARD_SPR[t]}" alt=""><b>${n}</b></div>`).join('')}</div>
        <div class="db-status" style="color:${status[1]}">${status[0]}</div>
        ${dup ? `<div class="db-dup" data-a="db-dup"><img src="/art/${encodeURIComponent(dup.image)}" alt=""><span>${LU('Error_DuplicateCard', 'Already in the mix')}</span></div>` : ''}
      </div>
      ${drawer('deckbuilder', `${dbSize} CARDS`, `DUPLICATES: ${dbDupes ? 'YES' : 'NO'}`, true,
        [{ a: 'db-size', stops: [String(R.size[1]), String(R.size[0])], i: dbFull ? 1 : 0 }, { a: 'db-dupes', stops: ['NO', 'YES'], i: dbDupes ? 1 : 0 }])}`;
  }

  // ---- Menu_CollectionClaim: cards on the board count as Collection_InCollection / Collection_NotInCollection; ADD checks each new
  // card's signature through the board and keeps it in the local collection (data/collection.json) ----
  let clState = null, clAt = 0, clOpenSent = false;
  const clText = (k, f) => OMLoc.t(k, f);
  function locLoad() { OMLoc.load((settingsCache && settingsCache.language) || 'current'); }
  function claimHtml() {
    locLoad();
    if (Date.now() - clAt > 400) { clAt = Date.now(); fetch('/api/collection/claim').then(r => r.json()).then(d => { clState = d; window.OrigUI.render(lastState); }).catch(() => {}); }
    if (!clOpenSent) { clOpenSent = true; post('/api/collection/claim', { open: true }); }
    const d = clState || { inCollection: 0, notInCollection: 0, cards: [], maxPerSlot: 30, maxTotal: 150 };
    const noBoard = clState && clState.board !== 'connected';
    const expl = clText('Collection_ClaimExplanation', 'Put cards on the board to see which ones are already in your collection (up to {0} per slot, {1} in all).\n\nPress ADD to add the rest.')
      .replace('{0}', d.maxPerSlot).replace('{1}', d.maxTotal);
    const canAdd = !d.busy && d.notInCollection > 0 && !noBoard;
    const msg = d.busy ? clText('Collection_AddingCards', 'Reading your cards — leave them on the board until this finishes.')
      : noBoard ? 'CONNECT THE DROPMIX BOARD TO ADD CARDS.' : (d.message || '');
    const thumbs = [0, 1, 2, 3, 4].map(k => `<div class="cl-slot">${d.cards.filter(c => c.slot === k).map(c => c.image
        ? `<div class="cl-card${c.owned ? ' own' : ''}"><img src="/art/${encodeURIComponent(c.image)}?v=${(lastState.gate && lastState.gate.cards && lastState.gate.cards.version) || 0}" alt="" draggable="false"></div>`
        : `<div class="cl-card"><span>?</span></div>`).join('')}</div>`).join('');
    const gate = !!(lastState.gate && lastState.gate.needCards);
    return topbar(clText('Menu_CollectionClaim', 'My cards').toUpperCase(), gate ? '' : `<img data-a="cl-back" src="/ui/sys_back" alt="back" class="mn-top-ic">`, '') + `
      ${gate ? `<div class="cl-gate">Welcome to OpenMix. Connect your DropMix board and add at least one of your cards to start.</div>` : ''}
      <div class="cl-wrap">
        <div class="cl-expl">${locHtml(expl)}</div>
        <div class="cl-counts">
          <div><b>${d.inCollection}</b><span>${esc(clText('Collection_InCollection', 'Owned'))}</span></div>
          <div><b>${d.notInCollection}</b><span>${esc(clText('Collection_NotInCollection', 'New'))}</span></div>
        </div>
        <div class="cl-add${canAdd ? '' : ' off'}" data-a="cl-add">${esc(clText('Collection_ClaimCards', 'ADD'))}</div>
        <div class="cl-msg${d.error && !d.busy ? ' err' : ''}">${esc(msg)}${!d.busy && !d.error && d.newCards > 0 && d.message ? `<br><b>${esc(clText('Collection_NewCards', '{0} added').replace('{0}', d.newCards))}</b>` : ''}</div>
        <div class="cl-slots">${thumbs}</div>
        <div class="cl-total">${d.collection ?? 0} CARDS REGISTERED</div>
        ${(() => { const cc = ccDetails(); return cc.owned > cc.downloaded ? `<div class="cl-add cl-dl" data-a="cc-dl">${esc(clText('Menu_Settings_CardCacheDownloadCollection', 'Get my cards').toUpperCase())}</div>` : ''; })()}
      </div>`;
  }

  // ---- Walkthrough_TutorialEntry (Walkthrough): the host runs the voiced steps; the table shows the step's text
  // (kioskFlowText), the last card played (8 s, then a 2 s fade), the lit slot, the scene's highlights and the Set Up popup ----
  let wtOfferChecked = false;
  const wtText = (k, f) => OMLoc.t(k, f);
  const WT_COL = { Loop: '#EB0C6D', Beat: '#00BFF3', Bass: '#50B048', Lead: '#FFF100', Wild: '#ffffff' };
  function walkHtml(state) {
    const w = state.walk;
    if (!w) return '';
    locLoad();
    const on = new Set(w.on || []), tops = (state.pads || []).map(p => p.top);
    let h = '';
    if (on.has('removecards')) h += `<div class="wt-flow">${esc(wtText('Walkthrough_RemoveCards', 'Clear the board to begin'))}</div>`;
    else if (w.text) h += `<div class="wt-flow">${locHtml(wtText(w.text, w.textValue || ''))}</div>`;
    if (w.lastCard) h += `<div class="wt-last" style="animation:wt-fade ${w.lastFull + w.lastFade}s linear forwards;--hold:${(w.lastFull / (w.lastFull + w.lastFade) * 100).toFixed(1)}%" data-k="${w.lastCardId}">${esc(w.lastCard).toUpperCase()}</div>`;
    if (on.has('hint') && !on.has('removecards')) h += `<div class="wt-hint">Swap cards in and out, or press the board's button to shake up the mix.</div>`;
    // callouts for the scene objects the step switches on
    const callout = (label, inner) => `<div class="wt-call"><div class="lb">${esc(label).toUpperCase()}</div><div class="row">${inner}</div></div>`;
    const icons = tops.filter(Boolean);
    if (on.has('playlist') || on.has('playlistDetail'))
      h += callout(wtText('Collection_Playlist', 'Playlist'), [...new Set(icons.map(c => c.icon))].map(ic => `<div class="it"><i style="-webkit-mask-image:url('/ui/${ic}');mask-image:url('/ui/${ic}')"></i><span>${esc(DECK_NAME[ic] || ic)}</span></div>`).join('') || ['sweets', 'highness', 'theblade'].map(ic => `<div class="it"><i style="-webkit-mask-image:url('/ui/${ic}');mask-image:url('/ui/${ic}')"></i><span>${esc(DECK_NAME[ic] || ic.toUpperCase())}</span></div>`).join(''));
    else if (on.has('levels'))
      h += callout(wtText('Collection_Power', 'Level'), [1, 2, 3].map(n => `<div class="it"><img src="/ui/shadow_power_${n}" alt=""><span>${n}</span></div>`).join(''));
    else if (on.has('instruments') || on.has('instruments2'))
      h += callout(wtText('Walkthrough_InstrumentIcons', 'Instrument'), [...new Set(icons.flatMap(c => (c.instruments || []).map(x => String(x).toLowerCase())))].filter(x => INSTR.includes(x))
        .map(x => `<div class="it"><i style="-webkit-mask-image:url('/ui/instrument_${x}');mask-image:url('/ui/instrument_${x}')"></i><span>${x.toUpperCase()}</span></div>`).join(''));
    else if (on.has('fx') || on.has('fx2'))
      h += callout('FX', `<div class="it"><img src="/ui/sys_fx_card" alt="" style="height:170px"></div>`);
    if (on.has('modes'))
      h += `<div class="wt-modes"><div class="lb">${esc(wtText('Walkthrough_GameModes', 'Ways to play')).toUpperCase()}</div><div class="row">${MODE_LIST.filter(x => x.id !== 'lanes').map(x =>   // the scene's tutorial_<mode>_overview images
        `<div class="md${on.has(x.id) ? ' on' : ''}"><img src="/ui/tutorial_${x.id}_overview" alt=""><span>${x.label}</span></div>`).join('')}</div></div>`;
    if (w.popup === 'setup')
      h += `<div class="pz" style="z-index:68"><div class="dim"></div><div class="wt-pop"><div class="hd">${esc(wtText('Walkthrough_Setup', 'Before you start')).toUpperCase()}</div>
        <p>${esc(wtText('Walkthrough_DevicePlacement', 'Keep your phone or tablet on the board while you play.'))}</p>
        <div class="bt"><span data-a="wt-test">${esc(wtText('Walkthrough_TestSound', 'Sound check')).toUpperCase()}</span><span data-a="wt-pop">${esc(wtText('Walkthrough_Continue', 'Continue')).toUpperCase()}</span></div></div></div>`;
    if (w.skip) h += `<div class="wt-skip" data-a="wt-skip">${esc(wtText('Tutorial_FirstTime_DontWatch', 'Skip')).toUpperCase()}</div>`;
    return h;
  }
  // effects on the table itself: the lit slot (slotEffect in the slot's colour), the Save Mix button, levels / art rings
  function walkFx(state) {
    const w = state.walk, on = new Set((w && w.on) || []);
    stage.querySelectorAll('.wt-lit').forEach(el => { if (!w || +el.dataset.wt !== w.slot) { el.classList.remove('wt-lit'); el.removeAttribute('data-wt'); } });
    if (w && w.slot >= 0) {
      const el = stage.querySelectorAll('.o-slots .o-slot')[w.slot];
      if (el && !el.classList.contains('wt-lit')) { el.classList.add('wt-lit'); el.dataset.wt = w.slot; el.style.setProperty('--wt', WT_COL[w.slotType] || '#fff'); }
    }
    stage.querySelectorAll('.mx-save').forEach(b => b.classList.toggle('wt-ring', on.has('savemix')));
    stage.querySelectorAll('.o-slots .o-slot .vu').forEach(v => v.classList.toggle('wt-ring', on.has('levels')));
    stage.querySelectorAll('.o-slots .o-slot .art').forEach(v => v.classList.toggle('wt-ring', on.has('playlist') || on.has('instruments')));
  }

  // ---- GameOver_Leaderboard panel (34_puzzle_scores): rainbow border, trash to reset, place / score / name / difficulty ----
  let scoresOpen = false, textView = null;
  // Settings text pages (credits / EULA / privacy): <font="Avenir-Heavy">…</font> lines become headings
  function textViewHtml() {
    if (!textView) return '';
    const title = { 'openmix-about': 'ABOUT OPENMIX', 'openmix-privacy': 'OPENMIX PRIVACY', 'openmix-licenses': 'OPENMIX LICENCES',
      credits: 'DROPMIX CREDITS (ORIGINAL GAME)', eula: 'DROPMIX EULA (ORIGINAL GAME)', privacy: 'DROPMIX PRIVACY POLICY (ORIGINAL GAME)' }[textView.t];
    const body = esc(textView.txt).replace(/&lt;font=&quot;Avenir-Heavy&quot;&gt;([\s\S]*?)&lt;\/font&gt;/g, '<b>$1</b>').replace(/&lt;[^&]*&gt;/g, '').replace(/\n/g, '<br>');
    return `<div class="pz" style="z-index:66"><div class="dim" data-a="tv-x"></div><div class="tv"><img class="x" data-a="tv-x" src="/ui/sys_close" alt="close"><div class="hd">${title}</div><div class="bd">${body}</div></div></div>`;
  }
  // deck display names come from the imported deck list (the game's own card-set tables), not from this file
  const DECK_NAME = {};
  fetch('/api/decks').then(r => r.json()).then(d => { for (const x of d.decks || []) DECK_NAME[x.icon] = String(x.name || x.icon).toUpperCase(); }).catch(() => {});
  function scoresHtml(m) {
    if (!scoresOpen || !(m.id === 'party' || m.id === 'lanes')) return '';
    const sfx = n => ['th', 'st', 'nd', 'rd'][(n % 100 > 10 && n % 100 < 14) ? 0 : n % 10 < 4 ? n % 10 : 0] || 'th';
    const rows = (m.leaderboard || []).map((e, i) => `<div class="hs-row"><span class="pl">${i + 1}<sup>${sfx(i + 1)}</sup></span><b>${e.score.toLocaleString()}</b>
      <span class="nm">${esc(DECK_NAME[String(e.name || '').toLowerCase()] || e.name || '').toUpperCase()}<small>${m.id === 'lanes' ? (e.hard ? 'FEVER' : 'NORMAL') : esc(e.difficulty || '').toUpperCase()}${m.id === 'party' && e.players ? ` · ${e.players} PLAYER${e.players === 1 ? '' : 'S'}` : ''}</small></span></div>`).join('');
    return `<div class="pz" style="z-index:65"><div class="dim" data-a="hs-x"></div><div class="hs"><img class="x" data-a="hs-x" src="/ui/sys_close" alt="close">
      <div class="hd"><img data-a="hs-reset" src="/ui/sys_delete" alt="reset">${LU('GameOver_Leaderboard', 'BEST SCORES')}</div><div class="ls">${rows || '<div style="padding:60px;text-align:center;color:#888">NO SCORES YET</div>'}</div></div></div>`;
  }

  // ---- badges: shield = card art (or icon) through badge_fill, rim from badge_frame; locked ones greyed ----
  let badgesCache = null, badgesAt = 0, badgeDeck = 'sweets', badgeSeen = 0;
  const shield = (b, size) => `<div class="bdg${b.done ? ' on' : ''}" style="width:${size}px;height:${size}px">
      <div class="art" style="${b.image ? `background-image:url('/art/${encodeURIComponent(b.image)}')` : `background:#2a2a2a`}">${!b.image && b.icon ? `<i style="-webkit-mask-image:url('/ui/${b.icon}');mask-image:url('/ui/${b.icon}')"></i>` : ''}</div><b class="fr"></b></div>`;
  function badgesHtml() {
    if (!badgesCache || Date.now() - badgesAt > 4000) { badgesAt = Date.now(); fetch('/api/badges').then(r => r.json()).then(d => { badgesCache = d; window.OrigUI.render(lastState); }).catch(() => {}); }
    const all = badgesCache || [], g = all.find(x => x.playlist === badgeDeck) || all[0];
    return topbar('BADGES', backBtn('decks'), '') + `<div class="bd-decks">${all.map(x => `<div class="${x.playlist === (g && g.playlist) ? 'on' : ''}" data-a="bdg-deck" data-p="${x.playlist}">
        <i style="-webkit-mask-image:url('/ui/${x.playlist}');mask-image:url('/ui/${x.playlist}')"></i><small>${x.unlocked}/${x.badges.length}</small></div>`).join('')}</div>
      <div class="bd-grid">${g ? g.badges.map(b => `<div class="bd-cell">${shield(b, 190)}<div><b>${esc(b.title).toUpperCase()}</b><span>${esc(b.text)}</span>
        ${b.done ? '<em>UNLOCKED</em>' : b.count > 1 ? `<em>${b.progress}/${b.count}</em>` : ''}</div></div>`).join('') : ''}</div>`;
  }
  // unlock pop-up (badges pop centre-bottom: rainbow shield + title)
  function badgePopHtml(state) {
    const list = state.badges || [];
    const u = list.length ? list[list.length - 1] : null;
    if (!u) return '';
    if (u.id > badgeSeen) { badgeSeen = u.id; badgesCache = null; }
    return `<div class="bd-pop">${shield({ ...u, done: true }, 170)}<div><small>BADGE UNLOCKED</small><b>${esc(u.title).toUpperCase()}</b><span>${esc(u.text)}</span></div></div>`;
  }

  // ---- GUIDE (TutorialViewer): the original's tutorials, slide images and texts (data/guide.json + the text table) ----
  let guideData = null, guideSel = 2, guideSlide = 0;
  const locText = k => OMLoc.t(k, '');
  const locHtml = t => OMLoc.rich(t);
  function guideHtml() {
    locLoad();
    if (!guideData) { guideData = []; fetch('/api/guide').then(r => r.json()).then(d => { guideData = d; window.OrigUI.render(lastState); }).catch(() => {}); }
    const items = guideData || [], cur = items[guideSel] || {}, sl = (cur.slides || [])[guideSlide];
    const list = items.map((t, i) => t.header ? `<div class="gd-h">${locHtml(locText(t.title)).toUpperCase()}</div>`
      : `<div class="gd-i${i === guideSel ? ' on' : ''}" data-a="gd-pick" data-i="${i}">${locHtml(locText(t.title) || t.title)}</div>`).join('');
    const body = cur.video ? `<div class="gd-txt" style="text-align:center;margin-top:200px">${locHtml(locText(cur.title))}<br><br><span style="color:#888">The video guides aren't included in this port.</span></div>`
      : sl ? `<div class="gd-imgs">${(sl.images || []).map(n => `<img src="/ui/${encodeURIComponent(n)}" alt="">`).join('')}</div>
             <div class="gd-txt">${sl.texts.map(k => `<p>${locHtml(locText(k))}</p>`).join('')}</div>
             <div class="gd-nav"><span data-a="gd-prev" class="${guideSlide > 0 ? '' : 'off'}">‹</span><div class="dots">${cur.slides.map((x, i) => `<i class="${i === guideSlide ? 'on' : ''}"></i>`).join('')}</div><span data-a="gd-next" class="${guideSlide < cur.slides.length - 1 ? '' : 'off'}">›</span></div>` : '';
    return topbar('GUIDE', backBtn('main'), '') + `<div class="gd-list">${list}</div><div class="gd-view">${body}</div>`;
  }

  // ---- board connection UI (BoardConnectPanel, BoardBatteryDisplay, BoardBatteryInfoPopup, UnknownCardError) ----
  let bdPanel = false, bdPanelAuto = false, bdPopup = null, bdForcedSeen = false;
  const boardIcon = st => { const b = st.board || {}; return b.enabled && b.state !== 'connected' ? `<img data-a="bd-open" src="/ui/sys_bluetooth" class="mn-top-ic bd-pulse" alt="board" title="Connect the DropMix board">` : ''; };
  function drawBoardUi(state) {
    const b = state.board || {};
    let el = document.getElementById('bd-layer');
    if (!el) { el = document.createElement('div'); el.id = 'bd-layer'; }
    if (stage.lastElementChild !== el) stage.appendChild(el);
    if (b.state === 'connected' && bdPanel) bdPanel = false;               // auto-dismiss once the board is ready
    if (b.forcedOff && !bdForcedSeen) { bdForcedSeen = true; bdPopup = 'forced'; }
    if (b.state === 'connected') bdForcedSeen = false;
    let html = '';
    // battery: shown while connected and low (always in Settings); blinks when critical
    if (b.state === 'connected' && b.batteryLevel >= 0 && (b.batteryLow || window.__menu === 'settings')) {
      const lv = Math.max(0, Math.min(1, b.batteryLevel)), hue = Math.round(Math.max(0, Math.min(1, (lv - .25) / .5)) * 120);
      html += `<div class="bd-batt${b.batteryCritical ? ' crit' : ''}" data-a="bd-batt" title="DropMix board battery"><i style="width:${Math.round(lv * 100)}%;background:hsl(${hue},85%,50%)"></i></div>`;
    }
    // unknown cards (> 2 s in a slot): an error button per slot
    if ((b.unknownSlots || []).length)
      html += `<div class="bd-unk">${b.unknownSlots.map(k => `<span data-a="bd-unknown"><img src="/ui/warning" alt="">SLOT ${k + 1}: UNKNOWN CARD</span>`).join('')}</div>`;
    // the connect panel (05_connect)
    if (bdPanel && b.enabled) {
      // BoardConnectPanel texts per connection state (Menu_Searching/PressToConnect, MoveDeviceReason/MoveDevice,
      // ConnectionError/ConnectionErrorRetry — tap to retry)
      const st = b.state, retry = st === 'failed' || st === 'outofrange';
      const l1 = st === 'outofrange' ? LU('Menu_MoveDeviceReason', 'Board too far away') : st === 'failed' ? LU('Menu_ConnectionError', 'Board connection failed')
        : st === 'lost' ? LU('Menu_BoardDisconnected', 'Board connection lost') : LU('Menu_Searching', 'Looking for the board…');
      const l2 = st === 'outofrange' ? LU('Menu_MoveDevice', 'Bring this PC nearer to the board') : st === 'failed' ? LU('Menu_ConnectionErrorRetry', 'Tap to try again')
        : LU('Menu_PressToConnect', 'Then press <sprite name="dropmix"> on the board');
      html += `<div class="pz" style="z-index:75"><div class="dim" data-a="bd-close"></div><div class="bd-panel"${retry ? ' data-a="bd-retry" style="cursor:pointer"' : ''}><img class="x" data-a="bd-close" src="/ui/sys_close" alt="close">
        <div class="ex">!</div><div class="l1">${l1}</div>
        <div class="l2"${retry ? ' style="font-size:60px"' : ''}>${l2}</div>
        <div class="l3">Close DropMix on your phone first: the board takes one connection at a time.</div></div></div>`;
    }
    const pop = { forced: 'THE BOARD DISCONNECTED BECAUSE ITS BATTERIES ARE LOW. PUT IN FRESH BATTERIES TO KEEP PLAYING.',
                  batt: LU('Popup_BatteryInfo', 'Board battery: {0}', b.battery >= 0 ? b.battery + '%' : '—'),
                  unknown: 'THIS CARD ISN\'T IN YOUR IMPORTED CARD LIST.' }[bdPopup];
    if (pop) html += `<div class="pz" style="z-index:76"><div class="dim" data-a="bd-ok"></div><div class="mx-pop"><p>${pop}</p><div><span data-a="bd-ok">OK</span></div></div></div>`;
    setHtml(el, html);
  }

  // ---- quitting to the main menu: the game ends and Freestyle plays behind the menu (as at boot) ----
  const uiSfx = name => fetch('/api/uisfx', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name }) }).catch(() => {});
  function goMain() {
    window.__menu = 'main'; stage.innerHTML = ''; stage._mode = null;
    if ((lastState.mode || {}).id !== 'freestyle') post('/api/mode', { mode: 'freestyle' });
    window.OrigUI.render(lastState);
  }
  // ---- YES/NO popup (the original's PopupYesNo look: black panel, white border) ----
  let cfYes = null;
  function confirmBox(text, onYes) {
    cfYes = onYes;
    let el = document.getElementById('cf-layer');
    if (!el) { el = document.createElement('div'); el.id = 'cf-layer'; }
    stage.appendChild(el);
    el.innerHTML = `<div class="pz" style="z-index:70"><div class="dim" data-a="cf-no"></div><div class="mx-pop"><p>${esc(text)}</p>
      <div><span data-a="cf-yes">YES</span><span data-a="cf-no">NO</span></div></div></div>`;
  }
  function startWalk() { window.__menu = null; stage.innerHTML = ''; stage._mode = null; post('/api/walkthrough/start', {}); }
  function closeConfirm() { cfYes = null; document.getElementById('cf-layer')?.remove(); }

  // "Connect a phone" help, reachable from the ? in every top bar: shows the address and port to open on a phone.
  function connectBox() {
    let el = document.getElementById('cn-layer');
    if (!el) { el = document.createElement('div'); el.id = 'cn-layer'; }
    stage.appendChild(el);
    el.innerHTML = `<div class="pz" style="z-index:72"><div class="dim" data-a="cn-x"></div>
      <div class="mx-pop cn-pop"><div class="cn-h">CONNECT A PHONE</div>
        <div class="cn-sub">On a phone or tablet on the same Wi-Fi, open its web browser and go to:</div>
        <div class="cn-url" id="cn-url">…</div>
        <div class="cn-qr" id="cn-qr"></div>
        <div class="cn-sub cn-alt" id="cn-alt"></div>
        <div><span data-a="cn-x">CLOSE</span></div></div></div>`;
    const nice = u => u.replace(/^https?:\/\//, '').replace(/\/$/, '');
    fetch('/api/info').then(r => r.json()).then(info => {
      const urls = (info.joinUrls && info.joinUrls.length) ? info.joinUrls : [location.origin + '/'];
      const u = document.getElementById('cn-url'); if (u) u.textContent = nice(urls[0]);
      const q = document.getElementById('cn-qr');
      if (q && window.qrcode) { try { const qr = qrcode(0, 'M'); qr.addData(urls[0]); qr.make(); q.innerHTML = qr.createImgTag(6, 0); } catch { } }
      const alt = document.getElementById('cn-alt');
      if (alt && urls.length > 1) alt.textContent = 'Or: ' + urls.slice(1).map(nice).join('   ·   ');
    }).catch(() => { const u = document.getElementById('cn-url'); if (u) u.textContent = location.host; });
  }

  // ---- boot: the OpenMix notice (tap to skip), then the main menu. No third-party logos: OpenMix isn't endorsed by them ----
  function bootSplash() {
    if (sessionStorage.getItem('om-booted')) return;
    try { sessionStorage.setItem('om-booted', '1'); } catch { }
    const el = document.createElement('div'); el.id = 'boot';
    el.innerHTML = `<style>#boot { position: fixed; inset: 0; z-index: 9999; background: #000; display: grid; place-items: center; cursor: pointer; }
      #boot img, #boot .om { position: absolute; max-width: 46vw; max-height: 40vh; opacity: 0; transition: opacity .5s; } #boot .on { opacity: 1; }
      #boot .om { color: #ddd; font: 600 2.2vh/1.5 system-ui, sans-serif; text-align: center; max-width: 60vw; max-height: none; }
      #boot .om b { display: block; font-size: 5vh; color: #fff; margin-bottom: 1.5vh; }</style>
      <div class="om"><b>OpenMix</b>An unofficial, fan-made player for DropMix boards and cards.<br>
      Not affiliated with or endorsed by Hasbro or Harmonix. Game content is imported from your own copy.</div>
`;
    document.body.appendChild(el);
    const imgs = () => [...el.querySelectorAll('img, .om')]; let i = 0, t;
    uiSfx('SplashAnim');                                         // Actions/SplashAnim
    const done = () => {
      clearTimeout(t); el.remove();
      const b = lastState.board || {};
      if (b.enabled && b.state !== 'connected') bdPanel = true;          // BoardConnectPanel once after the splash
      if (!window.__menu && (lastState.mode || {}).id === 'freestyle') { window.__menu = 'main'; stage.innerHTML = ''; stage._mode = null; }
      window.OrigUI.render(lastState);
    };
    const step = () => { const all = imgs(); all.forEach((im, k) => im.classList.toggle('on', k === i)); i++; t = setTimeout(i > all.length ? done : step, i > all.length ? 0 : (i === 1 ? 3200 : 1800)); };
    el.onclick = done; step();
  }

  function wire() {
    stage.onclick = e => {
      const a = e.target.closest('[data-a]')?.dataset.a; if (!a) return;
      // SFXCard/button_select on buttons; SFXCard/slider on the tempo/key steppers (mix save and EQ have their own)
      if (['t-', 't+', 'k-', 'k+', 'tset', 'kset'].includes(a)) uiSfx('slider_');
      else if (!['mix-save', 'eq', 'mx-export', 'mn-mix'].includes(a)) uiSfx('button_select');
      const m = lastState.mode || {};
      if (false) {}
      else if (a === 'connect') connectBox();
      else if (a === 'cn-x') document.getElementById('cn-layer')?.remove();
      else if (a === 'start') host('start');
      else if (a === 'lobby') host('lobby');
      else if (a === 'studio') post('/api/studio', { action: 'open', value: (lastState.mix || {}).studioOpen ? 0 : 1 });
      else if (a === 't-' || a === 't+') post('/api/studio', { action: 'tempoStep', value: a === 't+' ? 1 : -1 });
      else if (a === 'k-' || a === 'k+') post('/api/studio', { action: 'key', value: a === 'k+' ? 1 : -1 });
      else if (a === 'kmode') post('/api/studio', { action: 'mode' });
      else if (a === 'tset' || a === 'kset') {
        const r = e.target.closest('.f-track').getBoundingClientRect(), mx = lastState.mix || {}, f = Math.max(0, Math.min(1, (e.clientX - r.left) / r.width));
        if (a === 'tset') { const lo = mx.minTempo, hi = mx.maxTempo; if (hi > lo) post('/api/studio', { action: 'tempo', value: Math.round(lo + (hi - lo) * f) }); }
        else { const d = Math.round(11 * f) - circlePos(mx.key); (async () => { for (let i = 0; i < Math.abs(d); i++) await post('/api/studio', { action: 'key', value: Math.sign(d) }); })(); }
      }
      else if (a === 'wt-skip') post('/api/walkthrough/skip', {});
      else if (a === 'wt-pop') post('/api/walkthrough/popup', {});
      else if (a === 'wt-test') post('/api/walkthrough/testsound', {});
      else if (a === 'cl-back') { post('/api/collection/claim', { open: false }); clOpenSent = false; window.__menu = 'cards'; window.OrigUI.render(lastState); }
      else if (a === 'cl-add') { if (clState) clState.busy = true; window.OrigUI.render(lastState); post('/api/collection/claim/add', {}).then(r => r.json()).then(d => { if (d && 'busy' in d) clState = d; clAt = 0; window.OrigUI.render(lastState); }).catch(() => {}); }
      else if (a === 'db-back') { post('/api/deckbuilder', { open: false }); dbOpenSent = false; window.__menu = 'cards'; window.OrigUI.render(lastState); }
      else if (a === 'db-size') { dbFull = !!+e.target.closest('[data-v]').dataset.v; dbDupes = dbFull; window.OrigUI.render(lastState); }   // SetCardCount resets duplicates
      else if (a === 'db-dupes') { dbDupes = !!+e.target.closest('[data-v]').dataset.v; window.OrigUI.render(lastState); }
      else if (a === 'db-dup') confirmBox(dbDupes ? 'A DECK CAN HOLD AT MOST 2 OF THE SAME CARD, AND THIS ONE HAS MORE.'
        : 'A DECK CAN HOLD ONLY 1 OF EACH CARD, AND THIS ONE HAS MORE.', () => {});
      else if (a === 'f-info-x') { fInfo = null; window.OrigUI.render(lastState); }
      else if (a === 'scores') { scoresOpen = true; window.OrigUI.render(lastState); }
      else if (a === 'hs-x') { scoresOpen = false; window.OrigUI.render(lastState); }
      else if (a === 'hs-reset') confirmBox(((lastState.mode || {}).id === 'party' ? LT('Party_ResetScoreConfirm', 'Clear the Party best score?') : LT('Leaderboard_DeleteConfirm', 'Clear the best scores?')).toUpperCase(), () => host('resetscores'));
      else if (a === 'st-rem') post('/api/settings', { reminders: !(settingsCache && settingsCache.reminders !== false) }).then(r => r.json()).then(d => { settingsCache = d; window.OrigUI.render(lastState); });
      else if (a === 'st-text') { const t = e.target.closest('[data-t]').dataset.t; fetch('/api/text/' + t).then(r => r.text()).then(txt => { textView = { t, txt }; window.OrigUI.render(lastState); }); }
      else if (a === 'tv-x') { textView = null; window.OrigUI.render(lastState); }
      else if (a === 'bdg-deck') { badgeDeck = e.target.closest('[data-p]').dataset.p; window.OrigUI.render(lastState); }
      else if (a === 'gd-pick') { guideSel = +e.target.closest('[data-i]').dataset.i; guideSlide = 0;
        if ((guideData[guideSel] || {}).title === 'Walkthrough_TutorialEntry') { startWalk(); return; }
        window.OrigUI.render(lastState); }
      else if (a === 'gd-prev') { guideSlide = Math.max(0, guideSlide - 1); window.OrigUI.render(lastState); }
      else if (a === 'gd-next') { guideSlide++; window.OrigUI.render(lastState); }
      else if (a === 'pz-guide') { pauseOpen = false; post('/api/pause', { on: false }); window.__menu = 'guide'; window.OrigUI.render(lastState); }
      else if (a === 'st-lang') { const ls = (settingsCache && settingsCache.languages) || ['en']; const i = ls.indexOf(settingsCache.language || 'en');
        post('/api/settings', { language: ls[(i + 1) % ls.length] }).then(r => r.json()).then(d => { settingsCache = d; badgesCache = null; window.OrigUI.render(lastState); }); }
      else if (a === 'c-reroll') host('reroll', { side: e.target.closest('[data-side]').dataset.side });
      else if (a === 'drawer') { const id = e.target.closest('[data-id]').dataset.id; drawerOpen[id] = !drawerOpen[id]; window.OrigUI.render(lastState); }
      else if (a === 'c-score') { const v = +e.target.closest('[data-v]').dataset.v; host('options', { score: v === 0 ? '15' : '21' }); }
      else if (a === 'c-timer') { const v = +e.target.closest('[data-v]').dataset.v; host('options', { timer: String([0, 15, 30, 60][v]) }); }
      else if (a === 'p-diff') { const v = +e.target.closest('[data-v]').dataset.v; host('difficulty', { value: ['Easy', 'Normal', 'Hard'][v] }); }
      else if (a === 'l-pl') host('playlist', { value: e.target.closest('[data-pl]').dataset.pl });
      else if (a === 'l-diff') { const v = +e.target.closest('[data-v]').dataset.v; host('difficulty', { value: v ? 'hard' : 'normal' }); }
      else if (a === 'eq') post('/api/button', {});
      else if (a === 'm-level') host('level', { arg: e.target.closest('[data-i]').dataset.i });
      else if (a === 'm-next') host('next'); else if (a === 'm-retry') host('retry'); else if (a === 'm-skip') host('skip');
      else if (a === 'm-levels') host('levels'); else if (a === 'm-unlock') host('unlockall'); else if (a === 'm-finish') host('finish');
      else if (a === 'm-swap') host('swap');
      else if (a === 'mix-save') {
        const btn = e.target.closest('[data-a]');
        fetch('/api/mixes/save', { method: 'POST' }).then(async r => { const j = await r.json().catch(() => ({})); mixSaved(r.ok, r.ok ? LT('Snapshot_Captured', 'Saved') : (j.error || LT('Snapshot_NotEnoughCards', 'Add more cards first')), btn); });
      }
      else if (a === 'mn-mix') {
        const sl = e.target.closest('.mx-slide'); if (sl._swallow) return;
        const row = sl.closest('.mx-row'); if (row.classList.contains('open')) { row.classList.remove('open'); return; }
        post('/api/mixes/play', { id: sl.dataset.id }).then(r => { if (r.ok) { window.__menu = null; stage.innerHTML = ''; stage._mode = null; } });
      }
      else if (a === 'mx-del') post('/api/mixes/delete', { id: e.target.dataset.id }).then(() => { mixesCache = null; mixesAt = 0; window.OrigUI.render(lastState); });
      else if (a === 'mx-back') { mxConfirm = false; window.__menu = 'mixes'; mixesCache = null; post('/api/mode', { mode: 'freestyle' }); window.OrigUI.render(lastState); }
      else if (a === 'mx-export') {
        const btn = e.target.closest('[data-a]'); if (btn.classList.contains('busy')) return;
        btn.classList.add('busy'); mixSaved(true, 'Exporting…', btn, false);
        post('/api/mixes/export', { id: (m.mix || {}).id }).then(async r => { const j = await r.json().catch(() => ({})); btn.classList.remove('busy');
          mixSaved(r.ok, r.ok ? 'Saved to Exports' : (j.error || 'Export failed'), btn, false); });
      }
      else if (a === 'mx-trash') { mxConfirm = true; window.OrigUI.render(lastState); }
      else if (a === 'mx-no') { mxConfirm = false; window.OrigUI.render(lastState); }
      else if (a === 'mx-yes') { mxConfirm = false; const id = (m.mix || {}).id; window.__menu = 'mixes'; mixesCache = null;
        post('/api/mixes/delete', { id }).then(() => window.OrigUI.render(lastState)); }
      else if (a === 'pause') { pauseOpen = true; post('/api/pause', { on: true }); window.OrigUI.render(lastState); }
      else if (a === 'pz-close') { pauseOpen = false; post('/api/pause', { on: false }); window.OrigUI.render(lastState); }
      else if (a === 'pz-menu') confirmBox(LT('Pause_ConfirmQuit', 'Leave this game?').toUpperCase(), () => { pauseOpen = false; post('/api/pause', { on: false }); goMain(); });
      else if (a === 'pz-restart') confirmBox(LT('Pause_ConfirmRestart', 'Start this game over?').toUpperCase(), () => { pauseOpen = false; post('/api/pause', { on: false }); const id = (lastState.mode || {}).id; stage.innerHTML = ''; stage._mode = null; if (id) post('/api/mode', { mode: id }); });
      else if (a === 'to-main') goMain();
      else if (a === 'bd-open') { bdPanel = true; window.OrigUI.render(lastState); }
      else if (a === 'bd-retry') post('/api/board/retry', {});
      else if (a === 'bd-close') { bdPanel = false; window.OrigUI.render(lastState); }
      else if (a === 'bd-batt') { bdPopup = 'batt'; window.OrigUI.render(lastState); }
      else if (a === 'bd-unknown') { bdPopup = 'unknown'; window.OrigUI.render(lastState); }
      else if (a === 'bd-ok') { bdPopup = null; window.OrigUI.render(lastState); }
      else if (a === 'cf-yes') { const f = cfYes; closeConfirm(); if (f) f(); }
      else if (a === 'cf-no') closeConfirm();
      else if (a === 'cc-dl') post('/api/cardcontent/download', {});
      else if (a === 'cc-cancel') post('/api/cardcontent/cancel', {});
      else if (a === 'cc-del') { locLoad(); confirmBox(clText('Menu_Settings_CardCacheDeleteConfirm', 'Remove all card art and music?'), () => post('/api/cardcontent/delete', {}).then(() => { ccAt = 0; })); }
      else if (a === 'menu') { window.__menu = 'main'; window.OrigUI.render(lastState); }
      else if (a === 'mn-close') { window.__menu = null; stage.innerHTML = ''; stage._mode = null; window.OrigUI.render(lastState); }
      else if (a === 'mn-go') { window.__menu = e.target.closest('[data-to]').dataset.to; window.OrigUI.render(lastState); }
      else if (a === 'mn-mode') { const id = e.target.closest('[data-mode]').dataset.mode; window.__menu = null; stage.innerHTML = ''; stage._mode = null;
        if ((lastState.mode || {}).id !== id) post('/api/mode', { mode: id }); else window.OrigUI.render(lastState); }
      else if (a === 'mn-deck') { const icon = e.target.closest('[data-icon]').dataset.icon;
        post('/api/decks', { toggle: [icon] }).then(async r => { if (r.ok) { decksCache = await r.json(); decksAt = Date.now(); } else { const j = await r.json().catch(() => ({})); flashMsg(j.error || 'Not now'); } window.OrigUI.render(lastState); }); }
      else if (a === 'mn-decks-all') post('/api/decks', { preset: 'all' }).then(async r => { decksCache = await r.json(); decksAt = Date.now(); window.OrigUI.render(lastState); });
      else if (a === 'diff') { const ds = ['Easy', 'Normal', 'Hard']; host('difficulty', { value: ds[(ds.indexOf(m.difficulty) + 1) % 3] }); }
    };
  }
  wire();
  vuLoop();
  bootSplash();
  post('/api/pause', { on: false });   // a freshly loaded table screen is never paused
  // timed moments (coin flip, FX panel, hand badges) need redraws between state updates
  setInterval(() => { if (lastState.mode && document.body.classList.contains('orig')) window.OrigUI.render(lastState); }, 400);
  fetch('/api/info').then(r => r.json()).then(i => { window.__joinUrl = (i.joinUrls || [])[0] || ''; }).catch(() => {});

  // ---- live slot meters + timeline (VUMeterTriangle.Update / the Freestyle bar cursor) ----
  // Host streams latency-aligned slot levels (the original's audio: analyzer VU, 50 dB range) and the heard beat at
  // ~30 Hz; this runs at display rate. Per bar: fill = sqrt(level * jitter[i]), jitter a random walk of
  // Random.Range(-2,2)*dt clamped to 0.6..1. Wild cycles its slot's colours once per beat with a cosine blend.
  function vuLoop() {
    const S = { beat: -1, bpm: 120, at: 0, lv: [0, 0, 0, 0, 0] }, shown = [0, 0, 0, 0, 0];
    const jit = Array.from({ length: 5 }, () => [1, 1, 1].map(() => .6 + Math.random() * .4));
    let clock = -1, last = performance.now();
    const connect = () => {
      const es = new EventSource('/api/levels');
      es.onmessage = e => { try { const m = JSON.parse(e.data); Object.assign(S, m); S.at = performance.now(); } catch { } };
      es.onerror = () => { es.close(); setTimeout(connect, 2000); };
    };
    connect();
    const hex = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16));
    const mixHex = (a, b, t) => { const x = hex(a), y = hex(b); return `rgb(${x.map((v, i) => Math.round(v + (y[i] - v) * t)).join(',')})`; };
    function frame(now) {
      const dt = Math.min(.1, (now - last) / 1000); last = now;
      // beat clock: extrapolate from the last sample, ease small errors, snap big ones (seek/stop)
      if (S.beat < 0) clock = -1;
      else {
        const target = S.beat + (now - S.at) / 60000 * S.bpm;
        const pred = clock < 0 ? target : clock + dt * S.bpm / 60;
        clock = Math.abs(target - pred) > .5 ? target : pred + (target - pred) * .1;
      }
      for (const el of document.querySelectorAll('.o-slot[data-pad]')) {
        const k = +el.dataset.pad, bars = el.querySelectorAll('.vu i');
        // level: ease toward the 30 Hz samples so the bars don't step
        shown[k] += ((S.lv[k] || 0) - shown[k]) * (1 - Math.exp(-dt / .04));
        bars.forEach((b, i) => {
          const j = jit[k]; j[i] = Math.max(.6, Math.min(1, j[i] + (Math.random() * 4 - 2) * dt));
          b.style.setProperty('--f', Math.sqrt(shown[k] * j[i]).toFixed(3));
        });
        if (el.dataset.ty === 'Wild' && clock >= 0) {
          const [g1, g2] = GLOW[k], ph = clock % 2, idx = Math.floor(ph), fr = ph - idx;
          const t = 1 - (Math.cos(fr * Math.PI) + 1) / 2, a = idx ? g2 : g1, b = idx ? g1 : g2;
          el.style.setProperty('--c', mixHex(a, b, t));
        }
      }
      // VUMeterTriangle: a slot the wheel targets blinks red, alpha 0.3/1.0 on quarter beats
      const mm = (typeof lastState !== 'undefined' && lastState && lastState.mode) || {};
      const wt = mm.id === 'clash' && mm.wheel && mm.wheel.state === 'spinning' ? (mm.wheel.side === 'Red' ? 'Blue' : 'Red') : null;
      document.querySelectorAll('.c-col .c-slot').forEach((el, k) => {
        const hit = wt && (lastState.pads[k] || {}).owner === wt && lastState.pads[k].top;
        el.classList.toggle('wt', !!hit);
        if (hit) el.style.setProperty('--wa', Math.floor(Math.max(0, clock) * 4) % 2 ? '1' : '.3');
      });
      const ticks = document.querySelectorAll('#f-bar .f-time i'), bar = clock < 0 || !TB.loopBars ? -1 : Math.floor(clock / 4) % TB.loopBars;
      ticks.forEach((t, i) => t.classList.toggle('on', i === bar));
      requestAnimationFrame(frame);
    }
    requestAnimationFrame(frame);
  }

  function drawPause() {
    let pz = document.getElementById('pz-layer');
    if (pauseOpen && !window.__menu && document.body.classList.contains('orig')) {
      if (!pz) { pz = document.createElement('div'); pz.id = 'pz-layer'; }
      if (stage.lastElementChild !== pz) stage.appendChild(pz);
      setHtml(pz, pauseHtml());
    } else if (pz) pz.remove();
  }
  window.OrigUI = {
    render(state) {
      locLoad();
      const r = this._render(state); drawPause();
      const n = (state.pads || []).filter(p => p.top).length;     // SnapshotManager.CanTakeSnapshot: minRequiredCards (imported)
      stage.querySelectorAll('.mx-save').forEach(b => b.classList.toggle('can', TB.snapshotMinCards != null && n >= TB.snapshotMinCards));
      if (document.body.classList.contains('orig')) { drawBoardUi(state); let hs = document.getElementById('hs-layer'); if (!hs) { hs = document.createElement('div'); hs.id = 'hs-layer'; } if (stage.lastElementChild !== hs) stage.appendChild(hs); setHtml(hs, scoresHtml(state.mode || {}) + textViewHtml() + badgePopHtml(state) + walkHtml(state) + cardContentFx(state)); walkFx(state); }
      if (window.__menu !== 'deckbuilder' && dbOpenSent) { dbOpenSent = false; post('/api/deckbuilder', { open: false }); }
      if (window.__menu !== 'claim' && clOpenSent) { clOpenSent = false; post('/api/collection/claim', { open: false }); }   // left the screen: cards play again   // left the checker: cards play again
      return r;
    },
    _render(state) {
      const m = state.mode || {};
      // ownership gate: no card registered yet -> stay on Add to Collection (after the boot splash)
      if (state.gate && state.gate.needCards && !document.getElementById('boot') && window.__menu !== 'claim') { window.__menu = 'claim'; }
      if (m.icons) { TEAM_ICON.Red = m.icons.red || TEAM_ICON.Red; TEAM_ICON.Blue = m.icons.blue || TEAM_ICON.Blue; }
      const on = m.id === 'party' || m.id === 'freestyle' || m.id === 'clash' || m.id === 'lanes' || m.id === 'mixgrid' || m.id === 'mixplay';
      document.body.classList.toggle('orig', on);
      document.getElementById('ostage-wrap').classList.toggle('crowd', on && m.phase !== 'lobby' && m.id !== 'freestyle' && m.id !== 'mixplay');
      if (!on) { document.body.classList.remove('menu-open'); if (stage._mode) { stage.innerHTML = ''; stage._mode = null; } return false; }
      lastState = state;
      if (window.__menu) {
        document.body.classList.add('orig'); document.getElementById('ostage-wrap').classList.remove('crowd');
        if (stage._mode !== 'menu') { stage.innerHTML = ''; stage._mode = 'menu'; }
        renderMenu(state); return true;
      }
      if (m.id === 'mixgrid') {
        const sub = m.phase === 'lobby' || m.phase === 'over' ? 'mg-lobby' : 'mg-game';
        if (stage._mode !== sub) { stage.innerHTML = ''; stage._mode = sub; mPrevScore = null; mLastPlay = null; }
        mixgrid(state, m); return true;
      }
      if (m.id === 'lanes') {
        const sub = m.phase === 'lobby' ? 'lanes-lobby' : 'lanes-game';
        if (stage._mode !== sub) { stage.innerHTML = ''; stage._mode = sub; lPrev = null; lScore = m.score || 0; lMsg = null; lCard = null; lFinale = null; }
        if (sub === 'lanes-lobby') lanesLobby(state, m); else lanesGame(state, m);
        return true;
      }
      if (m.id === 'clash' || m.id === 'party' || m.id === 'lanes') wipeOnStart(m, { clash: 'sys_clash', party: 'sys_party', lanes: 'icon_lanes' }[m.id]);
      if (m.id === 'clash') {
        const sub = m.phase === 'lobby' ? 'clash-lobby' : 'clash-game';
        if (stage._mode !== sub) { stage.innerHTML = ''; stage._mode = sub; if (sub === 'clash-lobby') { cPrev = null; cPopSeen = null; } }
        if (sub === 'clash-lobby') { cPrev = { current: m.current, phase: 'lobby', tops: state.pads.map(p => p.top?.cid) }; clashLobby(state, m); }
        else clashGame(state, m);
        return true;
      }
      if (m.id === 'mixplay') {
        if (stage._mode !== 'mixplay') { stage.innerHTML = ''; stage._mode = 'mixplay'; mxConfirm = false; }
        mixView(state, m); return true;
      }
      if (m.id === 'freestyle') {
        if (stage._mode !== 'freestyle') { stage.innerHTML = ''; stage._mode = 'freestyle'; lastTops = null; tickerCard = null; }
        freestyle(state, m); return true;
      }
      const sub = m.phase === 'lobby' ? 'lobby' : 'game';
      if (stage._mode !== 'party-' + sub) { stage.innerHTML = ''; stage._mode = 'party-' + sub; tileSeen = new Set(); }
      const lead = (state.players || [])[0]?.name;
      window.__partyName = lead ? `${lead}'s Party` : 'The Party';
      if (sub === 'lobby') partyLobby(state, m); else partyGame(state, m);
      return true;
    },
  };
})();
