// MixGrid UI shared by the table screen and the phones: level select, the 7×7 grid with path
// drawing, the shared hand (active + on-deck), goal and results. MixGrid.render(el, mode, act, opts)
// where act(action, arg) posts to the server.
(function () {
  const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const ACC = [['Lead', 'Loop'], ['Loop'], ['Loop', 'Beat'], ['Beat'], ['Beat', 'Bass']];
  const COL = { Beat: 'var(--blue)', Bass: 'var(--green)', Loop: 'var(--magenta)', Lead: 'var(--yellow)' };
  const pretty = s => s ? s[0].toUpperCase() + s.slice(1) : s;

  document.head.insertAdjacentHTML('beforeend', `<style>
    .mg { --cs: 7.2vh; display: flex; gap: 2vw; align-items: center; justify-content: center; width: 100%; }
    .mg.phone { --cs: min(12.4vw, 52px); flex-direction: column; gap: 8px; }
    .mg .side { display: flex; flex-direction: column; gap: 1.2vh; min-width: 15vw; align-items: center; }
    .mg.phone .side { flex-direction: row; min-width: 0; width: 100%; justify-content: space-between; gap: 8px; }
    .mg .stat small { display: block; color: var(--dim); font-weight: 800; letter-spacing: .14em; font-size: 1.3vh; text-transform: uppercase; }
    .mg.phone .stat small { font-size: 10px; }
    .mg .stat b { font-family: var(--font-num); font-size: 4.5vh; line-height: 1; }
    .mg.phone .stat b { font-size: 22px; }
    .mg .mult { font-family: var(--font-num); font-size: 3vh; color: #f5c542; text-shadow: 0 0 1.5vh #f5c54288; }
    .mg.phone .mult { font-size: 18px; }
    .mg .goal { background: #000b; border: 1px solid #333; border-radius: 1.2vh; padding: .8vh 1vw; text-align: center; min-width: 14vw; }
    .mg.phone .goal { padding: 6px 10px; border-radius: 10px; min-width: 0; flex: 1; }
    .mg .goal .gt { font-weight: 900; letter-spacing: .08em; font-size: 1.6vh; text-transform: uppercase; }
    .mg.phone .goal .gt { font-size: 11px; }
    .mg .goal .gi { display: flex; gap: .8vw; justify-content: center; margin-top: .4vh; flex-wrap: wrap; }
    .mg .goal .gi span { font-family: var(--font-num); font-size: 2vh; } .mg.phone .goal .gi span { font-size: 13px; }
    .mg .goal .gi span.done { color: var(--green); }
    .mg .goal.done { border-color: var(--green); box-shadow: 0 0 2vh var(--green); }
    .mg .timer { font-family: var(--font-num); font-size: 3vh; color: var(--orange); }
    .mg .gridbox { position: relative; padding: .8vh; border-radius: 1.4vh; background: #000c; border: 1px solid #2a2a2a; background-size: cover; background-position: center; }
    .mg .gridbox::before { content: ''; position: absolute; inset: 0; border-radius: inherit; background: #000a; }
    .mg .grid { position: relative; display: grid; grid-template-columns: repeat(7, var(--cs)); grid-auto-rows: var(--cs); gap: calc(var(--cs) * .08); touch-action: none; user-select: none; }
    .mg .cell { position: relative; border-radius: 22%; display: grid; place-items: center; font-weight: 900; font-size: calc(var(--cs) * .22); letter-spacing: .02em; color: #000; transition: transform .1s, opacity .2s; }
    .mg .cell.hole { visibility: hidden; }
    .mg .cell.req { background: #1b1b1f; color: #ddd; border: calc(var(--cs) * .05) solid #333; }
    .mg .cell.req.col { background: var(--c); border-color: color-mix(in srgb, var(--c) 60%, #fff); color: #000; }
    .mg .cell.req.lvl { background: #fff; } .mg .cell.req.lvl img { width: 70%; }
    .mg .cell.req.ins { background: #2a2a33; color: #fff; font-size: calc(var(--cs) * .17); }
    .mg .cell.req.wt { background: #fff; border-color: #fff; color: #000; box-shadow: 0 3px 8px #0008; }
    .mg .cell.req.wt img { width: 92%; } .mg .cell.req.wt img.ins { filter: brightness(0); }
    .mg .cell.req.wt .x2 { position: absolute; right: 3px; bottom: 1px; font-family: var(--font-num); font-size: calc(var(--cs) * .22); color: #000; }
    .mg .cell.tg::before { content: ""; position: absolute; left: -10%; top: -10%; width: 40%; height: 40%; background: url('/ui/goal_target') center / contain no-repeat; z-index: 2; }
    .mg .cell.tg { box-shadow: 0 0 0 calc(var(--cs) * .06) #f5c542, 0 0 calc(var(--cs) * .3) #f5c542aa; }
    .mg .cell.tg::after { content: '★'; position: absolute; top: -18%; right: -10%; color: #f5c542; font-size: calc(var(--cs) * .32); text-shadow: 0 0 4px #000; }
    .mg .cell.wall { outline: calc(var(--cs) * .05) dashed #888; outline-offset: -2px; }
    .mg .cell.bump::before { content: ''; position: absolute; left: 10%; right: 10%; bottom: 8%; height: 10%; border-radius: 99px; background: #fff8; }
    .mg .cell.absorb { outline: calc(var(--cs) * .06) solid #EB0C6D; }
    .mg .cell.pin:not(.tg) { box-shadow: inset 0 0 0 calc(var(--cs) * .04) #fff6; }
    .mg .cell.gcard { background: #000; border: calc(var(--cs) * .05) solid var(--c, #fff); overflow: hidden; }
    .mg .cell.gcard img { width: 100%; height: 100%; object-fit: cover; }
    .mg .cell.gcard .sl { position: absolute; bottom: 2%; right: 4%; font-family: var(--font-num); color: #fff; text-shadow: 0 0 3px #000, 0 0 3px #000; font-size: calc(var(--cs) * .26); }
    .mg .cell.gcard.mute { opacity: .45; }
    .mg .cell.ok { transform: scale(1.04); }
    .mg .cell.dim { opacity: .35; }
    .mg .cell.on { transform: scale(.88); box-shadow: 0 0 0 calc(var(--cs) * .08) #fff, 0 0 calc(var(--cs) * .4) #fff; opacity: 1; }
    .mg .cell.flash { animation: mgflash .7s ease-out; }
    @keyframes mgflash { 0% { transform: scale(1.25); filter: brightness(2.5); } 100% { transform: scale(1); filter: none; } }
    .mg .pop { position: absolute; left: 50%; top: 40%; transform: translate(-50%, -50%); font-family: var(--font-num); font-size: 6vh; font-weight: 700; color: #fff; text-shadow: 0 0 2vh #fff; pointer-events: none; animation: mgpop 1.3s ease-out forwards; }
    .mg.phone .pop { font-size: 34px; }
    @keyframes mgpop { 0% { opacity: 0; transform: translate(-50%, -30%) scale(.6); } 20% { opacity: 1; transform: translate(-50%, -50%) scale(1.1); } 100% { opacity: 0; transform: translate(-50%, -110%) scale(1); } }
    .mg .hand { display: flex; gap: 1vw; align-items: flex-end; }
    .mg.phone .hand { gap: 10px; justify-content: center; }
    .mg .hc { display: flex; flex-direction: column; align-items: center; gap: .4vh; background: none; border: 0; color: #fff; padding: 0; }
    .mg .hc .face { width: 11vh; aspect-ratio: 5/7; border-radius: 1vh; overflow: hidden; border: .4vh solid var(--c, #fff); background: #000; }
    .mg.phone .hc .face { width: 84px; border-width: 3px; border-radius: 8px; }
    .mg .hc.deck .face { width: 7.5vh; opacity: .75; } .mg.phone .hc.deck .face { width: 60px; }
    .mg .hc .face img { width: 100%; height: 100%; object-fit: cover; }
    .mg .hc small { font-size: 1.3vh; color: var(--dim); font-weight: 800; letter-spacing: .1em; text-transform: uppercase; max-width: 12vh; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .mg.phone .hc small { font-size: 10px; max-width: 90px; }
    .mg .hc.Wild .face { border-image: conic-gradient(var(--green), var(--blue), var(--magenta), var(--yellow), var(--green)) 1; }
    .mg .btnrow { display: flex; gap: .6vw; flex-wrap: wrap; justify-content: center; }
    .mg-lobby { display: flex; flex-direction: column; gap: 1.6vh; align-items: center; width: 100%; }
    .mg-levels { display: grid; grid-template-columns: repeat(auto-fill, minmax(12vw, 1fr)); gap: .8vh .6vw; width: 100%; max-height: 46vh; overflow-y: auto; padding-right: .6vw; }
    .phone .mg-levels { grid-template-columns: repeat(2, 1fr); max-height: 46vh; gap: 6px; }
    .mg-levels button { text-align: left; padding: .8vh .8vw; border-radius: 1vh; border: 1px solid #2c2c2c; background: #000; color: #ccc; display: grid; grid-template-columns: auto 1fr; gap: 0 .6vw; }
    .phone .mg-levels button { padding: 6px 8px; border-radius: 8px; gap: 0 6px; }
    .mg-levels button b { font-family: var(--font-num); font-size: 2.6vh; grid-row: span 2; align-self: center; color: #fff; min-width: 3.2vh; text-align: center; }
    .phone .mg-levels button b { font-size: 16px; min-width: 20px; }
    .mg-levels button span { font-weight: 900; font-size: 1.6vh; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; } .phone .mg-levels button span { font-size: 12px; }
    .mg-levels button i { font-style: normal; font-size: 1.3vh; color: var(--dim); } .phone .mg-levels button i { font-size: 10px; }
    .mg-levels button.on { border-color: #fff; box-shadow: 0 0 2vh #fff5; color: #fff; }
    .mg-levels button.locked { opacity: .3; }
    .mg-levels .st { color: #f5c542; letter-spacing: .1em; }
    .mg-sel { text-align: center; } .mg-sel h2 { margin: 0; font-size: 3.6vh; } .phone .mg-sel h2 { font-size: 20px; }
    .mg-sel div { color: var(--dim); font-size: 1.8vh; font-weight: 800; letter-spacing: .08em; } .phone .mg-sel div { font-size: 12px; }
    .mg-result { text-align: center; } .mg-result h1 { margin: 0; font-size: 7vh; } .phone .mg-result h1 { font-size: 30px; }
    .mg-result .stars { font-size: 5vh; color: #f5c542; letter-spacing: .3em; text-shadow: 0 0 2vh #f5c54288; } .phone .mg-result .stars { font-size: 28px; }
    .mg-result .stars .off { color: #333; text-shadow: none; }
    .mg .banner { position: absolute; inset: 0; display: grid; place-items: center; text-align: center; z-index: 2; pointer-events: none; }
    .mg .banner div { background: #000d; padding: 1.4vh 2vw; border-radius: 1.4vh; border: 1px solid #fff4; font-weight: 900; font-size: 2.6vh; letter-spacing: .06em; }
    .mg.phone .banner div { font-size: 15px; padding: 10px 14px; }
    .mg .banner small { display: block; color: var(--dim); font-size: .7em; margin-top: .4vh; }
  </style>`);

  // ---- rules mirror (server is authoritative) ----
  function options(c) {
    const o = [];
    for (let s = 0; s < 5; s++) {
      if (c.type === 'Wild' || c.type === 'Fx') ACC[s].forEach(t => o.push([s, t]));
      else if (ACC[s].includes(c.type)) o.push([s, c.type]);
    }
    return o;
  }
  function hasInst(c, slot, inst) {
    const ins = c.instruments || [];
    if (c.type !== 'Wild') return ins.some(i => i.toLowerCase() === inst.toLowerCase());
    if (ins.length !== 4) return false;
    const idx = { 0: [3, 2], 1: [2], 2: [2, 1], 3: [1] }[slot] || [1, 0];
    return idx.some(i => ins[i].toLowerCase() === inst.toLowerCase());
  }
  function sat(t, c, o) {
    if (/^Level[123]$/.test(t)) return c.power === +t[5];
    if (COL[t]) return o[1] === t;
    if (t === 'FX') return c.type === 'Fx';
    if (/^Slot\d$/.test(t)) return o[0] === +t[4];
    return hasInst(c, o[0], t);
  }

  function cellHtml(c, active) {
    if (!c) return `<div class="cell hole"></div>`;
    const cls = ['cell'], k = `data-x="${c.x}" data-y="${c.y}"`;
    if (c.tg) cls.push('tg'); if (c.wall && c.noEnd) cls.push('wall'); if (c.mustEnd) cls.push('bump'); if (c.absorb) cls.push('absorb'); if (c.pin) cls.push('pin');
    if (c.card) {
      const cc = c.card.c;
      return `<div class="${cls.join(' ')} gcard${c.card.slot < 0 ? ' mute' : ''} c-${cc.type}" ${k} title="${esc(cc.name)}">${cc.image ? `<img src="/art/${encodeURIComponent(cc.image)}" alt="" draggable="false">` : ''}<span class="sl">${c.card.slot >= 0 ? c.card.slot + 1 : ''}</span></div>`;
    }
    if (!c.t) return `<div class="${cls.join(' ')}" ${k}></div>`;
    cls.push('req');
    if (active && options(active).some(o => sat(c.t, active, o)) && !c.noEnd) cls.push('ok');
    // request visuals (shadow_* sprites) on a white PartyRequest-style tile, as on the table screen
    const SHC = { Lead: 'shadow_yellow_card', Loop: 'shadow_red_card', Beat: 'shadow_blue_card', Bass: 'shadow_green_card' };
    const SHI = { synth: 'shadow_synth', vocal: 'shadow_microphone', guitar: 'shadow_guitar', drums: 'shadow_drum', horns: 'shadow_horns', strings: 'shadow_strings', sampler: 'shadow_sampler' };
    const lv = /^Level([123])$/.exec(c.t), ins = SHI[c.t.toLowerCase()];
    const inner = lv ? `<img src="/ui/shadow_power_${lv[1]}" alt="" draggable="false">` : SHC[c.t] ? `<img src="/ui/${SHC[c.t]}" alt="" draggable="false">`
      : ins ? `<img class="ins" src="/ui/${ins}" alt="" draggable="false">` : esc(c.t.toUpperCase().slice(0, 6));
    return `<div class="${cls.join(' ')} wt" ${k}>${inner}${c.pts > 100 ? `<span class="x2">${c.pts / 100}×</span>` : ''}</div>`;
  }

  function goalHtml(m) {
    if (m.encore) return `<div class="goal"><div class="gt">Encore · double points</div><div class="timer">${Math.ceil(m.encoreLeft || 0)}s</div></div>`;
    const g = m.goal || {};
    return `<div class="goal${g.done ? ' done' : ''}"><div class="gt">Round ${m.round + 1}/${m.rounds}</div>
      <div class="gi">${(g.items || []).map(i => `<span class="${i.cur >= i.target ? 'done' : ''}">${i.type === 'Score' ? `${Math.min(i.cur, i.target).toLocaleString()} / ${i.target.toLocaleString()}` : `${Math.min(i.cur, i.target)}/${i.target} ${esc(i.label)}`}</span>`).join('')}</div></div>`;
  }

  function handHtml(m) {
    const h = m.hand || [];
    const face = (c, cls, lbl) => `<button class="hc ${cls} ${c.type} c-${c.type}" ${cls === 'deck' ? 'data-act="swap"' : ''}><div class="face">${c.image ? `<img src="/art/${encodeURIComponent(c.image)}" alt="" draggable="false">` : ''}</div><small>${lbl}</small></button>`;
    return `<div class="hand">${h[0] ? face(h[0], 'active', esc(h[0].name)) : ''}${h[1] ? face(h[1], 'deck', 'tap to swap') : ''}
      ${!h.length ? `<button class="pill" data-act="finish">End level</button>` : ''}</div>`;
  }

  function lobbyHtml(m, phone) {
    const sel = m.level, r = m.result;
    const lv = (m.levels || []).map(l => `<button class="${l.i === m.levelIdx ? 'on' : ''}${l.unlocked ? '' : ' locked'}" data-act="level" data-arg="${l.i}"><b>${l.i + 1}</b><span>${esc(l.name)}</span>
      <i>${l.beaten ? `<span class="st">${'★'.repeat(l.stars)}</span> ${l.best.toLocaleString()}` : l.skipped ? 'skipped' : l.unlocked ? (l.rounds > 1 ? '3 rounds' : 'new') : 'locked'}</i></button>`).join('');
    const pls = (m.allPlaylists || []).map(pl => `<button class="pl${m.playlists.includes(pl) ? ' on' : ''}" data-act="playlist" data-arg="${pl}"><img src="/ui/i_${pl}" alt="">${pretty(pl)}</button>`).join('');
    return `<div class="mg-lobby">
      ${r ? `<div class="mg-result"><h1 class="heavy">${r.passed ? 'LEVEL PASSED' : 'LEVEL FAILED'}</h1>
        <div class="stars">${[0, 1, 2].map(i => `<span class="${i < r.stars ? '' : 'off'}">★</span>`).join('')}</div>
        <div class="stat"><small>Score${r.newBest ? ' · new best!' : ''}</small><b>${m.score.toLocaleString()}</b></div>
        <div class="btnrow" style="margin-top:1vh">${r.hasNext ? `<button class="pill solid" data-act="next">Next level</button>` : ''}
          <button class="pill${r.hasNext ? '' : ' solid'}" data-act="retry">Retry</button>${r.canSkip ? `<button class="pill" data-act="skip">Skip</button>` : ''}<button class="pill" data-act="levels">Levels</button></div></div>`
      : `${sel ? `<div class="mg-sel"><h2 class="heavy">${sel.number}. ${esc(sel.name)}</h2><div>${esc(sel.sub || (sel.rounds > 1 ? 'Three rounds' : ''))}${sel.rounds > 1 ? ' · 3 rounds' : ''}</div></div>` : ''}
        <div class="mg-levels">${lv}</div>
        <div style="color:var(--dim);font-size:${phone ? '11px' : '1.5vh'};letter-spacing:.12em;font-weight:800">PICK ${m.numPlaylists} PLAYLISTS</div>
        <div class="plgrid">${pls}</div>
        <div class="btnrow"><button class="pill solid${phone ? '' : ' bigpill'}" data-act="start">Play level</button>
          <button class="pill${m.unlockAll ? ' solid' : ''}" data-act="unlockall">${m.unlockAll ? 'All unlocked' : 'Unlock all'}</button></div>`}</div>`;
  }

  function gameHtml(m, phone) {
    const bg = m.level?.bg ? `background-image:url('/art/${encodeURIComponent(m.level.bg)}')` : '';
    const active = m.hand?.[0];
    const banner = m.phase === 'reveal' ? `<div class="banner"><div>ROUND ${m.round + 1}/${m.rounds}<small>${esc(m.goal?.text || '')}</small></div></div>`
      : m.phase === 'goal' ? `<div class="banner"><div>GOAL COMPLETE!<small>Bonus +${(m.bonus || 0).toLocaleString()}</small></div></div>` : '';
    const grid = `<div class="gridbox" style="${bg}"><div class="grid">${(m.grid || []).flat().map(c => cellHtml(c, active)).join('')}</div>${banner}</div>`;
    const stats = `<div class="stat"><small>Score</small><b>${m.score.toLocaleString()}</b></div><div class="mult">×${m.mult}</div>`;
    const deck = `<div class="stat"><small>Deck</small><b>${m.deckLeft}</b></div>`;
    if (phone) return `<div class="side">${stats}${goalHtml(m)}${deck}</div>${grid}${handHtml(m)}`;
    return `<div class="side">${stats}${goalHtml(m)}</div>${grid}<div class="side">${handHtml(m)}${deck}</div>`;
  }

  // ---- path drawing ----
  function bindGrid(root, getM, act) {
    const grid = root.querySelector('.grid'); if (!grid) return;
    let path = null, card = null, opts = null, origin = null, dm = null;
    const cellAt = (x, y) => { const el = document.elementFromPoint(x, y)?.closest?.('.cell[data-x]'); return el && grid.contains(el) ? el : null; };
    const data = el => { const m = dm || getM(); const x = +el.dataset.x, y = +el.dataset.y; return (m.grid || []).flat().find(c => c && c.x === x && c.y === y); };
    const optsFor = p => { let o = options(card); for (const c of p) o = o.filter(op => sat(c.t, card, op)); return o; };
    const paint = () => grid.querySelectorAll('.cell[data-x]').forEach(el => {
      const on = path && path.some(c => c.x === +el.dataset.x && c.y === +el.dataset.y);
      const isOrigin = origin && origin.x === +el.dataset.x && origin.y === +el.dataset.y;
      el.classList.toggle('on', !!(on || isOrigin));
    });
    grid.onpointerdown = e => {
      dm = null; const m = getM(); if (m.phase !== 'play') return; dm = m;
      const el = cellAt(e.clientX, e.clientY); if (!el) return;
      const c = data(el); if (!c) return;
      if (c.card) { card = c.card.c; origin = c; path = []; opts = options(card); }
      else if (c.t && m.hand?.[0]) {
        card = m.hand[0]; origin = null;
        const o = optsFor([c]); if (!o.length) { root.dispatchEvent(new CustomEvent('mgmsg', { detail: `“${card.name}” can't meet that request` })); return; }
        path = [c]; opts = o;
      } else return;
      grid.setPointerCapture(e.pointerId); root._dragging = true; paint(); e.preventDefault();
    };
    grid.onpointermove = e => {
      if (!path) return;
      const el = cellAt(e.clientX, e.clientY); if (!el) return;
      const c = data(el); if (!c) return;
      const last = path.length ? path[path.length - 1] : origin;
      if (c === last) return;
      if (path.length >= 1 && path.length >= 2 && c === path[path.length - 2]) { path.pop(); paint(); return; }   // step back
      if (path.length === 1 && origin && c === origin) { path.pop(); paint(); return; }
      if (!c.t || c.card || path.includes(c)) return;
      if (Math.max(Math.abs(c.x - last.x), Math.abs(c.y - last.y)) !== 1) return;
      if (last !== origin && last.mustEnd) return;
      const o = optsFor([...path, c]); if (!o.length) return;
      path.push(c); opts = o; paint();
    };
    const end = () => {
      if (!path) return;
      const p = path, o = origin; path = null; origin = null; root._dragging = false;
      while (p.length && p[p.length - 1].noEnd) p.pop();
      paint();
      if (!p.length) { root.dispatchEvent(new CustomEvent('mgmsg', { detail: 'A path has to end on an open request' })); root._flush?.(); return; }
      const cells = (o ? [o, ...p] : p).map(c => `${c.x},${c.y}`).join(';');
      act('play', cells);
      root._flush?.();
    };
    grid.onpointerup = end; grid.onpointercancel = () => { path = null; origin = null; root._dragging = false; paint(); root._flush?.(); };
  }

  window.MixGrid = {
    bindGrid, sat, options,
    render(root, m, act, opts = {}) {
      const phone = !!opts.phone;
      root._m = m;
      if (root._dragging) { root._pending = m; return; }   // never rebuild under a finger
      const lobby = m.phase === 'lobby' || m.phase === 'over';
      const html = `<div class="mg${phone ? ' phone' : ''}">${lobby ? lobbyHtml(m, phone) : gameHtml(m, phone)}</div>`;
      if (root._html !== html) {
        root.innerHTML = html; root._html = html;
        root.querySelectorAll('[data-act]').forEach(b => b.onclick = () => act(b.dataset.act, b.dataset.arg));
        if (!lobby) bindGrid(root, () => root._m, act);
        root._flush = () => { if (root._pending) { const p = root._pending; root._pending = null; MixGrid.render(root, p, act, opts); } };
      }
      // score pop + flash on each new play
      if (m.last && m.last.id !== root._lastId) {
        const first = root._lastId === undefined; root._lastId = m.last.id;
        if (!first && !lobby) {
          const box = root.querySelector('.gridbox');
          if (box) { const pop = document.createElement('div'); pop.className = 'pop'; pop.textContent = `+${m.last.total.toLocaleString()}${m.last.mult > 1 ? ` ×${m.last.mult}` : ''}`; box.appendChild(pop); setTimeout(() => pop.remove(), 1400); }
          for (const [x, y] of m.last.cells) root.querySelector(`.cell[data-x="${x}"][data-y="${y}"]`)?.classList.add('flash');
        }
      }
    },
  };
})();
