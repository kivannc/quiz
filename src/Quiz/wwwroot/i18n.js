/*
 * Every visible string on both screens, in both languages, and the switch between them.
 * Nothing user-visible is written into the markup: elements carry data-i18n="key" and
 * applyStatic() fills them, while anything with numbers in it is a function called from
 * the page. Turkish is the default; the choice is remembered in localStorage.
 *
 * Question text, option text and player names are not here: they come from the server.
 */

const STORAGE_KEY = 'quiz.lang';
const DEFAULT_LANG = 'tr';

export const LANGS = ['tr', 'en'];

const STRINGS = {
  tr: {
    // --- phone ---
    phoneTitle: 'Quiz — Katıl',
    joinHeading: 'Katıl',
    codeLabel: 'Oda kodu',
    nameLabel: 'Adın',
    joinButton: 'GİR',
    waitingForStart: 'oda hazır, başlamasını bekle',
    pickYours: 'cevabını seç',
    answerSending: 'gönderiliyor…',
    answerSent: 'cevabın alındı',
    answerRefused: 'cevap geçmedi',
    // Spoken names for the four suits. They are never shown: the buzzers carry the glyph
    // alone, and this is what a screen reader reads out instead of an empty button.
    markerName: (marker) => ({ hearts: 'kupa', diamonds: 'karo', clubs: 'sinek', spades: 'maça' })[marker] ?? marker,
    questionClosed: 'soru kapandı, ekrana bak',
    paused: 'oyun duraklatıldı, bekle',
    roundOver: 'tur bitti',
    roomClosed: 'oda kapandı',
    scoreOf: (score) => `${score} p`,

    // --- TV ---
    hostTitle: 'Quiz — Sunucu',
    openHeading: 'Oda aç',
    hostKeyLabel: 'Sunucu anahtarı',
    setLabel: 'Soru seti',
    openButton: 'ODA AÇ',
    codeTag: '[ KOD ]',
    joinAt: (host) => `telefonundan ${host} adresine gir`,
    waitingForPlayers: 'oyuncu bekleniyor',
    startButton: 'BAŞLAT',
    roomTag: (code) => `[ oda ${code} ]`,
    questionCounter: (index, total) => `soru ${pad(index)}/${pad(total)}`,
    answeredCounter: (answered, total) => `cevaplayan ${answered}/${total}`,
    clock: (seconds) => `t-${pad(seconds)}s`,
    closedTag: 'kapandı',
    correctIs: 'Doğru cevap:',
    nextButton: 'SONRAKİ SORU',
    resultsButton: 'SONUÇLAR',
    finishedHeading: 'TUR BİTTİ',
    newRoomButton: 'YENİ ODA',
    pauseButton: 'DURAKLAT',
    resumeButton: 'DEVAM ET',
    pausedBanner: 'DURAKLATILDI',
    pausedHint: 'oyun bekliyor',
    closeRoomButton: 'ODAYI KAPAT',
    closeRoomConfirm: 'EMİN MİSİN?',

    // --- both: connection and errors ---
    reconnecting: 'bağlantı koptu, yeniden bağlanılıyor…',
    disconnected: 'bağlantı kurulamadı, sayfayı yenile',
    errNoSuchRoom: 'böyle bir oda yok',
    errInvalidName: 'ad 1–24 karakter olmalı',
    errThrottled: 'çok fazla deneme, bir dakika bekle',
    errRoomFull: 'oda dolu',
    errRoundInProgress: 'tur başladı, bitmesini bekle',
    errForbidden: 'anahtar kabul edilmedi',
    errTooManyRooms: 'şu an açılabilecek oda kalmadı',
    errUnknownSet: 'böyle bir soru seti yok',
    errGeneric: 'bir şeyler ters gitti, tekrar dene',
  },

  en: {
    // --- phone ---
    phoneTitle: 'Quiz — Join',
    joinHeading: 'Join',
    codeLabel: 'Room code',
    nameLabel: 'Your name',
    joinButton: 'ENTER',
    waitingForStart: 'you are in, waiting for the host',
    pickYours: 'pick your answer',
    answerSending: 'sending…',
    answerSent: 'answer locked in',
    answerRefused: 'that answer did not count',
    markerName: (marker) => ({ hearts: 'hearts', diamonds: 'diamonds', clubs: 'clubs', spades: 'spades' })[marker] ?? marker,
    questionClosed: 'question closed, look at the screen',
    paused: 'the game is paused, hold on',
    roundOver: 'round over',
    roomClosed: 'the room was closed',
    scoreOf: (score) => `${score} p`,

    // --- TV ---
    hostTitle: 'Quiz — Host',
    openHeading: 'Open a room',
    hostKeyLabel: 'Host key',
    setLabel: 'Question set',
    openButton: 'OPEN ROOM',
    codeTag: '[ CODE ]',
    joinAt: (host) => `open ${host} on your phone`,
    waitingForPlayers: 'waiting for players',
    startButton: 'START',
    roomTag: (code) => `[ room ${code} ]`,
    questionCounter: (index, total) => `question ${pad(index)}/${pad(total)}`,
    answeredCounter: (answered, total) => `answered ${answered}/${total}`,
    clock: (seconds) => `t-${pad(seconds)}s`,
    closedTag: 'closed',
    correctIs: 'Correct answer:',
    nextButton: 'NEXT QUESTION',
    resultsButton: 'RESULTS',
    finishedHeading: 'ROUND OVER',
    newRoomButton: 'NEW ROOM',
    pauseButton: 'PAUSE',
    resumeButton: 'RESUME',
    pausedBanner: 'PAUSED',
    pausedHint: 'the game is waiting',
    closeRoomButton: 'CLOSE ROOM',
    closeRoomConfirm: 'ARE YOU SURE?',

    // --- both: connection and errors ---
    reconnecting: 'connection lost, reconnecting…',
    disconnected: 'could not reconnect, reload the page',
    errNoSuchRoom: 'no room with that code',
    errInvalidName: 'a name must be 1–24 characters',
    errThrottled: 'too many tries, wait a minute',
    errRoomFull: 'the room is full',
    errRoundInProgress: 'the round has started, wait for it to end',
    errForbidden: 'that host key was refused',
    errTooManyRooms: 'no room can be opened right now',
    errUnknownSet: 'no such question set',
    errGeneric: 'something went wrong, try again',
  },
};

/** Two digits, so the TV's counters and countdown do not jump about. */
function pad(n) {
  return String(n).padStart(2, '0');
}

let current = read();
const listeners = new Set();

function read() {
  let stored = null;
  try {
    stored = localStorage.getItem(STORAGE_KEY);
  } catch {
    // Private mode or storage switched off: the default language is fine.
  }
  return LANGS.includes(stored) ? stored : DEFAULT_LANG;
}

export function lang() {
  return current;
}

export function setLang(code) {
  if (!LANGS.includes(code) || code === current) return;
  current = code;
  try {
    localStorage.setItem(STORAGE_KEY, code);
  } catch {
    // Not remembering the choice is better than failing the switch.
  }
  document.documentElement.lang = code;
  for (const listener of listeners) listener();
}

/** Runs fn whenever the language changes, and once now. */
export function onLangChange(fn) {
  listeners.add(fn);
  fn();
}

/** The string for a key; extra arguments go to the ones that take numbers. */
export function t(key, ...args) {
  const value = STRINGS[current][key] ?? STRINGS[DEFAULT_LANG][key];
  if (value === undefined) throw new Error(`No string for "${key}"`);
  return typeof value === 'function' ? value(...args) : value;
}

/** Fills every [data-i18n] element under root. Placeholders are translated too. */
export function applyStatic(root = document) {
  for (const el of root.querySelectorAll('[data-i18n]')) {
    el.textContent = t(el.dataset.i18n);
  }
  for (const el of root.querySelectorAll('[data-i18n-placeholder]')) {
    el.placeholder = t(el.dataset.i18nPlaceholder);
  }
}

/** Builds the TR / EN switch into el. The codes themselves are not translated. */
export function mountLangSwitch(el) {
  el.classList.add('langs');
  el.replaceChildren();
  LANGS.forEach((code, i) => {
    if (i > 0) {
      const sep = document.createElement('span');
      sep.className = 'sep';
      sep.textContent = '/';
      el.append(sep);
    }
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = code.toUpperCase();
    button.lang = code;
    button.addEventListener('click', () => setLang(code));
    el.append(button);
  });
  const mark = () => {
    for (const button of el.querySelectorAll('button')) {
      button.setAttribute('aria-pressed', String(button.lang === current));
    }
  };
  onLangChange(mark);
}

/**
 * The hub throws HubExceptions carrying a short code ("forbidden", "throttled", ...).
 * The browser client wraps that message in a sentence of its own, so match on the code.
 */
const ERROR_STRINGS = {
  'no-such-room': 'errNoSuchRoom',
  'invalid-name': 'errInvalidName',
  'throttled': 'errThrottled',
  'forbidden': 'errForbidden',
  'too-many-rooms': 'errTooManyRooms',
  'unknown-set': 'errUnknownSet',
};

/** A readable message for whatever an invoke rejected with. */
export function errorText(error) {
  const message = String(error?.message ?? error ?? '');
  for (const [code, key] of Object.entries(ERROR_STRINGS)) {
    if (message.includes(code)) return t(key);
  }
  return t('errGeneric');
}

document.documentElement.lang = current;
