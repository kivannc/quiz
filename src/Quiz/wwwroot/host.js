/*
 * The host's console: a phone that drives the room. The television (tv.html) is the display and
 * paints the game; this page paints none of it, because the host is looking at the television like
 * everyone else. What it owns is the pacing — the countdown that closes a question, the beat after
 * everyone has answered, the beat after a reveal — and the buttons: start, pause, next, close.
 *
 * It never speaks to the television directly. A room is opened on a screen this console has claimed
 * with the four-digit code that screen is showing, and from then on the hub sends the two of them
 * the same room messages.
 *
 * There are no timers on the server: when this page's countdown reaches zero it calls CloseQuestion
 * itself. The host key is typed once and kept in sessionStorage, so a phone that slept mid-round
 * re-sends it, asks the hub for the room as it stands, and comes back to its controls.
 */

import { t, applyStatic, mountLangSwitch, onLangChange, errorText } from './i18n.js';

/* Everyone has answered: the beat the room gets to see the last tap land before the reveal. */
const EVERYONE_IN_MS = 3000;
/* How long the reveal stays up — the bars and the standings — before the round moves on by itself. */
const REVEAL_MS = 6000;

/* Under this many seconds left, the clock on the console says so without being read. */
const CLOCK_LOW_S = 5;

const STORE_KEY = 'quiz.hostKey';
const STORE_CODE = 'quiz.code';
const STORE_SET = 'quiz.setId';
const STORE_DEADLINE = 'quiz.deadline';

const el = (id) => document.getElementById(id);

/*
 * Two screens on this page: the form that claims a television and opens a room, and the console
 * proper. The room's phase is state.view, as it is on the television, because the pacing is written
 * in terms of it — but all four phases are drawn in the one section, since what changes between
 * them here is a line of text and which button is up.
 */
const RUN_VIEWS = ['lobby', 'question', 'reveal', 'finished'];

const state = {
  // 'resuming' paints nothing: it is the moment between asking the hub for the room and hearing back.
  view: 'setup',
  code: null,
  hostKey: null,
  setId: null,
  claimed: false,   // a screen has been claimed and no room opened on it yet
  sets: [],         // { id, title, language } from GET /sets; their titles are content, not UI strings
  players: [],      // [{ name, score }], from RoomState
  answered: 0,      // AnswerCount, host screens only
  seated: 0,        // how many players that count is out of
  question: null,   // the QuestionStarted payload: which question of how many, and whether it is the last
  paused: false,    // the room's clock is stopped, which the server knows too
  error: '',
  repair: null,
  connection: '',   // reconnecting / disconnected notice
};

/* ---------- session memory ---------- */

function remember() {
  try {
    sessionStorage.setItem(STORE_KEY, state.hostKey ?? '');
    sessionStorage.setItem(STORE_CODE, state.code ?? '');
    sessionStorage.setItem(STORE_SET, state.setId ?? '');
  } catch {
    // Without storage a reload simply cannot resume; everything else still works.
  }
}

function forgetRoom() {
  state.code = null;
  try {
    sessionStorage.removeItem(STORE_CODE);
  } catch { /* nothing to forget */ }
}

function recall() {
  try {
    return {
      hostKey: sessionStorage.getItem(STORE_KEY) || null,
      code: sessionStorage.getItem(STORE_CODE) || null,
      setId: sessionStorage.getItem(STORE_SET) || null,
    };
  } catch {
    return { hostKey: null, code: null, setId: null };
  }
}

function rememberDeadline(index, at) {
  try {
    sessionStorage.setItem(STORE_DEADLINE, JSON.stringify({ code: state.code, index, at }));
  } catch { /* the countdown still runs; only a reload would have used this */ }
}

/*
 * A remembered deadline is a promise about wall-clock time, and a paused room has stopped spending
 * it. Rather than keep a stale one, drop it: a console reloading into a paused question is then told
 * what is left by the room itself, which is the number that stood still.
 */
function forgetDeadline() {
  try {
    sessionStorage.removeItem(STORE_DEADLINE);
  } catch { /* nothing to forget */ }
}

/*
 * The deadline this page set for this very question, if it is the page that set it. A reload must
 * not hand the players a fresh twenty seconds: the room goes on refusing answers after the original
 * limit, so the extra time would be a lie. A console that never ran this question has nothing stored
 * and falls back to the full limit.
 */
function recallDeadline(index) {
  try {
    const stored = JSON.parse(sessionStorage.getItem(STORE_DEADLINE) ?? 'null');
    return stored && stored.code === state.code && stored.index === index ? stored.at : null;
  } catch {
    return null;
  }
}

/* ---------- the countdown, which belongs to this page ---------- */

let deadline = 0;
let ticker = 0;
// What was left of the clock when it was frozen, in milliseconds; 0 when it is running.
let frozen = 0;

function stopCountdown() {
  clearInterval(ticker);
  ticker = 0;
  deadline = 0;
  frozen = 0;
}

function startCountdown(index, at) {
  stopCountdown();
  deadline = at;
  rememberDeadline(index, at);
  drawClock();
  ticker = setInterval(() => {
    drawClock();
    if (Date.now() >= deadline) {
      stopCountdown();
      closeQuestion();
    }
  }, 200);
}

/*
 * Stops the clock where it stands and keeps what was left of it. The room stops counting too, so
 * the frozen number is the truth rather than a frozen picture of it; it is read a moment after the
 * room stopped, so it is a shade short, which is the safe direction.
 */
function freezeCountdown() {
  if (!ticker) return;
  frozen = Math.max(0, deadline - Date.now());
  clearInterval(ticker);
  ticker = 0;
  forgetDeadline();
  drawClock(frozen);
}

/*
 * Starts it again with exactly what was left, never with the whole limit — and starts it even when
 * what was left is nothing, because a question frozen at zero still has to close, and the next tick
 * is what does it. This page has no other way to close a question by hand.
 *
 * A running ticker is what tells a frozen clock from one that was never frozen, since zero left and
 * never frozen both read as `frozen === 0`: freezing always stops the ticker, so a console with one
 * still running has nothing to thaw. Without that, a stray resume — a second console on the same
 * key, or one that lands between a reload's QuestionStarted and its Paused — would restart the
 * countdown with nothing on it and close a running question a tick later.
 */
function thawCountdown() {
  if (!state.question || ticker) return;
  const left = frozen;
  frozen = 0;
  startCountdown(state.question.index, Date.now() + left);
}

function drawClock(ms = deadline - Date.now()) {
  const left = Math.max(0, Math.ceil(ms / 1000));
  const clock = el('tag-clock');
  clock.textContent = t('clock', left);
  // The last few seconds, said in a colour rather than in a word: the host is watching the room.
  clock.classList.toggle('low', left <= CLOCK_LOW_S && !state.paused);
}

/* ---------- pacing: the two beats that carry the round on its own ---------- */

/*
 * Two one-shot timers, never more than one of them armed: a question closes early once everyone is
 * in, and a reveal hands the round on once it has been read. Both call the same host actions the
 * buttons do, and the hub ignores a host action that does not fit the phase — but a timer left over
 * from the phase before would fire into the next one and skip a question, so show() drops both on
 * every view change and the reveal arms its own only after that.
 */
let everyoneIn = 0;
let nextUp = 0;

function cancelEveryoneIn() {
  clearTimeout(everyoneIn);
  everyoneIn = 0;
}

function cancelNextUp() {
  clearTimeout(nextUp);
  nextUp = 0;
}

function cancelPacing() {
  cancelEveryoneIn();
  cancelNextUp();
}

/* Everyone seated has answered the question that is open. A paused room is waiting for its host,
 * not for its players, so it is never ready to close on its own. */
function everyoneIsIn() {
  return state.view === 'question' && !state.paused && state.seated > 0 && state.answered >= state.seated;
}

/* The beat before the reveal. Already armed means the room was already full: a second count saying
 * the same thing must not push it back. */
function armEveryoneIn() {
  if (everyoneIn) return;
  everyoneIn = setTimeout(() => {
    everyoneIn = 0;
    closeQuestion();
  }, EVERYONE_IN_MS);
}

/* The beat after it. Its caller has just shown the reveal, which is what cleared the question's. */
function armNextUp() {
  nextUp = setTimeout(() => {
    nextUp = 0;
    advance();
  }, REVEAL_MS);
}

/* ---------- hub ---------- */

const conn = new signalR.HubConnectionBuilder()
  .withUrl('/hub')
  .withAutomaticReconnect()
  // Warnings and worse only: the client chats at Information level, and a phone in a dark
  // living room has no console to read it in.
  .configureLogging(signalR.LogLevel.Warning)
  .build();

conn.on('RoomState', (players, phase) => {
  state.players = players ?? [];
  // The question and reveal payloads own those two phases; RoomState only refreshes the roster.
  if (phase === 'lobby') show('lobby');
  else if (phase === 'finished') show('finished');
  else render();
});

conn.on('AnswerCount', (answered, total) => {
  // Both numbers come from the server: total counts seated players, answered the ones in.
  state.answered = answered;
  state.seated = total;
  drawRun();

  // Nobody is left to wait for, so there is nothing for the rest of the limit to buy: close after a
  // beat instead. The countdown stays the backstop — a phone that went to sleep never answers, and
  // the question still closes at zero. Any other count leaves the beat unarmed, which in practice
  // means the 0-of-n a question opens with: nobody can join a round in progress and raise the total.
  if (everyoneIsIn()) armEveryoneIn();
  else cancelEveryoneIn();
});

conn.on('QuestionStarted', (question) => {
  state.question = question;
  // A resume during the reveal sends the question with no time on it, only so this page knows which
  // question of how many is being revealed, and whether it is the last. Nothing is open, so no
  // countdown runs: the Revealed that follows is what moves the console on.
  if (question.limitSeconds <= 0) return;

  state.answered = 0;
  state.paused = false;
  show('question');
  // A console that has run this question already counts its own deadline down. One arriving fresh
  // uses limitSeconds, which on a resume is what the room says is left, not the whole limit.
  const at = (resuming ? recallDeadline(question.index) : null) ?? Date.now() + question.limitSeconds * 1000;
  startCountdown(question.index, at);
});

conn.on('Paused', (paused) => {
  state.paused = Boolean(paused);
  if (state.paused) {
    // Nothing of its own may happen while the room waits: not the countdown, and not the beat that
    // closes a question everyone has answered.
    freezeCountdown();
    cancelPacing();
  } else {
    thawCountdown();
    // The room may have filled up while it was stopped, or before it stopped: either way the beat
    // starts now, since no further AnswerCount is coming to start it.
    if (everyoneIsIn()) armEveryoneIn();
  }
  render();
});

conn.on('RoomClosed', () => {
  // The room is gone for everyone, the television included: back to the form, ready to claim the
  // code the television is showing again and open another room on it.
  stopCountdown();
  cancelPacing();
  forgetDeadline();
  forgetRoom();
  state.question = null;
  state.paused = false;
  state.error = '';
  show('setup');
});

conn.on('Revealed', () => {
  stopCountdown();
  // The room drops its pause when the question closes, and so does the console.
  state.paused = false;
  show('reveal');
  // Armed after show(), which is what cleared the question's own timer. A console that reloads
  // during a reveal is sent this again and arms it too, so a phone waking at the wrong moment does
  // not stall the round. The last question's reveal advances the same way: that is what ends it.
  armNextUp();
});

conn.on('RoundFinished', () => {
  stopCountdown();
  state.paused = false;
  // The standings are the television's to show. Nothing is armed here: the round is over.
  show('finished');
});

conn.onreconnecting(() => {
  // An invoke now would only fail. Resuming re-sends AnswerCount or Revealed, which arms it again.
  cancelPacing();
  state.connection = t('reconnecting');
  render();
});

conn.onreconnected(() => {
  // A reconnected connection is a new connection: it is in no groups, and any claim it was holding
  // belonged to the one that is gone. So it asks for the room again — which is enough for the
  // console, because the room remembers which screen it was opened on.
  state.connection = '';
  state.claimed = false;
  render();
  resume();
});

conn.onclose(() => {
  stopCountdown();
  cancelPacing();
  state.claimed = false;
  state.connection = t('disconnected');
  render();
});

/* ---------- actions ---------- */

/*
 * Claims the television showing `code`. A room can only be opened on a claimed screen, and the same
 * call puts a television that has reloaded back into the room this console is already running — the
 * one thing that screen asks for when it comes back up.
 */
async function claimScreen(code) {
  await conn.invoke('ClaimScreen', code, state.hostKey);
  state.claimed = true;
}

async function createRoom(hostKey, setId) {
  state.code = await conn.invoke('CreateRoom', hostKey, setId);
  state.hostKey = hostKey;
  state.setId = setId;
  // Opening the room spends the claim: the next room needs the next code the television shows.
  state.claimed = false;
  state.players = [];
  state.question = null;
  remember();
  show('lobby');
}

/* Host actions are quietly ignored by the server when they do not fit the phase, so a
 * double press needs no guard here. A room that has gone (expired, or the server restarted)
 * is the one failure worth acting on: forget it and ask for the key again. */
async function hostAction(method) {
  try {
    await conn.invoke(method, state.code, state.hostKey);
    state.error = '';
    render();
  } catch (error) {
    state.error = error;
    if (String(error?.message ?? '').includes('no-such-room')) {
      forgetRoom();
      show('setup');
      return;
    }
    render();
  }
}

/*
 * Takes the room back after a reload or a dropped connection: re-sends the key (which re-joins the
 * host groups) and asks the hub to send this console the room as it stands. ResumeHost is read-only,
 * so a console coming back cannot start, close or advance anything — and it needs no screen, because
 * the television is still in the room on its own connection.
 */
let resuming = false;

/*
 * Load-bearing ordering: the hub's sends for this resume reach the client before the invoke's own
 * completion, so every QuestionStarted that belongs to a resume is handled while this flag is still
 * true. Do not move the reset, or awaken it after other awaits — a resumed console would then throw
 * away the deadline it was already counting down and restart the question's clock.
 */
async function resume() {
  if (!state.code || !state.hostKey) return;
  resuming = true;
  try {
    await conn.invoke('ResumeHost', state.code, state.hostKey);
    state.error = '';
  } catch (error) {
    // The room has gone, or this key no longer drives it: ask for a room again.
    state.error = error;
    forgetRoom();
    show('setup');
  } finally {
    resuming = false;
  }
}

/* The question sets come from the server, so adding one needs no change here. */
async function loadSets() {
  try {
    const response = await fetch('/sets');
    if (!response.ok) throw new Error(`/sets answered ${response.status}`);
    state.sets = await response.json();
  } catch (error) {
    state.sets = [];
    state.error = error;
    console.error('could not load the question sets', error);
  }
  render();
}

const startRound = () => hostAction('StartRound');
const closeQuestion = () => hostAction('CloseQuestion');
const advance = () => hostAction('AdvanceQuestion');
/* The room is the one that decides: this only asks, and the Paused that comes back is what moves
 * the console. A press that does not fit the phase is ignored by the hub, like every other. */
const togglePause = () => hostAction(state.paused ? 'ResumeRound' : 'PauseRound');

el('form-open').addEventListener('submit', async (event) => {
  event.preventDefault();
  const form = event.target;
  const code = form.elements.pairCode.value.trim();
  const hostKey = form.elements.hostKey.value;
  const setId = form.elements.setId.value;
  form.elements.hostKey.blur();
  try {
    state.error = '';
    state.hostKey = hostKey;
    // No code typed and no screen in hand is left to the server to answer: it says "no-screen",
    // which is the plainest way this page can put it.
    if (code) {
      await claimScreen(code);
      // The code is spent the moment the claim succeeds. Leaving it in the field would send a retry
      // after a failed CreateRoom back to ClaimScreen with a dead code; state.claimed carries the claim.
      form.elements.pairCode.value = '';
    }
    await createRoom(hostKey, setId);
  } catch (error) {
    state.error = error;
    render();
  }
});

el('start').addEventListener('click', startRound);
/* Moving on sooner by hand must not leave the automatic advance to fire into the next question. */
el('next').addEventListener('click', () => {
  cancelNextUp();
  advance();
});

el('pause').addEventListener('click', togglePause);

/* ---------- putting a television that reloaded back into the room ---------- */

el('repair-open').addEventListener('click', () => {
  const form = el('form-repair');
  form.hidden = !form.hidden;
  state.repair = null;
  drawRepair();
  if (!form.hidden) form.elements.pairCode.focus();
});

el('form-repair').addEventListener('submit', async (event) => {
  event.preventDefault();
  const form = event.target;
  try {
    await claimScreen(form.elements.pairCode.value.trim());
    form.elements.pairCode.value = '';
    form.hidden = true;
    state.repair = { done: true };
  } catch (error) {
    state.repair = { error };
  }
  drawRepair();
});

/*
 * Closing the room ends the game for everyone in it, so it takes two presses: the first arms the
 * button and says so, the second does it. A dialog would be one more thing to hit on a phone and
 * block nothing usefully; this says the same thing in the room's own language. The arming lapses on
 * its own, and on any change of phase, so a button left armed is not waiting to end the next round.
 */
const CLOSE_ARMED_MS = 5000;
let closeArmed = 0;

function disarmClose() {
  clearTimeout(closeArmed);
  closeArmed = 0;
  drawControls();
}

el('close-room').addEventListener('click', () => {
  if (closeArmed) {
    disarmClose();
    hostAction('CloseRoom');
    return;
  }
  closeArmed = setTimeout(disarmClose, CLOSE_ARMED_MS);
  drawControls();
});

/* ---------- rendering ---------- */

function show(view) {
  // Every phase change comes through here, so this is where a timer armed for the phase being left
  // dies. Callers that want one for the phase they are entering arm it after calling show().
  cancelPacing();
  clearTimeout(closeArmed);
  closeArmed = 0;
  el('form-repair').hidden = true;
  state.repair = null;
  stopCountdown();
  state.view = view;
  render();
}

/* The re-pair line is a confirmation or an error; it keeps what happened so it can be redrawn in
 * the language now chosen. */
function drawRepair() {
  const status = el('repair-status');
  const { done, error } = state.repair ?? {};
  set(status, done ? t('rePairDone') : error ? errorText(error) : '');
  status.classList.toggle('error', Boolean(error));
}

function render() {
  const running = RUN_VIEWS.includes(state.view);
  el('view-setup').hidden = state.view !== 'setup';
  el('view-run').hidden = !running;

  set(el('tag-room'), state.code && running ? t('roomTag', state.code) : '');
  set(el('tag-connection'), state.connection);
  document.body.classList.toggle('paused', state.paused);

  drawRepair();
  if (state.view === 'setup') drawSetup();
  if (running) {
    drawRun();
    drawControls();
  }
}

/* state.error holds what went wrong, not its words, so a language switch can say it again. */
function errorOf(error) {
  return error ? errorText(error) : '';
}

function set(node, text) {
  node.textContent = text;
  node.hidden = text === '';
}

function drawSetup() {
  const select = el('set-id');
  if (select.options.length !== state.sets.length) {
    select.replaceChildren();
    for (const item of state.sets) select.add(new Option(item.title, item.id));
    if (state.sets.some((item) => item.id === state.setId)) select.value = state.setId;
  }
  // Nothing can be opened before the sets are known, so do not pretend otherwise.
  el('form-open').querySelector('button').disabled = state.sets.length === 0;

  // The television's own address, so the host knows what to open over there before anything else.
  el('tv-hint').textContent = t('tvHint', `${location.host}/tv`);
  // A console that still holds a screen says so, and then the code field is the way to swap screens
  // rather than the only way forward.
  el('setup-paired').hidden = !state.claimed;

  // Only ever prefill an untouched field: a redraw must not overwrite what is being typed.
  const field = el('form-open').elements.hostKey;
  if (field.value === '' && document.activeElement !== field) field.value = state.hostKey ?? '';
  el('setup-error').textContent = errorOf(state.error);
}

/*
 * What the console says while a room is open. Every line here has to help the host act: the phase,
 * because the buttons mean different things in each one; the clock, because the host is the one who
 * closes a question; how many have answered, because that is what says whether waiting is worth it;
 * and which question of how many, because that is what says whether the evening is nearly over.
 * The question and its options are deliberately absent — they are on the television.
 */
function drawRun() {
  const phases = { lobby: 'phaseLobby', question: 'phaseQuestion', reveal: 'phaseReveal', finished: 'finishedHeading' };
  el('phase').textContent = t(phases[state.view] ?? 'phaseLobby');

  el('tag-clock').hidden = state.view !== 'question';
  set(el('tag-question'), (state.view === 'question' || state.view === 'reveal') && state.question
    ? t('questionCounter', state.question.index + 1, state.question.total)
    : '');

  if (state.view === 'question') set(el('tag-answered'), t('answeredCounter', state.answered, state.seated));
  else if (state.view === 'reveal') set(el('tag-answered'), t('closedTag'));
  else set(el('tag-answered'), '');

  set(el('phase-detail'), detail());
  set(el('run-error'), errorOf(state.error));
}

function detail() {
  if (state.paused) return t('pausedBanner');
  if (state.view === 'lobby') {
    return state.players.length === 0 ? t('waitingForPlayers') : t('playerCount', state.players.length);
  }
  if (state.view === 'reveal') return t('revealHint');
  if (state.view === 'finished') return t('newRoomHint');
  return '';
}

/* The buttons. Only the ones that do something in this phase are up, so there is nothing to aim
 * past: a phone is held in one hand and pressed with one thumb. */
function drawControls() {
  const start = el('start');
  // The server refuses to start an empty room, so the button only appears once someone is in.
  start.hidden = state.view !== 'lobby' || state.players.length === 0;
  start.textContent = t('startButton');

  const next = el('next');
  next.hidden = state.view !== 'reveal';
  const last = state.question && state.question.index + 1 >= state.question.total;
  next.textContent = t(last ? 'resultsButton' : 'nextButton');

  const pause = el('pause');
  pause.hidden = state.view !== 'question';
  pause.textContent = t(state.paused ? 'resumeButton' : 'pauseButton');

  const close = el('close-room');
  close.textContent = t(closeArmed ? 'closeRoomConfirm' : 'closeRoomButton');
  close.dataset.armed = String(Boolean(closeArmed));
}

/* ---------- start ---------- */

mountLangSwitch(el('langs'));
onLangChange(() => {
  document.title = t('hostTitle');
  applyStatic();
  render();
});

const stored = recall();
state.hostKey = stored.hostKey;
state.setId = stored.setId;
state.code = stored.code;
// A console that is about to resume shows nothing rather than a form it is going to replace.
show(stored.hostKey && stored.code ? 'resuming' : 'setup');

try {
  await conn.start();
} catch (error) {
  // Nothing works without the hub, so say so on screen and leave the reason in the console.
  state.connection = t('disconnected');
  show('setup');
  console.error('could not reach the hub', error);
}

await loadSets();

if (conn.state === signalR.HubConnectionState.Connected) {
  await resume();
}
if (state.view === 'resuming') show('setup');
