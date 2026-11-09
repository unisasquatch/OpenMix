// DropMix board component: a black slab with 5 recessed card wells. Each well glows with
// the colours of the card types it currently ACCEPTS (driven by the active mode's rules),
// with LED dashes beside it, like the physical board's light strips.
//
//   renderBoard(el, state, { selectedCid, legal, onTap, size: 'phone'|'table' })

// Game text: the imported strings table (/api/loc/<lang>), looked up by key. OMLoc.t(key, fallback) gives the text
// (fallback = OpenMix's own wording when the table lacks the key); OMLoc.html(...) also turns the rich-text tags into
// HTML (sprites → images, <b>/<i> kept, others dropped, \n → <br>); OMLoc.fmt replaces {0}, {1}… with the arguments.
window.OMLoc = (function () {
  let tbl = null, lang = null;
  const subs = [];
  const escH = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const SPRITE_INDEX = { 34: 'remove', 41: 'sys_blocked_icon' };   // fx_sprite_sheet cells that have a /ui image of their own
  function load(l) {
    const want = l || 'current';
    if (want === lang) return;
    lang = want;
    fetch('/api/loc/' + encodeURIComponent(want)).then(r => r.json()).then(d => { tbl = d; subs.forEach(f => { try { f(); } catch (e) { } }); }).catch(() => {});
  }
  const t = (k, f) => (tbl && tbl[k]) || f;
  const fmt = (s, ...a) => a.reduce((acc, v, i) => acc.split('{' + i + '}').join(v), s);
  // opt.upper: capitals (text only, not the tags); opt.img: the icon's inline style
  function rich(s, opt = {}) {
    const icon = n => n === 'dropmix' ? '<span class="o-dmpill"></span>' : `<img src="/ui/${escH(n)}" style="${opt.img ?? 'height:1.2em;vertical-align:-.25em'}" alt="">`;
    return String(s ?? '').split(/(<[^>]*>)/).map(p => {
      if (!p.startsWith('<')) return escH(opt.upper ? p.toUpperCase() : p).replace(/\n/g, '<br>');
      let m;
      if ((m = /^<sprite[^>]*\bname="?(\w+)"?/.exec(p))) return icon(m[1]);
      if ((m = /^<sprite[^>]*\bindex=(\d+)/.exec(p))) return SPRITE_INDEX[m[1]] ? icon(SPRITE_INDEX[m[1]]) : '';
      if ((m = /^<(\/?)(b|i)>$/.exec(p))) return `<${m[1]}${m[2]}>`;
      return '';                                   // font, colour, size … tags: dropped
    }).join('');
  }
  const plain = s => String(s ?? '').replace(/<[^>]*>/g, '').replace(/\s+/g, ' ').trim();
  load();
  return { load, t, fmt, rich, plain, html: (k, f, ...a) => rich(fmt(t(k, f), ...a)), text: (k, f, ...a) => plain(fmt(t(k, f), ...a)), onLoad: f => subs.push(f) };
})();

(function () {
  // colours imported from the user's game (/api/tables uiColours) as CSS variables: --om-<Class>-<field>
  fetch('/api/tables').then(r => r.json()).then(d => {
    for (const [k, c] of Object.entries(d.uiColours || {}))
      document.documentElement.style.setProperty('--om-' + k.replace('.', '-'), `rgb(${c.map(v => Math.round(v * 255)).join(',')})`);
  }).catch(() => {});
  const TYPE_COLOUR = { Bass: 'green', Beat: 'blue', Loop: 'magenta', Lead: 'yellow' };
  const HEX = { green: '#50B048', blue: '#00BFF3', magenta: '#EB0C6D', yellow: '#FFF100', white: '#F2F2F2' };
  // Game slots 0..4 left->right (program logic, mirrors Rules.SlotAccepts on the server).
  // (The shell_slot_00..04 frame images are numbered in the opposite direction.) Fallback only:
  // the server's mode block sends the authoritative per-slot accepts list.
  const DEFAULT_ACCEPTS = [['Lead', 'Loop'], ['Loop'], ['Loop', 'Beat'], ['Beat'], ['Beat', 'Bass']];

  function accepts(state, pad) {
    const a = state?.mode?.slots?.[pad]?.accepts;
    return Array.isArray(a) ? a : DEFAULT_ACCEPTS[pad];
  }
  function colours(types) {
    const set = [];
    for (const t of types) { const c = TYPE_COLOUR[t]; if (c && !set.includes(c)) set.push(c); }
    if (types.includes('Wild') && set.length === 0) set.push('white');
    return set.length ? set : ['white'];
  }
  function rim(cols) {
    const h = cols.map(c => HEX[c]);
    if (h.length === 1) return `${h[0]}`;
    if (h.length === 2) return `linear-gradient(225deg, ${h[0]} 0 50%, ${h[1]} 50% 100%)`;
    return `conic-gradient(${h.map((c, i) => `${c} ${i * 360 / h.length}deg ${(i + 1) * 360 / h.length}deg`).join(',')})`;
  }
  const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

  window.renderBoard = function (el, state, opt = {}) {
    if (!state) return;
    const legal = opt.legal;                       // null = any, array = allowed pads
    const sel = opt.selectedCid;
    el.classList.add('dmboard', opt.size === 'table' ? 'big' : 'small');
    if (opt.size !== 'table') { el.classList.add('orig'); return renderPhoneSlots(el, state, opt); }
    el.classList.toggle('has-lanes', !!opt.above);
    el.innerHTML = state.pads.map(p => {
      const c = p.top;
      const acc = accepts(state, p.pad);
      const cols = colours(acc);
      const lit = c ? 'on' : '';
      const tgt = sel ? ((legal == null || legal.includes(p.pad)) ? 'target' : 'dead') : '';
      const glow = cols.map(k => HEX[k]);
      const style = `--rim:${rim(cols)};--g1:${glow[0]};--g2:${glow[glow.length - 1]}`;
      const who = !c ? '' : p.source === 'Board' ? 'on the board' : (p.playedBy || '');
      const owner = p.owner ? `<span class="owner-ribbon t-${p.owner.toLowerCase()}">${p.owner.toUpperCase()}</span>` : '';
      const st = `${p.silenced ? 'silenced' : ''} ${c && p.valid === false ? 'invalid-play' : ''}`;
      return `<div class="well-col" data-pad="${p.pad}">${opt.above ? opt.above(p.pad) : ''}
        <div class="well ${lit} ${tgt} ${st}" style="${style}">${owner}
          <div class="rimlight"></div>
          <div class="pocket">${c
            ? `<div class="incard c-${c.type}"><img src="/art/${encodeURIComponent(c.image)}" alt="">${opt.size === 'table' && c.power ? `<img class="pw" src="/ui/sys_power_${Math.min(3, c.power)}" alt="">` : ''}</div>`
            : `<span class="slotnum">${p.pad + 1}</span>`}
            ${p.stack > 1 ? `<span class="stackn">×${p.stack}</span>` : ''}</div>
        </div>
        <div class="leds">${cols.map(k => `<i style="--l:${HEX[k]}"></i>`).join('')}</div>
        ${opt.size === 'table'
          ? `<div class="winfo"><b>${c ? esc(c.name) : '&nbsp;'}</b><span>${c ? esc(c.artist) : '&nbsp;'}</span><em>${esc(who) || '&nbsp;'}</em></div>`
          : `<div class="winfo"><b>${c ? esc(c.name) : '&nbsp;'}</b><em>${esc(who) || '&nbsp;'}</em></div>`}
      </div>`;
    }).join('');
    if (opt.onTap) el.querySelectorAll('.well-col').forEach(w => w.onclick = () => opt.onTap(+w.dataset.pad));
    popScores(el, state);
    if (state?.mode?.id === 'lanes') laneBursts(el, state);
  };

  // Phone slots (original look): neon shell_slot frames, card art + power bars, white glow for legal targets
  const SLOT_GLOW = [['#FFF100', '#EB0C6D'], ['#EB0C6D', '#EB0C6D'], ['#EB0C6D', '#00BFF3'], ['#00BFF3', '#00BFF3'], ['#00BFF3', '#50B048']];
  const CARD_COL = { Lead: '#FFF100', Loop: '#EB0C6D', Beat: '#00BFF3', Bass: '#50B048', Wild: '#fff', Fx: '#ccc' };
  const TEAM_COL = { Red: '#EB0C6D', Blue: '#00BFF3' };
  const TEAM_ICON = { Red: 'fever', Blue: 'ouroboros' };       // same team icons as the table (orig.js)
  function renderPhoneSlots(el, state, opt) {
    const ic = state?.mode?.icons; if (ic) { TEAM_ICON.Red = ic.red || TEAM_ICON.Red; TEAM_ICON.Blue = ic.blue || TEAM_ICON.Blue; }
    const legal = opt.legal, sel = opt.selectedCid;
    el.innerHTML = state.pads.map(p => {
      const c = p.top, k = p.pad, [g1, g2] = SLOT_GLOW[k];
      const tgt = sel ? ((legal == null || legal.includes(k)) ? ' target' : ' dead') : '';
      const n = c ? Math.max(0, Math.min(3, c.power || 0)) : 0;
      const body = c
        ? `<div class="vu">${[0, 1, 2].map(i => `<i class="${i < n ? 'on' : ''}"></i>`).join('')}</div><div class="art"><img src="/art/${encodeURIComponent(c.image)}" alt=""></div>
           ${p.silenced ? '<div class="x"></div>' : ''}${p.valid === false ? '<div class="bad"></div>' : ''}${p.owner ? `<div class="team"><i style="-webkit-mask-image:url('/ui/${TEAM_ICON[p.owner]}');mask-image:url('/ui/${TEAM_ICON[p.owner]}')"></i></div>` : ''}`
        : `<img class="frame" src="/ui/shell_slot_0${4 - k}" alt="">`;
      const who = !c ? '' : p.source === 'Board' ? 'on the board' : (p.playedBy || '');
      const team = c && p.owner ? TEAM_COL[p.owner] : null;              // Clash: whose card this is
      return `<div class="well-col" data-pad="${k}">${opt.above ? opt.above(k) : ''}
        <div class="pslot${tgt}${team ? ' owned' : ''}" style="--g1:${g1};--g2:${g2};--c:${c ? (c.type === 'Wild' ? g1 : CARD_COL[c.type]) : '#fff'}${team ? `;--team:${team}` : ''}">${body}</div>
        <div class="winfo"><b>${c ? esc(c.name) : '&nbsp;'}</b>${team ? `<strong class="tm" style="color:${team}">${esc(p.owner)}</strong>` : ''}<em>${esc(who) || '&nbsp;'}</em></div></div>`;
    }).join('');
    if (opt.onTap) el.querySelectorAll('.well-col').forEach(w => w.onclick = () => opt.onTap(+w.dataset.pad));
    popScores(el, state);
    if (state?.mode?.id === 'lanes') laneBursts(el, state);
  }

  // Float "+1 / new colour / full mix" pops over slots for score events we haven't shown yet.
  const shown = new WeakMap();
  function popScores(el, state) {
    const evs = state?.mode?.events || [];
    let last = shown.get(el);
    if (last === undefined) { shown.set(el, evs.length ? evs[evs.length - 1].id : 0); return; }   // don't replay history on load
    const KIND = { Slot: '', BountyBass: 'new green', BountyBeat: 'new blue', BountyLoop: 'new red', BountyLead: 'new yellow',
                   FX: 'FX', MixFill: OMLoc.text('HeadToHead_FullMix', 'all slots').toLowerCase() + '!', Wheel: 'bonus!', Clear: 'cleared',
                   White: '', Gold: 'gold block', Rainbow: 'rainbow block', FullClear: OMLoc.text('Lanes_FullClear', 'row bonus!').toLowerCase(), Bonus: 'bonus', Overflow: 'overflow' };
    for (const e of evs) {
      if (e.id <= last) continue;
      last = e.id;
      const well = el.querySelectorAll('.well')[e.slot];
      if (!well) continue;
      const r = well.getBoundingClientRect();
      const d = document.createElement('div');
      d.className = `pop t-${String(e.side).toLowerCase()}${e.points < 0 ? ' neg' : ''}`;
      d.innerHTML = `${e.points > 0 ? '+' : ''}${e.points || ''}${(e.note || KIND[e.kind]) ? `<small>${esc(e.note || KIND[e.kind])}</small>` : ''}`;
      const delay = (evs.indexOf(e) - evs.findIndex(x => x.id > (shown.get(el) || 0))) * 0.35;
      Object.assign(d.style, { position: 'fixed', left: `${r.left + r.width / 2}px`, top: `${r.top + r.height * 0.3}px`,
                               animationDelay: `${delay}s`, opacity: 0, fontSize: el.classList.contains('big') ? '6vh' : '22px' });
      document.body.appendChild(d);
      setTimeout(() => d.remove(), 2600 + delay * 1000);
    }
    shown.set(el, last);
  }
  // ---- Lanes ("Puzzle"): request columns above each well, cell 0 nearest the slot ----
  const REQ_HEX = { Beat: HEX.blue, Bass: HEX.green, Loop: HEX.magenta, Lead: HEX.yellow };
  window.renderLane = function (lane, idx) {
    if (!lane) return '';
    const cells = [];
    for (let k = lane.cells.length - 1; k >= 0; k--) {
      const c = lane.cells[k];
      if (!c) { cells.push(`<i class="lc empty" data-k="${k}"></i>`); continue; }
      const lvl = c.t.startsWith('Level') ? +c.t.slice(5) : 0;
      const spr = lvl ? `shadow_power_${lvl}` : ({ Lead: 'shadow_yellow_card', Loop: 'shadow_red_card', Beat: 'shadow_blue_card', Bass: 'shadow_green_card' })[c.t] || 'sys_wild_card';
      cells.push(`<i class="lc req o${c.n > 1 ? ' blk' : ''}" data-k="${k}"><img src="/ui/${spr}" alt=""></i>`);   // LanesRequest look: dark tile, white border; merged = light grey
    }
    return `<div class="lane${lane.danger ? ' danger' : ''}${lane.full ? ' full' : ''}" data-lane="${idx}">${cells.join('')}</div>`;
  };
  const burstSeen = new WeakMap();
  function laneBursts(el, state) {
    const cl = state.mode.clears || [];
    let last = burstSeen.get(el);
    if (last === undefined) { burstSeen.set(el, cl.length ? cl[cl.length - 1].id : 0); return; }
    for (const c of cl) {
      if (c.id <= last) continue;
      last = c.id;
      const lane = el.querySelector(`.lane[data-lane="${c.lane}"]`); if (!lane) continue;
      for (const k of c.cells) {
        const cell = lane.querySelector(`[data-k="${k}"]`); if (!cell) continue;
        const r = cell.getBoundingClientRect(), d = document.createElement('div');
        d.className = 'lane-burst' + (c.kind === 'fail' ? ' fail' : '');
        Object.assign(d.style, { left: `${r.left}px`, top: `${r.top}px`, width: `${r.width}px`, height: `${r.height}px` });
        document.body.appendChild(d); setTimeout(() => d.remove(), 900);
      }
    }
    burstSeen.set(el, last);
  }

  // ---- Party: request tiles ----
  const TILE_HEX = { Lead: HEX.yellow, RemoveLead: HEX.yellow, Loop: HEX.magenta, RemoveLoop: HEX.magenta,
                     Beat: HEX.blue, RemoveBeat: HEX.blue, Bass: HEX.green, RemoveBass: HEX.green };
  const SH_CARD = { Lead: 'shadow_yellow_card', Loop: 'shadow_red_card', Beat: 'shadow_blue_card', Bass: 'shadow_green_card' };
  const SH_INST = { synth: 'shadow_synth', vocal: 'shadow_microphone', guitar: 'shadow_guitar', drums: 'shadow_drum', horns: 'shadow_horns', strings: 'shadow_strings', sampler: 'shadow_sampler' };
  function tileIcon(t) {
    const ty = t.type, lvl = /^Level(\d)/.exec(ty), rm = /^Remove(\w+)/.exec(ty);
    if (lvl) return [`<img src="/ui/shadow_power_${lvl[1]}" alt="">`, OMLoc.text('Party_LevelRequest', 'any type').toUpperCase()];
    if (SH_CARD[ty]) return [`<img src="/ui/${SH_CARD[ty]}" alt="">`, ''];
    if (rm && SH_CARD[rm[1]]) return [`<img src="/ui/${SH_CARD[rm[1]].replace('shadow_', 'shadow_remove_')}" alt="">`, 'REMOVE'];
    if (ty === 'Equalizer') return [`<img class="inst" src="/ui/shadow_dm_button" alt="">`, ''];
    const inst = (t.instrument || ty.replace(/^Add/, '')).toLowerCase();
    if (SH_INST[inst]) return [`<img class="inst" src="/ui/${SH_INST[inst]}" alt="">`, ''];
    return ['', esc(t.prompt || ty).toUpperCase()];
  }
  function tileInner(t) {
    if (t.state === 'active') {
      const [img, lab] = tileIcon(t), h = Math.round((1 - Math.max(0, Math.min(1, t.fill))) * 102);
      return `<div class="drain" style="height:${h}px"></div><div class="line" style="top:${h}px"></div><div class="ico">${img}</div>${lab ? `<div class="lab">${lab}</div>` : ''}`;
    }
    if (t.state === 'failed') return `<div class="res"><img src="/ui/sys_close" alt=""></div>`;
    return `<div class="res"><img src="/ui/sys_check_mark" alt="">${t.state === 'dismissed' ? '' : `<b>${(t.points || 0).toLocaleString()}</b>`}</div>`;
  }
  window.renderTiles = m => (m.tiles || []).map(t => `<div class="ptile2 ${t.state}" data-id="${t.id}">${tileInner(t)}</div>`).join('');
  window.updateTiles = function (el, m) {
    if (!el) return;
    const ids = (m.tiles || []).map(t => t.id + t.state).join(',');
    if (el.dataset.ids !== ids) { el.dataset.ids = ids; el.innerHTML = window.renderTiles(m); return; }
    (m.tiles || []).forEach((t, i) => {
      const node = el.children[i]; if (!node || t.state !== 'active') return;
      const h = Math.round((1 - Math.max(0, Math.min(1, t.fill))) * 102);
      const d = node.querySelector('.drain'), l = node.querySelector('.line'); if (d) d.style.height = h + 'px'; if (l) l.style.top = h + 'px';
    });
  };
  window.renderPips = m => `<div class="ppips2">${(m.pips || []).map(p => `<i class="${p}"></i>`).join('')}</div>`;

  // ---- a hand card drawn like the physical card ----
  const CARD_HEX = { Lead: HEX.yellow, Loop: HEX.magenta, Beat: HEX.blue, Bass: HEX.green, Fx: '#f2f2f2' };
  const WILD_INST = ['#50B048', '#00BFF3', '#EB0C6D', '#FFF100'];   // a Wild's Instrument 1..4 = green, blue, red, yellow
  const INST = ['synth', 'vocal', 'guitar', 'drums', 'horns', 'strings', 'sampler'];
  let gradN = 0;
  function powerSvg(c) {
    const n = Math.max(0, Math.min(3, c.power || 0)), wild = c.type === 'Wild', id = 'pw' + (++gradN);
    const fill = wild ? `url(#${id})` : (CARD_HEX[c.type] || '#fff');
    const steps = ['2.5,2.5 93.5,2.5 69.5,26.5 2.5,26.5', '2.5,35 61,35 37,59 2.5,59', '2.5,68 28,68 2.5,93.5'];   // right edges all on x = 96 - y
    return `<svg class="pcpow" viewBox="0 0 100 100" preserveAspectRatio="xMinYMin meet">${wild ? `<defs><linearGradient id="${id}" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#EB0C6D"/><stop offset=".25" stop-color="#FFF100"/><stop offset=".5" stop-color="#EB0C6D"/><stop offset=".7" stop-color="#00BFF3"/><stop offset="1" stop-color="#50B048"/></linearGradient></defs>` : ''}
      ${steps.map((pts, i) => (3 - i) <= n ? `<polygon points="${pts}" fill="${fill}" stroke="${fill}" stroke-width="5" stroke-linejoin="round"/>` : `<polygon points="${pts}" fill="none" stroke="${fill}" stroke-width="5" stroke-linejoin="round"/>`).join('')}</svg>`;
  }
  // FX ability script -> the printed pictogram line (e.g. "+1 • synth", "drop + blue card", "-3 player opponent")
  const CARD_SPRITE = { Bass: 'sys_green_card', Beat: 'sys_blue%20card', Loop: 'sys_red_card', Lead: 'sys_yellow_card' };
  const ORDER = ['Bass', 'Beat', 'Loop', 'Lead'];
  const pic = (name, cls = '') => `<img class="pp ${cls}" src="/ui/${name}" alt="">`;
  const num = n => { const a = Math.abs(n), w = ['', 'one', 'two', 'three'][a]; return `<span class="pnum">${pic(n < 0 ? 'subtract' : 'add', 'sign')}${w ? pic(w) : `<b>${a}</b>`}</span>`; };
  function filterPics(f) {
    const m = /(\w+)\{([^}]*)\}/.exec(f || ''); if (!m) return f && f.includes('random') ? pic('random') : '';
    const vals = m[2].split('|');
    if (m[1] === 'type') return ORDER.filter(t => vals.includes(t)).map(t => pic(CARD_SPRITE[t], 'pcol')).join('');
    if (m[1] === 'instrument') return vals.map(v => pic('instrument_' + v.toLowerCase(), 'pinst')).join('');
    if (m[1] === 'power') return vals.map(v => pic('shadow_power_' + v.replace('power_', ''), 'pinst')).join('');
    return '';
  }
  const who = t => t === 'Both' ? pic('player') + pic('opponent') : t === 'Opponent' ? pic('opponent') : t === 'Self' ? pic('player') : '';
  function abilityPics(raw) {
    const m = /^(?:\w+:)?\s*(\w+)\((.*)\)\s*$/.exec(raw || ''); if (!m) return '';
    const kind = m[1], args = m[2].split(/,(?![^{]*\})/).map(x => x.trim());
    const n = args.map(Number).find(x => !isNaN(x) && x !== 0);
    const target = args.find(a => /^(Self|Opponent|Both)$/.test(a));
    const filt = args.find(a => a.includes('{'));
    const dot = '<span class="pdot"></span>';
    switch (kind) {
      case 'set_bonus': return num(n ?? 1) + dot + filterPics(filt);
      case 'score_adjust': return num(n ?? 1) + who(target);
      case 'extra_action': return pic('drop') + filterPics(filt);
      case 'remove': return pic('remove') + filterPics(filt) + who(target);
      case 'draw': return pic('draw') + (n ? num(n) : '') + who(target);
      case 'combo': return num(n ?? 1) + dot + filterPics(filt);
      case 'cover': return pic('hand') + filterPics(filt);
      case 'eq_spin': return pic('equalizer');
      case 'block': return pic('blocked') + filterPics(filt);
      default: return '';
    }
  }

  window.abilityPics = abilityPics;
  window.renderPhysCard = function (c, extra = '') {
    const col = CARD_HEX[c.type] || '#fff';
    const ins = (c.instruments || []).map((x, i) => [String(x).toLowerCase(), c.type === 'Wild' ? WILD_INST[i] : col]).filter(([x]) => INST.includes(x));
    if (c.type === 'Wild') ins.reverse();
    return `<div class="pcard c-${c.type}" style="--pc:${col}">
      <div class="pcart">${c.image ? `<img src="/art/${encodeURIComponent(c.image)}" alt="" draggable="false">` : ''}</div>${powerSvg(c)}
      <div class="pctext">${c.type === 'Fx' ? `<b>${esc(c.name)}</b>` :`<b>${esc(c.artist)}</b><span>${esc(c.name)}</span>`}</div>
      <div class="pcfoot"><div class="pcins">${c.type === 'Fx' ? (c.picto ? `<img class="pcfx" src="/ui/${encodeURIComponent(c.picto)}" alt="">` : abilityPics(c.ability)) : ''}${ins.map(([x, k]) => `<i style="--ic:${k};-webkit-mask-image:url('/ui/instrument_${x}');mask-image:url('/ui/instrument_${x}')"></i>`).join('')}</div>
</div>
      <div class="pcser">${c.icon ? `<i style="-webkit-mask-image:url('/ui/${encodeURIComponent(c.icon)}');mask-image:url('/ui/${encodeURIComponent(c.icon)}')"></i>` : ''}<small>${esc(c.num || '')}</small></div>
      <div class="pcprint">${esc(c.print || '')}</div>${extra}</div>`;
  };

  window.DM_ACCEPTS = accepts;
})();
