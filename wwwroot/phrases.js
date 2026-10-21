'use strict';
// Rewrites what the user typed or said into the form Alexa's text command understands best:
//  1. the user's own fixes ("quando eu disser X, enviar Y") win over everything else;
//  2. spoken imperatives become infinitives ("liga" → "ligar"), which the text command parses reliably;
//  3. smart-home device names are replaced by their exact registered spelling (accents, case);
//  4. "ligar/desligar <device>" for a device with power control is flagged so the caller can switch it
//     directly instead of relying on Alexa's interpretation.
(function (root) {
  // Each infinitive followed by the conversational forms that should become it.
  const VERBS = {
    ligar: ['liga', 'ligue', 'liguem', 'ligá'],
    desligar: ['desliga', 'desligue', 'desliguem', 'desligá'],
    acender: ['acende', 'acenda'],
    apagar: ['apaga', 'apague'],
    abrir: ['abre', 'abra'],
    fechar: ['fecha', 'feche'],
    aumentar: ['aumenta', 'aumente', 'sobe', 'suba'],
    diminuir: ['diminui', 'diminua', 'abaixa', 'abaixe', 'baixa', 'baixe'],
    tocar: ['toca', 'toque'],
    colocar: ['coloca', 'coloque', 'bota', 'bote', 'põe', 'ponha'],
    parar: ['para', 'pare'],
    pausar: ['pausa', 'pause'],
    continuar: ['continua', 'continue'],
    retomar: ['retoma', 'retome'],
    iniciar: ['inicia', 'inicie'],
    começar: ['começa', 'comece'],
    ativar: ['ativa', 'ative'],
    desativar: ['desativa', 'desative'],
    trancar: ['tranca', 'tranque'],
    destrancar: ['destranca', 'destranque'],
    definir: ['define', 'defina'],
    ajustar: ['ajusta', 'ajuste'],
    mudar: ['muda', 'mude', 'troca', 'troque'],
    mostrar: ['mostra', 'mostre'],
    criar: ['cria', 'crie'],
    adicionar: ['adiciona', 'adicione', 'acrescenta', 'acrescente'],
    remover: ['remove', 'remova', 'tira', 'tire'],
    cancelar: ['cancela', 'cancele'],
  };
  const TO_INFINITIVE = new Map();
  for (const [inf, forms] of Object.entries(VERBS)) for (const f of forms) TO_INFINITIVE.set(fold(f), inf);

  // Words after which a new instruction can start ("liga a luz e desliga a tv").
  const CLAUSE_STARTERS = new Set(['e', 'depois', 'entao', 'tambem']);
  const POWER_ON = new Set(['ligar', 'acender']);
  const POWER_OFF = new Set(['desligar', 'apagar']);

  /** Lowercases and strips accents one character at a time, so indexes still match the original text. */
  function fold(text) {
    let out = '';
    for (const ch of text) out += ch.normalize('NFD')[0].toLowerCase();
    return out;
  }

  function clean(text) {
    return text
      .replace(/\s+/g, ' ')
      .trim()
      .replace(/^((ok|ei|oi|hey)[\s,]+)?al[eé]x[ai][\s,.!?:]*/i, '')
      .replace(/^(você\s+|voce\s+)?(pode(ria)?|consegue|poderia)\s+(por favor\s+)?/i, '')
      .replace(/^(eu\s+)?quero\s+que\s+(você\s+|voce\s+)?/i, '')
      .replace(/[,\s]*(por favor|por gentileza)[,\s]*/gi, ' ')
      .replace(/\s+(pra|para)\s+mim\s*$/i, '')
      .replace(/\s+a[ií]\s*$/i, '')
      .replace(/[\s.!?…,;]+$/, '')
      .trim();
  }

  function applyFixes(text, fixes) {
    const folded = fold(text);
    for (const fix of fixes || []) {
      const from = fold((fix.from || '').trim());
      if (!from || !fix.to) continue;
      const at = folded.indexOf(from);
      if (at >= 0) return text.slice(0, at) + fix.to.trim() + text.slice(at + from.length);
    }
    return null;
  }

  function toInfinitives(text) {
    const words = text.split(' ');
    for (let i = 0; i < words.length; i++) {
      const prev = i === 0 ? null : fold(words[i - 1]);
      if (i > 0 && !CLAUSE_STARTERS.has(prev)) continue;
      const inf = TO_INFINITIVE.get(fold(words[i]));
      if (inf) words[i] = inf;
    }
    return words.join(' ');
  }

  /** Puts each device's registered name in place of a differently spelled mention ("robo aspirador"). */
  function exactDeviceNames(text, devices) {
    const byLength = [...(devices || [])].filter(d => d.name).sort((a, b) => b.name.length - a.name.length);
    let folded = fold(text);
    for (const d of byLength) {
      const name = fold(d.name);
      let at = 0;
      while ((at = folded.indexOf(name, at)) >= 0) {
        const before = at === 0 || /[^a-z0-9]/.test(folded[at - 1]);
        const after = at + name.length === folded.length || /[^a-z0-9]/.test(folded[at + name.length]);
        if (before && after) {
          text = text.slice(0, at) + d.name + text.slice(at + name.length);
          folded = fold(text);
        }
        at += name.length;
      }
    }
    return text;
  }

  function powerIntent(text, devices) {
    const m = /^(\S+)\s+(?:(?:o|a|os|as)\s+)?(.+)$/i.exec(text);
    if (!m) return null;
    const verb = fold(m[1]);
    const on = POWER_ON.has(verb) ? true : POWER_OFF.has(verb) ? false : null;
    if (on === null) return null;
    const device = findDevice(m[2], (devices || []).filter(d => d.hasPower));
    return device ? { id: device.id, name: device.name, on } : null;
  }

  /**
   * Exact name first; otherwise the single device whose name contains every word said, as word prefixes
   * ("robo aspirador" → "Robô Aspirador de Pó 100"). Ambiguous mentions return null and go to Alexa as text.
   */
  function findDevice(mention, devices) {
    const target = fold(mention).replace(/[^a-z0-9 ]/g, ' ').trim();
    if (!target) return null;
    const exact = devices.find(d => fold(d.name) === target);
    if (exact) return exact;
    const said = target.split(/\s+/);
    const matches = devices.filter(d => {
      const words = fold(d.name).split(/[^a-z0-9]+/).filter(Boolean);
      return said.every(w => words.some(n => n.startsWith(w)));
    });
    return matches.length === 1 ? matches[0] : null;
  }

  /**
   * @param {string} input what the user typed or said
   * @param {{devices?: {id: string, name: string, hasPower?: boolean}[], fixes?: {from: string, to: string}[], auto?: boolean}} ctx
   * @returns {{text: string, power: null | {id: string, name: string, on: boolean}, changed: boolean, fixed: boolean}}
   */
  function normalize(input, ctx = {}) {
    const original = (input || '').trim();
    let text = clean(original);
    const fixed = applyFixes(text, ctx.fixes);
    if (fixed !== null) return { text: fixed, power: null, changed: fixed !== original, fixed: true };
    if (ctx.auto !== false) {
      text = toInfinitives(text);
      text = exactDeviceNames(text, ctx.devices);
    }
    const power = ctx.auto !== false ? powerIntent(text, ctx.devices) : null;
    return { text, power, changed: text !== original, fixed: false };
  }

  const api = { normalize, fold, findDevice };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.Phrases = api;
})(typeof window !== 'undefined' ? window : globalThis);
