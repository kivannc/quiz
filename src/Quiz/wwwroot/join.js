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
  chosen: null,     // the marker this phone sent for the current question
  error: '',
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

conn.on('QuestionStarted', (question) => {
  // The payload carries the question text and the options; this screen deliberately shows neither.
  // Its index is used only to tell this phone's own answer apart from an older one.
  state.phase = 'question';
  state.index = question.index;
  state.chosen = recallTap(question.index);
  state.error = '';
  render();
});

conn.on('Revealed', (reveal) => {
  // The correct marker is in here too, but the TV is where the answer is read out.
  state.phase = 'closed';
  takeScore(reveal.standings);
  render();
});

conn.on('RoundFinished', (standings) => {
  state.phase = 'finished';
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
    await join(code, name);
  } catch (error) {
    state.error = errorText(error);
    render();
  } finally {
    button.disabled = false;
  }
});

async function answer(marker) {
  if (state.phase !== 'question' || state.chosen) return;
  // Optimistic: the server accepts the first answer per question and quietly ignores the rest,
  // so the phone locks itself immediately rather than waiting for a round trip it is not told about.
  state.chosen = marker;
  rememberTap(state.index, marker);
  render();
  try {
    await conn.invoke('Answer', state.code, marker);
  } catch (error) {
    state.chosen = null;
    rememberTap(state.index, null);
    state.error = errorText(error);
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

  el('play-status').textContent = state.connection || state.error || statusText();
  el('play-status').classList.toggle('error', Boolean(!state.connection && state.error));

  for (const buzzer of el('buzzers').querySelectorAll('.buzz')) {
    const marker = buzzer.dataset.marker;
    buzzer.setAttribute('aria-pressed', String(marker === state.chosen));
    buzzer.setAttribute('aria-label', t('markerName', marker));
    buzzer.disabled = state.phase !== 'question' || Boolean(state.chosen);
  }
}

function statusText() {
  if (state.phase === 'question') return state.chosen ? t('answerSent') : t('pickYours');
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
