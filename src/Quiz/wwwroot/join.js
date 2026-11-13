/*
 * The phone. It joins a room with a code and a name, then shows four suit buzzers and
 * nothing else: no question text, no option text, no letters. The TV is the shared screen.
 *
 * The code and name are kept for the tab, so a reload or a dropped connection re-sends the
 * same name and lands back in the same seat with the same score.
 *
 * It is also where a room is opened. The television shows one code for the whole evening, and
 * whoever scans it lands here, so the page has to serve two people without asking which one it has:
 * a guest, who types a name and waits for the room if there is none yet, and the person holding the
 * key, who opens the room on that television from the same form. That person then takes a seat
 * like everyone else — the host used to need a second page, a console, and a host with a console
 * in hand cannot play — and gets a short row of buttons under the buzzers: start, pause, next,
 * another round, close. The television paces the round, so those buttons are all a host is for.
 */

import { t, applyStatic, mountLangSwitch, onLangChange, errorText } from './i18n.js';
import { keepAwake } from './awake.js';

const STORE_CODE = 'quiz.code';
const STORE_NAME = 'quiz.name';
const STORE_TAP = 'quiz.tap';
const STORE_OPEN = 'quiz.open';
/* The name last played under, kept across evenings so the form only asks for it once. */
const STORE_LAST_NAME = 'quiz.lastName';
/* The host key and the set last chosen. The names are the ones the old console page kept them
 * under, so a phone that typed the key there has not got to type it again here. */
const STORE_KEY = 'quiz.hostKey';
const STORE_SET = 'quiz.setId';
/* The room this phone opened and the name it sat down under, so that any tab on it — the one a
 * second scan of the television opens, say — is the host of that room again. */
const STORE_HOSTING = 'quiz.hosting';

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

  // Standing in front of a television that has no room on it yet.
  screen: null,     // the code that television was showing when this phone scanned or typed it
  waiting: null,    // the name to walk in under the moment a room opens there
  opening: false,   // the part of the form that opens the room is up
  claimed: false,   // that television has been claimed and the room is not open yet

  // Hosting: only ever true on the phone that opened the room.
  hostKey: null,
  setId: null,
  hosting: null,    // { code, name } of the room this phone opened, from storage
  sets: [],         // { id, title, language } from GET /sets, loaded when first needed
  total: null,      // how many questions the round has, to tell the last reveal from the others
  display: true,    // the room still has a television in it
  hostError: '',
};

/* This phone opened the room it is in, and still holds the key that drives it. */
function isHost() {
  return Boolean(state.code && state.hostKey && state.hosting && state.hosting.code === state.code);
}

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
    // The room spells a name back the way it first heard it; that spelling is the seat.
    if (isHost() && state.name && state.hosting.name !== state.name) rememberHosting(state.code, state.name);
  } catch { /* the form asks for the name again next time */ }
}

function recallHost() {
  try {
    const hosting = JSON.parse(localStorage.getItem(STORE_HOSTING) ?? 'null');
    return {
      hostKey: localStorage.getItem(STORE_KEY) || null,
      setId: localStorage.getItem(STORE_SET) || null,
      hosting: hosting && typeof hosting.code === 'string' && typeof hosting.name === 'string' ? hosting : null,
    };
  } catch {
    return { hostKey: null, setId: null, hosting: null };
  }
}

function rememberHosting(code, name) {
  state.hosting = { code, name };
  try {
    localStorage.setItem(STORE_KEY, state.hostKey ?? '');
    localStorage.setItem(STORE_SET, state.setId ?? '');
    localStorage.setItem(STORE_HOSTING, JSON.stringify(state.hosting));
  } catch { /* this tab is still the host for as long as it lives */ }
}

/* The room this phone opened is over, or was never its to drive. The key stays: it opens the next. */
function forgetHosting() {
  state.hosting = null;
  try {
    localStorage.removeItem(STORE_HOSTING);
  } catch { /* nothing to forget */ }
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

/*
 * How long the server's silence is believed. A phone's connection dies without a word all the time
 * — the screen locks, the radio sleeps, Wi-Fi hands over to mobile — and nothing closes: the page
 * goes on showing the last thing it was told while the room moves on. The server pings every five
 * seconds, so twelve of silence means the line is dead; the default was thirty, which is three
 * questions sat through in front of buzzers that never came.
 */
conn.serverTimeoutInMilliseconds = 12_000;
conn.keepAliveIntervalInMilliseconds = 5_000;

/* How long a call may go unanswered before the connection is taken for dead. */
const ANSWER_WITHIN_MS = 4_000;
/* The same for the calls that exist only to find that out, which have nothing to wait for. */
const PROBE_WITHIN_MS = 2_500;
/* How often a page with a part in a room asks whether its connection is alive. */
const HEARTBEAT_MS = 4_000;

class Unanswered extends Error {}

/** A hub call that is given up on: a dead connection answers nothing, and never says so. */
function ask(method, ...args) {
  return within(ANSWER_WITHIN_MS, method, ...args);
}

function within(ms, method, ...args) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Unanswered(method)), ms);
    conn.invoke(method, ...args).then(
      (value) => { clearTimeout(timer); resolve(value); },
      (error) => { clearTimeout(timer); reject(error); },
    );
  });
}

/* A failure of the line rather than an answer from the room. */
function lineFailed(error) {
  return error instanceof Unanswered || conn.state !== signalR.HubConnectionState.Connected;
}

/*
 * Throws the connection away and starts another: the cure for one that has gone silent. What comes
 * back is a new connection, so the page then takes its place back the way it does after any drop.
 */
let restarting = null;

function restart() {
  restarting ??= (async () => {
    try {
      await conn.stop();
    } catch { /* it was dead anyway */ }
    try {
      await conn.start();
      state.connection = '';
      render();
      if (arrived) await comeBack();
      else await arrive();
    } catch {
      // Unreachable for now. onclose has said so; the next look at the page, or the network
      // coming back, tries again.
    } finally {
      restarting = null;
    }
  })();
  return restarting;
}

/*
 * The moment to doubt the connection: the page has just come back into view, or the network has
 * just changed. A connection that looks open may have died while nobody was looking, and the
 * client would take up to twelve seconds to notice by itself — so the room is asked, and has a few
 * seconds to answer. The same question brings back what the room is doing now, which a page that
 * was frozen for a while has missed.
 */
let doubting = false;

async function doubt() {
  if (displaced || doubting || restarting || document.visibilityState !== 'visible') return;
  if (conn.state === signalR.HubConnectionState.Disconnected) {
    await revive();
    return;
  }
  if (conn.state !== signalR.HubConnectionState.Connected) return;
  // On the form with nothing at stake there is nothing to lose and nothing to ask about.
  const seated = state.phase !== 'join' && state.code;
  if (!seated && !state.screen) return;
  doubting = true;
  try {
    const here = await within(PROBE_WITHIN_MS, 'StillHere', state.code ?? '');
    // Alive, but the room does not have this connection in that seat: take it back by name.
    if (seated && !here) await rejoin();
    else if (seated) resend();
  } catch (error) {
    if (lineFailed(error)) await restart();
  } finally {
    doubting = false;
  }
}

/*
 * And between those moments, a heartbeat. With the phone in the hand and the page in front nothing
 * prompts a doubt, and the connection can still die — a router restarting, Wi-Fi handing over to
 * mobile without the browser saying "offline". The client's own patience is twelve seconds and it
 * checks it only now and then; asking outright every few seconds finds a dead line in six or seven.
 * Only while the page is in view and has something to lose: a seat, or a place in front of a television.
 */
setInterval(async () => {
  if (displaced || doubting || restarting || document.visibilityState !== 'visible') return;
  if (conn.state !== signalR.HubConnectionState.Connected) return;
  if (!(state.phase !== 'join' && state.code) && !state.screen) return;
  try {
    await within(PROBE_WITHIN_MS, 'Ping');
  } catch (error) {
    if (error instanceof Unanswered) restart();
  }
}, HEARTBEAT_MS);

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

conn.on('RoomClosed', leaveRoom);

function leaveRoom() {
  // Not an error and not a dead screen: the game ended, and this phone can join another room.
  forget();
  // Only the record of the room being left: this phone may be a guest here and the host of another.
  if (state.hosting?.code === state.code) forgetHosting();
  state.display = true;
  state.hostError = '';
  disarmClose();
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
}

/*
 * A room has opened on the television this phone is standing in front of. A guest who has already
 * given a name walks straight in; one who has not is handed the code, which is all that was missing.
 * The phone that opened the room is not sent this: it has the code as the answer to its own call.
 */
conn.on('RoomReady', (code) => {
  if (state.phase !== 'join') return;
  const name = state.waiting;
  leaveScreen();
  el('form-join').elements.code.value = code;
  if (!name) {
    render();
    return;
  }
  join(code, name).catch((error) => {
    state.error = errorText(error);
    render();
  });
});

/* What only a room's host screens are sent. The phone that opened the room is one of them; on any
 * other phone these never arrive. */
conn.on('RoomState', () => {});
conn.on('AnswerCount', () => {});
conn.on('Displayed', (present) => {
  // The television is what carries the round, so the host is told when it has gone: the round is
  // waiting, and scanning the code now on that screen with this phone is what puts it back.
  state.display = Boolean(present);
  render();
});

/*
 * The television this phone was waiting in front of has gone: reloaded, or a blink of its Wi-Fi. It
 * comes back as another connection showing another code, and a room opened there would never reach
 * a phone still waiting for the old one — so the phone stops waiting and says what to do.
 */
conn.on('ScreenGone', () => {
  if (!state.screen) return;
  leaveScreen();
  state.error = t('errScreenCodeGone');
  render();
});

/*
 * This seat is being played from somewhere else now: another tab on this phone, most often, opened
 * by scanning the television a second time. The room would refuse every tap from here, so this page
 * stops being a buzzer rather than looking like a broken one — and stops for good, connection and
 * all, because a page that reconnected later would take the seat back from the tab that is in use.
 *
 * It may also be somebody else entirely: a seat is found by its name, so a second Ali who joins takes
 * the first one's. The form is therefore left holding the room and the name, and says both ways out.
 * Entering them as they stand is a decision to play from this tab after all; changing the name is
 * sitting down as someone else. Either is carried out by a reload, which comes up as whatever the
 * form said — see the submit handler.
 */
let displaced = false;
// Whether this page has done what it came for yet; see arrive(), at the end.
let arrived = false;

conn.on('SeatTaken', () => {
  displaced = true;
  clearTimeout(retry);
  const form = el('form-join');
  form.elements.code.value = state.code ?? '';
  form.elements.name.value = state.name ?? '';
  state.phase = 'join';
  state.code = null;
  state.name = null;
  state.pending = null;
  state.chosen = null;
  state.final = null;
  state.paused = false;
  clearResult();
  leaveScreen();
  state.error = '';
  state.notice = t('seatTaken');
  conn.stop();
  render();
});

/*
 * What this seat has already answered, said by the room as part of handing the question back. The
 * phone may not know: a new tab remembers no tap, and a tap whose acknowledgement was lost with the
 * connection was taken all the same. The buzzers lock on it, as if the tap had just been confirmed.
 */
conn.on('Answered', (marker) => {
  if (state.phase !== 'question') return;
  state.chosen = marker;
  state.pending = null;
  wanted = null;
  state.error = '';
  rememberTap(state.index, marker);
  render();
});

conn.on('RoundReset', (players) => {
  // Not this page's round if it is on the form: an opener whose own seat was refused is among the
  // room's host screens without being in the room, and must not be dropped into its lobby.
  if (state.phase === 'join') return;
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
  state.total = question.total;
  // A host coming back during a reveal is sent the question with no time on it, for context: what
  // was asked, and whether it was the last. Nothing has opened, and the Revealed that follows owns
  // the screen — treating this as a question would take the total as it stands now for a "before".
  if (question.limitSeconds <= 0) return;
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
  // Which question this was, for a page that arrived during the reveal and never saw it open: the
  // host's button has to know whether what follows is another question or the standings.
  state.index = reveal.index;
  state.round = reveal.round;
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
  comeBack();
});

/* Back to wherever this phone was: its seat, or the television it was waiting in front of. */
async function comeBack() {
  // A claim belonged to the connection that is gone.
  state.claimed = false;
  if (state.code && state.name) {
    await rejoin();
  } else if (state.screen) {
    const name = state.waiting;
    const opening = state.opening;
    try {
      await enterScreen(state.screen);
      state.waiting = name;
      state.opening = opening;
    } catch (error) {
      // Most often the code it scanned has been replaced, and a new connection has never stood
      // there. Anything else is said as what it is.
      leaveScreen();
      state.error = screenError(error);
    }
    render();
  }
}

/* A screen code that is on no television any more gets its own words: "scan it again". */
function screenError(error) {
  return String(error?.message ?? '').includes('no-such-code') ? t('errScreenCodeGone') : errorText(error);
}

conn.onclose(() => {
  // Closed on purpose, by SeatTaken: the page has said why, and that is not "disconnected".
  if (displaced) return;
  state.connection = t('disconnected');
  render();
});

/*
 * A phone that was locked or put away comes back with its socket long gone, and sometimes with the
 * reconnecting given up on by the browser rather than by this page. Coming back to the page is the
 * moment to try again, so the player never has to know what a reload is.
 */
async function revive() {
  if (displaced) return;
  if (document.visibilityState !== 'visible' || conn.state !== signalR.HubConnectionState.Disconnected) return;
  try {
    await conn.start();
    state.connection = '';
    render();
    // A page whose very first connection failed has not arrived anywhere yet: what its address
    // carried is still waiting to be used.
    if (arrived) await comeBack();
    else await arrive();
  } catch {
    // Still unreachable. The notice stays, and the next time the page is looked at tries again.
  }
}
document.addEventListener('visibilitychange', doubt);
window.addEventListener('online', doubt);
// The network going is the surest sign of all that the connection needs looking at when it returns.
window.addEventListener('offline', () => { if (!displaced && conn.state === signalR.HubConnectionState.Connected) restart(); });

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
  // Not in that room and not on its way back to it: nothing is left to rejoin.
  state.code = null;
  state.name = null;
  forget();
  render();
}

/*
 * Re-sends the name this phone already has. A room that has gone sends it back to the form — and
 * only that does. Being told to slow down, or losing the connection in the middle of asking, says
 * nothing about the room: the seat and, on the host's phone, the record that makes it the host are
 * kept, and the asking is done again. Forgetting them here would leave a live room with nobody able
 * to start, pause or close it, since there is no way to become the host of a room one did not open.
 */
const REJOIN_RETRY_MS = 10_000;
/* Two minutes of asking. Each try is counted against the household's shared allowance, and a
 * refusal that lasts longer than that is not one that asking again will cure: the form is left
 * with the error, and a reload or a look at the page starts over. */
const REJOIN_RETRIES = 12;
let retry = 0;
let retried = 0;

async function rejoin() {
  clearTimeout(retry);
  retry = 0;
  if (displaced || !state.code || !state.name) return;
  try {
    await join(state.code, state.name);
    retried = 0;
  } catch (error) {
    const message = String(error?.message ?? '');
    if (message.includes('no-such-room') || message.includes('invalid-name')) {
      if (state.hosting?.code === state.code) forgetHosting();
      state.phase = 'join';
      state.code = null;
      state.name = null;
      state.chosen = null;
      state.error = errorText(error);
      forget();
      render();
      return;
    }
    state.error = errorText(error);
    render();
    // A connection that dropped has its own way back, which ends here again.
    if (conn.state === signalR.HubConnectionState.Connected && retried < REJOIN_RETRIES) {
      retried++;
      retry = setTimeout(rejoin, REJOIN_RETRY_MS);
    }
    return;
  }
  if (state.phase !== 'join' && isHost()) await resumeHost();
  resend();
}

/*
 * A tap that could not be sent because the line failed under it. It is the player's answer, and
 * the player has stopped looking at the phone: once the seat is back and the same question is
 * still open, it is sent again. If the room had taken it after all, the seat coming back said so
 * (Answered) and there is nothing left to send.
 */
let wanted = null;

function resend() {
  const tap = wanted;
  wanted = null;
  if (!tap || state.phase !== 'question' || state.paused || state.chosen || state.pending) return;
  if (tap.round !== state.round || tap.index !== state.index) return;
  answer(tap.marker);
}

/* ---------- at a television with no room on it yet ---------- */

/*
 * Stands this phone in front of the television showing `code`. The hub tells it the room the moment
 * one opens there. Throws what the hub throws: a code that is on no screen, or too many tries.
 */
async function enterScreen(code) {
  await conn.invoke('AwaitRoom', code);
  state.screen = code;
  state.waiting = null;
  state.claimed = false;
  // A phone that already holds the key is most likely the one that opens the room, so that part of
  // the form is up without being asked for. Everyone else gets a name field and nothing to puzzle over.
  state.opening = Boolean(state.hostKey);
  if (state.opening) loadSets();
}

function leaveScreen() {
  state.screen = null;
  state.waiting = null;
  state.opening = false;
  state.claimed = false;
}

/* The question sets come from the server, so adding one needs no change here. Asked for once, and
 * only by a phone that is about to open a room or is hosting one. */
let setsAsked = false;

async function loadSets() {
  if (setsAsked) return;
  setsAsked = true;
  try {
    const response = await fetch('/sets');
    if (!response.ok) throw new Error(`/sets answered ${response.status}`);
    state.sets = await response.json();
  } catch (error) {
    setsAsked = false;
    console.error('could not load the question sets', error);
  }
  render();
}

/*
 * Opens a room on the television this phone is standing in front of, and sits down in it. Three
 * calls on the one connection: claim the screen, open the room, take a seat. A claim that succeeded
 * is kept if the room then cannot be opened — a wrong set, a full server — because the code that
 * bought it is spent and the retry must not ask for it again.
 */
async function openRoom() {
  const form = el('form-join');
  const name = form.elements.name.value.trim();
  const hostKey = form.elements.hostKey.value;
  const setId = form.elements.setId.value;
  state.notice = '';
  if (!name) {
    state.error = t('errInvalidName');
    render();
    form.elements.name.focus();
    return;
  }
  if (!hostKey) {
    state.error = t('errKeyNeeded');
    render();
    form.elements.hostKey.focus();
    return;
  }

  // Return in a field and the button are two ways here, and one room is all that is wanted.
  if (openingNow) return;
  openingNow = true;
  const button = el('open-room');
  button.disabled = true;
  try {
    state.error = '';
    if (!state.claimed) {
      await conn.invoke('ClaimScreen', state.screen, hostKey);
      state.claimed = true;
    }
    // Opened on a screen claimed a moment ago, so the room has a television in it — said before the
    // call, because the hub says "Displayed false" during it when that screen has gone in the
    // meantime, and saying it afterwards would paint over the one thing the host must not miss.
    state.display = true;
    const code = await conn.invoke('CreateRoom', hostKey, setId);
    // Should taking the seat fail, the room is still open: the form is left holding its code.
    form.elements.code.value = code;
    state.hostKey = hostKey;
    state.setId = setId;
    state.hostError = '';
    leaveScreen();
    // Before the seat is taken, so that the Seated which answers the join already finds a host.
    state.code = code;
    rememberHosting(code, name);
    await join(code, name);
  } catch (error) {
    // The one refusal worth its own words: the television has moved on to another code.
    state.error = screenError(error);
    render();
  } finally {
    openingNow = false;
    button.disabled = false;
  }
}

let openingNow = false;

el('open-room').addEventListener('click', openRoom);

el('open-toggle').addEventListener('click', () => {
  state.opening = !state.opening;
  state.waiting = null;
  state.error = '';
  if (state.opening) loadSets();
  render();
  if (state.opening) el('form-join').elements[state.hostKey ? 'name' : 'hostKey'].focus();
});

/* ---------- the host's buttons ---------- */

/*
 * Takes the host's part of the room back after a reload or a dropped connection: re-sends the key,
 * which puts this connection among the room's host screens again, and is told whether the room
 * still has a television in it. The seat itself came back with the name; this is only the buttons.
 */
async function resumeHost() {
  try {
    await conn.invoke('ResumeHost', state.code, state.hostKey);
    state.hostError = '';
  } catch (error) {
    // The key no longer drives this room: the seat is kept, the buttons go.
    if (String(error?.message ?? '').includes('forbidden')) forgetHosting();
  }
  loadSets();
  render();
}

/* Host actions are quietly ignored by the server when they do not fit the phase, so a double press
 * needs no guard here. A room that has gone is the one failure worth acting on. */
async function hostAction(method, ...more) {
  try {
    try {
      await ask(method, state.code, state.hostKey, ...more);
    } catch (error) {
      if (!lineFailed(error)) throw error;
      // The press went nowhere: the line is dead. A new one is made and the press is sent once
      // more — which is safe even if the first one did arrive, because every host action names
      // what it wants (pause, not "toggle") and the room ignores one that no longer fits.
      await (error instanceof Unanswered ? restart() : (restarting ?? Promise.resolve()));
      if (!isHost()) return;
      await ask(method, state.code, state.hostKey, ...more);
    }
    state.hostError = '';
  } catch (error) {
    if (String(error?.message ?? '').includes('no-such-room')) {
      leaveRoom();
      return;
    }
    state.hostError = errorText(error);
  }
  render();
}

el('host-start').addEventListener('click', () => hostAction('StartRound'));
el('host-pause').addEventListener('click', () => hostAction(state.paused ? 'ResumeRound' : 'PauseRound'));
/* Sooner than the television would move on by itself. */
el('host-next').addEventListener('click', () => hostAction('AdvanceQuestion'));
el('host-again').addEventListener('click', async () => {
  const setId = el('host-again-set').value;
  await hostAction('NewRound', setId);
  if (!state.hostError && isHost()) {
    state.setId = setId;
    rememberHosting(state.code, state.hosting.name);
  }
});

/*
 * Closing the room ends the game for everyone in it, so it takes two presses: the first arms the
 * button and says so, the second does it. The arming lapses on its own.
 */
const CLOSE_ARMED_MS = 5000;
let closeArmed = 0;

function disarmClose() {
  clearTimeout(closeArmed);
  closeArmed = 0;
}

el('host-close').addEventListener('click', () => {
  if (closeArmed) {
    disarmClose();
    hostAction('CloseRoom');
    return;
  }
  closeArmed = setTimeout(() => {
    disarmClose();
    render();
  }, CLOSE_ARMED_MS);
  render();
});

/* A television that reloaded shows a code again, and scanning it with the host's phone opens this
 * page with that code: claiming it is all that is being asked, and puts the screen back in the room. */
async function reseat(screenCode) {
  try {
    await conn.invoke('ClaimScreen', screenCode, state.hostKey);
    state.hostError = '';
  } catch (error) {
    state.hostError = screenError(error);
  }
  render();
}

/* The same thing by hand, for a camera that opens another browser or a host who would rather type:
 * the field is up for exactly as long as the room has no television in it. */
el('host-repair').addEventListener('submit', async (event) => {
  event.preventDefault();
  const field = event.target.elements.screenCode;
  const code = field.value.trim();
  if (!/^[0-9]{4}$/.test(code)) {
    field.focus();
    return;
  }
  await reseat(code);
  if (!state.hostError) field.value = '';
});

// A join on its way: the button waits for the answer rather than taking a second press.
let joining = false;

/* A guest who is waiting for the room and changes the name is no longer waiting under the old one:
 * the button comes back, and pressing it waits again under what the field says now. */
el('form-join').elements.name.addEventListener('input', () => {
  if (!state.waiting) return;
  state.waiting = null;
  render();
});

el('form-join').addEventListener('submit', async (event) => {
  event.preventDefault();
  const form = event.target;
  const code = form.elements.code.value.trim();
  const name = form.elements.name.value.trim();

  // "required" is satisfied by a space; the room is not.
  if (!name) {
    state.error = t('errInvalidName');
    render();
    form.elements.name.focus();
    return;
  }
  // This tab lost its seat to another connection and closed its own. What the form says now is
  // what it comes back as: the same name takes the seat back, a different one sits down beside
  // whoever has it. The tab's memory is rewritten first, because that is what a reload reads —
  // left alone, it would bring back the old name whatever had been typed over it.
  if (displaced) {
    if (!/^[0-9]{4}$/.test(code)) {
      state.error = t('errCodeNeeded');
      render();
      return;
    }
    try {
      sessionStorage.setItem(STORE_CODE, code);
      sessionStorage.setItem(STORE_NAME, name);
      sessionStorage.removeItem(STORE_TAP);
      sessionStorage.removeItem(STORE_OPEN);
    } catch { /* the reload then asks for them again */ }
    location.reload();
    return;
  }
  // Whatever this page was still trying to get back to, the form now says otherwise.
  clearTimeout(retry);
  retry = 0;
  if (state.phase === 'join') {
    state.code = null;
    state.name = null;
  }

  if (state.screen) {
    // At a television with no room on it. Return in the name field means whichever button is up.
    if (state.opening) {
      openRoom();
      return;
    }
    // Nothing to join yet: the name is kept, and the room lets this phone in when it opens.
    state.waiting = name;
    state.error = '';
    state.notice = '';
    try {
      localStorage.setItem(STORE_LAST_NAME, name);
    } catch { /* asked for again next time */ }
    render();
    return;
  }

  if (!/^[0-9]{4}$/.test(code)) {
    state.error = t('errCodeNeeded');
    render();
    form.elements.code.focus();
    return;
  }

  joining = true;
  render();
  try {
    state.error = '';
    state.notice = '';
    await join(code, name);
  } catch (error) {
    state.error = errorText(error);
    // Four digits read off a television are a room code once a room is open and the television's
    // own code before that. Nobody typing them should have to know which: a code that is no room
    // is tried as a screen, and only if it is neither does the form say so.
    if (String(error?.message ?? '').includes('no-such-room')) {
      try {
        await enterScreen(code);
        state.error = '';
        if (!state.opening) state.waiting = name;
      } catch { /* neither: the first refusal stands */ }
    }
  } finally {
    joining = false;
    render();
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
  const asked = { round: state.round, index: state.index, marker };
  try {
    if (await ask('Answer', state.code, marker)) {
      state.chosen = marker;
      rememberTap(state.index, marker);
    } else {
      state.error = t('answerRefused');
    }
  } catch (error) {
    if (lineFailed(error)) {
      // Not refused: never heard. Kept, and sent again when the seat is back — which a silent
      // connection needs help with, and a dropped one is already doing.
      wanted = asked;
      if (error instanceof Unanswered) restart();
    } else {
      state.error = errorText(error);
    }
  } finally {
    // Only if nothing newer has taken this question over in the meantime.
    if (state.pending === marker && state.round === asked.round && state.index === asked.index) state.pending = null;
    render();
  }
}

for (const buzzer of el('buzzers').querySelectorAll('.buzz')) {
  buzzer.addEventListener('click', () => answer(buzzer.dataset.marker));
}

/* ---------- rendering ---------- */

function render() {
  // A page that lost its seat stays on the form whatever arrives in the moment before its
  // connection has finished closing: a question painted over that notice would be a dead buzzer.
  const view = state.phase === 'join' || displaced ? 'join' : 'play';
  for (const [name, section] of Object.entries(views)) section.hidden = name !== view;

  el('bar-me').textContent = state.name && view === 'play' ? state.name : '';
  el('bar-score').textContent = view === 'play' ? t('scoreOf', state.score) : '';
  el('join-error').textContent = view === 'join' ? state.error : '';
  if (view === 'join') drawJoin();
  drawHostBar(view === 'play' && isHost());

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

  // A tap waiting for the line to come back is still this player's tap: it stays lit, and the
  // other three stay locked, until it has been sent or the question has gone.
  const waiting = wanted && wanted.round === state.round && wanted.index === state.index ? wanted.marker : null;
  const picked = state.chosen ?? state.pending ?? waiting;
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
  // And while it waits in front of a television: a phone that locks there comes back to a code that
  // has been replaced, and its owner to a room everyone else is already in.
  keepAwake((view === 'play' && state.phase !== 'finished') || Boolean(state.waiting));
}

/*
 * The form, which is one of three things: a room code and a name; a name alone, in front of a
 * television that has no room on it yet; or that plus the key and a set, for the phone opening it.
 */
function drawJoin() {
  const form = el('form-join');
  const atScreen = Boolean(state.screen);
  const opening = atScreen && state.opening;

  const tag = el('at-screen');
  tag.hidden = !atScreen;
  tag.textContent = state.waiting ? t('waitingForRoom') : t('atScreen');
  tag.classList.toggle('waiting', Boolean(state.waiting));

  el('code-field').hidden = atScreen;
  el('open-panel').hidden = !opening;
  el('join-submit').hidden = opening;
  // A name has been given and the room has not opened, or a join is on its way: nothing to press.
  el('join-submit').disabled = Boolean(state.waiting) || joining;

  const toggle = el('open-toggle');
  toggle.hidden = !atScreen;
  toggle.textContent = t(opening ? 'openToggleBack' : 'openToggle');

  if (opening) {
    const select = el('set-id');
    fillSets(select);
    el('open-room').disabled = state.sets.length === 0;
    // Only ever prefill an untouched field: a redraw must not overwrite what is being typed.
    const key = form.elements.hostKey;
    if (key.value === '' && document.activeElement !== key) key.value = state.hostKey ?? '';
  }
}

/* Fills a set picker once, and opens it on the set this phone chose last. */
function fillSets(select) {
  if (select.options.length === state.sets.length) return;
  select.replaceChildren();
  for (const item of state.sets) select.add(new Option(item.title, item.id));
  if (state.sets.some((item) => item.id === state.setId)) select.value = state.setId;
}

/*
 * The host's buttons. Only the ones that do something in this phase are up: start in the lobby,
 * pause while a question is open, next while the answer is on the television, and at the end
 * another round or closing the room. Closing is also offered in the lobby and while the round is
 * stopped, which are the moments a host decides the evening is over.
 */
function drawHostBar(shown) {
  el('hostbar').hidden = !shown;
  if (!shown) return;

  el('host-gone').hidden = state.display;
  el('host-repair').hidden = state.display;
  const error = el('host-error');
  error.textContent = state.hostError;
  error.hidden = state.hostError === '';

  const start = el('host-start');
  start.hidden = state.phase !== 'lobby';
  start.textContent = t('startButton');

  const pause = el('host-pause');
  pause.hidden = state.phase !== 'question';
  pause.textContent = t(state.paused ? 'resumeButton' : 'pauseButton');

  const next = el('host-next');
  next.hidden = state.phase !== 'closed';
  const last = state.total !== null && state.index !== null && state.index + 1 >= state.total;
  next.textContent = t(last ? 'resultsButton' : 'nextButton');

  const finished = state.phase === 'finished' && state.sets.length > 0;
  el('host-again-field').hidden = !finished;
  if (finished) fillSets(el('host-again-set'));
  const again = el('host-again');
  again.hidden = !finished;
  again.textContent = t('againButton');

  const close = el('host-close');
  close.hidden = !(state.phase === 'lobby' || state.phase === 'finished' || state.paused);
  close.textContent = t(closeArmed ? 'closeRoomConfirm' : 'closeRoomButton');
  close.dataset.armed = String(Boolean(closeArmed));
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
    if (state.pending || wanted) return t('answerSending');
    return t('pickYours');
  }
  if (state.phase === 'closed') return t('questionClosed');
  if (state.phase === 'finished') return t('roundOver');
  // The one phone that is not waiting for anybody: it is the one everybody is waiting for.
  return t(isHost() ? 'hostLobby' : 'waitingForStart');
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
 * The television's QR codes open this page with a code in the address, so nobody types one off a
 * screen across the room: `screen` is the television's own code, shown while it has no room on it,
 * and `code` is the room's, shown in the lobby. Either is taken out of the address once read — a
 * room code is spent when the room closes, a screen code within minutes, and a reload or a bookmark
 * must not keep offering one.
 */
const params = new URLSearchParams(location.search);
const digits = (value) => (/^[0-9]{4}$/.test(value ?? '') ? value : null);
const scannedRoom = digits(params.get('code'));
const scannedScreen = digits(params.get('screen'));
if (params.has('code') || params.has('screen')) history.replaceState(null, '', location.pathname);

const host = recallHost();
state.hostKey = host.hostKey;
state.setId = host.setId;
state.hosting = host.hosting;

/*
 * Arriving: taking back the seat this phone has, or standing in front of the television it scanned.
 * Done once, on the first connection that works — which is usually the one at load, and otherwise
 * the one revive() makes, so that a page opened in a moment without signal still does what its
 * address asked for when the signal comes back.
 */
async function arrive() {
  arrived = true;
  /*
   * The seat to take back. A reload rejoins by itself, on the same path a dropped connection takes —
   * and a phone that opened a room is that room's host in any tab, which is what lets a second scan
   * of the television (a new tab, with nothing of its own remembered) do the host's work.
   */
  const stored = recall();
  const seat = stored.code && stored.name ? stored : (state.hostKey && state.hosting) || { code: null, name: null };
  const fresh = scannedRoom !== null && scannedRoom !== seat.code;
  if (!fresh && seat.code && seat.name) {
    state.code = seat.code;
    state.name = seat.name;
    await rejoin();
    // A room this phone opened on another evening and never closed has long been swept. That is how
    // most evenings end, not something that went wrong: the form comes up with nothing to say.
    if (state.phase === 'join' && !state.code && seat !== stored) state.error = '';
  }

  if (state.phase !== 'join') {
    // In a room. A screen code in the address then means one thing: this is the host, and the
    // television reloaded and is asking to be put back.
    if (scannedScreen && isHost()) await reseat(scannedScreen);
  } else if (scannedScreen && !state.code) {
    try {
      await enterScreen(scannedScreen);
    } catch (error) {
      state.error = screenError(error);
    }
  }

  if (state.phase === 'join' && fresh) el('form-join').elements.code.value = scannedRoom;
  fillForm();
}

/* The form as a phone first sees it: the name it played under last, and the cursor where it is needed. */
function fillForm() {
  if (state.phase !== 'join') return;
  const form = el('form-join');
  try {
    if (!form.elements.name.value) form.elements.name.value = localStorage.getItem(STORE_LAST_NAME) ?? '';
  } catch { /* nothing remembered */ }
  render();
  // The cursor goes to whichever field is still empty.
  if (!form.elements.name.value || state.screen) form.elements.name.focus();
  else (form.elements.code.value ? form.elements.name : form.elements.code).focus();
}

if (conn.state === signalR.HubConnectionState.Connected) await arrive();
else fillForm();
