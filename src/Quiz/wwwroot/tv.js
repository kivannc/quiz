/*
 * The television: the display half of the game, and nothing else. It opens one address, shows a
 * four-digit pairing code, and waits for the host's phone to claim it. From then on the console
 * drives the round and this page only paints what the room sends — lobby, question, reveal,
 * standings. There is nothing to type here and nothing to press, which is the whole point: a TV
 * remote is the worst input device in the house.
 *
 * Two things this page deliberately does not do. It never calls a host action, so its countdown is
 * a picture of the console's clock rather than a timer that closes anything: when it reaches zero
 * it stops and the question stays up until the console closes it. And it never asks for the host
 * key — a screen is put into a room by being claimed, never by proving anything.
 */

import { t, applyStatic, onLangChange, setLang } from './i18n.js';

/* The order the four markers are drawn in. Glyph and colour come from app.css. */
const MARKERS = ['hearts', 'diamonds', 'clubs', 'spades'];

/*
 * How much of a code's life to use before asking for the next one. The lifetime itself comes with
 * the code — QUIZ_PAIRING_SECONDS is a setting, and a screen that assumed the default would show a
 * dead code on any deployment that shortened it, without ever saying so. Replacing it early costs
 * nothing: a screen only ever holds one code, and the new one replaces the old.
 */
const CODE_REFRESH_SHARE = 0.9;
/* However short a lifetime is configured, never spin faster than this. */
const CODE_REFRESH_FLOOR_MS = 5_000;
/*
 * After a refused OpenScreen. The hub answers the same "pairing-unavailable" whether the four-digit
 * space is crowded or this address has asked too often, so the only sensible thing is to wait and
 * ask again — slowly enough that the retries themselves stay well inside the allowance.
 */
const CODE_RETRY_MS = 15_000;
/*
 * A phone claimed this screen and then never opened a room: the host was called away, or that phone
 * is gone for good. Nothing on the wire says so, and a screen stuck on "phone connected" cannot be
 * rescued from the sofa, so after a while it goes back to showing a code. A room that does arrive
 * later is still painted: the handler does not care which screen was up.
 */
const CLAIMED_WITHOUT_ROOM_MS = 120_000;
/* While the hub cannot be reached at all. A TV is left on; it has to keep trying by itself. */
const RECONNECT_MS = 5_000;

/* The room this screen was painting, so a reload can say which room it fell out of. */
const STORE_ROOM = 'quiz.tv.room';

const el = (id) => document.getElementById(id);

const views = {
  pairing: el('view-pairing'),
  paired: el('view-paired'),
  lobby: el('view-lobby'),
  question: el('view-question'),
  reveal: el('view-reveal'),
  finished: el('view-finished'),
};

const state = {
  view: 'pairing',
  code: null,        // the pairing code on screen, null while there is none to show
  codeDeadAt: 0,     // when that code stops being claimable, as the server timed it
  codeFailed: false, // the last OpenScreen was refused
  room: null,        // the room this screen is in, from RoomOpened
  wasIn: null,       // the room it was painting before a reload or a reconnect
  players: [],       // [{ name, score }], from RoomState
  answered: 0,       // from AnswerCount
  seated: 0,         // how many players that count is out of
  question: null,    // the QuestionStarted payload
  reveal: null,      // the Revealed payload
  standings: [],     // from RoundFinished
  paused: false,
  connection: '',    // reconnecting / disconnected notice
};

/* ---------- session memory ---------- */

/*
 * A reload loses the connection, and with it the room's groups and the claim that put this screen
 * in them; there is no key here to resume with, so the way back is to be claimed again. What the
 * screen remembers is only the room it fell out of, so the pairing screen can say so rather than
 * looking like a television that was just switched on. The console's claim re-seats it into that
 * room at once. If the room has gone in the meantime, nothing here is wrong: the code on screen is
 * a fresh one, and the next room opened on it paints over the line.
 */
function rememberRoom(code) {
  try {
    sessionStorage.setItem(STORE_ROOM, code);
  } catch {
    // Without storage a reloaded screen simply cannot say which room it was in; it still pairs.
  }
}

function forgetRoom() {
  state.room = null;
  state.wasIn = null;
  try {
    sessionStorage.removeItem(STORE_ROOM);
  } catch { /* nothing to forget */ }
}

function recallRoom() {
  try {
    return sessionStorage.getItem(STORE_ROOM) || null;
  } catch {
    return null;
  }
}

/* ---------- the clock, which is a display and not a timer ---------- */

let deadline = 0;
let ticker = 0;
// What was left of the clock when the room was paused, in milliseconds; 0 when it is running.
let frozen = 0;

function stopClock() {
  clearInterval(ticker);
  ticker = 0;
  deadline = 0;
  frozen = 0;
}

/*
 * Counts down what the room says is left and stops at zero. Nothing happens at zero: the console is
 * the one that closes a question, and this screen showing t-00s for a moment is the truth — the
 * question is over and the reveal is on its way.
 */
function startClock(ms) {
  stopClock();
  deadline = Date.now() + ms;
  drawClock();
  ticker = setInterval(() => {
    drawClock();
    if (Date.now() >= deadline) {
      clearInterval(ticker);
      ticker = 0;
    }
  }, 200);
}

/* Stops it where it stands: a paused room has stopped spending the limit, and so does the screen. */
function freezeClock() {
  if (!ticker) return;
  frozen = Math.max(0, deadline - Date.now());
  clearInterval(ticker);
  ticker = 0;
  drawClock(frozen);
}

/* Starts it again with exactly what was left, never with the whole limit. */
function thawClock() {
  if (!state.question || ticker) return;
  const left = frozen;
  frozen = 0;
  if (left <= 0) {
    // Either it ran out while the room was stopped, or it was never frozen. Nothing to count.
    drawClock(0);
    return;
  }
  startClock(left);
}

function drawClock(ms = deadline - Date.now()) {
  el('tag-clock').textContent = t('clock', Math.max(0, Math.ceil(ms / 1000)));
}

/* ---------- hub ---------- */

const conn = new signalR.HubConnectionBuilder()
  .withUrl('/hub')
  // Never gives up: nobody is going to walk over and reload the television. Two quick tries, then
  // one every ten seconds for as long as it takes.
  .withAutomaticReconnect({
    nextRetryDelayInMilliseconds: (context) => (context.previousRetryCount < 2 ? 2_000 : 10_000),
  })
  // Warnings and worse only: the client chats at Information level, and a living-room screen has no
  // console to read it in.
  .configureLogging(signalR.LogLevel.Warning)
  .build();

conn.on('Paired', () => {
  // A phone has this screen. The code it typed is spent, so stop showing one and wait for the room.
  show('paired');
});

conn.on('RoomOpened', (code) => {
  state.room = code;
  state.wasIn = null;
  rememberRoom(code);
  state.players = [];
  state.question = null;
  state.reveal = null;
  state.standings = [];
  state.paused = false;
  stopClock();
  // A screen seated into a room that is already in progress is sent the room's state right after
  // this, which paints over the lobby. A room just opened has no state to send and the lobby is
  // exactly right.
  show('lobby');
});

conn.on('RoomState', (players, phase) => {
  state.players = players ?? [];
  // The question and reveal payloads own those two views; RoomState only refreshes the roster.
  if (phase === 'lobby') {
    show('lobby');
  } else if (phase === 'finished') {
    // A screen seated into a finished room never saw RoundFinished, so order the roster itself.
    if (state.standings.length === 0) {
      state.standings = [...state.players].sort((a, b) => b.score - a.score);
    }
    show('finished');
  } else {
    render();
  }
});

conn.on('AnswerCount', (answered, total) => {
  // Both numbers come from the server: total counts seated players, answered the ones in.
  state.answered = answered;
  state.seated = total;
  drawTopbar();
});

conn.on('QuestionStarted', (question) => {
  state.question = question;
  // A screen seated during a reveal is sent the question with no time on it, only so it knows what
  // was asked. Nothing is open, so nothing is painted here: the Revealed that follows does that.
  if (question.limitSeconds <= 0) return;

  state.reveal = null;
  state.answered = 0;
  state.paused = false;
  show('question');
  // What the room says is left, which on a screen arriving mid-question is not the whole limit.
  startClock(question.limitSeconds * 1000);
});

conn.on('Paused', (paused) => {
  state.paused = Boolean(paused);
  if (state.paused) freezeClock();
  else thawClock();
  render();
});

conn.on('Revealed', (reveal) => {
  stopClock();
  // The room drops its pause when the question closes, and so does the screen.
  state.paused = false;
  state.reveal = reveal;
  show('reveal');
});

conn.on('RoundFinished', (standings) => {
  stopClock();
  state.paused = false;
  state.standings = standings ?? [];
  show('finished');
});

conn.on('RoomClosed', () => {
  // The room is gone for everyone, including this screen: back to a pairing code, ready to be
  // claimed for the next one.
  stopClock();
  state.question = null;
  state.reveal = null;
  state.standings = [];
  state.paused = false;
  forgetRoom();
  show('pairing');
});

conn.onreconnecting(() => {
  state.connection = t('reconnecting');
  render();
});

conn.onreconnected(() => {
  /*
   * A reconnected connection is a new connection: it is in none of the room's groups, and the claim
   * that put the old one there was spent when the room was opened. So this screen is no longer in
   * the room, however much of it is still on display, and the only way back is to be claimed again.
   */
  state.connection = '';
  state.wasIn = state.room ?? recallRoom();
  state.room = null;
  stopClock();
  show('pairing');
});

conn.onclose(() => {
  /*
   * The reconnect policy above never gives up, so getting here means the connection ended in a way
   * it could not retry — a server that aborted this one, or a start that was never completed. There
   * is nobody in front of a television to reload it, so it starts itself again. Whatever was on
   * screen, the new connection will be in no groups: back to a pairing code, as after a reload.
   */
  stopClock();
  state.wasIn = state.room ?? recallRoom();
  state.room = null;
  state.connection = t('tvOffline');
  show('pairing');
  connect();
});

/* ---------- pairing ---------- */

// Either the refresh of a live code or the retry after a refused one; never both.
let codeTimer = 0;
// A claim that has not turned into a room yet.
let claimTimer = 0;

function clearPairingTimers() {
  clearTimeout(codeTimer);
  codeTimer = 0;
  clearTimeout(claimTimer);
  claimTimer = 0;
}

/*
 * Asks the hub for a code to show. The one failure is "pairing-unavailable", which the hub answers
 * both to a caller that has asked too often and when no free code could be drawn: the difference is
 * nothing a television could act on, so it is not shown one, and it certainly does not say anything
 * about a rate limit to a room full of people. It waits and asks again.
 */
async function openScreen() {
  clearPairingTimers();
  // Not connected yet, or no longer: whoever brings the connection back asks again.
  if (conn.state !== signalR.HubConnectionState.Connected) return;

  let pairing = null;
  try {
    // { code, lifetimeSeconds }
    pairing = await conn.invoke('OpenScreen');
  } catch {
    // Not reported any further: the screen says so itself, and nobody is reading a console here.
  }

  // A phone may have claimed the screen, or a room may have arrived, while this was in flight.
  if (state.view !== 'pairing') return;

  if (pairing) {
    state.code = pairing.code;
    state.codeDeadAt = Date.now() + pairing.lifetimeSeconds * 1000;
    state.codeFailed = false;
    codeTimer = setTimeout(openScreen, Math.max(
      CODE_REFRESH_FLOOR_MS, pairing.lifetimeSeconds * 1000 * CODE_REFRESH_SHARE));
  } else {
    // Keep the code that is up: it was refused a replacement, not withdrawn, and until its own
    // lifetime runs out it can still be typed. Only once it is certainly dead does the screen stop
    // offering it — a television showing nothing but a line of small red text reads as broken.
    state.codeFailed = true;
    const alive = state.code !== null && Date.now() < state.codeDeadAt;
    if (!alive) state.code = null;
    // Ask again in a while, or the moment the code on screen dies if that comes first, so that a
    // code nobody can claim any more is never the one left up.
    codeTimer = setTimeout(openScreen, alive
      ? Math.min(CODE_RETRY_MS, state.codeDeadAt - Date.now())
      : CODE_RETRY_MS);
  }
  render();
}

/* ---------- rendering ---------- */

function show(view) {
  // Every change of screen comes through here, so this is where the timers of the screen being left
  // die: a pairing code is not refreshed while a question is up, and a claim that became a room is
  // no longer waiting to be given up on.
  clearPairingTimers();
  state.view = view;

  if (view === 'pairing') {
    state.code = null;
    state.codeDeadAt = 0;
    state.codeFailed = false;
    render();
    openScreen();
    return;
  }
  if (view === 'paired') claimTimer = setTimeout(() => show('pairing'), CLAIMED_WITHOUT_ROOM_MS);
  render();
}

function render() {
  for (const [name, section] of Object.entries(views)) section.hidden = name !== state.view;

  drawTopbar();
  if (state.view === 'pairing') drawPairing();
  if (state.view === 'lobby') drawLobby();
  if (state.view === 'question') drawQuestion();
  if (state.view === 'reveal') drawReveal();
  if (state.view === 'finished') drawBoard(el('final-board'), state.standings);
}

function drawTopbar() {
  const playing = state.view === 'question' || state.view === 'reveal';
  const pairing = state.view === 'pairing' || state.view === 'paired';

  // The room code travels with the word "room" wherever it appears, which is one of the things that
  // keeps it from being read as the pairing code.
  set(el('tag-room'), state.room && !pairing ? t('roomTag', state.room) : '');
  set(el('tag-question'), playing && state.question
    ? t('questionCounter', state.question.index + 1, state.question.total)
    : '');

  if (state.view === 'question') set(el('tag-answered'), t('answeredCounter', state.answered, state.seated));
  else if (state.view === 'reveal') set(el('tag-answered'), t('closedTag'));
  else set(el('tag-answered'), '');

  el('tag-clock').hidden = state.view !== 'question';
  document.body.classList.toggle('paused', state.paused);
  el('paused-banner').hidden = !state.paused;
  set(el('tag-connection'), state.connection);
}

function set(node, text) {
  node.textContent = text;
  node.hidden = text === '';
}

function drawPairing() {
  // Either the first code has not arrived yet, or the one that was up has outlived itself. The slot
  // keeps its size and shows dashes instead: at three metres an empty screen is a broken screen.
  const waiting = state.code === null;
  const code = el('pair-code');
  /*
   * Drawn as two pairs — 48 21 — where the room code is drawn as one solid block. Four digits that
   * are read out to someone holding a phone pair naturally; four that a room full of people type
   * do not. Together with the label, the colour and the size, that is four separate reasons not to
   * mistake one code for the other.
   */
  const halves = waiting ? ['--', '--'] : [state.code.slice(0, 2), state.code.slice(2)];
  code.replaceChildren(...halves.map((part) => {
    const half = document.createElement('span');
    half.textContent = part;
    return half;
  }));
  code.classList.toggle('waiting', waiting);

  set(el('pair-where'), waiting ? '' : t('pairHint', consoleAddress()));

  const status = el('pair-status');
  set(status, state.codeFailed ? t('pairFailed') : '');
  status.classList.toggle('error', state.codeFailed);
  // With no code under it, this line is the only thing on the screen that explains it, so it is read
  // at the distance the code is read from rather than in the small print.
  status.classList.toggle('big', state.codeFailed && waiting);

  // Only ever beside a code: on its own it would tell the room to type something that is not there.
  set(el('pair-rejoin'), state.wasIn && !waiting ? t('rejoinHint', state.wasIn) : '');
}

/* Where the host's console lives, which is the one thing this screen asks anybody to do. */
function consoleAddress() {
  return `${location.host}/host.html`;
}

function drawLobby() {
  el('lobby-code').textContent = state.room ?? '';
  el('lobby-where').textContent = t('joinAt', location.host);

  const pills = el('lobby-players');
  pills.replaceChildren(...state.players.map((player) => {
    const pill = document.createElement('span');
    pill.className = 'pill';
    pill.textContent = player.name;
    return pill;
  }));

  // Nothing is pressed here, so the lobby only ever says what it is waiting for.
  set(el('lobby-status'), state.players.length === 0 ? t('waitingForPlayers') : '');
}

function drawQuestion() {
  const question = state.question;
  if (!question) return;
  el('question-text').textContent = question.text;
  el('question-options').replaceChildren(...question.options.map(optionCard));
}

function optionCard(option) {
  const card = document.createElement('div');
  card.className = 'opt';
  card.dataset.marker = option.marker;

  const glyph = document.createElement('span');
  glyph.className = 'glyph';

  const text = document.createElement('span');
  text.textContent = option.text;

  card.append(glyph, text);
  return card;
}

function drawReveal() {
  const reveal = state.reveal;
  if (!reveal) return;

  el('reveal-glyph').dataset.marker = reveal.correct;
  const correct = state.question?.options.find((option) => option.marker === reveal.correct);
  el('reveal-text').textContent = correct?.text ?? '';

  const counts = MARKERS.map((marker) => reveal.distribution[marker] ?? 0);
  const most = Math.max(1, ...counts);
  el('reveal-bars').replaceChildren(...MARKERS.map((marker, i) => {
    const column = document.createElement('div');
    column.className = 'b';
    column.dataset.marker = marker;

    const glyph = document.createElement('span');
    glyph.className = 'glyph';

    const fill = document.createElement('span');
    fill.className = 'fill';
    fill.style.height = `${Math.round((counts[i] / most) * 100)}%`;

    const n = document.createElement('span');
    n.className = 'n';
    n.textContent = String(counts[i]);

    column.append(glyph, fill, n);
    return column;
  }));

  drawBoard(el('reveal-board'), reveal.standings);
}

function drawBoard(list, standings) {
  list.classList.toggle('dense', standings.length > 8);
  list.replaceChildren(...standings.map((player, i) => {
    const row = document.createElement('li');

    const who = document.createElement('span');
    const rank = document.createElement('span');
    rank.className = 'rank';
    rank.textContent = String(i + 1);
    const name = document.createElement('span');
    name.textContent = player.name;
    who.append(rank, name);

    const score = document.createElement('span');
    score.className = 'score';
    score.textContent = String(player.score);

    row.append(who, score);
    return row;
  }));
}

/* ---------- start ---------- */

/*
 * There is no language switch on this page — a switch is a control, and this page has none. The
 * address can carry the choice instead, once: ?lang=en is remembered in localStorage like the
 * switch on the other two pages, so a television is set to its language when it is set up and
 * never asked again. Anything else is ignored.
 */
setLang(new URLSearchParams(location.search).get('lang') ?? '');

onLangChange(() => {
  document.title = t('tvTitle');
  applyStatic();
  render();
});

// Which room it fell out of, if this is a reload rather than a television just switched on.
state.wasIn = recallRoom();
show('pairing');

/*
 * Keeps trying for as long as the page is open: there is nobody in front of it to reload it. Called
 * at load and again from onclose, and reentrant-safe, because a second loop would double every
 * retry and could ask for two pairing codes at once.
 */
let connecting = false;

async function connect() {
  if (connecting) return;
  connecting = true;
  try {
    while (conn.state === signalR.HubConnectionState.Disconnected) {
      try {
        await conn.start();
        state.connection = '';
        render();
      } catch (error) {
        // Nothing works without the hub, so say so on screen and leave the reason in the console.
        state.connection = t('tvOffline');
        render();
        console.error('could not reach the hub', error);
        await new Promise((resolve) => setTimeout(resolve, RECONNECT_MS));
      }
    }
  } finally {
    connecting = false;
  }

  // The screen was waiting for a connection to ask on; it has one now. A fresh connection is in no
  // groups and the code it showed before belongs to a connection that is gone, so it asks again.
  if (state.view === 'pairing') openScreen();
}

await connect();
