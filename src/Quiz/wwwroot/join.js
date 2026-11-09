/*
 * The phone. It joins a room with a code and a name, then shows four suit buzzers and
 * nothing else: no question text, no option text, no letters. The TV is the shared screen.
 *
 * The code and name are kept for the tab, so a reload or a dropped connection re-sends the
 * same name and lands back in the same seat with the same score.
 */

import { t, applyStatic, mountLangSwitch, onLangChange, errorText } from './i18n.js';

const STORE_CODE = 'quiz.code';
const STORE_NAME = 'quiz.name';
const STORE_TAP = 'quiz.tap';

const el = (id) => document.getElementById(id);

const views = {
  join: el('view-join'),
  play: el('view-play'),
};

const state = {
  phase: 'join',    // join | lobby | question | closed | finished
  code: null,
  name: null,       // as the server spells it back in Seated
  score: 0,
  index: null,      // which question is open, so a reload can tell whose answer it kept
  pending: null,    // a tap on its way to the server, not yet taken
  chosen: null,     // the marker the room accepted for the current question
  paused: false,    // the room's clock is stopped: taps are refused until it starts again
  error: '',
  notice: '',       // something that happened rather than something that went wrong
  connection: '',
};

/* ---------- session memory ---------- */

function remember() {
  try {
    sessionStorage.setItem(STORE_CODE, state.code ?? '');
    sessionStorage.setItem(STORE_NAME, state.name ?? '');
  } catch {
    // Without storage a reload just means typing the code again.
  }
}

function forget() {
  try {
    sessionStorage.removeItem(STORE_CODE);
    sessionStorage.removeItem(STORE_NAME);
    sessionStorage.removeItem(STORE_TAP);
  } catch { /* nothing to forget */ }
}

function rememberTap(index, marker) {
  try {
    sessionStorage.setItem(STORE_TAP, JSON.stringify({ code: state.code, index, marker }));
  } catch { /* the lock still holds for as long as this page lives */ }
}

/*
 * What this phone sent for this very question, if anything. Without it a reloaded player would see
 * four live buzzers, tap one, and watch the server quietly refuse the second answer — which reads
 * as a broken phone rather than as "you have already answered".
 */
function recallTap(index) {
  try {
    const stored = JSON.parse(sessionStorage.getItem(STORE_TAP) ?? 'null');
    return stored && stored.code === state.code && stored.index === index ? stored.marker : null;
  } catch {
    return null;
  }
}

function recall() {
  try {
    return { code: sessionStorage.getItem(STORE_CODE) || null, name: sessionStorage.getItem(STORE_NAME) || null };
  } catch {
    return { code: null, name: null };
  }
}

/* ---------- hub ---------- */

const conn = new signalR.HubConnectionBuilder()
  .withUrl('/hub')
  .withAutomaticReconnect()
  // Warnings and worse only: the client chats at Information level, and a living-room
  // screen has no console to read it in.
  .configureLogging(signalR.LogLevel.Warning)
  .build();

conn.on('Seated', (name, score) => {
  state.name = name;
  state.score = score;
  if (state.phase === 'join') state.phase = 'lobby';
  remember();
  render();
});

conn.on('Paused', (paused) => {
  // The room refuses answers while it is stopped, so the buzzers say so rather than taking a tap
  // the server would only throw away.
  state.paused = Boolean(paused);
  state.error = '';
  render();
});

conn.on('RoomClosed', () => {
  // Not an error and not a dead screen: the game ended, and this phone can join another room.
  forget();
  // That code went with the room and will never work again, so it is not worth offering back; the
  // name is, and whoever opens the next room will read it out anyway.
  el('form-join').elements.code.value = '';
  state.phase = 'join';
  state.code = null;
  state.name = null;
  state.score = 0;
  state.index = null;
  state.pending = null;
  state.chosen = null;
  state.paused = false;
  state.error = '';
  state.notice = t('roomClosed');
  render();
});

conn.on('QuestionStarted', (question) => {
  // The payload carries the question text and the options; this screen deliberately shows neither.
  // Its index is used only to tell this phone's own answer apart from an older one.
  state.phase = 'question';
  state.index = question.index;
  state.paused = false;
  state.pending = null;
  state.chosen = recallTap(question.index);
  state.error = '';
  render();
});

conn.on('Revealed', (reveal) => {
  // The correct marker is in here too, but the TV is where the answer is read out.
  state.phase = 'closed';
  state.paused = false;
  takeScore(reveal.standings);
  render();
});

conn.on('RoundFinished', (standings) => {
  state.phase = 'finished';
  state.paused = false;
  takeScore(standings);
  render();
});

conn.onreconnecting(() => {
  state.connection = t('reconnecting');
  render();
});

conn.onreconnected(() => {
  state.connection = '';
  render();
  // A reconnected phone is a new connection to the room: re-sending the same name puts it
  // back in its seat, and the server replies with whatever is on screen right now.
  rejoin();
});

conn.onclose(() => {
  state.connection = t('disconnected');
  render();
});

/* The standings are the only place a player's own score comes from. */
function takeScore(standings) {
  const mine = (standings ?? []).find((player) => player.name === state.name);
  if (mine) state.score = mine.score;
}

/* ---------- actions ---------- */

async function join(code, name) {
  const result = await conn.invoke('Join', code, name);
  if (result === 'joined' || result === 'rejoined') {
    state.code = code;
    state.error = '';
    // Seated (and, mid-round, the current question or reveal) has already arrived by now.
    if (state.phase === 'join') state.phase = 'lobby';
    remember();
    render();
    return;
  }
  state.error = result === 'roomFull' ? t('errRoomFull') : t('errRoundInProgress');
  state.phase = 'join';
  forget();
  render();
}

/* Re-sends the name this phone already has. A room that has gone sends it back to the form. */
async function rejoin() {
  if (!state.code || !state.name) return;
  try {
    await join(state.code, state.name);
  } catch (error) {
    state.phase = 'join';
    state.name = null;
    state.chosen = null;
    state.error = errorText(error);
    forget();
    render();
  }
}

el('form-join').addEventListener('submit', async (event) => {
  event.preventDefault();
  const form = event.target;
  const code = form.elements.code.value.trim();
  const name = form.elements.name.value.trim();
  const button = form.querySelector('button');
  button.disabled = true;
  try {
    state.error = '';
    state.notice = '';
    await join(code, name);
  } catch (error) {
    state.error = errorText(error);
    render();
  } finally {
    button.disabled = false;
  }
});

async function answer(marker) {
  if (state.phase !== 'question' || state.paused || state.chosen || state.pending) return;
  // The tap shows at once, because a buzzer that does nothing for a moment feels broken — but it
  // only locks once the room says it took the answer. A host tab the browser has put to sleep can
  // leave a question open past its limit, and the room refuses answers that arrive after it;
  // telling that player their answer is in would be a lie the scoreboard then contradicts.
  state.pending = marker;
  state.error = '';
  render();
  try {
    if (await conn.invoke('Answer', state.code, marker)) {
      state.chosen = marker;
      rememberTap(state.index, marker);
    } else {
      state.error = t('answerRefused');
    }
  } catch (error) {
    state.error = errorText(error);
  } finally {
    state.pending = null;
    render();
  }
}

for (const buzzer of el('buzzers').querySelectorAll('.buzz')) {
  buzzer.addEventListener('click', () => answer(buzzer.dataset.marker));
}

/* ---------- rendering ---------- */

function render() {
  const view = state.phase === 'join' ? 'join' : 'play';
  for (const [name, section] of Object.entries(views)) section.hidden = name !== view;

  el('bar-me').textContent = state.name && view === 'play' ? state.name : '';
  el('bar-score').textContent = view === 'play' ? t('scoreOf', state.score) : '';
  el('join-error').textContent = view === 'join' ? state.error : '';

  // The join form is a screen of its own: without this a player would only learn the hub is
  // unreachable after typing a code and tapping GİR, as a generic failure.
  const notice = el('join-connection');
  notice.textContent = view === 'join' ? (state.connection || state.notice) : '';
  notice.hidden = notice.textContent === '';

  // A tap that raced the pause is refused, and the refusal is true — but "that answer did not count"
  // under a locked buzzer reads as a broken phone, when what actually happened is on the TV and this
  // phone already knows it. While the room is paused, the room's state is the message.
  const failure = state.paused ? '' : state.error;
  el('play-status').textContent = state.connection || failure || statusText();
  el('play-status').classList.toggle('error', Boolean(!state.connection && failure));

  const picked = state.chosen ?? state.pending;
  for (const buzzer of el('buzzers').querySelectorAll('.buzz')) {
    const marker = buzzer.dataset.marker;
    buzzer.setAttribute('aria-pressed', String(marker === picked));
    buzzer.setAttribute('aria-label', t('markerName', marker));
    buzzer.disabled = state.phase !== 'question' || state.paused || Boolean(picked);
  }
}

function statusText() {
  // What the room is doing comes before what this phone is doing: a locked buzzer with "pick your
  // answer" under it is the one thing a player would read as broken.
  if (state.paused) return t('paused');
  if (state.phase === 'question') {
    if (state.chosen) return t('answerSent');
    if (state.pending) return t('answerSending');
    return t('pickYours');
  }
  if (state.phase === 'closed') return t('questionClosed');
  if (state.phase === 'finished') return t('roundOver');
  return t('waitingForStart');
}

/* ---------- start ---------- */

mountLangSwitch(el('langs'));
onLangChange(() => {
  document.title = t('phoneTitle');
  applyStatic();
  render();
});

try {
  await conn.start();
} catch (error) {
  state.connection = t('disconnected');
  render();
  console.error('could not reach the hub', error);
}

/* A reload rejoins by itself, on the same path a dropped connection takes. */
const stored = recall();
if (stored.code && stored.name && conn.state === signalR.HubConnectionState.Connected) {
  state.code = stored.code;
  state.name = stored.name;
  await rejoin();
}
