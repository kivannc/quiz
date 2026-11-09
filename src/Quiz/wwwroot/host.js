/*
 * The TV screen. It opens a room, shows the code, starts the round, owns the countdown
 * and walks the room through question -> reveal -> finished. Players never advance anything.
 *
 * There are no timers on the server: when this page's countdown reaches zero it calls
 * CloseQuestion itself. The host key is typed once and kept in sessionStorage, so a reload
 * re-sends it, asks the hub for the room as it stands now, and paints itself back.
 */

import { t, applyStatic, mountLangSwitch, onLangChange, errorText } from './i18n.js';

/* The order the four markers are drawn in. Glyph and colour come from app.css. */
const MARKERS = ['hearts', 'diamonds', 'clubs', 'spades'];

/* Everyone has answered: the beat the room gets to see the last tap land before the reveal. */
const EVERYONE_IN_MS = 3000;
/* How long the reveal stays up — the bars and the standings — before the round moves on by itself. */
const REVEAL_MS = 6000;

const STORE_KEY = 'quiz.hostKey';
const STORE_CODE = 'quiz.code';
const STORE_SET = 'quiz.setId';
const STORE_DEADLINE = 'quiz.deadline';

const el = (id) => document.getElementById(id);

const views = {
  setup: el('view-setup'),
  lobby: el('view-lobby'),
  question: el('view-question'),
  reveal: el('view-reveal'),
  finished: el('view-finished'),
};

const state = {
  // 'resuming' paints nothing: it is the moment between asking the hub for the room and hearing back.
  view: 'setup',
  code: null,
  hostKey: null,
  setId: null,
  sets: [],         // { id, title, language } from GET /sets; their titles are content, not UI strings
  players: [],      // [{ name, score }], from RoomState
  answered: 0,      // AnswerCount, host screens only
  seated: 0,        // how many players that count is out of
  question: null,   // the QuestionStarted payload
  paused: false,    // the room's clock is stopped, which the server knows too
  reveal: null,     // the Revealed payload
  standings: [],    // from RoundFinished
  error: '',
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
 * it. Rather than keep a stale one, drop it: a screen reloading into a paused question is then told
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
 * limit, so the extra time would be a lie. A screen that never ran this question has nothing stored
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
 * is what does it. This screen has no other way to close a question by hand.
 *
 * A running ticker is what tells a frozen clock from one that was never frozen, since zero left and
 * never frozen both read as `frozen === 0`: freezing always stops the ticker, so a screen with one
 * still running has nothing to thaw. Without that, a stray resume — a second screen on the same
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
  el('tag-clock').textContent = t('clock', left);
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
  // Warnings and worse only: the client chats at Information level, and a living-room
  // screen has no console to read it in.
  .configureLogging(signalR.LogLevel.Warning)
  .build();

conn.on('RoomState', (players, phase) => {
  state.players = players ?? [];
  // The question and reveal payloads own those two views; RoomState only refreshes the roster.
  if (phase === 'lobby') {
    show('lobby');
  } else if (phase === 'finished') {
    // A page that resumed a finished room never saw RoundFinished, so order the roster itself.
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
  // the question still closes at zero. Any other count leaves the beat unarmed, which in practice
  // means the 0-of-n a question opens with: nobody can join a round in progress and raise the total.
  if (everyoneIsIn()) armEveryoneIn();
  else cancelEveryoneIn();
});

conn.on('QuestionStarted', (question) => {
  state.question = question;
  // A resume during the reveal sends the question with no time on it, only so this screen knows what
  // was asked — which question of how many, and what the winning option says. Nothing is open, so
  // nothing is painted here and no countdown runs: the Revealed that follows does the painting.
  if (question.limitSeconds <= 0) return;

  state.reveal = null;
  state.answered = 0;
  state.paused = false;
  show('question');
  // A screen that has run this question already counts its own deadline down. One arriving fresh
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
  // The room is gone for everyone, including this screen: back to the form, ready to open another.
  stopCountdown();
  cancelPacing();
  forgetDeadline();
  forgetRoom();
  state.question = null;
  state.reveal = null;
  state.standings = [];
  state.paused = false;
  state.error = '';
  show('setup');
});

conn.on('Revealed', (reveal) => {
  stopCountdown();
  // The room drops its pause when the question closes, and so does the screen.
  state.paused = false;
  state.reveal = reveal;
  show('reveal');
  // Armed after show(), which is what cleared the question's own timer. A screen that reloads during
  // a reveal is sent this again and arms it too, so a refresh at the wrong moment does not stall the
  // round. The last question's reveal advances the same way: that is what ends the round.
  armNextUp();
});

conn.on('RoundFinished', (standings) => {
  stopCountdown();
  state.paused = false;
  // The round is over: the standings stay up until the host asks for a new room. Nothing is armed here.
  state.standings = standings ?? [];
  show('finished');
});

conn.onreconnecting(() => {
  // An invoke now would only fail. Resuming re-sends AnswerCount or Revealed, which arms it again.
  cancelPacing();
  state.connection = t('reconnecting');
  render();
});

conn.onreconnected(() => {
  // A reconnected connection is in no groups, and this one may have missed what happened while it
  // was away, so it asks for the room again. Resuming changes nothing in the room.
  state.connection = '';
  render();
  resume();
});

conn.onclose(() => {
  stopCountdown();
  cancelPacing();
  state.connection = t('disconnected');
  render();
});

/* ---------- actions ---------- */

async function createRoom(hostKey, setId) {
  state.code = await conn.invoke('CreateRoom', hostKey, setId);
  state.hostKey = hostKey;
  state.setId = setId;
  state.players = [];
  state.question = null;
  state.reveal = null;
  state.standings = [];
  remember();
  show('lobby');
}

/* Host actions are quietly ignored by the server when they do not fit the phase, so a
 * double click needs no guard here. A room that has gone (expired, or the server restarted)
 * is the one failure worth acting on: forget it and ask for the key again. */
async function hostAction(method) {
  try {
    await conn.invoke(method, state.code, state.hostKey);
    state.error = '';
    render();
  } catch (error) {
    state.error = errorText(error);
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
 * host groups) and asks the hub to send this screen the room as it stands. ResumeHost is read-only,
 * so a screen coming back cannot start, close or advance anything.
 */
let resuming = false;

/*
 * Load-bearing ordering: the hub's sends for this resume reach the client before the invoke's own
 * completion, so every QuestionStarted that belongs to a resume is handled while this flag is still
 * true. Do not move the reset, or awaken it after other awaits — a resumed screen would then throw
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
    state.error = errorText(error);
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
    state.error = t('errGeneric');
    console.error('could not load the question sets', error);
  }
  render();
}

const startRound = () => hostAction('StartRound');
const closeQuestion = () => hostAction('CloseQuestion');
const advance = () => hostAction('AdvanceQuestion');
/* The room is the one that decides: this only asks, and the Paused that comes back is what moves
 * the screen. A press that does not fit the phase is ignored by the hub, like every other. */
const togglePause = () => hostAction(state.paused ? 'ResumeRound' : 'PauseRound');

el('form-open').addEventListener('submit', async (event) => {
  event.preventDefault();
  const form = event.target;
  const hostKey = form.elements.hostKey.value;
  const setId = form.elements.setId.value;
  form.elements.hostKey.blur();
  try {
    state.error = '';
    await createRoom(hostKey, setId);
  } catch (error) {
    state.error = errorText(error);
    render();
  }
});

el('start').addEventListener('click', startRound);
/* Moving on sooner by hand must not leave the automatic advance to fire into the next question. */
el('next').addEventListener('click', () => {
  cancelNextUp();
  advance();
});

/* A new room reuses the key and set already in hand, so the host types nothing again. */
async function newRoom() {
  try {
    state.error = '';
    await createRoom(state.hostKey, state.setId);
  } catch (error) {
    state.error = errorText(error);
    forgetRoom();
    show('setup');
  }
}

el('new-room').addEventListener('click', newRoom);
el('pause').addEventListener('click', togglePause);

/*
 * Closing the room ends the game for everyone in it, so it takes two presses: the first arms the
 * button and says so, the second does it. A dialog would read badly on a TV and block nothing
 * usefully; this says the same thing in the room's own language. The arming lapses on its own, and
 * on any change of phase, so a button left armed is not waiting to end the next round.
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
  state.view = view;
  render();
}

function render() {
  for (const [name, section] of Object.entries(views)) section.hidden = name !== state.view;

  drawTopbar();
  drawControls();
  if (state.view === 'setup') drawSetup();
  if (state.view === 'lobby') drawLobby();
  if (state.view === 'question') drawQuestion();
  if (state.view === 'reveal') drawReveal();
  if (state.view === 'finished') drawBoard(el('final-board'), state.standings);
}

/* The two controls that belong to the host alone. They are on this page, which the key protects. */
function drawControls() {
  const open = state.code !== null && state.view !== 'setup' && state.view !== 'resuming';
  el('controls').hidden = !open;

  const pause = el('pause');
  pause.hidden = state.view !== 'question';
  pause.textContent = t(state.paused ? 'resumeButton' : 'pauseButton');

  const close = el('close-room');
  close.textContent = t(closeArmed ? 'closeRoomConfirm' : 'closeRoomButton');
  close.dataset.armed = String(Boolean(closeArmed));
}

function drawTopbar() {
  const playing = state.view === 'question' || state.view === 'reveal';

  set(el('tag-room'), state.code && state.view !== 'setup' ? t('roomTag', state.code) : '');
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

function drawSetup() {
  const select = el('set-id');
  if (select.options.length !== state.sets.length) {
    select.replaceChildren();
    for (const item of state.sets) select.add(new Option(item.title, item.id));
    if (state.sets.some((item) => item.id === state.setId)) select.value = state.setId;
  }
  // Nothing can be opened before the sets are known, so do not pretend otherwise.
  el('form-open').querySelector('button').disabled = state.sets.length === 0;

  // Only ever prefill an untouched field: a redraw must not overwrite what is being typed.
  const field = el('form-open').elements.hostKey;
  if (field.value === '' && document.activeElement !== field) field.value = state.hostKey ?? '';
  el('setup-error').textContent = state.error;
}

function drawLobby() {
  el('lobby-code').textContent = state.code ?? '';
  el('lobby-where').textContent = t('joinAt', location.host);

  const pills = el('lobby-players');
  pills.replaceChildren(...state.players.map((player) => {
    const pill = document.createElement('span');
    pill.className = 'pill';
    pill.textContent = player.name;
    return pill;
  }));

  // The server refuses to start an empty room, so the button only appears once someone is in.
  const empty = state.players.length === 0;
  el('start').hidden = empty;
  const status = el('lobby-status');
  set(status, empty ? t('waitingForPlayers') : '');
  if (state.error) set(status, state.error);
  status.classList.toggle('error', Boolean(state.error));
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

  const last = state.question && state.question.index + 1 >= state.question.total;
  el('next').textContent = last ? t('resultsButton') : t('nextButton');
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
// A screen that is about to resume shows nothing rather than a form it is going to replace.
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
