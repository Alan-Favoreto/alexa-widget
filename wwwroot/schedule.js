'use strict';
// When a widget routine with a time is due. Timers run late while the widget is hidden and not at all while the
// PC sleeps, so a routine still runs up to CATCH_UP_MINUTES after its time (also across midnight), once per slot.
(function (root) {
  const CATCH_UP_MINUTES = 15;

  const pad = n => String(n).padStart(2, '0');
  const dayKey = d => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;

  /**
   * The slot of `routine` that should run at `now`, or null.
   * @param {{enabled?: boolean, time?: string, days?: number[]}} routine  time "HH:MM"; days 0 = Sunday, empty = every day
   * @returns {null | {key: string, lateMinutes: number}}  key identifies the slot (store it as lastRun)
   */
  function dueSlot(routine, now = new Date()) {
    if (!routine?.enabled || !/^\d{1,2}:\d{2}$/.test(routine.time || '')) return null;
    const [hh, mm] = routine.time.split(':').map(Number);
    // Today's time, or yesterday's when a routine near midnight is caught up after it.
    for (const daysBack of [0, 1]) {
      const due = new Date(now.getFullYear(), now.getMonth(), now.getDate() - daysBack, hh, mm);
      const late = (now - due) / 60_000;
      if (late < 0 || late > CATCH_UP_MINUTES) continue;
      if (routine.days?.length && !routine.days.includes(due.getDay())) continue;
      return { key: `${dayKey(due)} ${pad(hh)}:${pad(mm)}`, lateMinutes: Math.floor(late) };
    }
    return null;
  }

  const api = { dueSlot, CATCH_UP_MINUTES };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.Schedule = api;
})(typeof window !== 'undefined' ? window : globalThis);
