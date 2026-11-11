/*
 * The host's console: the phone that opens a room and holds its buttons. The television (tv.html) is
 * the display and paints the game; this page paints none of it, because the host is looking at the
 * television like everyone else.
 *
 * It never speaks to the television directly. A room is opened on a screen this console has claimed
 * with the four-digit code that screen is showing, and from then on the hub sends the two of them
 * the same room messages.
 *
 * What it does not do is pace the round. It used to: the countdown that closed a question and the
 * beats between questions ran here, and a browser stops the timers of a page nobody is looking at —
 * so a host who locked the phone, or opened another tab to play, stopped the round for the whole
 * room. The television carries the round now (see tv.js). This page shows where the round stands
 * and offers what only a person can decide: start, pause, move on sooner, another round, close.
 * Its clock is a picture of the television's, and nothing happens here when it reaches zero.
 *
 * The host key is typed once and remembered by the phone, so a console that was away re-sends it,
 * asks the hub for the room as it stands, and comes back to its controls.
 *
 * "Displayed" is whether the room still has a television in it. The round does not move by itself
 * without one — there is nothing to move it — and this page says so and offers the way back, since
 * the host is the one holding the phone that can claim a screen.
 */

import { t, applyStatic, mountLangSwitch, onLangChange, errorText } from './i18n.js';

/* Under this many seconds left, the clock on the console says so without being read. */
const CLOCK_LOW_S = 5;

const STORE_KEY = 'quiz.hostKey';
const STORE_CODE = 'quiz.code';
const STORE_SET = 'quiz.setId';
const STORE_DEADLINE = 'quiz.deadline';

const el = (id) => document.getElementById(id);

/*
 * Two screens on this page: the form that claims a television and opens a room, and the console
 * proper. The room's phase is state.view, as it is on the television — but all four phases are
 * drawn in the one section, since what changes between them here is a line of text and which
 * button is up.
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
  display: true,    // the room still has a television in it; the server is what knows
  error: '',
  repair: null,
  connection: '',   // reconnecting / disconnected notice
};

/* ---------- what this phone remembers ---------- */

/*
 * Kept for the phone rather than for the tab. Scanning the television's QR code opens a new tab,
 * and that tab has to be the same console: it must find the room it is running, so that a
 * television which reloaded is put back by the scan alone, and it must not ask for the key again
 * every evening. The key is therefore at rest on the host's own phone — the one place it was already
 * being typed — and nowhere else.
 */
const store = {
  getItem(key) { return localStorage.getItem(key); },
  setItem(key, value) { localStorage.setItem(key, value); },
  removeItem(key) { localStorage.removeItem(key); },
};

function remember() {
  try {
    store.setItem(STORE_KEY, state.hostKey ?? '');
    store.setItem(STORE_CODE, state.code ?? '');
    store.setItem(STORE_SET, state.setId ?? '');
  } catch {
    // Without storage a reload simply cannot resume; everything else still works.
  }
}

/*
 * Forgets the room this page was running. What the phone remembers is shared by every console tab on
 * it, so the stored code is only dropped if it is still this page's room: an old tab waking up to
 * find its long-dead room gone must not take away the code of the room another tab opened since.
 */
function forgetRoom() {
  const gone = state.code;
  state.code = null;
  try {
    if (store.getItem(STORE_CODE) === gone) store.removeItem(STORE_CODE);
  } catch { /* nothing to forget */ }
}

function recall() {
  try {
    return {
      hostKey: store.getItem(STORE_KEY) || null,
      code: store.getItem(STORE_CODE) || null,
      setId: store.getItem(STORE_SET) || null,
    };
  } catch {
    return { hostKey: null, code: null, setId: null };
  }
}

function rememberDeadline(question, at) {
  try {
    store.setItem(STORE_DEADLINE, JSON.stringify({ code: state.code, round: question.round, index: question.index, at }));
  } catch { /* the countdown still runs; only a reload would have used this */ }
}

/*
 * A remembered deadline is a promise about wall-clock time, and a paused room has stopped spending
 * it. Rather than keep a stale one, drop it: a console reloading into a paused question is then told
 * what is left by the room itself, which is the number that stood still.
 */
function forgetDeadline() {
  try {
    store.removeItem(STORE_DEADLINE);
  } catch { /* nothing to forget */ }
}

/*
 * The deadline this page set for this very question, if it is the page that set it. A reload must
 * not hand the players a fresh twenty seconds: the room goes on refusing answers after the original
 * limit, so the extra time would be a lie. A console that never ran this question has nothing stored
 * and falls back to the full limit. "This very question" is the round and the index together: a
 * room plays several rounds, and each counts its questions from zero.
 */
function recallDeadline(question) {
  try {
    const stored = JSON.parse(store.getItem(STORE_DEADLINE) ?? 'null');
    return stored && stored.code === state.code && stored.round === question.round && stored.index === question.index
      ? stored.at
      : null;
  } catch {
    return null;
  }
}

/* ---------- the clock, which is a picture of the television's ---------- */

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

/*
 * Counts the room's own deadline down and stops at zero. Nothing happens at zero: the television is
 * what closes a question, and this page only lets the host see how long is left without looking up.
 */
function startCountdown(question, at) {
  stopCountdown();
  deadline = at;
  rememberDeadline(question, at);
  drawClock();
  ticker = setInterval(() => {
    drawClock();
    if (Date.now() >= deadline) {
      clearInterval(ticker);
      ticker = 0;
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

/* Starts it again with exactly what was left, never with the whole limit. */
function thawCountdown() {
  if (!state.question || ticker) return;
  const left = frozen;
  frozen = 0;
  if (left <= 0) {
    drawClock(0);
    return;
  }
  startCountdown(state.question, Date.now() + left);
}

function drawClock(ms = deadline - Date.now()) {
  const left = Math.max(0, Math.ceil(ms / 1000));
  const clock = el('tag-clock');
  clock.textContent = t('clock', left);
  // The last few seconds, said in a colour rather than in a word: the host is watching the room.
  clock.classList.toggle('low', left <= CLOCK_LOW_S && !state.paused);
}

/* ---------- hub ---------- */

const conn = new signalR.HubConnectionBuilder()
  .withUrl('/hub')
  // Never gives up, like the television. The round goes on without this phone, but its buttons do
  // not: it must come back by itself, not by being reloaded.
  .withAutomaticReconnect({
    nextRetryDelayInMilliseconds: (context) => [0, 2_000, 5_000][context.previousRetryCount] ?? 10_000,
  })
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
  const at = (resuming ? recallDeadline(question) : null) ?? Date.now() + question.limitSeconds * 1000;
  startCountdown(question, at);
});

conn.on('Paused', (paused) => {
  state.paused = Boolean(paused);
  if (state.paused) freezeCountdown();
  else thawCountdown();
  render();
});

/*
 * Whether the room still has a television in it. The television is what carries the round, so
 * without one nothing moves by itself; the server pauses an open question when its screen goes, and
 * the Paused that follows is what stops the clock here. This page's part is to say so, and to put
 * the way back in the host's hand: the field for the code the television is showing.
 *
 * Coming back is not resuming. A paused room stays paused until the host presses the button,
 * because the host is the one who knows the room is looking at the television again.
 */
conn.on('Displayed', (present) => {
  state.display = Boolean(present);
  if (state.display) {
    // Back — by the form below, or by a scan that opened another tab. Either way the form is done.
    el('form-repair').hidden = true;
  } else {
    // "The screen is back in the room" is only cleared by a change of phase, so a television that
    // leaves again in the same question would otherwise leave that confirmation sitting under the
    // notice that contradicts it.
    state.repair = null;
  }
  render();
});

conn.on('RoundReset', () => {
  // Another round in the same room. The lobby that follows is what moves this page; this only
  // drops the last question of the round that is over, so nothing is drawn from it.
  state.question = null;
  state.paused = false;
});

conn.on('RoomClosed', () => {
  // The room is gone for everyone, the television included: back to the form, ready to claim the
  // code the television is showing again and open another room on it. That code is a new one, and
  // ending the room drops the claim on its screen server-side, so the claim this page was holding
  // is spent too: saying otherwise would be telling the host not to type the code they can see.
  state.claimed = false;
  stopCountdown();
  forgetDeadline();
  forgetRoom();
  state.question = null;
  state.paused = false;
  state.display = true;
  state.error = '';
  show('setup');
});

conn.on('Revealed', () => {
  stopCountdown();
  // The room drops its pause when the question closes, and so does the console.
  state.paused = false;
  // The television moves the round on from here; the button below does it sooner.
  show('reveal');
});

conn.on('RoundFinished', () => {
  stopCountdown();
  state.paused = false;
  // The standings are the television's to show.
  show('finished');
});

conn.onreconnecting(() => {
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
  state.claimed = false;
  state.connection = t('disconnected');
  render();
});

/*
 * A phone that was locked anyway comes back with its socket gone, and sometimes with the browser
 * having given up on it. Looking at the page again is the moment to try, so the host never has to
 * reload a console in the middle of a round.
 */
async function revive() {
  if (document.visibilityState !== 'visible' || conn.state !== signalR.HubConnectionState.Disconnected) return;
  try {
    await conn.start();
    state.connection = '';
    render();
    await resume();
  } catch {
    // Still unreachable. The notice stays, and the next look at the page tries again.
  }
}
document.addEventListener('visibilitychange', revive);
window.addEventListener('online', revive);

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
  // Opened on a screen that was claimed a moment ago, so the room has a television in it — set
  // before the invoke, because the hub says "Displayed false" during it when that screen has gone
  // in the meantime, and this would otherwise paint over the one thing the console must not lose.
  state.display = true;
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
    const message = String(error?.message ?? '');
    if (message.includes('no-such-room')) {
      // The room ended while this phone was away — closed, swept, or the server restarted. That is
      // how most evenings end, not something that went wrong: the form comes back with nothing to say.
      state.error = '';
      forgetRoom();
    } else if (message.includes('forbidden')) {
      // This key no longer drives it: ask for a room, and a key, again.
      state.error = error;
      forgetRoom();
    } else {
      // Anything else is the connection, not the room. The room is kept, so the next look at this
      // page, or a reload, takes it back instead of finding it forgotten.
      state.error = error;
    }
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
/* Moving on sooner than the television would. The television names the reveal it means when its own
 * beat fires, so one that fires after this press moves nothing. */
el('next').addEventListener('click', advance);

el('pause').addEventListener('click', togglePause);

/*
 * Another round, in the same room and on the same television, from whichever set is picked. The room
 * answers with its lobby, which is what moves this page; nothing is assumed here. The set is
 * remembered as the phone's choice, so the picker opens on it next time.
 */
el('again').addEventListener('click', async () => {
  const setId = el('again-set').value;
  try {
    await conn.invoke('NewRound', state.code, state.hostKey, setId);
    state.setId = setId;
    state.error = '';
    remember();
  } catch (error) {
    state.error = error;
    if (String(error?.message ?? '').includes('no-such-room')) {
      forgetRoom();
      show('setup');
      return;
    }
  }
  render();
});

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
  // Every phase change comes through here, so this is where what belonged to the phase being left
  // is dropped: an armed close button, an open re-pair form, the clock.
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
  // Its words are static, so applyStatic keeps them in the language chosen; this says when to look.
  el('display-gone').hidden = state.display;
  // And the way back is put in the host's hand with it: the notice asks for the code the
  // television is showing, so the field for that code opens rather than waiting to be found.
  if (!state.display) el('form-repair').hidden = false;
  set(el('run-error'), errorOf(state.error));
}

function detail() {
  if (state.paused) return t('pausedBanner');
  if (state.view === 'lobby') {
    return state.players.length === 0 ? t('waitingForPlayers') : t('playerCount', state.players.length);
  }
  // Nothing opens by itself while the room has no television in it, so do not promise that it will.
  if (state.view === 'reveal') return state.display ? t('revealHint') : '';
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

  // The round is over: the same room can play another, from any set. The picker is filled once,
  // and opens on the set that was just played so that "again" is one press.
  const finished = state.view === 'finished' && state.sets.length > 0;
  const picker = el('again-set');
  if (picker.options.length !== state.sets.length) {
    picker.replaceChildren();
    for (const item of state.sets) picker.add(new Option(item.title, item.id));
    if (state.sets.some((item) => item.id === state.setId)) picker.value = state.setId;
  }
  el('again-field').hidden = !finished;
  const again = el('again');
  again.hidden = !finished;
  again.textContent = t('againButton');

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

/*
 * The television's QR code opens this page with its screen code in the address. Read once and taken
 * out again: a screen code is spent the moment it is claimed, and a reload must not offer a dead one.
 */
const scanned = new URLSearchParams(location.search).get('pair');
if (scanned !== null) history.replaceState(null, '', location.pathname);
const pair = /^[0-9]{4}$/.test(scanned ?? '') ? scanned : null;

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

if (pair) {
  if (RUN_VIEWS.includes(state.view)) {
    // This console is running a room and has just been handed a screen code: a television that
    // reloaded and was scanned. Claiming it is all that is being asked, so it is done at once.
    try {
      await claimScreen(pair);
      state.repair = { done: true };
    } catch (error) {
      state.repair = { error };
      el('form-repair').elements.pairCode.value = pair;
      el('form-repair').hidden = false;
    }
    drawRepair();
  } else {
    // No room yet: the code goes into the form, and what is left is the key (once per phone) and
    // the question set.
    const form = el('form-open');
    form.elements.pairCode.value = pair;
    (form.elements.hostKey.value ? form.elements.setId : form.elements.hostKey).focus();
  }
}
