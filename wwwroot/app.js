'use strict';

const $ = sel => document.querySelector(sel);
const bridge = window.chrome?.webview ?? window.mockBridge;

// ---------- bridge to MainForm.cs ----------
const pending = new Map();
let seq = 0;

function call(method, params) {
  return new Promise((resolve, reject) => {
    const id = ++seq;
    pending.set(id, { resolve, reject });
    bridge.postMessage({ id, method, params });
  });
}

bridge.addEventListener('message', e => {
  const msg = e.data;
  if (msg.event === 'mode') return setMode(msg.mode);
  if (msg.event === 'voice') return setVoiceState(msg.state, msg.progress);
  if (msg.event === 'voice-trigger') return startVoice();
  if (msg.event === 'command-hotkey') return runQuickHotkey(msg.index);
  if (msg.event === 'login') return onBrowserLogin(msg);
  const p = pending.get(msg.id);
  if (!p) return;
  pending.delete(msg.id);
  if (msg.ok) return p.resolve(msg.result);
  const err = new Error(msg.error);
  err.code = msg.code;
  p.reject(err);
});

// ---------- state ----------
const store = {
  get(key, fallback) {
    try { const v = localStorage.getItem(key); return v ? JSON.parse(v) : fallback; } catch { return fallback; }
  },
  set(key, value) {
    try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* storage unavailable */ }
  },
};

const state = {
  mode: 'compact',
  tab: store.get('tab', 'command'),
  devices: [],
  echos: [],
  endpoints: [],
  notifications: [],
  notificationsAt: 0,
  history: store.get('history', []),
  selected: store.get('device', null),
};

const CATEGORY = {
  LIGHT: 'Luz', SMARTPLUG: 'Tomada', SWITCH: 'Interruptor', FAN: 'Ventilador', TV: 'TV',
  AIR_CONDITIONER: 'Ar-condicionado', THERMOSTAT: 'Termostato', SPEAKER: 'Caixa de som',
  GAME_CONSOLE: 'Console', SMARTLOCK: 'Fechadura', VACUUM_CLEANER: 'Aspirador', OTHER: 'Dispositivo',
};
const RECURRENCE = {
  P1D: 'Todos os dias', 'XXXX-WD': 'Dias úteis', 'XXXX-WE': 'Fins de semana',
  'XXXX-WXX-1': 'Segundas', 'XXXX-WXX-2': 'Terças', 'XXXX-WXX-3': 'Quartas', 'XXXX-WXX-4': 'Quintas',
  'XXXX-WXX-5': 'Sextas', 'XXXX-WXX-6': 'Sábados', 'XXXX-WXX-7': 'Domingos',
};

// ---------- helpers ----------
function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v == null || v === false) continue;
    if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'class') el.className = v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  el.append(...children.flat().filter(c => c != null && c !== false));
  return el;
}

function icon(name) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
  use.setAttribute('href', '#i-' + name);
  svg.append(use);
  return svg;
}

let toastTimer;
function toast(text, error = false) {
  const el = $('#toast');
  el.textContent = text;
  el.className = error ? 'error' : '';
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (el.hidden = true), error ? 6000 : 2500);
}

function fail(err) {
  if (err.code === 'login') return showLogin();
  toast(err.message, err.code !== 'rate');
}

// Background refreshes stay quiet when Amazon throttles; the wait doubles on each refusal (5 → 10 → 20 → 40 min).
// /api/notifications is throttled per account, so it is asked as rarely as the UI allows.
const BASE_POLL_MS = 5 * 60_000;
const poll = { delay: BASE_POLL_MS, nextAt: Date.now() + BASE_POLL_MS };

async function backgroundRefresh() {
  try {
    await loadAlarms();
    if (state.tab === 'home') await loadHome();
    poll.delay = BASE_POLL_MS;
  } catch (err) {
    if (err.code === 'rate') poll.delay = Math.min(poll.delay * 2, 8 * BASE_POLL_MS);
    else fail(err);
  }
  poll.nextAt = Date.now() + poll.delay;
}

const fmtTime = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit' });
const fmtDay = new Intl.DateTimeFormat('pt-BR', { weekday: 'short', day: '2-digit', month: 'short' });

function dayLabel(ts) {
  const d = new Date(ts);
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const diff = Math.round((new Date(d).setHours(0, 0, 0, 0) - today) / 864e5);
  if (diff === 0) return 'Hoje';
  if (diff === 1) return 'Amanhã';
  return fmtDay.format(d);
}

function countdown(ms) {
  const s = Math.max(0, Math.round(ms / 1000));
  const hh = Math.floor(s / 3600), mm = Math.floor((s % 3600) / 60), ss = s % 60;
  const pad = n => String(n).padStart(2, '0');
  return hh ? `${hh}:${pad(mm)}:${pad(ss)}` : `${pad(mm)}:${pad(ss)}`;
}

// ---------- mode / navigation ----------
function setMode(mode) {
  state.mode = mode;
  document.body.className = mode;
  const btn = $('#btnMode');
  btn.title = mode === 'app' ? 'Voltar ao widget' : 'Abrir como app';
  btn.querySelector('use').setAttribute('href', mode === 'app' ? '#i-collapse' : '#i-expand');
  if (mode === 'app' && state.tab === 'more') selectTab('skills');
  else markActiveTab();
}

// In the widget these live under "Mais"; in the app window they have their own sidebar entries.
const MORE_TABS = new Set(['skills', 'music', 'echos', 'settings']);

function markActiveTab() {
  const tab = state.mode === 'compact' && MORE_TABS.has(state.tab) ? 'more' : state.tab;
  for (const b of document.querySelectorAll('[data-tab]')) b.classList.toggle('active', b.dataset.tab === tab);
}

function selectTab(tab) {
  state.tab = tab;
  store.set('tab', tab);
  markActiveTab();
  for (const s of document.querySelectorAll('.panel')) s.hidden = s.id !== 'tab-' + tab;
  $('main').scrollTop = 0;
  // Each section refreshes itself when opened, so there is no refresh button to remember.
  if (tab === 'home' || tab === 'command') loadHome().catch(fail);
  if (tab === 'alarms' && Date.now() - state.notificationsAt > 120_000) loadAlarms(true).catch(fail);
  if (tab === 'settings') loadPrefs();
  if (tab === 'routines') loadAlexaRoutines();
  if (tab === 'music') loadMusic();
}

function showLogin() {
  $('#app').hidden = true;
  $('#login').hidden = false;
  $('#btnRefresh').hidden = true;
}

function showApp() {
  $('#login').hidden = true;
  $('#app').hidden = false;
  $('#btnRefresh').hidden = false;
  selectTab(state.mode === 'app' && state.tab === 'more' ? 'command' : state.tab);
}

// ---------- devices / command ----------
function isEcho(d) {
  if (['WHA', 'VOX', 'UNKNOWN'].includes(d.deviceFamily)) return false;
  return ['ECHO', 'KNIGHT', 'ROOK'].includes(d.deviceFamily) || (d.capabilities || []).includes('VOLUME_SETTING');
}

async function loadDevices() {
  const res = await call('devices');
  state.devices = res?.devices || [];
  state.echos = state.devices
    .filter(isEcho)
    .sort((a, b) => (b.online - a.online) || a.accountName.localeCompare(b.accountName, 'pt-BR'));
  if (!state.echos.some(d => d.serialNumber === state.selected)) {
    state.selected = state.echos.find(d => d.online)?.serialNumber ?? state.echos[0]?.serialNumber ?? null;
  }
  renderDevices();
}

function renderDevices() {
  const sel = $('#device');
  sel.replaceChildren(...state.echos.map(d =>
    h('option', { value: d.serialNumber, selected: d.serialNumber === state.selected },
      d.accountName + (d.online ? '' : ' (offline)'))));
  if (!state.echos.length) sel.append(h('option', { value: '' }, 'Nenhum Echo encontrado'));

  $('#echoList').replaceChildren(...(state.echos.length ? state.echos.map(d =>
    h('li', { class: 'row' + (d.online ? '' : ' offline') },
      h('span', { class: 'badge' }, icon('speaker')),
      h('div', { class: 'body' },
        h('div', { class: 'title' }, d.accountName),
        h('div', { class: 'sub' }, h('span', { class: 'dot' + (d.online ? ' ok' : '') }), d.online ? 'Online' : 'Offline', ' · ', d.deviceFamily)),
      h('button', { class: 'subtle', disabled: !d.online, onclick: () => useAsSpeaker(d) }, 'Caixa do PC'),
      h('button', { class: 'subtle', onclick: () => { pickDevice(d.serialNumber); selectTab('command'); $('#cmdText').focus(); } }, 'Usar'))
  ) : [h('li', { class: 'empty' }, 'Nenhum Echo na conta.')]));
}

function pickDevice(serial) {
  state.selected = serial;
  store.set('device', serial);
  renderDevices();
}

/** Resolves to true when the command was carried out. */
async function sendCommand(text) {
  text = text.trim();
  if (!text) return false;
  const d = state.echos.find(x => x.serialNumber === state.selected);
  if (!d) {
    toast('Escolha um dispositivo Echo.', true);
    return false;
  }
  const btn = $('#cmdForm button[type=submit]');
  btn.disabled = true;
  try {
    // A widget routine phrase runs the routine; "ligar/desligar <device>" switches it directly;
    // anything else goes to Alexa rewritten into the form it understands best (see phrases.js).
    const resolved = resolveCommand(text);
    let sent;
    if (resolved.routine) {
      $('#cmdText').value = '';
      sent = `rotina "${resolved.routine.name}"`;
      await runWidgetRoutine(resolved.routine, 'frase');
    } else if (resolved.power) {
      const { id, name, on } = resolved.power;
      await call('power', { id, on });
      const ep = state.endpoints.find(e => e.id === id);
      if (ep) { ep.power = on; renderHome(); }
      sent = `${on ? 'ligou' : 'desligou'} ${name} (direto)`;
      toast(`${name} ${on ? 'ligado' : 'desligado'}`);
    } else {
      await call('command', { serial: d.serialNumber, deviceType: d.deviceType, customerId: d.deviceOwnerCustomerId, text: resolved.text });
      sent = resolved.text;
      toast(resolved.changed && Phrases.fold(resolved.text) !== Phrases.fold(text)
        ? `Enviado como "${resolved.text}"` : `Enviado para ${d.accountName}`);
    }
    state.history = [{ text, sent, device: d.accountName, at: Date.now() }, ...state.history.filter(x => x.text !== text)].slice(0, 30);
    store.set('history', state.history);
    renderHistory();
    $('#cmdText').value = '';
    return true;
  } catch (err) {
    fail(err);
    return false;
  } finally {
    btn.disabled = false;
  }
}

function renderHistory() {
  $('#btnClearHistory').hidden = !state.history.length;
  const shown = state.history.slice(0, state.mode === 'compact' ? 5 : 30);
  $('#history').replaceChildren(...(shown.length ? shown.map(item =>
    h('li', {}, h('button', { class: 'row', title: 'Enviar de novo', onclick: () => sendCommand(item.text) },
      h('div', { class: 'body' },
        h('div', { class: 'title' }, item.text),
        h('div', { class: 'sub' }, [
          item.sent && Phrases.fold(item.sent) !== Phrases.fold(item.text) ? `→ ${item.sent}` : null,
          item.device, `${dayLabel(item.at)} ${fmtTime.format(item.at)}`,
        ].filter(Boolean).join(' · '))))))
    : [h('li', { class: 'empty' }, 'Os comandos enviados aparecem aqui.')]));
}

// ---------- voice: microphone → offline transcription (Whisper) → text command ----------
let voiceActive = false;

function beep(frequency) {
  try {
    const ctx = new AudioContext();
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.frequency.value = frequency;
    gain.gain.setValueAtTime(0.08, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.18);
    osc.connect(gain).connect(ctx.destination);
    osc.start();
    osc.stop(ctx.currentTime + 0.18);
    osc.onended = () => ctx.close();
  } catch { /* audio output unavailable */ }
}

function setVoiceState(stateName, progress) {
  const mic = $('#btnMic');
  const status = $('#voiceStatus');
  mic.classList.toggle('listening', stateName === 'listening');
  mic.classList.toggle('working', ['preparing', 'transcribing', 'downloading'].includes(stateName));
  // Only visible while the microphone is in use.
  status.hidden = !stateName;
  status.classList.toggle('active', !!stateName);
  status.textContent = {
    preparing: 'Preparando o microfone… (clique de novo para cancelar)',
    downloading: `Baixando o modelo de voz (só na primeira vez): ${progress ?? 0}%`,
    listening: 'Ouvindo… fale agora. Clique de novo para parar.',
    transcribing: 'Entendendo o que você disse…',
  }[stateName] ?? '';
  if (stateName === 'listening') beep(880);
}

async function startVoice() {
  if (voiceActive) return call('stopListening');
  if ($('#app').hidden) return;
  selectTab('command');
  const d = state.echos.find(x => x.serialNumber === state.selected);
  if (!d) return toast('Escolha um dispositivo Echo.', true);
  voiceActive = true;
  setVoiceState('preparing');
  try {
    const text = await call('listen');
    if (!text) return toast('Nada para enviar. Se você falou, tente mais perto do microfone.');
    beep(660);
    $('#cmdText').value = text;
    await sendCommand(text);
  } catch (err) {
    fail(err);
  } finally {
    voiceActive = false;
    setVoiceState(null);
  }
}

// ---------- settings: shortcut and mouse button ----------
function renderPrefs(prefs) {
  $('#hotkeyLabel').textContent = prefs.hotkeyLabel || 'Nenhum';
  $('#hotkeyLabel').classList.remove('recording');
  $('#mouseButton').value = prefs.mouseButton || '';
}

async function loadPrefs() {
  try { renderPrefs(await call('prefs')); } catch (err) { fail(err); }
}

const KEY_NAMES = { 32: 'Espaço', 13: 'Enter', 9: 'Tab', 192: "'", 186: 'Ç', 188: ',', 190: '.', 191: ';', 189: '-', 187: '=' };

/**
 * Waits for the next key combination and resolves to {modifiers, key, label}, or null on Esc/invalid.
 * Modifiers use RegisterHotKey's bits: Alt 1, Ctrl 2, Shift 4. A plain key needs a modifier unless it is F1–F24.
 */
function captureHotkey(labelEl) {
  labelEl.textContent = 'Pressione a combinação (Esc cancela)…';
  labelEl.classList.add('recording');
  return new Promise(resolve => {
    const onKey = e => {
      e.preventDefault();
      e.stopPropagation();
      if (['Control', 'Alt', 'Shift', 'Meta'].includes(e.key)) return; // wait for the actual key
      document.removeEventListener('keydown', onKey, true);
      labelEl.classList.remove('recording');
      if (e.key === 'Escape') return resolve(null);
      const isFunctionKey = e.keyCode >= 112 && e.keyCode <= 135;
      const modifiers = (e.altKey ? 1 : 0) | (e.ctrlKey ? 2 : 0) | (e.shiftKey ? 4 : 0);
      if (!modifiers && !isFunctionKey) {
        toast('Use Ctrl, Alt ou Shift junto com a tecla (ou uma tecla F1–F24).', true);
        return resolve(null);
      }
      const keyName = isFunctionKey ? `F${e.keyCode - 111}` : KEY_NAMES[e.keyCode] ?? (e.key.length === 1 ? e.key.toUpperCase() : e.key);
      const label = [e.ctrlKey && 'Ctrl', e.altKey && 'Alt', e.shiftKey && 'Shift', keyName].filter(Boolean).join('+');
      resolve({ modifiers, key: e.keyCode, label });
    };
    document.addEventListener('keydown', onKey, true);
  });
}

async function recordHotkey() {
  const combo = await captureHotkey($('#hotkeyLabel'));
  if (!combo) return loadPrefs();
  try {
    renderPrefs(await call('setHotkey', combo));
    toast(`Atalho definido: ${combo.label}`);
  } catch (err) {
    fail(err);
    loadPrefs();
  }
}

// ---------- Echo as the PC's Bluetooth speaker ----------
const sheet = {
  open(title) {
    $('#sheetTitle').textContent = title;
    $('#sheetSteps').replaceChildren();
    $('#sheetChoices').replaceChildren();
    $('#sheetResult').textContent = '';
    $('#sheetClose').disabled = true;
    $('#sheet').hidden = false;
  },
  step(text) {
    const li = h('li', { class: 'run' }, text);
    $('#sheetSteps').append(li);
    return {
      ok: label => { li.className = 'ok'; if (label) li.textContent = label; },
      fail: label => { li.className = 'err'; if (label) li.textContent = label; },
    };
  },
  choose(items) {
    return new Promise(resolve => {
      $('#sheetChoices').replaceChildren(...items.map(item =>
        h('li', {}, h('button', { class: 'row', onclick: () => { $('#sheetChoices').replaceChildren(); resolve(item); } },
          h('span', { class: 'badge' }, icon('speaker')),
          h('div', { class: 'body' }, h('div', { class: 'title' }, item.name), h('div', { class: 'sub' }, 'Toque para parear'))))));
    });
  },
  finish(text) {
    $('#sheetResult').textContent = text;
    $('#sheetClose').disabled = false;
  },
};

async function useAsSpeaker(d) {
  if (!d) return toast('Escolha um dispositivo Echo.', true);
  sheet.open(`Caixa de som do PC: ${d.accountName}`);
  let current = sheet.step('Verificando pareamentos antigos no Windows…');
  let pairingModeOn = false;
  try {
    const paired = await call('btPaired');
    current.ok(paired.length ? `O Windows tem ${paired.length} Echo(s) pareado(s)` : 'Nenhum pareamento antigo');
    for (const p of paired) {
      const stale = confirm(`O Windows já tem "${p.name}" pareado.\n\nSe esse Echo foi resetado ou reconfigurado, esse pareamento não funciona mais. Remover e parear de novo?`);
      if (!stale) continue;
      current = sheet.step(`Removendo "${p.name}" do Windows…`);
      await call('btUnpair', { id: p.id });
      current.ok(`"${p.name}" removido do Windows`);
    }

    current = sheet.step(`Colocando ${d.accountName} em modo de pareamento…`);
    await call('btPairingMode', {
      serial: d.serialNumber, deviceType: d.deviceType, softwareVersion: String(d.softwareVersion ?? '0'),
      customerId: d.deviceOwnerCustomerId,
    });
    pairingModeOn = true;
    current.ok(`${d.accountName} em modo de pareamento`);

    current = sheet.step('Procurando o Echo pelo Bluetooth do PC (até 30 s)…');
    const found = await call('btDiscover');
    if (!found.length) throw new Error('Nenhum Echo apareceu no Bluetooth. Deixe o Echo perto do PC, diga "Alexa, parear" e tente de novo.');
    current.ok(found.length === 1 ? `Encontrado: "${found[0].name}"` : `${found.length} Echos encontrados. Escolha qual parear:`);
    const target = found.length === 1 ? found[0] : await sheet.choose(found);

    current = sheet.step(`Pareando com "${target.name}"…`);
    await call('btPair', { id: target.id });
    pairingModeOn = false;
    current.ok(`Pareado com "${target.name}"`);
    sheet.finish(`Pronto! O som do Windows deve passar para o Echo. Se não trocar sozinho, clique no ícone de som da barra de tarefas e escolha "${target.name}". Para desconectar, diga "Alexa, desconectar".`);
  } catch (err) {
    current.fail(err.message);
    if (pairingModeOn) call('btCancelPairingMode', { serial: d.serialNumber, deviceType: d.deviceType }).catch(() => {});
    sheet.finish(err.code === 'login' ? 'Sua sessão expirou. Entre de novo.' : '');
    if (err.code === 'login') showLogin();
  }
}

// ---------- smart home ----------
function shapeEndpoint(e) {
  let hasPower = false, power = null, reachable = true, brightness = null, battery = null;
  const modes = [];
  const operations = new Set();
  for (const f of e.features || []) {
    for (const o of f.operations || []) operations.add(o.name);
    if (f.name === 'power') hasPower = true;
    for (const p of f.properties || []) {
      if (p?.powerStateValue) power = p.powerStateValue === 'ON';
      if (p?.reachabilityStatusValue) reachable = p.reachabilityStatusValue !== 'NOT_REACHABLE';
      if (p?.brightnessStateValue != null) brightness = p.brightnessStateValue;
      if (p?.modeValue?.value) modes.push({ instance: f.instance, value: p.modeValue.value });
      if (p?.rangeValue?.value != null && f.instance === 'battery') battery = Math.round(p.rangeValue.value);
    }
  }
  const features = new Set((e.features || []).map(f => f.name));
  return {
    id: e.id,
    name: e.friendlyNameObject?.value?.text || 'Sem nome',
    category: e.displayCategories?.primary?.value || 'OTHER',
    hasPower, power, reachable, brightness, battery, modes,
    hasBrightness: operations.has('setBrightness') || features.has('brightness'),
    hasColor: features.has('color'),
    hasColorTemperature: features.has('colorTemperature'),
  };
}

let homeLoading = null;
function loadHome() {
  // Shared in-flight request: several screens ask for it at once.
  homeLoading ??= call('smarthome').then(endpoints => {
    state.endpoints = (endpoints || []).map(shapeEndpoint)
      .filter(e => e.category !== 'ALEXA_VOICE_ENABLED')
      .sort((a, b) => a.name.localeCompare(b.name, 'pt-BR'));
    renderHome();
    renderPinned();
  }).finally(() => (homeLoading = null));
  return homeLoading;
}

function renderHome() {
  const q = $('#homeSearch').value.trim().toLocaleLowerCase('pt-BR');
  const items = state.endpoints.filter(e => !q || e.name.toLocaleLowerCase('pt-BR').includes(q));
  $('#homeSearch').hidden = state.endpoints.length < 7;
  $('#homeList').replaceChildren(...(items.length ? items.map(e =>
    h('li', { class: 'row' + (e.power ? ' on' : '') + (e.reachable ? '' : ' offline') },
      h('span', { class: 'badge' }, icon(deviceIcon(e))),
      h('div', { class: 'body' },
        h('div', { class: 'title' }, e.name),
        h('div', { class: 'sub' }, [CATEGORY[e.category] || CATEGORY.OTHER, e.battery != null && `bateria ${e.battery}%`,
          !e.reachable && 'sem resposta'].filter(Boolean).join(' · '))),
      e.hasPower ? h('button', {
        class: 'switch', role: 'switch', 'aria-checked': String(!!e.power), 'aria-label': e.name,
        disabled: !e.reachable, onclick: () => togglePower(e),
      }) : null,
      h('button', { class: 'icon small more', title: 'Opções do dispositivo', 'aria-label': `Opções de ${e.name}`, onclick: () => openDevice(e) }, icon('more'))))
    : [h('li', { class: 'empty' }, state.endpoints.length ? 'Nada encontrado.' : 'Nenhum dispositivo de casa inteligente.')]));
}

function deviceIcon(e) {
  return e.category === 'LIGHT' ? 'bulb' : e.category === 'SPEAKER' ? 'speaker' : 'plug';
}

async function togglePower(e) {
  const target = !e.power;
  e.power = target;
  renderHome();
  renderPinned();
  try {
    await call('power', { id: e.id, on: target });
  } catch (err) {
    e.power = !target;
    renderHome();
    renderPinned();
    fail(err);
  }
}

// ---------- alarms ----------
const KINDS = [
  { type: 'Timer', title: 'Timers' },
  { type: 'Alarm', title: 'Alarmes', also: ['MusicAlarm'] },
  { type: 'Reminder', title: 'Lembretes' },
];

function whenOf(n) {
  if (n.type === 'Timer') return n.remainingTime != null ? state.notificationsAt + n.remainingTime : null;
  if (n.alarmTime) return n.alarmTime;
  if (n.originalDate && n.originalTime) return new Date(`${n.originalDate}T${n.originalTime.slice(0, 8)}`).getTime();
  return null;
}

async function loadAlarms(fresh = false) {
  let res;
  try {
    res = await call('notifications', { fresh });
  } catch (err) {
    if (err.code === 'rate') {
      state.alarmsThrottled = true;
      renderAlarms();
    }
    throw err;
  }
  state.alarmsThrottled = false;
  state.notificationsAt = Date.now();
  state.notifications = (res?.notifications || [])
    .filter(n => n.status === 'ON')
    .map(n => ({ ...n, raw: n, when: whenOf(n) }))
    .filter(n => n.when != null)
    .sort((a, b) => a.when - b.when);
  renderAlarms();
}

function renderAlarms() {
  const names = Object.fromEntries(state.devices.map(d => [d.serialNumber, d.accountName]));
  const limit = state.mode === 'compact' ? 6 : Infinity;
  const sections = KINDS.map(kind => {
    const items = state.notifications.filter(n => n.type === kind.type || kind.also?.includes(n.type)).slice(0, limit);
    if (!items.length) return null;
    return [
      h('h3', {}, kind.title),
      h('ul', { class: 'list' }, items.map(n => {
        const label = n.type === 'Reminder' ? (n.reminderLabel || 'Lembrete')
          : n.type === 'Timer' ? (n.timerLabel || 'Timer')
          : (RECURRENCE[n.recurringPattern] || (n.type === 'MusicAlarm' ? 'Alarme com música' : 'Alarme'));
        const time = n.type === 'Timer'
          ? h('span', { class: 'time', 'data-until': n.when }, countdown(n.when - Date.now()))
          : h('span', { class: 'time' }, fmtTime.format(n.when));
        return h('li', { class: 'row' },
          h('span', { class: 'badge' }, icon('alarm')),
          h('div', { class: 'body' },
            h('div', { class: 'title' }, label),
            h('div', { class: 'sub' }, [n.type === 'Timer' ? null : dayLabel(n.when), names[n.deviceSerialNumber]].filter(Boolean).join(' · '))),
          h('div', { class: 'end' }, time),
          h('button', { class: 'icon small danger', title: 'Excluir', onclick: () => deleteAlarm(n) }, '✕'));
      })),
    ];
  }).filter(Boolean);
  const loaded = state.notificationsAt > 0;
  const status = state.alarmsThrottled
    ? h('p', { class: 'status warn' },
      `A Amazon está limitando a consulta de alarmes${loaded ? `; mostrando a lista de ${fmtTime.format(state.notificationsAt)}` : ''}. O widget tenta de novo sozinho em alguns minutos.`)
    : loaded ? h('p', { class: 'status' }, `Atualizado às ${fmtTime.format(state.notificationsAt)}`) : null;
  const empty = loaded ? 'Nenhum alarme, timer ou lembrete ativo.' : 'Alarmes ainda não carregados.';
  $('#alarmList').replaceChildren(...[status, ...(sections.length ? sections.flat() : [h('p', { class: 'empty' }, empty)])].filter(Boolean));
}

setInterval(() => {
  for (const el of document.querySelectorAll('[data-until]')) el.textContent = countdown(+el.dataset.until - Date.now());
}, 1000);

// ---------- refresh ----------
async function refreshAll() {
  const btn = $('#btnRefresh');
  btn.classList.add('spin');
  try {
    await loadDevices();
    // Settle both so a throttled alarms call doesn't hide the smart-home result (or vice versa).
    // Smart-home devices are always loaded: the phrase normalizer needs their exact names.
    const results = await Promise.allSettled([loadAlarms(true), loadHome()]);
    const failed = results.find(r => r.status === 'rejected');
    if (failed) throw failed.reason;
    poll.delay = BASE_POLL_MS;
    poll.nextAt = Date.now() + poll.delay;
  } catch (err) {
    fail(err);
  } finally {
    btn.classList.remove('spin');
  }
}

setInterval(() => {
  if ($('#app').hidden || document.hidden || Date.now() < poll.nextAt) return;
  poll.nextAt = Infinity; // one refresh at a time
  backgroundRefresh();
}, 15_000);

// ---------- wiring ----------
$('#btnRefresh').addEventListener('click', refreshAll);
$('#btnMode').addEventListener('click', () => call('mode', { mode: state.mode === 'app' ? 'compact' : 'app' }).catch(fail));
$('#btnHide').addEventListener('click', () => call('hide'));
$('#btnMin').addEventListener('click', () => call('minimize'));
for (const b of document.querySelectorAll('[data-goto]')) b.addEventListener('click', () => selectTab(b.dataset.goto));
$('#btnMic').addEventListener('click', startVoice);
$('#btnRecordHotkey').addEventListener('click', recordHotkey);
$('#btnClearHotkey').addEventListener('click', async () => {
  try { renderPrefs(await call('clearHotkey')); } catch (err) { fail(err); }
});
$('#mouseButton').addEventListener('change', async e => {
  try {
    renderPrefs(await call('setMouseButton', { button: e.target.value }));
    if (e.target.value) toast('Botão do mouse definido.');
  } catch (err) {
    fail(err);
    loadPrefs();
  }
});
async function finishLogin(btn, method, params) {
  btn.disabled = true;
  try {
    const s = await call(method, params);
    if (s.loggedIn) {
      $('#pasteForm').hidden = true;
      $('#pasteUrl').value = '';
      showApp();
      await refreshAll();
    }
  } catch (err) {
    toast(err.message, true);
  } finally {
    btn.disabled = false;
  }
}

$('#btnLogin').addEventListener('click', () => finishLogin($('#btnLogin'), 'login'));
$('#btnLoginBrowser').addEventListener('click', async () => {
  try {
    await call('loginBrowser');
    $('#pasteForm').hidden = false;
    $('#pasteUrl').focus();
  } catch (err) {
    toast(err.message, true);
  }
});
$('#pasteForm').addEventListener('submit', e => {
  e.preventDefault();
  finishLogin($('#pasteForm button'), 'loginPaste', { url: $('#pasteUrl').value });
});
// The app finished a browser sign-in by itself (the user copied the final address).
async function onBrowserLogin(msg) {
  if (!msg.ok) return toast(msg.error, true);
  $('#pasteForm').hidden = true;
  $('#pasteUrl').value = '';
  toast('Conectado à sua conta Amazon');
  showApp();
  await refreshAll();
}

$('#btnLogout').addEventListener('click', async () => {
  if (!confirm('Sair da conta Amazon neste PC? O registro "Alexa Widget (PC)" também sai da sua conta Amazon.')) return;
  try {
    const result = await call('logout');
    state.devices = state.echos = state.endpoints = state.notifications = [];
    showLogin();
    if (!result.deviceRemoved) {
      toast('Você saiu, mas não foi possível remover "Alexa Widget (PC)" da conta. Remova em amazon.com.br → Gerenciar dispositivos.', true);
    }
  } catch (err) {
    fail(err);
  }
});
for (const b of document.querySelectorAll('[data-tab]')) b.addEventListener('click', () => selectTab(b.dataset.tab));
$('#device').addEventListener('change', e => pickDevice(e.target.value));
$('#sheetClose').addEventListener('click', () => ($('#sheet').hidden = true));
$('#cmdForm').addEventListener('submit', e => { e.preventDefault(); sendCommand($('#cmdText').value); });
$('#homeSearch').addEventListener('input', renderHome);
$('#modal').addEventListener('click', e => { if (e.target.id === 'modal') closeModal(); });
$('#btnNewRoutine').addEventListener('click', () => editRoutine(null));
$('#btnQuickHotkeys').addEventListener('click', openQuickHotkeys);
$('#btnClearHistory').addEventListener('click', () => {
  if (!state.history.length || !confirm('Limpar todos os comandos recentes?')) return;
  state.history = [];
  store.set('history', state.history);
  renderHistory();
});
$('#btnAddAlarm').addEventListener('click', addAlarm);
$('#phraseAuto').addEventListener('change', e => { userData.phrase.auto = e.target.checked; savePhrase(); });
$('#fixForm').addEventListener('submit', e => {
  e.preventDefault();
  const from = $('#fixFrom').value.trim();
  const to = $('#fixTo').value.trim();
  if (!from || !to) return toast('Preencha as duas frases.', true);
  userData.phrase.fixes = [{ from, to }, ...userData.phrase.fixes.filter(f => Phrases.fold(f.from) !== Phrases.fold(from))];
  $('#fixFrom').value = $('#fixTo').value = '';
  savePhrase();
});
$('#phraseTest').addEventListener('input', e => {
  const r = e.target.value.trim() ? resolveCommand(e.target.value) : null;
  $('#phraseTestResult').textContent = !r ? '' : r.routine ? `→ executa a rotina "${r.routine.name}"`
    : r.power ? `→ ${r.power.on ? 'liga' : 'desliga'} "${r.power.name}" direto (sem passar pela Alexa)` : `→ envia "${r.text}"`;
});
renderHistory();

(async function init() {
  await initFeatures();
  const s = await call('state');
  setMode(s.mode);
  if (!s.loggedIn) return showLogin();
  showApp();
  await refreshAll();
})().catch(fail);
