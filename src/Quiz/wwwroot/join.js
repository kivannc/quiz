/*
 * The phone. It joins a room with a code and a name, then shows four suit buzzers and
 * nothing else: no question text, no option text, no letters. The TV is the shared screen.
 *
 * The code and name are kept for the tab, so a reload or a dropped connection re-sends the
 * same name and lands back in the same seat with the same score.
 */

import { t, applyStatic, mountLangSwitch, onLangChange, errorText } from './i18n.js';
import { keepAwake } from './awake.js';

const STORE_CODE = 'quiz.code';
const STORE_NAME = 'quiz.name';
const STORE_TAP = 'quiz.tap';
const STORE_OPEN = 'quiz.open';
/* The name last played under, kept across evenings so the form only asks for it once. */
const STORE_LAST_NAME = 'quiz.lastName';

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
  round: null,      // and which round it belongs to: every round counts its questions from zero again
  pending: null,    // a tap on its way to the server, not yet taken
  chosen: null,     // the marker the room accepted for the current question
  baseline: null,   // this player's total before the open question is scored, or null when not knowable
  reveal: null,     // { marker, points } while the TV reveals the answer; points null means "not knowable"
  final: null,      // { rank, of } once the round is over and this player is in the standings
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
  try {
    if (state.name) localStorage.setItem(STORE_LAST_NAME, state.name);
  } catch { /* the form asks for the name again next time */ }
}

function forget() {
  try {
    sessionStorage.removeItem(STORE_CODE);
    sessionStorage.removeItem(STORE_NAME);
    sessionStorage.removeItem(STORE_TAP);
    sessionStorage.removeItem(STORE_OPEN);
  } catch { /* nothing to forget */ }
}

function rememberTap(index, marker) {
  try {
    sessionStorage.setItem(STORE_TAP, JSON.stringify({ code: state.code, round: state.round, index, marker }));
  } catch { /* the lock still holds for as long as this page lives */ }
}

/*
 * The total this phone had before the open question is scored, kept with the question it belongs to.
 * A reload during the reveal loses everything in memory, and the reveal the server sends back carries
 * totals rather than the points just earned — so without this the phone could only say it does not
 * know. With it, a player who reloads mid-reveal still sees what the question was worth.
 */
function rememberOpen(index, baseline) {
  try {
    sessionStorage.setItem(STORE_OPEN, JSON.stringify({ code: state.code, round: state.round, index, baseline }));
  } catch { /* the figure is still in memory for as long as this page lives */ }
}

function recallOpen() {
  try {
    const stored = JSON.parse(sessionStorage.getItem(STORE_OPEN) ?? 'null');
    const usable = stored && stored.code === state.code
      && Number.isInteger(stored.round) && Number.isInteger(stored.index) && Number.isInteger(stored.baseline);
    return usable ? stored : null;
  } catch {
    return null;
  }
}

/*
 * What this phone sent for this very question, if anything. Without it a reloaded player would see
 * four live buzzers, tap one, and watch the server quietly refuse the second answer — which reads
 * as a broken phone rather than as "you have already answered".
 *
 * The round is part of which question it was. A room plays several, each from question zero, and a
 * tap remembered for the first question of one round must not lock the first question of the next.
 */
function recallTap(round, index) {
  try {
    const stored = JSON.parse(sessionStorage.getItem(STORE_TAP) ?? 'null');
    return stored && stored.code === state.code && stored.round === round && stored.index === index
      ? stored.marker
      : null;
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
  // Never gives up, like the television: a Wi-Fi that drops for a minute, or the server restarting
  // for a new version, must not leave a room of phones each asking to be reloaded by hand. Quick at
  // first, then every ten seconds for as long as it takes.
  .withAutomaticReconnect({
    nextRetryDelayInMilliseconds: (context) => [0, 2_000, 5_000][context.previousRetryCount] ?? 10_000,
  })
  // Warnings and worse only: the client chats at Information level, and a living-room
  // screen has no console to read it in.
  .configureLogging(signalR.LogLevel.Warning)
  .build();

conn.on('Seated', (name, score) => {
  state.name = name;
  state.score = score;
  if (state.phase === 'join') state.phase = 'lobby';
  // A seat handed back mid-round comes with the score it already has, which during a reveal already
  // includes the points being revealed: the total in memory is no longer a before. What was stored
  // when the question opened still is, and the reveal says which question it belongs to.
  clearResult();
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
  state.final = null;
  clearResult();
  state.paused = false;
  state.error = '';
  state.notice = t('roomClosed');
  render();
});

conn.on('RoundReset', (players) => {
  // The host has started another round in the same room. This phone keeps its seat and its
  // connection, so there is nothing to join: it goes back to waiting, with the score the room now
  // holds for it, and with nothing left on screen from the round that is over.
  state.phase = 'lobby';
  state.index = null;
  state.round = null;
  state.pending = null;
  state.chosen = null;
  state.final = null;
  state.paused = false;
  state.error = '';
  clearResult();
  takeScore(players);
  try {
    sessionStorage.removeItem(STORE_TAP);
    sessionStorage.removeItem(STORE_OPEN);
  } catch { /* the round number keeps the old ones from being used anyway */ }
  render();
});

conn.on('QuestionStarted', (question) => {
  // The payload carries the question text and the options; this screen deliberately shows neither.
  // Its index is used only to tell this phone's own answer apart from an older one.
  state.phase = 'question';
  state.final = null;
  state.index = question.index;
  state.round = question.round;
  state.paused = false;
  state.pending = null;
  state.chosen = recallTap(question.round, question.index);
  // The total as it stands before this question is scored. A reveal carries totals and not deltas,
  // so this is the only thing the points earned can be worked out from — in memory, and in storage
  // for a page that does not live long enough to use the one in memory.
  state.baseline = state.score;
  rememberOpen(question.index, state.score);
  // Whatever the last reveal said, it is not about this question: the buzzers come back.
  state.reveal = null;
  state.error = '';
  render();
});

conn.on('Revealed', (reveal) => {
  // The correct marker is in here too, but the TV is where the answer is read out. What the phone
  // adds is this player's own result, and it waits for this moment to show it: a phone that lit up
  // on the tap would let a fast player's face tell the room before the TV does.
  state.phase = 'closed';
  state.paused = false;
  // A tap the room refused has already said so, under the buzzers, while they were still up. The
  // result says the same thing now, and more kindly: whichever state the room is in owns this
  // screen, the way a pause already takes the refusal off it.
  state.error = '';
  // Worked out before the new total is taken, because the old one is half of the subtraction.
  state.reveal = resultOf(reveal);
  takeScore(reveal.standings);
  render();
});

conn.on('RoundFinished', (standings) => {
  state.phase = 'finished';
  state.paused = false;
  // The last question's tap is not an answer to anything any more.
  state.chosen = null;
  state.pending = null;
  clearResult();
  takeScore(standings);
  // Where this player finished. The standings arrive best first, so the place is the position.
  const place = (standings ?? []).findIndex((player) => player.name === state.name);
  state.final = place < 0 ? null : { rank: place + 1, of: standings.length };
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

/*
 * A phone that was locked or put away comes back with its socket long gone, and sometimes with the
 * reconnecting given up on by the browser rather than by this page. Coming back to the page is the
 * moment to try again, so the player never has to know what a reload is.
 */
async function revive() {
  if (document.visibilityState !== 'visible' || conn.state !== signalR.HubConnectionState.Disconnected) return;
  try {
    await conn.start();
    state.connection = '';
    render();
    await rejoin();
  } catch {
    // Still unreachable. The notice stays, and the next time the page is looked at tries again.
  }
}
document.addEventListener('visibilitychange', revive);
window.addEventListener('online', revive);

/*
 * The result and the total it would be worked out from both belong to one question, so they go
 * together: out of a seat handed back, out of a round that has ended, out of a room that has closed.
 */
function clearResult() {
  state.baseline = null;
  state.reveal = null;
}

/* The standings are the only place a player's own score comes from. */
function takeScore(standings) {
  const mine = (standings ?? []).find((player) => player.name === state.name);
  if (mine) state.score = mine.score;
}

/* The result with nothing to put a number on: better than a number that would be wrong. */
function unnumbered() {
  return { marker: null, points: null };
}

/*
 * This player's own result for the question that just closed: the suit they tapped, and what it
 * earned. A reveal carries totals, so the points are the difference from the total this phone had
 * before that question opened — and the only safe way to use such a total is to know which question
 * it was taken for, because a phone that missed a question opening (a locked screen, a dropped
 * socket) still holds the one before it, and subtracting that would hand the player a confident
 * number made of two questions. So both the total in memory and the one in storage are used only
 * when they were taken for the question this reveal is closing. Anything else shows no number.
 */
function resultOf(reveal) {
  const mine = (reveal.standings ?? []).find((player) => player.name === state.name);
  if (!mine) return unnumbered();

  // What this page watched open. A tap the room took but has not acknowledged yet is still this
  // player's answer, so the suit comes from the tap in flight when there is one.
  if (state.baseline !== null && state.round === reveal.round && state.index === reveal.index) {
    return earnedFrom(mine.score - state.baseline, state.chosen ?? state.pending);
  }

  // Nothing in memory for this question: the figure written when it opened stands in, which is what
  // carries a reload through the reveal.
  const open = recallOpen();
  if (!open || open.round !== reveal.round || open.index !== reveal.index) return unnumbered();
  return earnedFrom(mine.score - open.baseline, recallTap(open.round, open.index));
}

function earnedFrom(points, marker) {
  // A total that went down is not something this screen can explain, so it does not try.
  if (points < 0) return unnumbered();
  // The tap is shown whether it earned anything or not; what changes is how loudly.
  return { marker, points };
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

  // The result takes the buzzers' place for as long as the room is revealing, and no longer. A paused
  // room and a closed one own this screen on their own terms, so neither has to argue with the result.
  const result = state.phase === 'closed' && !state.paused ? state.reveal : null;
  // And once the round is over there is nothing left to press: the place this player finished in
  // takes the buzzers' place for good.
  const final = state.phase === 'finished' ? state.final : null;
  el('buzzers').hidden = result !== null || final !== null;
  paintResult(result);
  paintFinal(final);

  // Held awake for as long as this phone is in a room, so a screen that dims between two questions
  // is not what costs a player the next one.
  keepAwake(view === 'play' && state.phase !== 'finished');
}

/* The last screen: the place, and under it either that it is first or how many it is out of. */
function paintFinal(final) {
  const box = el('final');
  box.hidden = final === null;
  if (final === null) return;

  const won = final.rank === 1 && final.of > 1;
  if (won) box.dataset.won = '';
  else delete box.dataset.won;
  el('final-place').textContent = t('finalPlace', final.rank);
  el('final-of').textContent = won ? t('youWon') : t('finalOf', final.of);
  el('final-total').textContent = t('totalScore', state.score);
}

/*
 * The scale. A correct answer is worth 500 points on the buzzer and 1000 given at once, so the
 * scale has a floor and a ceiling: everything that says "how well did I do" is interpolated
 * linearly between them, and nothing is stepped. Zero is not on that scale — it is the muted
 * state below it.
 */
const POINTS_FLOOR = 500;
const POINTS_CEILING = 1000;
/* In the mockup's own units; app.css decides what one of them is worth on the screen in hand. */
const GLYPH_SIZE = [42, 66];
const POINTS_SIZE = [27, 54];
const WASH = [0.06, 0.26];
const NOTHING_GLYPH_SIZE = 38;
const NOTHING_POINTS_SIZE = 22;

function between([floor, ceiling], fraction) {
  return floor + (ceiling - floor) * fraction;
}

/* Paints the result screen, or hides it when there is nothing to reveal. */
function paintResult(result) {
  const box = el('result');
  const glyph = el('result-glyph');
  const points = el('result-points');

  box.hidden = result === null;
  if (result === null) return;

  const earned = result.points !== null && result.points > 0;
  const fraction = earned
    ? (Math.min(Math.max(result.points, POINTS_FLOOR), POINTS_CEILING) - POINTS_FLOOR) / (POINTS_CEILING - POINTS_FLOOR)
    : 0;

  // Colour belongs to the suit, so a result with no suit to name has no wash and no glow — but it
  // keeps the size its score earned, and the plain colour of a number that counted. Grey is kept for
  // a result that earned nothing, where it agrees with the number beside it.
  const tinted = earned && Boolean(result.marker);
  if (tinted) box.dataset.marker = result.marker;
  else delete box.dataset.marker;
  if (earned) box.dataset.earned = '';
  else delete box.dataset.earned;
  box.style.setProperty('--o', tinted ? between(WASH, fraction).toFixed(3) : '0');

  glyph.hidden = !result.marker;
  if (result.marker) {
    glyph.dataset.marker = result.marker;
    // The glyph is drawn by the stylesheet, so a screen reader has nothing to read without this.
    glyph.setAttribute('role', 'img');
    glyph.setAttribute('aria-label', t('markerName', result.marker));
  } else {
    delete glyph.dataset.marker;
    glyph.removeAttribute('role');
    glyph.removeAttribute('aria-label');
  }
  box.style.setProperty('--glyph', String(earned ? between(GLYPH_SIZE, fraction) : NOTHING_GLYPH_SIZE));
  box.style.setProperty('--pts', String(earned ? between(POINTS_SIZE, fraction) : NOTHING_POINTS_SIZE));

  points.hidden = result.points === null;
  points.textContent = result.points === null ? '' : t('pointsEarned', result.points);

  el('result-total').textContent = t('totalScore', state.score);
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

/*
 * The television's QR code opens this page with the room code in the address, so a player who scans
 * it has only a name to type — and not even that on a phone that has played before. The code is
 * taken out of the address once read: it is spent the moment the room closes, and a reload or a
 * bookmark must not keep offering it.
 */
const scanned = new URLSearchParams(location.search).get('code');
if (scanned !== null) history.replaceState(null, '', location.pathname);

/* A reload rejoins by itself, on the same path a dropped connection takes. */
const stored = recall();
const fresh = /^[0-9]{4}$/.test(scanned ?? '') && scanned !== stored.code;
if (!fresh && stored.code && stored.name && conn.state === signalR.HubConnectionState.Connected) {
  state.code = stored.code;
  state.name = stored.name;
  await rejoin();
}

if (state.phase === 'join') {
  const form = el('form-join');
  if (fresh) form.elements.code.value = scanned;
  try {
    if (!form.elements.name.value) form.elements.name.value = localStorage.getItem(STORE_LAST_NAME) ?? '';
  } catch { /* nothing remembered */ }
  // The cursor goes to whichever of the two is still empty.
  (form.elements.code.value ? form.elements.name : form.elements.code).focus();
}
