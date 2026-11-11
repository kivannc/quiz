/*
 * The television: the display half of the game. It opens one address, shows a four-digit pairing
 * code, and waits for the host's phone to claim it. From then on it paints what the room sends —
 * lobby, question, reveal, standings. There is nothing to type here and nothing to press, which is
 * the whole point: a TV remote is the worst input device in the house.
 *
 * It is also what paces the round. The countdown on this screen is the one that closes the question,
 * the beat after the last answer is counted here, and so is the time a reveal stays up. That used to
 * be the console's job, and a console is a phone: it gets locked, it gets put in a pocket, and the
 * person holding it wants to play, in another tab — and a browser stops the timers of a page nobody
 * is looking at, so the round stopped with it. The television is the one screen a round cannot be
 * played without, so whenever its timers are not running there is nobody reading the question
 * either. The clock the room watches is now the clock that acts.
 *
 * What it still never does is ask for the host key: a screen is put into a room by being claimed,
 * never by proving anything, and the hub lets the screen a room is shown on say two things about it
 * and no more — this question's time is up, this reveal has been read. Starting, pausing, skipping
 * ahead and closing the room stay with the console.
 */

import { t, applyStatic, onLangChange, setLang } from './i18n.js';
import { qrSvg } from './qr.js';
import { keepAwake } from './awake.js';

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

/* Everyone has answered: the beat the room gets to see the last tap land before the reveal. */
const EVERYONE_IN_MS = 3_000;
/* How long the reveal stays up — the answer, who chose what, and who earned what — before the round
 * moves on by itself. Long enough to find the lit card, say "I knew it" and look for a name. */
const REVEAL_MS = 9_000;

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
  taken: false,      // it lost that room to another screen rather than to a reload of its own
  players: [],       // [{ name, score }], from RoomState
  answered: 0,       // from AnswerCount
  seated: 0,         // how many players that count is out of
  question: null,    // the QuestionStarted payload
  reveal: null,      // the Revealed payload
  standings: [],     // from RoundFinished
  before: null,      // name -> total when the open question opened, so the reveal can say who earned what
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
  state.taken = false;
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

/* ---------- the clock, which closes the question ---------- */

let deadline = 0;
let ticker = 0;
// What was left of the clock when the room was paused, in milliseconds; 0 when it is running.
let frozen = 0;
// What the bar is full at: the time this screen was first told the open question had left.
let span = 0;
/* Under this many seconds the clock and the bar change colour, and so does the console's copy. */
const CLOCK_LOW_S = 5;

function stopClock() {
  clearInterval(ticker);
  ticker = 0;
  deadline = 0;
  frozen = 0;
}

/*
 * Counts down what the room says is left, and at zero tells the room so, which is what closes the
 * question. The number stays at zero for the moment that takes: the question is over and the reveal
 * is on its way.
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
      timeUp();
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

/*
 * Starts it again with exactly what was left, never with the whole limit. Nothing left means the
 * question ran out at the very moment the room was stopped, and the room did not close it then
 * because a paused room is waiting for its host: it has to be told again now, or nothing ever would.
 */
function thawClock() {
  if (!state.question || ticker) return;
  const left = frozen;
  frozen = 0;
  if (left <= 0) {
    drawClock(0);
    timeUp();
    return;
  }
  startClock(left);
}

function drawClock(ms = deadline - Date.now()) {
  const left = Math.max(0, ms);
  const seconds = Math.ceil(left / 1000);
  el('tag-clock').textContent = t('clock', seconds);
  // The same time as a length: it empties to nothing at the moment the number reaches zero.
  el('timebar-fill').style.width = `${span > 0 ? Math.min(100, (left / span) * 100) : 0}%`;
  document.body.classList.toggle('low', seconds <= CLOCK_LOW_S && !state.paused);
}

/* ---------- pacing: what carries the round on by itself ---------- */

/*
 * Two one-shot timers, never more than one of them armed: a question closes early once everyone is
 * in, and a reveal hands the round on once it has been read. A timer left over from the screen
 * before would fire into the next one, so show() drops both on every change of screen — and the hub
 * is told which question or reveal is meant, so one that fires late anyway moves nothing.
 */
let everyoneIn = 0;
let nextUp = 0;

function cancelEveryoneIn() {
  clearTimeout(everyoneIn);
  everyoneIn = 0;
}

function cancelPacing() {
  cancelEveryoneIn();
  clearTimeout(nextUp);
  nextUp = 0;
}

/* Everyone seated has answered the question that is up. A paused room is waiting for its host, not
 * for its players, so it is never ready to close by itself. */
function everyoneIsIn() {
  return state.view === 'question' && !state.paused && state.seated > 0 && state.answered >= state.seated;
}

/* The beat before the reveal, or none. Already armed means the room was already full: a second
 * count saying the same thing must not push it back. */
function paceQuestion() {
  if (!everyoneIsIn()) {
    cancelEveryoneIn();
    return;
  }
  if (everyoneIn) return;
  everyoneIn = setTimeout(() => {
    everyoneIn = 0;
    timeUp();
  }, EVERYONE_IN_MS);
}

/* The question on screen is over. Said to the room rather than done here: the room decides, and it
 * ignores this for a question that is paused, already closed, or not the one it has open. */
function timeUp() {
  const question = state.question;
  if (state.view !== 'question' || state.paused || !question || !state.room) return;
  say('TimeUp', question.round, question.index);
}

/* The reveal on screen has been read: on to the next question, or to the standings after the last. */
function revealRead() {
  const reveal = state.reveal;
  if (state.view !== 'reveal' || !reveal || !state.room) return;
  say('RevealRead', reveal.round, reveal.index);
}

/*
 * A refusal is not shown to a room full of people, and is not retried: the only ways to be refused
 * are to have fallen out of the room, which puts a pairing code on this screen anyway, or to have
 * lost the connection, which does the same when it comes back. Being claimed again re-sends the
 * question or the reveal, and that starts these timers afresh.
 */
function say(method, round, index) {
  conn.invoke(method, state.room, round, index).catch((error) => {
    console.error(`the room did not take ${method}`, error);
  });
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
  state.before = null;
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

conn.on('RoundReset', () => {
  // Another round in the same room. The lobby that follows is what this screen paints; this only
  // drops what belonged to the round that is over, so its podium cannot be shown for the next one.
  stopClock();
  cancelPacing();
  state.question = null;
  state.reveal = null;
  state.standings = [];
  state.before = null;
  state.paused = false;
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
  // Nobody is left to wait for, so there is nothing for the rest of the limit to buy: close after a
  // beat instead. The countdown stays the backstop — a phone that went to sleep never answers, and
  // the question still closes at zero.
  paceQuestion();
});

conn.on('QuestionStarted', (question) => {
  state.question = question;
  // A screen seated during a reveal is sent the question with no time on it, only so it knows what
  // was asked. Nothing is open, so nothing is painted here: the Revealed that follows does that.
  if (question.limitSeconds <= 0) return;

  state.reveal = null;
  state.answered = 0;
  state.paused = false;
  // The totals as they stand before this question is scored: the roster was refreshed after the
  // last reveal, so what it holds now is exactly the "before" the next reveal is measured from.
  state.before = new Map(state.players.map((player) => [player.name, player.score]));
  show('question');
  // What the room says is left, which on a screen arriving mid-question is not the whole limit.
  span = question.limitSeconds * 1000;
  startClock(span);
});

conn.on('Paused', (paused) => {
  state.paused = Boolean(paused);
  if (state.paused) {
    // Nothing of its own may happen while the room waits: not the countdown, and not the beat that
    // closes a question everyone has answered.
    freezeClock();
    cancelPacing();
  } else {
    thawClock();
    // The room may have filled up while it was stopped, or before it stopped: either way the beat
    // starts now, since no further AnswerCount is coming to start it.
    paceQuestion();
  }
  render();
});

conn.on('Revealed', (reveal) => {
  stopClock();
  // The room drops its pause when the question closes, and so does the screen.
  state.paused = false;
  state.reveal = reveal;
  show('reveal');
  // Armed after show(), which is what dropped the question's own timer. A screen claimed into a room
  // during a reveal is sent this too, and its nine seconds start then: it has only just begun
  // painting the answer. The last question's reveal moves on the same way, which is what ends the round.
  nextUp = setTimeout(() => {
    nextUp = 0;
    revealRead();
  }, REVEAL_MS);
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

conn.on('RoomTaken', (code) => {
  /*
   * Another television has been claimed into the room this screen was painting, so the room is on
   * that screen now and this one is out of it. The room has not ended — telling a screen three
   * metres away that it closed would be a lie — so this says which room moved on and goes back to
   * offering a pairing code, which is the only way into any room and so the way back into this one.
   */
  stopClock();
  state.question = null;
  state.reveal = null;
  state.standings = [];
  state.paused = false;
  forgetRoom();
  state.wasIn = code ?? null;
  state.taken = true;
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
  // die: a pairing code is not refreshed while a question is up, a claim that became a room is no
  // longer waiting to be given up on, and a beat armed for one question does not fire into the next.
  clearPairingTimers();
  cancelPacing();
  // A reveal and a podium are built once and then left alone; leaving the screen is what lets the
  // next one be built.
  if (view !== 'reveal') delete el('view-reveal').dataset.shown;
  if (view !== 'finished') {
    delete el('view-finished').dataset.shown;
    el('rain').replaceChildren();
  }
  if (view !== 'question') document.body.classList.remove('low');
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
  if (state.view === 'finished') drawFinished();

  // A television that is really a laptop must not dim and sleep in the middle of a round it is
  // pacing. Only while it is in a room: a screen left on its pairing code overnight may sleep.
  keepAwake(state.room !== null);
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
  // The same code as something to point a camera at: it opens the console with the four digits in
  // the field. Never beside a dead code, for the reason the address is not.
  drawQr(el('pair-scan'), el('pair-qr'), waiting ? null : `${location.origin}/host.html?pair=${state.code}`);

  const status = el('pair-status');
  set(status, state.codeFailed ? t('pairFailed') : '');
  status.classList.toggle('error', state.codeFailed);
  // With no code under it, this line is the only thing on the screen that explains it, so it is read
  // at the distance the code is read from rather than in the small print.
  status.classList.toggle('big', state.codeFailed && waiting);

  // Only ever beside a code: on its own it would tell the room to type something that is not there.
  // Which of the two lines it is depends on how the room was lost: a reload of this screen, or
  // another screen taking the room, which is a different thing to say and a different thing to do.
  set(el('pair-rejoin'), state.wasIn && !waiting
    ? t(state.taken ? 'roomMovedHint' : 'rejoinHint', state.wasIn)
    : '');
}

/* Where the host's console lives, which is the one thing this screen asks anybody to do. */
function consoleAddress() {
  return `${location.host}/host.html`;
}

/*
 * Draws `address` as a QR code into `box`, or hides `frame` when there is nothing to scan. The code
 * is only rebuilt when the address changes: every render comes through here, and a code that was
 * redrawn on each one would flicker under a camera trying to read it. An address this encoder
 * cannot draw hides the frame too — the typed code beside it still works, and a wrong code would not.
 */
function drawQr(frame, box, address) {
  if (!address) {
    frame.hidden = true;
    box.replaceChildren();
    delete box.dataset.address;
    return;
  }
  if (box.dataset.address !== address) {
    try {
      box.replaceChildren(qrSvg(address));
      box.dataset.address = address;
    } catch (error) {
      console.error('could not draw the QR code', error);
      frame.hidden = true;
      return;
    }
  }
  frame.hidden = false;
}

function drawLobby() {
  el('lobby-code').textContent = state.room ?? '';
  el('lobby-where').textContent = t('joinAt', location.host);
  drawQr(el('lobby-scan'), el('lobby-qr'), state.room ? `${location.origin}/?code=${state.room}` : null);

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

/*
 * The reveal keeps the question and its four cards where they were and changes what they say: the
 * right one is lit, the others step back, and each carries how many chose it. The room was looking
 * at exactly these cards a moment ago, so the answer lands where the eyes already are — and whoever
 * was reading the question aloud can still see what was asked.
 *
 * Under it, the standings with what each player just earned. A reveal carries totals, so the gain is
 * the difference from the totals this screen held when the question opened; a screen seated during
 * the reveal never held them and shows the totals alone.
 *
 * Built once per reveal: the roster refresh that follows every reveal comes back through render(),
 * and rebuilding the cards would start their animation again in the middle of being read.
 */
function drawReveal() {
  const reveal = state.reveal;
  if (!reveal) return;

  const view = el('view-reveal');
  const signature = `${state.room}:${reveal.round}:${reveal.index}:${document.documentElement.lang}`;
  if (view.dataset.shown === signature) return;
  view.dataset.shown = signature;

  el('reveal-question').textContent = state.question?.text ?? '';

  const options = state.question?.options ?? MARKERS.map((marker) => ({ marker, text: '' }));
  el('reveal-options').replaceChildren(...options.map((option) => {
    const card = optionCard(option);
    card.classList.add(option.marker === reveal.correct ? 'right' : 'wrong');
    const n = document.createElement('span');
    n.className = 'n';
    n.textContent = String(reveal.distribution[option.marker] ?? 0);
    card.append(n);
    return card;
  }));

  drawBoard(el('reveal-board'), reveal.standings, { before: state.before });
}

/*
 * A list of standings. `offset` is how many places stand above the first row, for the list under the
 * podium; `before` is the totals the gains are measured from, when this screen knows them.
 */
function drawBoard(list, standings, { offset = 0, before = null } = {}) {
  list.classList.toggle('dense', standings.length > 8);
  list.replaceChildren(...standings.map((player, i) => {
    const row = document.createElement('li');

    const who = document.createElement('span');
    who.className = 'who';
    const rank = document.createElement('span');
    rank.className = 'rank';
    rank.textContent = String(offset + i + 1);
    const name = document.createElement('span');
    name.textContent = player.name;
    who.append(rank, name);
    row.append(who);

    if (before?.has(player.name)) {
      const earned = player.score - before.get(player.name);
      const gain = document.createElement('span');
      gain.className = 'gain';
      gain.textContent = t('gained', Math.max(0, earned));
      if (earned > 0) gain.dataset.earned = '';
      row.append(gain);
    }

    const score = document.createElement('span');
    score.className = 'score';
    score.textContent = String(player.score);
    row.append(score);
    return row;
  }));
}

/* How many suits fall behind the podium. Enough to fill a television, few enough to stay calm. */
const RAIN = 36;

/*
 * The end of the round: the first three on a podium, built up from third place to first so the room
 * gets a moment to wonder, and everyone else in a list underneath. The stylesheet owns the timing;
 * each step only carries its place. Built once per set of standings, for the reason the reveal is.
 */
function drawFinished() {
  const view = el('view-finished');
  const signature = `${state.room}:${JSON.stringify(state.standings)}:${document.documentElement.lang}`;
  if (view.dataset.shown === signature) return;
  view.dataset.shown = signature;

  const top = state.standings.slice(0, 3);
  // Second, first, third: the winner stands in the middle, as on any podium.
  const order = [top[1], top[0], top[2]];
  el('podium').replaceChildren(...order.map((player, i) => {
    if (!player) return null;
    const place = [2, 1, 3][i];
    const step = document.createElement('div');
    step.className = 'step';
    step.dataset.place = String(place);

    if (place === 1) {
      const tag = document.createElement('div');
      tag.className = 'label';
      tag.textContent = t('winnerTag');
      step.append(tag);
    }
    const name = document.createElement('div');
    name.className = 'name';
    name.textContent = player.name;
    const score = document.createElement('div');
    score.className = 'score';
    score.textContent = String(player.score);
    const block = document.createElement('div');
    block.className = 'block';
    block.textContent = String(place);
    step.append(name, score, block);
    return step;
  }).filter(Boolean));

  const rest = state.standings.slice(3);
  el('final-board').hidden = rest.length === 0;
  drawBoard(el('final-board'), rest, { offset: 3 });

  // The four suits, falling. Each gets its own lane, speed and size here; the stylesheet moves
  // them, and does not move them at all for anyone who has asked their device for less motion.
  el('rain').replaceChildren(...Array.from({ length: RAIN }, (_, i) => {
    const drop = document.createElement('span');
    drop.className = 'glyph';
    drop.dataset.marker = MARKERS[i % MARKERS.length];
    drop.style.left = `${((i * 61) % 100) + Math.random() * 2}%`;
    drop.style.setProperty('--fall', `${5 + Math.random() * 5}s`);
    drop.style.setProperty('--wait', `${(Math.random() * 6).toFixed(2)}s`);
    drop.style.setProperty('--scale', (0.6 + Math.random() * 1.2).toFixed(2));
    return drop;
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
