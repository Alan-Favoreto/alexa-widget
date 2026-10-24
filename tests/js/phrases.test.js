'use strict';
// npm test (node --test "tests/js/*.test.js")
const test = require('node:test');
const assert = require('node:assert/strict');
const Phrases = require('../../wwwroot/phrases.js');

const devices = [
  { id: 'd1', name: 'Robô Aspirador de Pó 100', hasPower: true },
  { id: 'd2', name: 'Luz da Sala', hasPower: true },
  { id: 'd3', name: 'Luz do Quarto', hasPower: true },
  { id: 'd4', name: 'Sensor da Porta', hasPower: false },
];

test('spoken imperatives become infinitives, also after "e"', () => {
  assert.equal(Phrases.normalize('toca rock').text, 'tocar rock');
  assert.equal(Phrases.normalize('aumenta o volume e para a música').text, 'aumentar o volume e parar a música');
  assert.equal(Phrases.normalize('qual a previsão do tempo').text, 'qual a previsão do tempo');
});

test('politeness, the wake word and trailing punctuation are dropped', () => {
  assert.equal(Phrases.normalize('Alexa, você pode por favor tocar jazz?').text, 'tocar jazz');
  assert.equal(Phrases.normalize('ok Alexia toca rock').text, 'tocar rock');
  assert.equal(Phrases.normalize('Alexandre está em casa').text, 'Alexandre está em casa');
});

test('device names get their registered spelling', () => {
  assert.equal(Phrases.normalize('desliga o robô aspirador de po 100', { devices }).text, 'desligar o Robô Aspirador de Pó 100');
});

test('on/off for a device with power control is flagged for a direct switch', () => {
  assert.deepEqual(Phrases.normalize('liga a luz da sala', { devices }).power, { id: 'd2', name: 'Luz da Sala', on: true });
  assert.deepEqual(Phrases.normalize('apaga o robo', { devices }).power, { id: 'd1', name: 'Robô Aspirador de Pó 100', on: false });
});

test('ambiguous or powerless devices are left to Alexa', () => {
  assert.equal(Phrases.normalize('liga a luz', { devices }).power, null); // sala or quarto?
  assert.equal(Phrases.normalize('liga o sensor da porta', { devices }).power, null);
});

test("the user's fixes win and turn the automatic rewriting off for that phrase", () => {
  const r = Phrases.normalize('limpa a casa agora', { devices, fixes: [{ from: 'limpa a casa', to: 'iniciar limpeza' }] });
  assert.equal(r.text, 'iniciar limpeza agora');
  assert.equal(r.fixed, true);
  assert.equal(r.power, null);
});

test('auto: false keeps the text as typed (apart from the cleanup)', () => {
  const r = Phrases.normalize('liga a luz da sala', { devices, auto: false });
  assert.equal(r.text, 'liga a luz da sala');
  assert.equal(r.power, null);
});

test('fold lowercases and strips accents without moving characters', () => {
  assert.equal(Phrases.fold('Ação Pó'), 'acao po');
  assert.equal(Phrases.fold('Ação').length, 'Ação'.length);
});
