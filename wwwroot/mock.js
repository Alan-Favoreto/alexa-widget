'use strict';
// Fake host used only when the page is opened outside the app (plain browser), to preview the UI.
// Query string: ?mode=app for the window layout, ?login=0 for the sign-in screen.
if (!window.chrome?.webview) {
  const params = new URLSearchParams(location.search);
  const target = new EventTarget();
  const now = Date.now();
  const today = new Date().toISOString().slice(0, 10);
  let mode = params.get('mode') || 'compact';
  const power = (state) => ({ name: 'power', operations: [{ name: 'turnOn' }, { name: 'turnOff' }],
    properties: [state ? { powerStateValue: state } : { reachabilityStatusValue: 'NOT_REACHABLE' }] });
  const endpoints = [
    ['Luz da sala', 'LIGHT', [power('ON'), { name: 'brightness', operations: [{ name: 'setBrightness' }], properties: [{ brightnessStateValue: 70 }] },
      { name: 'color', operations: [{ name: 'setColor' }], properties: [] }, { name: 'colorTemperature', operations: [], properties: [] }]],
    ['Robô Aspirador de Pó 100', 'VACUUM_CLEANER', [power('OFF'),
      { name: 'mode', instance: 'Mode', operations: [{ name: 'setMode' }], properties: [{ modeValue: { value: 'random' } }] },
      { name: 'mode', instance: 'Fan.Speed', operations: [{ name: 'setMode' }], properties: [{ modeValue: { value: 'normal' } }] },
      { name: 'range', instance: 'battery', operations: [], properties: [{ rangeValue: { value: 74 } }] }]],
    ['Cafeteira', 'SMARTPLUG', [power('OFF')]],
    ['Luz da varanda', 'LIGHT', [power(null)]],
  ].map(([name, cat, features], i) => ({
    id: 'ep' + i,
    friendlyNameObject: { value: { text: name } },
    displayCategories: { primary: { value: cat } },
    features,
  }));
  const results = {
    state: () => ({ loggedIn: params.get('login') !== '0', mode, region: 'amazon.com.br' }),
    devices: () => ({ devices: [
      { accountName: 'Echo da Sala', serialNumber: 'S1', deviceType: 'A1', deviceFamily: 'ECHO', online: true, capabilities: ['VOLUME_SETTING'] },
      { accountName: 'Echo Show Cozinha', serialNumber: 'S2', deviceType: 'A2', deviceFamily: 'KNIGHT', online: true, capabilities: ['VOLUME_SETTING'] },
      { accountName: 'Echo Dot Quarto', serialNumber: 'S3', deviceType: 'A3', deviceFamily: 'ECHO', online: false, capabilities: ['VOLUME_SETTING'] },
      { accountName: 'Este app', serialNumber: 'S4', deviceType: 'A4', deviceFamily: 'VOX', online: true, capabilities: [] },
    ] }),
    notifications: () => ({ notifications: [
      { type: 'Timer', status: 'ON', remainingTime: 4 * 60e3 + 12e3, timerLabel: 'Macarrão', deviceSerialNumber: 'S2' },
      { type: 'Alarm', status: 'ON', alarmTime: now + 9 * 36e5, recurringPattern: 'XXXX-WD', deviceSerialNumber: 'S3' },
      { type: 'Alarm', status: 'OFF', alarmTime: now + 3 * 36e5, deviceSerialNumber: 'S3' },
      { type: 'Reminder', status: 'ON', originalDate: today, originalTime: '23:30:00.000', reminderLabel: 'tomar o remédio', deviceSerialNumber: 'S1' },
    ] }),
    smarthome: () => endpoints,
    btPaired: () => [{ id: 'bt-old', name: 'Echo Dot-EER', paired: true }],
    btDiscover: () => [{ id: 'bt-new', name: 'Echo Dot-EER', paired: false }],
    mode: p => { mode = p.mode; return results.state(); },
    prefs: () => prefs,
    setCommandHotkeys: p => ({ failed: p.items.map((x, i) => (x && x.modifiers === 3 && x.key === 65 ? i : -1)).filter(i => i >= 0) }),
    musicDefaults: () => ({ curatedMap: {
      MUSIC: [{ guiFriendlyName: 'Amazon Music', preferred: true }],
      PODCAST: [{ guiFriendlyName: 'Amazon Music', preferred: true }, { guiFriendlyName: 'Apple Podcasts' }, { guiFriendlyName: 'TuneIn' }],
      STATION: [{ guiFriendlyName: 'Amazon Music', preferred: true }],
    } }),
    loadData: p => stored[p.key] ?? null,
    saveData: p => { stored[p.key] = p.value; },
    routines: () => [{ id: 'r1', name: 'Bom dia', enabled: true, phrases: ['bom dia'], time: '07:00', days: [], actions: ['Alexa diz: Bom dia!', 'Comando: previsão do tempo'] }],
    setHotkey: p => Object.assign(prefs, { hotkeyLabel: p.label }),
    clearHotkey: () => Object.assign(prefs, { hotkeyLabel: null }),
    setMouseButton: p => Object.assign(prefs, { mouseButton: p.button || null }),
  };
  const prefs = { hotkeyLabel: 'Ctrl+Alt+A', mouseButton: null };
  const stored = {};
  const emit = data => target.dispatchEvent(Object.assign(new Event('message'), { data }));
  // listen: plays back the same status events the app sends, then "hears" a fixed phrase.
  results.listen = () => new Promise(resolve => {
    emit({ event: 'voice', state: 'listening' });
    setTimeout(() => emit({ event: 'voice', state: 'transcribing' }), 600);
    setTimeout(() => resolve('que horas são'), 1000);
  });
  window.mockBridge = {
    addEventListener: (type, fn) => target.addEventListener(type, fn),
    postMessage({ id, method, params: p }) {
      setTimeout(async () => {
        const result = (await results[method]?.(p)) ?? null;
        emit({ id, ok: true, result });
        if (method === 'mode') emit({ event: 'mode', mode });
      }, 150);
    },
  };
}
