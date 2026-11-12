#!/usr/bin/env python3
"""Splits the backend test suite into balanced shards for CI, and checks that every planned class ran.

Why: Casazen.Tests has about 19,000 tests and took 10-17 minutes in one job. CI compiles once (ci-cd.yml, job `build`), then
runs the shards in parallel (job `test`). xUnit runs the tests of one class one after the other, so the unit of work is the
class: a shard is a list of whole classes.

  plan     reads the output of `dotnet test <dll> --list-tests` (the discovery `dotnet test` itself does), the weights file
           (seconds per class, measured in CI) and the number of shards, gives every class to exactly one shard
           (longest-processing-time first, so the shards take about the same time) and writes, for each shard, a VSTest
           filter (`shard-N.filter`) and the list of its classes (`shard-N.classes`).
  verify   reads the TRX of one shard and fails when a class the plan gave to that shard has no result, i.e. when a filter
           silently selected nothing. The union of the shards is the whole suite by construction (the plan is built from the
           discovery of the very assembly the shards run); this checks that the filters did what the plan says.
  weights  turns the TRX files of a run (the shards upload them as the artifacts `test-results-N`) into a new weights file.

The weights only decide how well balanced the shards are, never what runs. A class the weights file does not know (a new one)
is placed with `default_seconds_per_test_<kind>` x its number of tests, so it still runs; refresh the file now and then:

  gh run download <run-id> --repo casazen/backend --pattern 'test-results-*' --dir trx
  python3 scripts/ci/test_shards.py weights --trx trx/*/*.trx > scripts/ci/test-class-weights.tsv
"""

import argparse
import os
import sys
import xml.etree.ElementTree as ET

# VSTest filter syntax: these characters are special in a value and must be escaped with a backslash.
FILTER_SPECIAL = set('\\()&|=!~')
# Seconds per test of a class the weights file does not list, by kind; used when the file has no such line.
DEFAULT_SECONDS_PER_TEST = {'unit': 0.012, 'integration': 0.12}
MIN_WEIGHT_TO_LIST = 1.0  # `weights` lists the classes that took at least this many seconds; the others use the defaults


def kind_of(cls):
    return 'integration' if '.Integration.' in cls else 'unit'


def escape_filter_value(value):
    return ''.join('\\' + ch if ch in FILTER_SPECIAL else ch for ch in value)


def class_of(display_name):
    """`Ns.Class.Method(arg: 1)` -> `Ns.Class` (the arguments of a theory row may contain dots and parentheses)."""
    head = display_name.split('(', 1)[0]
    if '.' not in head:
        return None
    return head.rsplit('.', 1)[0]


def read_list(path):
    """Classes of `dotnet test --list-tests` output, with their number of test cases."""
    counts = {}
    with open(path, encoding='utf-8', errors='replace') as handle:
        for raw in handle:
            line = raw.rstrip('\r\n')
            # The test names are the lines indented by exactly four spaces, under "The following Tests are available:".
            if len(line) > 4 and line.startswith('    ') and not line[4].isspace():
                cls = class_of(line.strip())
                if cls:
                    counts[cls] = counts.get(cls, 0) + 1
    return counts


def read_weights(path):
    weights = {}
    defaults = dict(DEFAULT_SECONDS_PER_TEST)
    if path and os.path.exists(path):
        with open(path, encoding='utf-8') as handle:
            for raw in handle:
                line = raw.strip()
                if not line or line.startswith('#'):
                    continue
                key, _, value = line.partition('\t')
                if key.startswith('default_seconds_per_test_'):
                    defaults[key[len('default_seconds_per_test_'):]] = float(value)
                else:
                    weights[value] = float(key)
    return weights, defaults


def cmd_plan(args):
    counts = read_list(args.list)
    if not counts:
        print('::error::No test found in %s: the discovery output is empty or has an unexpected format.' % args.list)
        return 1
    weights, defaults = read_weights(args.weights)
    estimated = {cls: weights.get(cls, defaults[kind_of(cls)] * n) for cls, n in counts.items()}
    known = sum(1 for cls in counts if cls in weights)

    shards = [{'classes': [], 'seconds': 0.0, 'tests': 0} for _ in range(args.shards)]
    for cls in sorted(counts, key=lambda c: (-estimated[c], c)):
        target = min(range(args.shards), key=lambda i: (shards[i]['seconds'], i))
        shards[target]['classes'].append(cls)
        shards[target]['seconds'] += estimated[cls]
        shards[target]['tests'] += counts[cls]

    os.makedirs(args.out, exist_ok=True)
    for index, shard in enumerate(shards, start=1):
        classes = sorted(shard['classes'])
        if classes:
            # `FullyQualifiedName~Ns.Class.` = contains: the trailing dot keeps `FooTests` from also selecting `FooTestsExtra`.
            expression = '|'.join('FullyQualifiedName~' + escape_filter_value(cls) + '.' for cls in classes)
        else:
            expression = 'FullyQualifiedName=ci.shard.has.no.classes'
        with open(os.path.join(args.out, 'shard-%d.filter' % index), 'w', encoding='utf-8', newline='\n') as handle:
            handle.write(expression)
        with open(os.path.join(args.out, 'shard-%d.classes' % index), 'w', encoding='utf-8', newline='\n') as handle:
            handle.write('\n'.join(classes) + ('\n' if classes else ''))

    total_tests = sum(counts.values())
    print('Test shard plan: %d classes (%d with a measured weight), %d test cases, %d shards' % (len(counts), known, total_tests, args.shards))
    for index, shard in enumerate(shards, start=1):
        print('  shard %d: %4d classes, %5d tests, estimated %6.0f s of test time' % (index, len(shard['classes']), shard['tests'], shard['seconds']))
    mean = sum(s['seconds'] for s in shards) / args.shards
    print('  heaviest shard / mean = %.2f' % (max(s['seconds'] for s in shards) / mean if mean else 1.0))
    with open(os.path.join(args.out, 'plan.txt'), 'w', encoding='utf-8', newline='\n') as handle:
        handle.write('classes=%d\ntests=%d\nshards=%d\n' % (len(counts), total_tests, args.shards))
    return 0


def local(tag):
    return tag.rsplit('}', 1)[-1]


def read_trx(path):
    """(results per class, results per outcome, seconds per class) from a TRX file."""
    class_of_test = {}
    results = []
    for _, element in ET.iterparse(path, events=('end',)):
        name = local(element.tag)
        if name == 'UnitTest':
            for child in element:
                if local(child.tag) == 'TestMethod':
                    class_of_test[element.get('id')] = child.get('className')
            element.clear()
        elif name == 'UnitTestResult':
            results.append((element.get('testId'), element.get('duration') or '0:0:0', element.get('outcome')))
            element.clear()
    per_class = {}
    seconds = {}
    outcomes = {}
    for test_id, duration, outcome in results:
        cls = class_of_test.get(test_id, '?')
        per_class[cls] = per_class.get(cls, 0) + 1
        seconds[cls] = seconds.get(cls, 0.0) + parse_duration(duration)
        outcomes[outcome] = outcomes.get(outcome, 0) + 1
    return per_class, outcomes, seconds


def parse_duration(text):
    days = 0
    if '.' in text.split(':')[0]:
        day_text, _, text = text.partition('.')
        days = int(day_text)
    hours, minutes, seconds = text.split(':')
    return days * 86400 + int(hours) * 3600 + int(minutes) * 60 + float(seconds)


def cmd_verify(args):
    with open(os.path.join(args.plan, 'shard-%d.classes' % args.shard), encoding='utf-8') as handle:
        planned = {line.strip() for line in handle if line.strip()}
    if not os.path.exists(args.trx):
        print('::error::Shard %d wrote no TRX file (%s): the test run did not complete.' % (args.shard, args.trx))
        return 1
    per_class, outcomes, _ = read_trx(args.trx)
    ran = set(per_class)
    missing = sorted(planned - ran)
    extra = sorted(ran - planned)
    print('Shard %d: %d results in %d classes (%s); the plan gave it %d classes' % (
        args.shard, sum(per_class.values()), len(ran), ', '.join('%s %d' % kv for kv in sorted(outcomes.items())), len(planned)))
    if extra:
        # Selected by a filter of this shard although planned for another one: it runs twice, harmless, but worth a look.
        print('::warning::%d classes ran in shard %d without being planned for it, e.g. %s' % (len(extra), args.shard, ', '.join(extra[:5])))
    if missing:
        print('::error::%d classes planned for shard %d produced no result (the filter selected nothing?): %s' % (
            len(missing), args.shard, ', '.join(missing[:20])))
        return 1
    return 0


def cmd_weights(args):
    # A class appears in one TRX per run (the shard that ran it): with the TRX files of several runs the weight is the mean,
    # which smooths the run-to-run noise (runner speed, which classes ran at the same time).
    seconds_by_class = {}
    tests_by_class = {}
    runs_by_class = {}
    for path in args.trx:
        per_class, _, seconds = read_trx(path)
        for cls, n in per_class.items():
            tests_by_class[cls] = tests_by_class.get(cls, 0) + n
            seconds_by_class[cls] = seconds_by_class.get(cls, 0.0) + seconds[cls]
            runs_by_class[cls] = runs_by_class.get(cls, 0) + 1
    seconds_by_class = {cls: s / runs_by_class[cls] for cls, s in seconds_by_class.items()}
    tests_by_class = {cls: n / runs_by_class[cls] for cls, n in tests_by_class.items()}
    heavy = {cls: s for cls, s in seconds_by_class.items() if s >= MIN_WEIGHT_TO_LIST}
    out = sys.stdout
    out.write('# Seconds of test time per class of Casazen.Tests, measured in CI (sum of the test durations of the class).\n')
    out.write('# Only the classes that took at least %.1f s are listed: the others cost default_seconds_per_test_<kind> x their tests.\n' % MIN_WEIGHT_TO_LIST)
    out.write('# Used by scripts/ci/test_shards.py to balance the test shards; it never decides what runs. Refresh: see that file.\n')
    for kind in ('unit', 'integration'):
        tail_seconds = sum(s for cls, s in seconds_by_class.items() if cls not in heavy and kind_of(cls) == kind)
        tail_tests = sum(n for cls, n in tests_by_class.items() if cls not in heavy and kind_of(cls) == kind)
        per_test = tail_seconds / tail_tests if tail_tests else DEFAULT_SECONDS_PER_TEST[kind]
        out.write('default_seconds_per_test_%s\t%.4f\n' % (kind, per_test))
    for cls, s in sorted(heavy.items(), key=lambda kv: (-kv[1], kv[0])):
        out.write('%.1f\t%s\n' % (s, cls))
    return 0


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    plan = sub.add_parser('plan')
    plan.add_argument('--list', required=True, help='output of `dotnet test <dll> --list-tests`')
    plan.add_argument('--weights', default='scripts/ci/test-class-weights.tsv')
    plan.add_argument('--shards', type=int, required=True)
    plan.add_argument('--out', required=True)
    plan.set_defaults(func=cmd_plan)
    verify = sub.add_parser('verify')
    verify.add_argument('--plan', required=True)
    verify.add_argument('--shard', type=int, required=True)
    verify.add_argument('--trx', required=True)
    verify.set_defaults(func=cmd_verify)
    weights = sub.add_parser('weights')
    weights.add_argument('--trx', nargs='+', required=True)
    weights.set_defaults(func=cmd_weights)
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
