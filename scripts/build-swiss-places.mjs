// Builds the seed of the Swiss municipalities and localities (Casazen.Infrastructure/Data/Seeds/swiss/swiss-localities.csv, E29/F01/US01/T01, NF-124).
//
//   node scripts/build-swiss-places.mjs <AMTOVZ_CSV_LV95.csv> <seeds/swiss folder>
//
// <AMTOVZ_CSV_LV95.csv> is the CSV of the official directory of the localities of swisstopo («Amtliches Ortschaftenverzeichnis mit Postleitzahl und
// Perimeter», open government data, source credit «swisstopo»), extracted from ortschaftenverzeichnis_plz_2056.csv.zip of the STAC API of
// data.geo.admin.ch. It is data of a third party: nothing in it is executed, every row is checked, and the build stops (exit 1) at the first thing that
// looks wrong. Run it from the repository, never from the folder of the download. Provenance and refresh: Data/Seeds/swiss/README.md and
// docs/compliance/swiss-municipalities.md.
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const [sourcePath, outDir] = process.argv.slice(2);
if (!sourcePath || !outDir) {
  console.error('usage: node scripts/build-swiss-places.mjs <AMTOVZ_CSV_LV95.csv> <seeds/swiss folder>');
  process.exit(2);
}

// The 26 cantons (ISO 3166-2:CH). A row of the directory without a canton is a locality of Liechtenstein, which the directory covers too: left out.
const CANTONS = new Set([
  'AG', 'AI', 'AR', 'BE', 'BL', 'BS', 'FR', 'GE', 'GL', 'GR', 'JU', 'LU', 'NE',
  'NW', 'OW', 'SG', 'SH', 'SO', 'SZ', 'TG', 'TI', 'UR', 'VD', 'VS', 'ZG', 'ZH',
]);

// Municipalities the rules of CasaZen depend on (the Ticino ones first): a new edition of the directory that does not have them as written here is
// a change somebody has to read (a merger, a renumbering), never a silent update.
const ANCHORS = [
  ['5192', 'Lugano', 'TI'],
  ['5113', 'Locarno', 'TI'],
  ['5002', 'Bellinzona', 'TI'],
  ['5254', 'Mendrisio', 'TI'],
  ['5091', 'Ascona', 'TI'],
  ['261', 'Zürich', 'ZH'],
  ['351', 'Bern', 'BE'],
  ['6621', 'Genève', 'GE'],
];

const COLUMNS = {
  locality: 'Ortschaftsname',
  postalCode: 'PLZ4',
  addOn: 'Zusatzziffer',
  municipality: 'Gemeindename',
  municipalityCode: 'BFS-Nr',
  canton: 'Kantonskürzel',
};

const OUTPUT_HEADER = 'postal_code;add_on;locality;municipality_code;municipality;canton';
const MAX_NAME_LENGTH = 100;

const fail = (message) => {
  console.error(`FAILED: ${message}`);
  process.exit(1);
};

const sha256 = (value) => createHash('sha256').update(value).digest('hex');

// Ordinal comparison: the order of the seed never depends on the locale of the machine that builds it.
const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

const bytes = readFileSync(sourcePath);
if (bytes.length === 0 || bytes.length > 20 * 1024 * 1024) fail(`unexpected size of the source: ${bytes.length} bytes`);

let text;
try {
  text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
} catch {
  fail('the source is not UTF-8');
}
if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
// The official file has no quoted cells: a quote would mean a format this builder does not read.
if (text.includes('"')) fail('the source has quoted cells, a format this builder does not read');

const lines = text.split(/\r?\n/);
while (lines.length > 0 && lines[lines.length - 1] === '') lines.pop();
if (lines.length < 2) fail('the source has no rows');

const header = lines[0].split(';');
const index = {};
for (const [key, name] of Object.entries(COLUMNS)) {
  const at = header.indexOf(name);
  if (at < 0) fail(`column «${name}» missing from the header: ${lines[0]}`);
  index[key] = at;
}

const rows = new Map();
const municipalities = new Map();
const excludedCodes = new Set();
let excluded = 0;

for (let i = 1; i < lines.length; i++) {
  const line = i + 1;
  const cells = lines[i].split(';');
  if (cells.length !== header.length) fail(`line ${line}: ${cells.length} cells, the header has ${header.length}`);

  const value = (key) => cells[index[key]].trim();
  const locality = value('locality');
  const postalCode = value('postalCode');
  const addOn = value('addOn');
  const municipality = value('municipality');
  const municipalityCode = value('municipalityCode');
  const canton = value('canton');

  if (!/^[1-9]\d{3}$/.test(postalCode)) fail(`line ${line}: postal code «${postalCode}» is not four digits`);
  if (!/^\d{2}$/.test(addOn)) fail(`line ${line}: add-on «${addOn}» is not two digits`);
  if (!/^[1-9]\d{0,3}$/.test(municipalityCode)) fail(`line ${line}: municipality number «${municipalityCode}» is not a BFS number`);
  for (const [label, name] of [['locality', locality], ['municipality', municipality]]) {
    if (name.length === 0 || name.length > MAX_NAME_LENGTH) fail(`line ${line}: ${label} name of ${name.length} characters`);
    if (/[\u0000-\u001f\u007f;]/.test(name)) fail(`line ${line}: ${label} name with a control character or a separator`);
  }

  if (canton === '') {
    excluded++;
    excludedCodes.add(municipalityCode);
    continue;
  }
  if (!CANTONS.has(canton)) fail(`line ${line}: canton «${canton}» is not one of the 26`);

  const known = municipalities.get(municipalityCode);
  if (known && (known.name !== municipality || known.canton !== canton))
    fail(`line ${line}: municipality ${municipalityCode} is «${municipality}» (${canton}) here and «${known.name}» (${known.canton}) above`);
  municipalities.set(municipalityCode, { name: municipality, canton });

  const key = `${postalCode};${addOn};${locality};${municipalityCode}`;
  if (!rows.has(key)) rows.set(key, { postalCode, addOn, locality, municipalityCode, municipality, canton });
}

for (const code of excludedCodes) {
  if (municipalities.has(code)) fail(`municipality ${code} is both inside and outside Switzerland`);
}

for (const [code, name, canton] of ANCHORS) {
  const found = municipalities.get(code);
  if (!found || found.name !== name || found.canton !== canton)
    fail(`anchor ${code} ${name} (${canton}) is ${found ? `«${found.name}» (${found.canton})` : 'missing'} in this edition: read the change before updating the anchors`);
}

const cantonsSeen = new Set([...municipalities.values()].map((m) => m.canton));
if (cantonsSeen.size !== CANTONS.size) fail(`only ${cantonsSeen.size} cantons have a municipality`);

const sorted = [...rows.values()].sort(
  (a, b) =>
    ordinal(a.postalCode, b.postalCode)
    || ordinal(a.addOn, b.addOn)
    || ordinal(a.locality, b.locality)
    || Number(a.municipalityCode) - Number(b.municipalityCode));

const output = [OUTPUT_HEADER, ...sorted.map((r) => [r.postalCode, r.addOn, r.locality, r.municipalityCode, r.municipality, r.canton].join(';'))].join('\n') + '\n';
const outPath = join(outDir, 'swiss-localities.csv');
writeFileSync(outPath, output, { encoding: 'utf8' });

const localities = new Set(sorted.map((r) => `${r.postalCode};${r.addOn};${r.locality}`));
console.log(`source            ${sourcePath}`);
console.log(`source sha256     ${sha256(bytes)}`);
console.log(`rows read         ${lines.length - 1}`);
console.log(`left out (no canton, Liechtenstein) ${excluded} rows, ${excludedCodes.size} municipalities`);
console.log(`rows written      ${sorted.length}`);
console.log(`localities        ${localities.size} (postal code, add-on, name)`);
console.log(`municipalities    ${municipalities.size} in ${cantonsSeen.size} cantons`);
for (const [code, name, canton] of ANCHORS.filter(([, , canton]) => canton === 'TI'))
  console.log(`anchor            ${code} ${name} ${canton}`);
console.log(`seed              ${outPath}`);
console.log(`seed sha256       ${sha256(output)}`);
