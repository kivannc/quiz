/*
 * Keeps a phone's screen on while it has a part in a round.
 *
 * It matters most on the console. The host is watching the television, not the phone, so the phone
 * dims and locks within a minute — and a locked phone runs no timers. The countdown that closes a
 * question and the beat that opens the next one both live on that phone, so the whole room would
 * sit in front of a question that never closes until somebody noticed and woke it. A player's phone
 * is held awake for a smaller reason: one that locks between two questions has to be unlocked and
 * reconnected before it can answer the next, and the clock is already running.
 *
 * The browser gives the lock up by itself whenever the page is hidden, so it is asked for again each
 * time the page comes back. Where the browser has no such thing (or refuses, as it may on a low
 * battery), nothing here fails: the phone simply behaves as it did before.
 */

let wanted = false;
let lock = null;
// A request on its way. The pages call keepAwake on every render, and several renders land while
// one request is still in flight: without this each of them would take a lock of its own, only the
// last would be remembered, and the rest would hold the screen on after the round was over.
let asking = false;

async function acquire() {
  if (!wanted || lock || asking || !('wakeLock' in navigator) || document.visibilityState !== 'visible') return;
  asking = true;
  try {
    const taken = await navigator.wakeLock.request('screen');
    taken.addEventListener('release', () => {
      if (lock === taken) lock = null;
    });
    // The round may have ended while the request was in flight.
    if (wanted) lock = taken;
    else taken.release().catch(() => {});
  } catch {
    // Refused. The page carries on; the phone may sleep.
  } finally {
    asking = false;
  }
}

/** Says whether the screen should be held on from now on. Safe to call on every render. */
export function keepAwake(on) {
  wanted = Boolean(on);
  if (wanted) {
    acquire();
  } else if (lock) {
    lock.release().catch(() => {});
    lock = null;
  }
}

document.addEventListener('visibilitychange', acquire);
