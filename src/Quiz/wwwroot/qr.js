/*
 * A QR code for a short address, drawn as SVG. The television shows two of them: one the host scans
 * to reach the console with the screen code already filled in, one the players scan to reach the
 * join form with the room code already filled in. Typing an address off a television is the slowest
 * part of getting an evening started, and a camera is in every hand in the room.
 *
 * Written here rather than vendored because what the game needs is a sliver of the standard: byte
 * mode, error-correction level L, versions 1 to 5. Those five versions all carry their data in a
 * single block and need no version information, which is most of what makes a general encoder
 * long. Version 5-L holds 106 bytes; an address longer than that throws, and the caller shows no
 * code rather than a wrong one. Level L is the right level for a clean, bright screen: it buys the
 * largest modules, and nothing on a television is torn or smudged.
 *
 * The matrix and the SVG are separate so the encoder can be checked without a browser.
 */

/* Data and error-correction codewords for versions 1..5 at level L, one block each. */
const VERSIONS = [null, [19, 7], [34, 10], [55, 15], [80, 20], [108, 26]];

/* Level L's two format bits. */
const FORMAT_L = 1;

/** The modules of the smallest code that holds `text`: rows of booleans, true is dark. */
export function qrMatrix(text) {
  const bytes = new TextEncoder().encode(text);
  // Mode (4 bits) + length (8 bits) + the bytes; the terminator is whatever room is left, up to 4.
  const version = VERSIONS.findIndex((v) => v && bytes.length * 8 + 12 <= v[0] * 8);
  if (version < 1) throw new Error(`"${text}" is too long for a QR code this encoder can draw`);

  const [dataCount, eccCount] = VERSIONS[version];
  const data = codewords(bytes, dataCount);
  const all = data.concat(remainder(data, divisor(eccCount)));

  const size = version * 4 + 17;
  const modules = Array.from({ length: size }, () => new Array(size).fill(false));
  const reserved = Array.from({ length: size }, () => new Array(size).fill(false));
  const put = (x, y, dark) => {
    modules[y][x] = dark;
    reserved[y][x] = true;
  };

  drawFunctionPatterns(put, size, version);
  drawFormat(put, size, 0); // reserves the format area; the real bits follow the mask choice
  drawData(modules, reserved, size, all);

  // Every mask is tried and the least busy one kept, which is what makes a code easy to read.
  let best = null;
  for (let mask = 0; mask < 8; mask++) {
    applyMask(modules, reserved, size, mask);
    drawFormat(put, size, mask);
    const score = penalty(modules, size);
    if (best === null || score < best.score) best = { mask, score };
    applyMask(modules, reserved, size, mask); // masking is its own inverse
  }
  applyMask(modules, reserved, size, best.mask);
  drawFormat(put, size, best.mask);
  return modules;
}

/**
 * The code as an SVG element, with the four-module quiet zone the standard asks for. Dark on light
 * whatever the page looks like: a scanner expects that, and an inverted code is the one most phone
 * cameras refuse.
 */
export function qrSvg(text) {
  const modules = qrMatrix(text);
  const quiet = 4;
  const size = modules.length + quiet * 2;
  let path = '';
  modules.forEach((row, y) => row.forEach((dark, x) => {
    if (dark) path += `M${x + quiet},${y + quiet}h1v1h-1z`;
  }));

  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', `0 0 ${size} ${size}`);
  svg.setAttribute('shape-rendering', 'crispEdges');
  const back = document.createElementNS(ns, 'rect');
  back.setAttribute('width', String(size));
  back.setAttribute('height', String(size));
  back.setAttribute('fill', '#fff');
  const dots = document.createElementNS(ns, 'path');
  dots.setAttribute('d', path);
  dots.setAttribute('fill', '#000');
  svg.append(back, dots);
  return svg;
}

/* ---------- codewords ---------- */

function codewords(bytes, dataCount) {
  const bits = [];
  const push = (value, length) => {
    for (let i = length - 1; i >= 0; i--) bits.push((value >>> i) & 1);
  };
  push(0b0100, 4); // byte mode
  push(bytes.length, 8);
  for (const b of bytes) push(b, 8);

  const capacity = dataCount * 8;
  push(0, Math.min(4, capacity - bits.length));
  push(0, (8 - (bits.length % 8)) % 8);
  for (let pad = 0xec; bits.length < capacity; pad ^= 0xec ^ 0x11) push(pad, 8);

  const out = [];
  for (let i = 0; i < bits.length; i += 8) {
    out.push(bits.slice(i, i + 8).reduce((byte, bit) => (byte << 1) | bit, 0));
  }
  return out;
}

/* Reed–Solomon over GF(2^8) with the QR polynomial x^8 + x^4 + x^3 + x^2 + 1. */
function multiply(x, y) {
  let z = 0;
  for (let i = 7; i >= 0; i--) {
    z = (z << 1) ^ ((z >>> 7) * 0x11d);
    z ^= ((y >>> i) & 1) * x;
  }
  return z;
}

function divisor(degree) {
  const result = new Array(degree).fill(0);
  result[degree - 1] = 1;
  let root = 1;
  for (let i = 0; i < degree; i++) {
    for (let j = 0; j < degree; j++) {
      result[j] = multiply(result[j], root);
      if (j + 1 < degree) result[j] ^= result[j + 1];
    }
    root = multiply(root, 2);
  }
  return result;
}

function remainder(data, generator) {
  const result = new Array(generator.length).fill(0);
  for (const b of data) {
    const factor = b ^ result.shift();
    result.push(0);
    generator.forEach((coefficient, i) => { result[i] ^= multiply(coefficient, factor); });
  }
  return result;
}

/* ---------- the matrix ---------- */

function drawFunctionPatterns(put, size, version) {
  // Timing: the dotted row and column that tell a reader how big a module is.
  for (let i = 0; i < size; i++) {
    put(6, i, i % 2 === 0);
    put(i, 6, i % 2 === 0);
  }

  // The three finders, each with its light separator.
  for (const [cx, cy] of [[3, 3], [size - 4, 3], [3, size - 4]]) {
    for (let dy = -4; dy <= 4; dy++) {
      for (let dx = -4; dx <= 4; dx++) {
        const x = cx + dx;
        const y = cy + dy;
        if (x < 0 || x >= size || y < 0 || y >= size) continue;
        const ring = Math.max(Math.abs(dx), Math.abs(dy));
        put(x, y, ring !== 2 && ring !== 4);
      }
    }
  }

  // One alignment pattern, bottom right, from version 2 up. The other positions the standard
  // lists for these versions fall on the finders and are left out.
  if (version >= 2) {
    const c = size - 7;
    for (let dy = -2; dy <= 2; dy++) {
      for (let dx = -2; dx <= 2; dx++) {
        put(c + dx, c + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
      }
    }
  }
}

/* The fifteen format bits — level and mask, with their own error correction — written twice. */
function drawFormat(put, size, mask) {
  const data = (FORMAT_L << 3) | mask;
  let rem = data;
  for (let i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >>> 9) * 0x537);
  const bits = ((data << 10) | rem) ^ 0x5412;
  const bit = (i) => ((bits >>> i) & 1) !== 0;

  for (let i = 0; i <= 5; i++) put(8, i, bit(i));
  put(8, 7, bit(6));
  put(8, 8, bit(7));
  put(7, 8, bit(8));
  for (let i = 9; i < 15; i++) put(14 - i, 8, bit(i));

  for (let i = 0; i < 8; i++) put(size - 1 - i, 8, bit(i));
  for (let i = 8; i < 15; i++) put(8, size - 15 + i, bit(i));
  put(8, size - 8, true); // always dark
}

/* The codewords, bit by bit, in the zigzag that climbs and descends two columns at a time. */
function drawData(modules, reserved, size, all) {
  let i = 0;
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5; // the timing column is skipped whole
    for (let vert = 0; vert < size; vert++) {
      for (let j = 0; j < 2; j++) {
        const x = right - j;
        const upward = ((right + 1) & 2) === 0;
        const y = upward ? size - 1 - vert : vert;
        if (reserved[y][x] || i >= all.length * 8) continue;
        modules[y][x] = ((all[i >>> 3] >>> (7 - (i & 7))) & 1) !== 0;
        i++;
      }
    }
  }
}

const MASKS = [
  (x, y) => (x + y) % 2 === 0,
  (x, y) => y % 2 === 0,
  (x) => x % 3 === 0,
  (x, y) => (x + y) % 3 === 0,
  (x, y) => (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0,
  (x, y) => ((x * y) % 2) + ((x * y) % 3) === 0,
  (x, y) => (((x * y) % 2) + ((x * y) % 3)) % 2 === 0,
  (x, y) => (((x + y) % 2) + ((x * y) % 3)) % 2 === 0,
];

function applyMask(modules, reserved, size, mask) {
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      if (!reserved[y][x] && MASKS[mask](x, y)) modules[y][x] = !modules[y][x];
    }
  }
}

/* The standard's four measures of how hard a pattern is to read; lower is better. */
function penalty(modules, size) {
  let result = 0;
  const lines = [];
  for (let i = 0; i < size; i++) {
    lines.push(modules[i]);
    lines.push(modules.map((row) => row[i]));
  }

  for (const line of lines) {
    // Runs of five or more of one colour.
    let run = 1;
    for (let i = 1; i <= size; i++) {
      if (i < size && line[i] === line[i - 1]) {
        run++;
      } else {
        if (run >= 5) result += run - 2;
        run = 1;
      }
    }
    // Anything that looks like a finder: dark, light, three dark, light, dark, with four light beside it.
    const text = line.map((dark) => (dark ? '1' : '0')).join('');
    for (const pattern of ['10111010000', '00001011101']) {
      for (let at = text.indexOf(pattern); at !== -1; at = text.indexOf(pattern, at + 1)) result += 40;
    }
  }

  // Two-by-two blocks of one colour.
  for (let y = 0; y < size - 1; y++) {
    for (let x = 0; x < size - 1; x++) {
      const c = modules[y][x];
      if (c === modules[y][x + 1] && c === modules[y + 1][x] && c === modules[y + 1][x + 1]) result += 3;
    }
  }

  // How far the share of dark modules strays from a half.
  const dark = modules.reduce((sum, row) => sum + row.filter(Boolean).length, 0);
  result += Math.floor(Math.abs((dark * 20) / (size * size) - 10)) * 10;
  return result;
}
