#!/usr/bin/env node
/*
 * Fetches and verifies the model files of the identity verification (E22/F01/US01/T01, ADR-013-R8 and R14).
 *
 * The models are listed in Casazen.Web/MlModels/models.manifest.json (source, licence, size, the variables to pin). This script
 * downloads the ones you choose to their target path, refuses a size that is off by more than 20% (the tolerance of the manifest),
 * computes the SHA-256 and prints the Railway variable lines to set. With --write-manifest it also writes the hashes into the manifest.
 *
 * NOTHING RUNS BY ITSELF. It is not part of the build, of the tests or of the CI (it refuses to download when the CI variable is
 * set), and no model is downloaded or committed without the permission of the product owner (decision O7, docs/runbooks/biometric-models.md).
 * Node 20 or later, no dependency.
 *
 * Usage:
 *   node scripts/biometrics/fetch-models.mjs --list
 *   node scripts/biometrics/fetch-models.mjs --dry-run [--only yunet,ocrb]
 *   node scripts/biometrics/fetch-models.mjs [--only yunet,minifasnet,auraface,ocrb] [--dir Casazen.Web/MlModels]
 *                                            [--revision yunet=<commit>] [--write-manifest] [--force]
 *
 * Options:
 *   --only a,b           the manifest ids to handle (default: every model that is not an alternative; sface is the alternative of auraface)
 *   --dir <folder>       the MlModels folder the target paths start from (default: Casazen.Web/MlModels of this repository)
 *   --manifest <file>    another manifest (default: <dir>/models.manifest.json)
 *   --revision id=rev    the commit or tag of the source to download id from (repeatable); written into the manifest with --write-manifest
 *   --write-manifest     write the SHA-256 (and the size) of what was verified into the manifest
 *   --force              accept a size off the tolerance and replace a file or a pinned hash that differs
 *   --dry-run            print what would be done: no network, no file written
 *   --list               print the manifest: no network, no file written
 *
 * Exit code: 0 everything verified, 1 a failure (download, size, hash that differs from the pinned one), 2 a manual step is still to do
 * (MiniFASNetV2 is exported from its PyTorch release with scripts/biometrics/export_minifasnet.py), 64 a wrong command line.
 */
import { createHash } from 'node:crypto';
import { createReadStream, createWriteStream } from 'node:fs';
import { mkdir, readFile, rename, rm, stat, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { pipeline } from 'node:stream/promises';
import { Readable } from 'node:stream';
import { fileURLToPath, pathToFileURL } from 'node:url';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const defaultDirectory = resolve(scriptDirectory, '..', '..', 'Casazen.Web', 'MlModels');
const defaultTolerance = 0.2;

export class UsageError extends Error {}

/** The options of the command line. Unknown options and options without a value are a usage error. */
export function parseArgs(argv) {
  const options = {
    only: null,
    dir: null,
    manifest: null,
    revisions: new Map(),
    writeManifest: false,
    force: false,
    dryRun: false,
    list: false,
    help: false,
  };

  for (let index = 0; index < argv.length; index += 1) {
    let name = argv[index];
    let inline = null;
    const equals = name.indexOf('=');
    if (name.startsWith('--') && equals > 0) {
      inline = name.slice(equals + 1);
      name = name.slice(0, equals);
    }

    const value = () => {
      if (inline !== null) return inline;
      index += 1;
      if (index >= argv.length) throw new UsageError(`${name} needs a value`);
      return argv[index];
    };

    switch (name) {
      case '--only':
        options.only = value().split(',').map((id) => id.trim()).filter(Boolean);
        break;
      case '--dir':
        options.dir = value();
        break;
      case '--manifest':
        options.manifest = value();
        break;
      case '--revision': {
        const pair = value();
        const separator = pair.indexOf('=');
        if (separator <= 0 || separator === pair.length - 1) throw new UsageError('--revision needs id=revision');
        options.revisions.set(pair.slice(0, separator), pair.slice(separator + 1));
        break;
      }
      case '--write-manifest':
        options.writeManifest = true;
        break;
      case '--force':
        options.force = true;
        break;
      case '--dry-run':
        options.dryRun = true;
        break;
      case '--list':
        options.list = true;
        break;
      case '--help':
      case '-h':
        options.help = true;
        break;
      default:
        throw new UsageError(`Unknown option ${name}`);
    }
  }

  return options;
}

/** True when `actual` is within `ratio` of `expected`; a model with no expected size is not checked. */
export function sizeWithinTolerance(actual, expected, ratio) {
  if (expected === null || expected === undefined) return true;
  return Math.abs(actual - expected) <= expected * ratio;
}

/** The tolerance of one model: its own when the manifest gives one (the README only says "a few MB" for OCR-B), otherwise the manifest's. */
export function toleranceOf(manifest, entry) {
  return entry.toleranceRatio ?? manifest.toleranceRatio ?? defaultTolerance;
}

/** The address a model is downloaded from, or null when it is not downloaded (MiniFASNetV2 is exported). */
export function sourceUrl(entry, revisionOverride) {
  const template = entry.source?.urlTemplate;
  if (!template) return null;

  const revision = revisionOverride ?? entry.source.revision ?? entry.source.defaultRevision;
  return {
    url: template.replace('{revision}', revision),
    revision,
    pinned: Boolean(revisionOverride ?? entry.source.revision),
  };
}

/** The models to handle: the --only ids, or every model that is not an alternative. An unknown id is a usage error. */
export function selectModels(manifest, only) {
  if (!only) return manifest.models.filter((entry) => !entry.alternative);

  const known = new Map(manifest.models.map((entry) => [entry.id, entry]));
  return only.map((id) => {
    const entry = known.get(id);
    if (!entry) throw new UsageError(`Unknown model "${id}". Known: ${[...known.keys()].join(', ')}`);
    return entry;
  });
}

/** The Railway variable lines to set for the verified models, in the order of the manifest. Values are hashes and addresses, never secrets. */
export function variableLines(results) {
  const lines = [];
  for (const { entry, sha256, url } of results) {
    if (!sha256) continue;

    lines.push(`${entry.config.sha256}=${sha256}`);
    if (entry.config.downloadUrl && url) lines.push(`${entry.config.downloadUrl}=${url}`);
    for (const [key, value] of Object.entries(entry.config.set ?? {})) lines.push(`${key}=${value}`);
  }

  return lines;
}

async function sha256Of(path) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(path)) hash.update(chunk);
  return hash.digest('hex').toUpperCase();
}

async function download(url, target, expected, tolerance, force) {
  const response = await fetch(url, { redirect: 'follow' });
  if (!response.ok || !response.body) throw new Error(`download failed: HTTP ${response.status}`);

  const announced = Number(response.headers.get('content-length'));
  if (!force && Number.isFinite(announced) && announced > 0 && !sizeWithinTolerance(announced, expected, tolerance)) {
    throw new Error(`the server announces ${announced} bytes, off by more than ${Math.round(tolerance * 100)}% from the ${expected} of the manifest (use --force to accept)`);
  }

  await mkdir(dirname(target), { recursive: true });
  const partial = `${target}.partial`;
  const hash = createHash('sha256');
  let bytes = 0;
  try {
    await pipeline(Readable.fromWeb(response.body), async function* (source) {
      for await (const chunk of source) {
        hash.update(chunk);
        bytes += chunk.length;
        yield chunk;
      }
    }, createWriteStream(partial));

    if (!force && !sizeWithinTolerance(bytes, expected, tolerance)) {
      throw new Error(`${bytes} bytes arrived, off by more than ${Math.round(tolerance * 100)}% from the ${expected} of the manifest (use --force to accept)`);
    }

    await rename(partial, target);
    return { bytes, sha256: hash.digest('hex').toUpperCase() };
  } finally {
    await rm(partial, { force: true });
  }
}

/** One model: use the file that is there, or download it; then check the size and the hash against what the manifest pins. */
async function handle(manifest, entry, options, directory) {
  const target = join(directory, entry.targetPath);
  const tolerance = toleranceOf(manifest, entry);
  const source = sourceUrl(entry, options.revisions.get(entry.id));
  const result = { entry, target, url: source?.url ?? null, sha256: null, bytes: null, status: 'failed', message: '' };

  if (options.dryRun) {
    result.status = 'dry-run';
    result.message = source
      ? `would download ${source.url}${source.pinned ? '' : ` (revision "${source.revision}" is not pinned: the SHA-256 you pin is what makes the file safe)`}`
      : `would not download: ${entry.notes?.[0] ?? 'produced by a recipe'}`;
    return result;
  }

  let present = false;
  try {
    present = (await stat(target)).isFile();
  } catch {
    present = false;
  }

  try {
    if (!present || options.force) {
      if (!source) {
        if (!present) {
          result.status = 'manual';
          result.message = entry.export
            ? `not downloadable: run ${entry.export.script}, then run this script again with --only ${entry.id}`
            : 'not downloadable: put the file at the target path, then run this script again';
          return result;
        }
      } else {
        const fetched = await download(source.url, target, entry.approxSizeBytes, tolerance, options.force);
        result.bytes = fetched.bytes;
        result.sha256 = fetched.sha256;
      }
    }

    if (result.sha256 === null) {
      result.bytes = (await stat(target)).size;
      if (!options.force && !sizeWithinTolerance(result.bytes, entry.approxSizeBytes, tolerance)) {
        throw new Error(`the file has ${result.bytes} bytes, off by more than ${Math.round(tolerance * 100)}% from the ${entry.approxSizeBytes} of the manifest (use --force to accept)`);
      }

      result.sha256 = await sha256Of(target);
    }

    if (entry.sha256 && entry.sha256.toUpperCase() !== result.sha256 && !options.force) {
      result.status = 'failed';
      result.message = 'the SHA-256 differs from the one pinned in the manifest: not the file that was approved (use --force with --write-manifest to replace the pin)';
      return result;
    }

    result.status = 'ok';
    result.message = source && !source.pinned ? `revision "${source.revision}" is not pinned in the manifest: write the commit you used (--revision ${entry.id}=<commit> --write-manifest)` : '';
  } catch (error) {
    result.status = 'failed';
    result.message = error instanceof Error ? error.message : String(error);
  }

  return result;
}

function printList(manifest) {
  for (const entry of manifest.models) {
    const flags = [entry.delivery, entry.alternative ? 'alternative' : null, entry.sha256 ? 'pinned' : 'not pinned'].filter(Boolean).join(', ');
    console.log(`${entry.id.padEnd(11)} ${entry.role.padEnd(17)} ${entry.name} ${entry.version} (${entry.license.id}, ~${Math.round(entry.approxSizeBytes / 1000)} KB; ${flags})`);
    console.log(`            -> ${entry.targetPath}   [${entry.config.sha256}]`);
  }
}

async function main(argv, environment) {
  const options = parseArgs(argv);
  if (options.help) {
    console.log('Usage: node scripts/biometrics/fetch-models.mjs [--list] [--dry-run] [--only ids] [--dir folder] [--revision id=rev] [--write-manifest] [--force]');
    console.log('See the header of this file and docs/runbooks/biometric-models.md.');
    return 0;
  }

  const directory = resolve(options.dir ?? defaultDirectory);
  const manifestPath = resolve(options.manifest ?? join(directory, 'models.manifest.json'));
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));

  if (options.list) {
    printList(manifest);
    return 0;
  }

  if (!options.dryRun && environment.CI) {
    console.error('Refusing to download models in CI (the CI variable is set): this script is run by hand, with the permission of the product owner.');
    return 1;
  }

  const results = [];
  for (const entry of selectModels(manifest, options.only)) {
    const result = await handle(manifest, entry, options, directory);
    results.push(result);
    const size = result.bytes === null ? '' : ` ${result.bytes} bytes`;
    console.log(`[${result.status}] ${entry.id}${size}${result.sha256 ? ` sha256=${result.sha256}` : ''}${result.message ? ` - ${result.message}` : ''}`);
  }

  const verified = results.filter((result) => result.status === 'ok');
  const lines = variableLines(verified);
  if (lines.length > 0) {
    console.log('\nRailway variables to set (test first, then production), with the models of this run:');
    for (const line of lines) console.log(`  ${line}`);
  }

  if (options.writeManifest && verified.length > 0) {
    for (const { entry, sha256, bytes } of verified) {
      entry.sha256 = sha256;
      entry.sizeBytes = bytes;
      const revision = options.revisions.get(entry.id);
      if (revision && entry.source) entry.source.revision = revision;
    }

    await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, 'utf8');
    console.log(`\nManifest updated: ${manifestPath}`);
  }

  if (results.some((result) => result.status === 'failed')) return 1;
  if (results.some((result) => result.status === 'manual')) return 2;
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2), process.env).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      console.error(error instanceof UsageError ? `${error.message}\nRun with --help.` : error);
      process.exitCode = error instanceof UsageError ? 64 : 1;
    },
  );
}
