'use strict';
// npm test (node --test "tests/js/*.test.js")
const test = require('node:test');
const assert = require('node:assert/strict');
const { dueSlot, CATCH_UP_MINUTES } = require('../../wwwroot/schedule.js');

// Local time; 2026-10-24 is a Saturday (getDay() 6).
const at = (day, hh, mm) => new Date(2026, 9, day, hh, mm);
const routine = (time, days = []) => ({ enabled: true, time, days });

test('due from its minute until the catch-up window ends', () => {
  assert.equal(dueSlot(routine('07:30'), at(24, 7, 29)), null);
  assert.deepEqual(dueSlot(routine('07:30'), at(24, 7, 30)), { key: '2026-10-24 07:30', lateMinutes: 0 });
  assert.deepEqual(dueSlot(routine('07:30'), at(24, 7, 30 + CATCH_UP_MINUTES)), { key: '2026-10-24 07:30', lateMinutes: CATCH_UP_MINUTES });
  assert.equal(dueSlot(routine('07:30'), at(24, 7, 31 + CATCH_UP_MINUTES)), null);
});

test('back from sleep 10 minutes after the time: runs late, same slot key', () => {
  const slot = dueSlot(routine('22:00'), at(24, 22, 10));
  assert.equal(slot.key, '2026-10-24 22:00');
  assert.equal(slot.lateMinutes, 10);
});

test('caught up across midnight, on the day it was due', () => {
  const slot = dueSlot(routine('23:55', [6]), at(25, 0, 5)); // due Saturday, now Sunday 00:05
  assert.deepEqual(slot, { key: '2026-10-24 23:55', lateMinutes: 10 });
});

test('only on the chosen weekdays', () => {
  assert.equal(dueSlot(routine('08:00', [1, 2, 3, 4, 5]), at(24, 8, 0)), null); // Saturday
  assert.ok(dueSlot(routine('08:00', [6]), at(24, 8, 0)));
});

test('disabled, missing or malformed times never run', () => {
  assert.equal(dueSlot({ enabled: false, time: '08:00' }, at(24, 8, 0)), null);
  assert.equal(dueSlot({ enabled: true, time: '' }, at(24, 8, 0)), null);
  assert.equal(dueSlot({ enabled: true, time: 'oito' }, at(24, 8, 0)), null);
});
