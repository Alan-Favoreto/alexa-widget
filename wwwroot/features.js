'use strict';
// Editable quick commands, widget routines, Alexa-app routines, creating/deleting alarms and reminders,
// and the phrase-fix settings. Loaded before app.js; everything here runs only after initFeatures().

const DEFAULT_QUICK = ['Que horas são?', 'Como vai estar o tempo hoje?', 'Apague todas as luzes', 'Toque música relaxante', 'Pare'];
const WEEKDAYS = ['D', 'S', 'T', 'Q', 'Q', 'S', 'S'];
const WEEKDAY_NAMES = ['dom', 'seg', 'ter', 'qua', 'qui', 'sex', 'sáb'];
const ACTION_KINDS = [
  ['command', 'Comando para a Alexa'],
  ['speak', 'Alexa diz'],
  ['wait', 'Esperar'],
  ['on', 'Ligar dispositivo'],
  ['off', 'Desligar dispositivo'],
];

// Quick commands are {text, hotkey?: {modifiers, key, label}}; older saves were plain strings.
const toQuick = list => list.map(x => (typeof x === 'string' ? { text: x } : x)).filter(x => x?.text);

const userData = {
  quick: toQuick(DEFAULT_QUICK),
  phrase: { auto: true, fixes: [], music: '' },
  routines: [],
  pins: [],        // endpoint ids shown as buttons on the home screen
  deviceKeys: [],  // [{id, name, hotkey}] global shortcuts that switch a device on/off
};

const MUSIC_SERVICES = ['Amazon Music', 'Spotify', 'Deezer', 'Apple Music'];

async function initFeatures() {
  const [quick, phrase, routines, pins, deviceKeys] = await Promise.all(
    ['quick-commands', 'phrase-settings', 'routines', 'pinned-devices', 'device-hotkeys']
      .map(key => call('loadData', { key }).catch(() => null)));
  if (Array.isArray(quick)) userData.quick = toQuick(quick);
  if (phrase && typeof phrase === 'object') userData.phrase = { auto: phrase.auto !== false, fixes: phrase.fixes || [], music: phrase.music || '' };
  if (Array.isArray(routines)) userData.routines = routines;
  if (Array.isArray(pins)) userData.pins = pins;
  if (Array.isArray(deviceKeys)) userData.deviceKeys = deviceKeys;
  renderQuick();
  syncCommandHotkeys();
  renderWidgetRoutines();
  renderPhraseSettings();
  wireSkillsAndMusic();
  setInterval(runScheduledRoutines, 20_000);
}

function saveData(key, value) {
  return call('saveData', { key, value }).catch(fail);
}

// ---------- generic modal ----------
function openModal(title, body, actions) {
  $('#modalTitle').textContent = title;
  $('#modalBody').replaceChildren(body);
  $('#modalActions').replaceChildren(...actions.map(([label, cls, onclick]) => h('button', { class: cls, onclick }, label)));
  $('#modal').hidden = false;
  body.querySelector('input, textarea, select')?.focus();
}
function closeModal() { $('#modal').hidden = true; }

function editableList(items, makeRow) {
  const list = h('ul', { class: 'edit-list' });
  const render = () => list.replaceChildren(...items.map((item, i) => h('li', {},
    makeRow(item, i),
    h('button', { class: 'icon small', title: 'Subir', disabled: i === 0, onclick: () => { [items[i - 1], items[i]] = [items[i], items[i - 1]]; render(); } }, '↑'),
    h('button', { class: 'icon small', title: 'Descer', disabled: i === items.length - 1, onclick: () => { [items[i + 1], items[i]] = [items[i], items[i + 1]]; render(); } }, '↓'),
    h('button', { class: 'icon small danger', title: 'Remover', onclick: () => { items.splice(i, 1); render(); } }, '✕'))));
  render();
  return { list, render };
}

// ---------- quick commands ----------
function renderQuick() {
  $('#chips').replaceChildren(
    ...userData.quick.map(q => h('button', { type: 'button', title: q.hotkey ? `Atalho: ${q.hotkey.label}` : null, onclick: () => sendCommand(q.text) },
      q.text, q.hotkey ? h('kbd', {}, q.hotkey.label) : null)),
    h('button', { type: 'button', class: 'chip-edit', title: 'Editar comandos rápidos e atalhos', 'aria-label': 'Editar comandos rápidos', onclick: editQuick }, '✎'));
}

function editQuick() {
  const items = userData.quick.map(q => ({ ...q }));
  const { list, render } = editableList(items, (q) =>
    h('input', { value: q.text, maxlength: 120, oninput: e => (q.text = e.target.value) }));
  const add = h('input', { placeholder: 'Novo comando rápido', maxlength: 120 });
  const addIt = () => { if (add.value.trim()) { items.push({ text: add.value.trim() }); add.value = ''; render(); } };
  add.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); addIt(); } });
  openModal('Comandos rápidos', h('div', {}, list, h('div', { class: 'cmd' }, add, h('button', { class: 'subtle', onclick: addIt }, 'Adicionar'))), [
    ['⌨ Atalhos', 'subtle', () => { closeModal(); openQuickHotkeys(); }],
    ['Restaurar padrão', 'subtle', () => { items.splice(0, items.length, ...toQuick(DEFAULT_QUICK)); render(); }],
    ['Cancelar', 'subtle', closeModal],
    ['Salvar', 'primary', () => saveQuick(items.map(q => ({ ...q, text: q.text.trim() })).filter(q => q.text), true)],
  ]);
}

async function saveQuick(items, close) {
  userData.quick = items;
  await saveData('quick-commands', userData.quick);
  renderQuick();
  const failed = await syncCommandHotkeys();
  if (close && !failed.length) closeModal();
  return failed;
}

/** All shortcuts in registration order: quick commands first, then devices. */
function allHotkeyTargets() {
  return [
    ...userData.quick.map(q => ({ kind: 'command', label: q.text, hotkey: q.hotkey, item: q })),
    ...userData.deviceKeys.map(d => ({ kind: 'device', label: d.name, hotkey: d.hotkey, item: d })),
  ];
}

/** Registers every command/device shortcut with Windows; returns the targets whose combination was taken. */
async function syncCommandHotkeys(quiet = false) {
  const targets = allHotkeyTargets();
  try {
    const { failed } = await call('setCommandHotkeys', {
      items: targets.map(t => (t.hotkey ? { modifiers: t.hotkey.modifiers, key: t.hotkey.key } : null)),
    });
    if (failed.length && !quiet) {
      toast(`Atalho em uso por outro programa (ou pelo atalho de voz): ${failed.map(i => targets[i].hotkey.label).join(', ')}`, true);
    }
    return failed.map(i => targets[i].item);
  } catch (err) {
    fail(err);
    return [];
  }
}

/** One screen for every global shortcut: quick commands and smart-home devices (on/off). */
function openQuickHotkeys() {
  const body = h('div', {});
  const taken = combo => allHotkeyTargets().find(t => t.hotkey?.label === combo.label);

  const row = (title, item, failed, onSet, onClear) => {
    const label = h('div', { class: 'sub' + (failed.includes(item) ? ' danger' : '') },
      item.hotkey ? item.hotkey.label + (failed.includes(item) ? ' (em uso, não funciona)' : '') : 'Sem atalho');
    return h('li', { class: 'row' },
      h('div', { class: 'body' }, h('div', { class: 'title' }, title), label),
      h('button', { class: 'subtle', onclick: async () => {
        const combo = await captureHotkey(label);
        if (!combo) return render(failed);
        const other = taken(combo);
        if (other && other.item !== item) {
          toast(`${combo.label} já é o atalho de "${other.label}".`, true);
          return render(failed);
        }
        render(await onSet(combo));
      } }, item.hotkey ? 'Trocar' : 'Definir'),
      item.hotkey ? h('button', { class: 'icon small danger', title: 'Remover atalho', onclick: async () => render(await onClear()) }, '✕') : null);
  };

  const render = (failed = []) => body.replaceChildren(
    h('p', { class: 'hint' }, 'Funcionam em qualquer programa, mesmo com o widget oculto.'),
    h('h3', {}, 'Comandos rápidos'),
    h('ul', { class: 'list' }, ...(userData.quick.length ? userData.quick.map((q, i) => row(q.text, q, failed,
      combo => saveQuick(userData.quick.map((o, j) => (j === i ? { ...o, hotkey: combo } : o)), false),
      () => saveQuick(userData.quick.map((o, j) => (j === i ? { text: o.text } : o)), false)))
      : [h('li', { class: 'empty' }, 'Nenhum comando rápido.')])),
    h('h3', {}, 'Dispositivos (liga/desliga)'),
    h('ul', { class: 'list' }, ...(state.endpoints.filter(e => e.hasPower).length
      ? state.endpoints.filter(e => e.hasPower).map(e => {
        const item = userData.deviceKeys.find(d => d.id === e.id) ?? { id: e.id, name: e.name };
        return row(e.name, item, failed,
          combo => setDeviceHotkey(e, combo),
          () => setDeviceHotkey(e, null));
      })
      : [h('li', { class: 'empty' }, 'Nenhum dispositivo com liga/desliga.')])));
  render();
  openModal('Atalhos', body, [['Fechar', 'primary', closeModal]]);
  syncCommandHotkeys(true).then(render); // marks shortcuts another program has taken since
}

async function setDeviceHotkey(e, combo) {
  const others = userData.deviceKeys.filter(d => d.id !== e.id);
  userData.deviceKeys = combo ? [...others, { id: e.id, name: e.name, hotkey: combo }] : others;
  await saveData('device-hotkeys', userData.deviceKeys);
  return syncCommandHotkeys();
}

/** A command/device shortcut fired (possibly with the widget hidden). */
async function runQuickHotkey(index) {
  const target = allHotkeyTargets()[index];
  if (!target) return;
  let message;
  if (target.kind === 'command') {
    message = (await sendCommand(target.item.text)) ? `Enviado: ${target.item.text}` : `Falhou: ${target.item.text}`;
  } else {
    const e = state.endpoints.find(x => x.id === target.item.id);
    message = e ? await togglePower(e).then(() => `${e.name} ${e.power ? 'ligado' : 'desligado'}`) : `${target.item.name} não encontrado`;
  }
  if (document.hidden) call('notify', { text: message }).catch(() => {});
}

// ---------- pinned devices (home screen) ----------
function renderPinned() {
  const devices = userData.pins.map(id => state.endpoints.find(e => e.id === id)).filter(Boolean);
  $('#pinned').hidden = !devices.length;
  $('#pinned').replaceChildren(...devices.map(e => h('button', {
    class: 'pin' + (e.power ? ' on' : ''), title: e.hasPower ? 'Ligar/desligar' : 'Opções', disabled: !e.reachable,
    onclick: () => (e.hasPower ? togglePower(e) : openDevice(e)),
    oncontextmenu: ev => { ev.preventDefault(); openDevice(e); },
  }, icon(deviceIcon(e)), e.name)));
}

function togglePin(e) {
  userData.pins = userData.pins.includes(e.id) ? userData.pins.filter(id => id !== e.id) : [...userData.pins, e.id];
  saveData('pinned-devices', userData.pins);
  renderPinned();
}

// ---------- device "…" options ----------
const MODE_NAMES = { Mode: 'Modo', 'Fan.Speed': 'Potência', 'Wash.Cycle': 'Ciclo' };
const MODE_VALUES = {
  random: 'Aleatório', auto: 'Automático', normal: 'Normal', quiet: 'Silencioso', silent: 'Silencioso', turbo: 'Turbo',
  max: 'Máximo', strong: 'Forte', low: 'Baixo', medium: 'Médio', high: 'Alto', spot: 'Localizado', edge: 'Bordas',
  mop: 'Pano', vacuum: 'Aspirar', eco: 'Econômico',
};
const COLORS = [['vermelho', '#e5484d'], ['laranja', '#f76b15'], ['amarelo', '#ffc53d'], ['verde', '#30a46c'], ['azul', '#0090ff'], ['roxo', '#8e4ec6'], ['rosa', '#e93d82'], ['branco', '#f5f5f5']];

/** Speaks a phrase about this device through the normal pipeline (normalizer, Echo). */
const deviceCommand = text => sendCommand(text);

function openDevice(e) {
  const body = h('div', { class: 'form' });
  const render = () => {
    const pinned = userData.pins.includes(e.id);
    const key = userData.deviceKeys.find(d => d.id === e.id);
    body.replaceChildren(...[
      h('dl', { class: 'kv' },
        h('dt', {}, 'Tipo'), h('dd', {}, CATEGORY[e.category] || CATEGORY.OTHER),
        h('dt', {}, 'Estado'), h('dd', {}, !e.reachable ? 'Sem resposta' : e.hasPower ? (e.power ? 'Ligado' : 'Desligado') : 'Online'),
        e.battery != null ? [h('dt', {}, 'Bateria'), h('dd', {}, `${e.battery}%`)] : null,
        ...e.modes.map(m => [h('dt', {}, MODE_NAMES[m.instance] || m.instance || 'Modo'), h('dd', {}, MODE_VALUES[m.value] || m.value)])),
      e.hasPower ? h('div', { class: 'button-row' },
        h('button', { class: 'subtle', onclick: async () => { if (!e.power) await togglePower(e); render(); } }, 'Ligar'),
        h('button', { class: 'subtle', onclick: async () => { if (e.power) await togglePower(e); render(); } }, 'Desligar')) : null,
      e.hasBrightness ? h('label', { class: 'field' }, h('span', {}, `Brilho${e.brightness != null ? ` (${e.brightness}%)` : ''}`),
        h('input', { type: 'range', class: 'slider', min: 1, max: 100, value: e.brightness ?? 50, onchange: ev => setBrightness(e, +ev.target.value).then(render) })) : null,
      e.hasColor ? h('div', { class: 'field' }, h('span', {}, 'Cor'), h('div', { class: 'swatches' },
        ...COLORS.map(([name, hex]) => h('button', { class: 'swatch', title: name, style: `background:${hex}`, onclick: () => deviceCommand(`mudar ${e.name} para ${name}`) })))) : null,
      e.hasColorTemperature ? h('div', { class: 'field' }, h('span', {}, 'Tom de branco'), h('div', { class: 'button-row' },
        ...['quente', 'neutro', 'frio'].map(t => h('button', { class: 'subtle', onclick: () => deviceCommand(`definir ${e.name} para branco ${t}`) }, t)))) : null,
      h('form', { class: 'cmd', onsubmit: ev => { ev.preventDefault(); const t = ev.target.elements[0].value.trim(); if (t) deviceCommand(t); } },
        h('input', { placeholder: e.category === 'VACUUM_CLEANER' ? `Ex.: iniciar limpeza do ${e.name}` : `Outro comando para ${e.name}…`, maxlength: 200 }),
        h('button', { type: 'submit', class: 'subtle' }, 'Enviar')),
      h('div', { class: 'button-row' },
        h('button', { class: 'subtle', onclick: () => { togglePin(e); render(); } }, pinned ? '★ Fixado no Início' : '☆ Fixar no Início'),
        e.hasPower ? h('button', { class: 'subtle', onclick: async ev => {
          const combo = await captureHotkey(ev.target);
          if (combo) await setDeviceHotkey(e, combo);
          render();
        } }, key ? `⌨ ${key.hotkey.label}` : '⌨ Definir atalho') : null,
        key ? h('button', { class: 'icon small danger', title: 'Remover atalho', onclick: async () => { await setDeviceHotkey(e, null); render(); } }, '✕') : null),
    ].filter(Boolean));
  };
  render();
  openModal(e.name, body, [['Fechar', 'primary', closeModal]]);
}

/** Uses the app's setBrightness feature operation; falls back to asking Alexa in words. */
async function setBrightness(e, value) {
  try {
    await call('setFeature', { id: e.id, feature: 'brightness', operation: 'setBrightness', payload: { brightness: value } });
    e.brightness = value;
  } catch {
    await deviceCommand(`definir brilho de ${e.name} para ${value} por cento`);
  }
}

// ---------- skills & music ----------
function wireSkillsAndMusic() {
  $('#skillForm').addEventListener('submit', ev => {
    ev.preventDefault();
    call('openStore', { query: $('#skillQuery').value.trim() }).catch(fail);
  });
  $('#btnSkillStore').addEventListener('click', () => call('openStore', {}).catch(fail));
  $('#btnSkillVoice').addEventListener('click', () => {
    const name = $('#skillQuery').value.trim();
    if (!name) return toast('Digite o nome da skill.', true);
    sendCommand(`ativar a skill ${name}`);
  });
  $('#musicPref').addEventListener('change', ev => {
    userData.phrase.music = ev.target.value;
    savePhrase();
    toast(ev.target.value ? `Pedidos de música vão para ${ev.target.value}` : 'Usando o padrão da Alexa');
  });
  $('#musicLinks').replaceChildren(...MUSIC_SERVICES.map(s =>
    h('button', { class: 'subtle', onclick: () => call('openStore', { query: s }).catch(fail) }, s)));
}

const CONTENT_TYPES = { MUSIC: 'Música', PODCAST: 'Podcasts', STATION: 'Estações de rádio' };

async function loadMusic() {
  $('#musicPref').value = userData.phrase.music || '';
  try {
    const defaults = await call('musicDefaults');
    const map = defaults?.curatedMap || {};
    $('#musicDefaults').replaceChildren(...Object.entries(map).map(([type, providers]) => {
      const preferred = providers.find(p => p.preferred) ?? providers[0];
      const others = providers.filter(p => p !== preferred).map(p => p.guiFriendlyName);
      return h('li', { class: 'row' },
        h('div', { class: 'body' }, h('div', { class: 'title' }, CONTENT_TYPES[type] || type),
          h('div', { class: 'sub' }, others.length ? `também: ${others.join(', ')}` : 'único serviço conectado')),
        h('div', { class: 'end' }, preferred?.guiFriendlyName ?? '—'));
    }));
  } catch (err) {
    $('#musicDefaults').replaceChildren(h('li', { class: 'empty' }, 'Não foi possível carregar.'));
    fail(err);
  }
}

// ---------- phrase fixes (Ajustes) ----------
function renderPhraseSettings() {
  $('#phraseAuto').checked = userData.phrase.auto;
  const fixes = userData.phrase.fixes;
  $('#fixList').replaceChildren(...(fixes.length ? fixes.map((f, i) => h('li', { class: 'row' },
    h('div', { class: 'body' }, h('div', { class: 'title' }, `"${f.from}"`), h('div', { class: 'sub' }, `envia: "${f.to}"`)),
    h('button', { class: 'icon small danger', title: 'Remover', onclick: () => { fixes.splice(i, 1); savePhrase(); } }, '✕')))
    : [h('li', { class: 'empty' }, 'Nenhuma correção ainda.')]));
}

function savePhrase() {
  saveData('phrase-settings', userData.phrase);
  renderPhraseSettings();
}

/** Text → what is actually executed: a widget routine, a direct device switch, or a (normalized) text command. */
function resolveCommand(text) {
  const key = Phrases.normalize(text, { auto: false }).text;
  const routine = userData.routines.find(r => r.enabled && (r.phrases || []).some(p => Phrases.fold(p) === Phrases.fold(key)));
  if (routine) return { routine };
  const result = Phrases.normalize(text, { devices: state.endpoints, fixes: userData.phrase.fixes, auto: userData.phrase.auto });
  // "tocar …" goes to the chosen service unless the phrase already names one.
  const service = userData.phrase.music;
  if (service && !result.power && /^tocar\b/i.test(result.text)
      && !/\b(no|na|pelo|pela)\s+(spotify|deezer|amazon music|apple music|youtube|tunein)\b/i.test(result.text)) {
    result.text += ` no ${service}`;
    result.changed = true;
  }
  return result;
}

// ---------- widget routines ----------
function newId() { return Date.now().toString(36) + Math.random().toString(36).slice(2, 6); }

function routineSummary(r) {
  const parts = [];
  if (r.phrases?.length) parts.push(`"${r.phrases[0]}"${r.phrases.length > 1 ? ` +${r.phrases.length - 1}` : ''}`);
  if (r.time) parts.push(`${r.time} ${r.days?.length && r.days.length < 7 ? r.days.map(d => WEEKDAY_NAMES[d]).join(',') : 'todo dia'}`);
  parts.push(`${r.actions.length} ação${r.actions.length === 1 ? '' : 'ões'}`);
  return parts.join(' · ');
}

function renderWidgetRoutines() {
  const list = userData.routines;
  $('#widgetRoutines').replaceChildren(...(list.length ? list.map(r => h('li', { class: 'row' + (r.enabled ? '' : ' offline') },
    h('button', { class: 'icon', title: 'Executar agora', onclick: () => runWidgetRoutine(r, 'manual') }, '▶'),
    h('div', { class: 'body' }, h('div', { class: 'title' }, r.name), h('div', { class: 'sub' }, routineSummary(r))),
    h('button', { class: 'subtle', onclick: () => editRoutine(r) }, 'Editar'),
    h('button', { class: 'switch', role: 'switch', 'aria-checked': String(r.enabled), 'aria-label': 'Ativa', onclick: () => {
      r.enabled = !r.enabled;
      saveData('routines', list);
      renderWidgetRoutines();
    } })))
    : [h('li', { class: 'empty' }, 'Nenhuma rotina do widget. Crie uma em "+ Nova".')]));
}

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

async function runWidgetRoutine(r, reason) {
  const echo = state.echos.find(e => e.serialNumber === r.serial) ?? state.echos.find(e => e.serialNumber === state.selected);
  if (!echo) return toast('Escolha um Echo para a rotina.', true);
  const target = { serial: echo.serialNumber, deviceType: echo.deviceType, customerId: echo.deviceOwnerCustomerId };
  toast(`Rotina "${r.name}" iniciada`);
  try {
    for (const a of r.actions) {
      if (a.kind === 'wait') await sleep((a.seconds || 5) * 1000);
      else if (a.kind === 'speak') await call('speak', { ...target, text: a.text });
      else if (a.kind === 'on' || a.kind === 'off') await call('power', { id: a.endpointId, on: a.kind === 'on' });
      else {
        const n = Phrases.normalize(a.text, { devices: state.endpoints, fixes: userData.phrase.fixes, auto: userData.phrase.auto });
        if (n.power) await call('power', { id: n.power.id, on: n.power.on });
        else await call('command', { ...target, text: n.text });
      }
    }
    if (reason !== 'agendada') toast(`Rotina "${r.name}" concluída`);
  } catch (err) {
    fail(err);
  }
}

// Checked every 20 s (and still at least once a minute while the window is hidden); runs each routine once per slot.
function runScheduledRoutines() {
  const now = new Date();
  const today = `${now.getFullYear()}-${now.getMonth() + 1}-${now.getDate()}`;
  const minutes = now.getHours() * 60 + now.getMinutes();
  for (const r of userData.routines) {
    if (!r.enabled || !r.time) continue;
    const [hh, mm] = r.time.split(':').map(Number);
    const due = hh * 60 + mm;
    const dayOk = !r.days?.length || r.days.includes(now.getDay());
    if (!dayOk || minutes < due || minutes > due + 2 || r.lastRun === `${today} ${r.time}`) continue;
    r.lastRun = `${today} ${r.time}`;
    saveData('routines', userData.routines);
    runWidgetRoutine(r, 'agendada');
  }
}

function editRoutine(existing) {
  const r = existing ? structuredClone(existing) : {
    id: newId(), name: '', enabled: true, phrases: [], time: '', days: [], serial: state.selected,
    actions: [{ kind: 'command', text: '' }],
  };
  const name = h('input', { value: r.name, placeholder: 'Ex.: Boa noite', maxlength: 60 });
  const phrases = h('textarea', { rows: 2, placeholder: 'Uma por linha. Ex.: boa noite', maxlength: 600 });
  phrases.value = (r.phrases || []).join('\n');
  const time = h('input', { type: 'time', value: r.time || '' });
  const days = new Set(r.days || []);
  const dayButtons = h('div', { class: 'days' }, ...WEEKDAYS.map((label, d) => {
    const b = h('button', { type: 'button', class: 'day' + (days.has(d) ? ' on' : ''), title: WEEKDAY_NAMES[d] }, label);
    b.addEventListener('click', () => { days.has(d) ? days.delete(d) : days.add(d); b.classList.toggle('on'); });
    return b;
  }));
  const echo = h('select', {}, ...state.echos.map(e => h('option', { value: e.serialNumber, selected: e.serialNumber === r.serial }, e.accountName)));

  const deviceSelect = (a) => h('select', { onchange: e => (a.endpointId = e.target.value) },
    h('option', { value: '' }, 'Escolha o dispositivo'),
    ...state.endpoints.map(ep => h('option', { value: ep.id, selected: ep.id === a.endpointId }, ep.name)));
  const { list: actions, render } = editableList(r.actions, (a) => {
    const kind = h('select', { onchange: e => { a.kind = e.target.value; render(); } },
      ...ACTION_KINDS.map(([k, label]) => h('option', { value: k, selected: k === a.kind }, label)));
    const field = a.kind === 'wait'
      ? h('input', { type: 'number', min: 5, max: 14400, step: 5, value: a.seconds ?? 30, title: 'Segundos', oninput: e => (a.seconds = +e.target.value) })
      : a.kind === 'on' || a.kind === 'off' ? deviceSelect(a)
      : h('input', { value: a.text ?? '', placeholder: a.kind === 'speak' ? 'O que a Alexa vai falar' : 'Ex.: tocar música relaxante', oninput: e => (a.text = e.target.value) });
    return h('div', { class: 'action' }, kind, field);
  });

  const body = h('div', { class: 'form' },
    h('label', { class: 'field' }, h('span', {}, 'Nome'), name),
    h('label', { class: 'field' }, h('span', {}, 'Quando eu disser (no widget, por voz ou texto)'), phrases),
    h('div', { class: 'field' }, h('span', {}, 'E/ou no horário (com o PC ligado; sem dias = todo dia)'), h('div', { class: 'cmd' }, time, dayButtons)),
    h('label', { class: 'field' }, h('span', {}, 'Echo'), echo),
    h('div', { class: 'field' }, h('span', {}, 'Ações, em ordem'), actions,
      h('button', { class: 'subtle', onclick: () => { r.actions.push({ kind: 'command', text: '' }); render(); } }, '+ Ação')));

  const buttons = [
    ['Cancelar', 'subtle', closeModal],
    ['Salvar', 'primary', () => {
      r.name = name.value.trim() || 'Rotina';
      r.phrases = phrases.value.split('\n').map(s => s.trim()).filter(Boolean);
      r.time = time.value;
      r.days = [...days].sort();
      r.serial = echo.value;
      r.actions = r.actions.filter(a => a.kind === 'wait' || (a.kind === 'on' || a.kind === 'off' ? a.endpointId : a.text?.trim()));
      if (!r.phrases.length && !r.time) return toast('Defina uma frase ou um horário.', true);
      if (!r.actions.length) return toast('Adicione pelo menos uma ação completa.', true);
      const i = userData.routines.findIndex(x => x.id === r.id);
      if (i >= 0) userData.routines[i] = r; else userData.routines.push(r);
      saveData('routines', userData.routines);
      renderWidgetRoutines();
      closeModal();
    }],
  ];
  if (existing) buttons.unshift(['Excluir', 'subtle danger', () => {
    if (!confirm(`Excluir a rotina "${existing.name}"?`)) return;
    userData.routines = userData.routines.filter(x => x.id !== existing.id);
    saveData('routines', userData.routines);
    renderWidgetRoutines();
    closeModal();
  }]);
  openModal(existing ? 'Editar rotina' : 'Nova rotina', body, buttons);
}

// ---------- routines made in the Alexa app ----------
async function loadAlexaRoutines() {
  const el = $('#alexaRoutines');
  try {
    const list = await call('routines');
    el.replaceChildren(...(list.length ? list.map(r => h('li', { class: 'row' + (r.enabled ? '' : ' offline') },
      h('button', { class: 'icon', title: 'Executar agora', onclick: async () => {
        try { await call('routineRun', { id: r.id }); toast(`Rotina "${r.name}" executada`); } catch (err) { fail(err); }
      } }, '▶'),
      h('div', { class: 'body' }, h('div', { class: 'title' }, r.name),
        h('div', { class: 'sub' }, [r.phrases[0] && `"${r.phrases[0]}"`, r.time, r.actions.slice(0, 2).join(', ')].filter(Boolean).join(' · '))),
      h('button', { class: 'icon small danger', title: 'Excluir', onclick: async () => {
        if (!confirm(`Excluir a rotina "${r.name}" da sua conta Alexa?`)) return;
        try { await call('routineDelete', { id: r.id }); loadAlexaRoutines(); } catch (err) { fail(err); }
      } }, '✕'),
      h('button', { class: 'switch', role: 'switch', 'aria-checked': String(r.enabled), 'aria-label': 'Ativa', onclick: async () => {
        try { await call('routineEnable', { id: r.id, enabled: !r.enabled }); loadAlexaRoutines(); } catch (err) { fail(err); }
      } })))
      : [h('li', { class: 'empty' }, 'Nenhuma rotina criada no app Alexa do celular.')]));
  } catch (err) {
    el.replaceChildren(h('li', { class: 'empty' }, 'Não foi possível carregar.'));
    fail(err);
  }
}

// ---------- alarms and reminders ----------
function addAlarm() {
  const pad = n => String(n).padStart(2, '0');
  const inAnHour = new Date(Date.now() + 3600_000);
  const type = h('select', {}, h('option', { value: 'Alarm' }, 'Alarme'), h('option', { value: 'Reminder' }, 'Lembrete'));
  const label = h('input', { placeholder: 'Ex.: tomar o remédio', maxlength: 120 });
  const labelField = h('label', { class: 'field', hidden: true }, h('span', {}, 'Lembrar de'), label);
  type.addEventListener('change', () => (labelField.hidden = type.value !== 'Reminder'));
  const date = h('input', { type: 'date', value: `${inAnHour.getFullYear()}-${pad(inAnHour.getMonth() + 1)}-${pad(inAnHour.getDate())}` });
  const time = h('input', { type: 'time', value: `${pad(inAnHour.getHours())}:00` });
  const repeat = h('select', {},
    h('option', { value: '' }, 'Não repetir'), h('option', { value: 'P1D' }, 'Todos os dias'),
    h('option', { value: 'XXXX-WD' }, 'Dias úteis'), h('option', { value: 'XXXX-WE' }, 'Fins de semana'));
  const echo = h('select', {}, ...state.echos.map(e => h('option', { value: e.serialNumber, selected: e.serialNumber === state.selected }, e.accountName)));
  const body = h('div', { class: 'form' },
    h('label', { class: 'field' }, h('span', {}, 'Tipo'), type), labelField,
    h('div', { class: 'field' }, h('span', {}, 'Quando'), h('div', { class: 'cmd' }, date, time)),
    h('label', { class: 'field' }, h('span', {}, 'Repetir'), repeat),
    h('label', { class: 'field' }, h('span', {}, 'Echo'), echo));
  openModal('Novo alarme ou lembrete', body, [
    ['Cancelar', 'subtle', closeModal],
    ['Criar', 'primary', async e => {
      const d = state.echos.find(x => x.serialNumber === echo.value);
      if (!d || !date.value || !time.value) return toast('Preencha data, hora e Echo.', true);
      if (type.value === 'Reminder' && !label.value.trim()) return toast('Escreva do que lembrar.', true);
      e.target.disabled = true;
      try {
        await call('notificationCreate', {
          type: type.value, serial: d.serialNumber, deviceType: d.deviceType,
          date: date.value, time: time.value, label: label.value.trim() || null, recurring: repeat.value || null,
        });
        closeModal();
        toast(`${type.value === 'Alarm' ? 'Alarme' : 'Lembrete'} criado`);
        setTimeout(() => loadAlarms(true).catch(fail), 4000); // the list takes a few seconds to include it
      } catch (err) {
        fail(err);
      } finally {
        e.target.disabled = false;
      }
    }],
  ]);
}

async function deleteAlarm(n) {
  const what = n.type === 'Reminder' ? `o lembrete "${n.reminderLabel || ''}"` : n.type === 'Timer' ? 'o timer' : 'o alarme';
  if (!confirm(`Excluir ${what} das ${fmtTime.format(n.when)}?`)) return;
  try {
    await call('notificationDelete', { notification: n.raw });
    state.notifications = state.notifications.filter(x => x.raw.id !== n.raw.id);
    renderAlarms();
    toast('Excluído');
  } catch (err) {
    fail(err);
  }
}
