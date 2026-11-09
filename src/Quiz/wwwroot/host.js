/*
 * The TV screen. It opens a room, shows the code, starts the round, owns the countdown
 * and walks the room through question -> reveal -> finished. Players never advance anything.
 *
 * There are no timers on the server: when this page's countdown reaches zero it calls
 * CloseQuestion itself. The host key is typed once and kept in sessionStorage, so a reload
 * can re-send it and take the room back.
 */

import { t, applyStatic, mountLangSwitch, onLangChange, errorText } from './i18n.js';

/* The order the four markers are drawn in. Glyph and colour come from app.css. */
const MARKERS = ['hearts', 'diamonds', 'clubs', 'spades'];

/*
 * The hub has no "list the question sets" call, so the three shipped sets are named here
 * by id. Their titles are content, like the questions themselves, and are not translated.
 */
const SETS = [
  { id: 'genel-kultur', title: 'Genel Kültür' },
  { id: 'tarih', title: 'Tarih' },
  { id: 'muzik-ve-film', title: 'Müzik ve Film' },
];

const STORE_KEY = 'quiz.hostKey';
const STORE_CODE = 'quiz.code';
const STORE_SET = 'quiz.setId';

const el = (id) => document.getElementById(id);

const views = {
  setup: el('view-setup'),
  resume: el('view-resume'),
  lobby: el('view-lobby'),
  question: el('view-question'),
  reveal: el('view-reveal'),
  finished: el('view-finished'),
};

const state = {
  view: 'setup',
  code: null,
  hostKey: null,
  setId: SETS[0].id,
  players: [],      // [{ name, score }], from RoomState
  answered: 0,      // AnswerCount, host screens only
  seated: 0,        // how many players that count is out of
  question: null,   // the QuestionStarted payload
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
    sessionStorage.setItem(STORE_SET, state.setId);
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
      setId: sessionStorage.getItem(STORE_SET) || SETS[0].id,
    };
  } catch {
    return { hostKey: null, code: null, setId: SETS[0].id };
  }
}

/* ---------- the countdown, which belongs to this page ---------- */

let deadline = 0;
let ticker = 0;

function stopCountdown() {
  clearInterval(ticker);
  ticker = 0;
  deadline = 0;
}

function startCountdown(seconds) {
  stopCountdown();
  deadline = Date.now() + seconds * 1000;
  drawClock();
  ticker = setInterval(() => {
    drawClock();
    if (Date.now() >= deadline) {
      stopCountdown();
      closeQuestion();
    }
  }, 200);
}

function drawClock() {
  const left = Math.max(0, Math.ceil((deadline - Date.now()) / 1000));
  el('tag-clock').textContent = t('clock', left);
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
});

conn.on('QuestionStarted', (question) => {
  state.question = question;
  state.reveal = null;
  state.answered = 0;
  show('question');
  startCountdown(question.limitSeconds);
});

conn.on('Revealed', (reveal) => {
  stopCountdown();
  state.reveal = reveal;
  show('reveal');
});

conn.on('RoundFinished', (standings) => {
  stopCountdown();
  state.standings = standings ?? [];
  show('finished');
});

conn.onreconnecting(() => {
  state.connection = t('reconnecting');
  render();
});

conn.onreconnected(() => {
  // The reconnected connection is in no groups until the next host action re-sends the key;
  // the countdown kept running here, so the next CloseQuestion or button press repairs it.
  state.connection = '';
  render();
});

conn.onclose(() => {
  stopCountdown();
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

const startRound = () => hostAction('StartRound');
const closeQuestion = () => hostAction('CloseQuestion');
const advance = () => hostAction('AdvanceQuestion');

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
el('next').addEventListener('click', advance);
el('resume-start').addEventListener('click', startRound);
el('resume-close').addEventListener('click', closeQuestion);
el('resume-next').addEventListener('click', advance);

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
el('resume-new').addEventListener('click', newRoom);

/* ---------- rendering ---------- */

function show(view) {
  state.view = view;
  render();
}

function render() {
  for (const [name, section] of Object.entries(views)) section.hidden = name !== state.view;

  drawTopbar();
  if (state.view === 'setup') drawSetup();
  if (state.view === 'resume') drawResume();
  if (state.view === 'lobby') drawLobby();
  if (state.view === 'question') drawQuestion();
  if (state.view === 'reveal') drawReveal();
  if (state.view === 'finished') drawBoard(el('final-board'), state.standings);
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
  set(el('tag-connection'), state.connection);
}

function set(node, text) {
  node.textContent = text;
  node.hidden = text === '';
}

function drawSetup() {
  const select = el('set-id');
  if (!select.options.length) {
    for (const { id, title } of SETS) select.add(new Option(title, id));
    select.value = state.setId;
  }
  // Only ever prefill an untouched field: a redraw must not overwrite what is being typed.
  const field = el('form-open').elements.hostKey;
  if (field.value === '' && document.activeElement !== field) field.value = state.hostKey ?? '';
  el('setup-error').textContent = state.error;
}

function drawResume() {
  el('resume-code').textContent = t('roomTag', state.code ?? '');
  el('resume-error').textContent = state.error;
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
state.setId = SETS.some((s) => s.id === stored.setId) ? stored.setId : SETS[0].id;
state.code = stored.code;
show(stored.hostKey && stored.code ? 'resume' : 'setup');

try {
  await conn.start();
} catch (error) {
  // Nothing works without the hub, so say so on screen and leave the reason in the console.
  state.connection = t('disconnected');
  render();
  console.error('could not reach the hub', error);
}
