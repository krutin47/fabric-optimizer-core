"""Compare two bench CSVs under the approved cheapest-first ranking.

Usage: compare.py base.csv new.csv
Prints, per job size, how often the new build's best plan ranks better / equal / worse, plus mean run time.
"""
import csv, sys, math
from decimal import Decimal
from collections import defaultdict


def load(path):
    rows = {}
    with open(path) as f:
        for r in csv.DictReader(f):
            rows[(int(r['instances']), int(r['seed']))] = r
    return rows


def key(r):
    d = lambda v: Decimal(v) if v not in ('', None) else Decimal(0)
    return (d(r['cost']), math.floor(d(r['scrap_m2']) / Decimal('0.01')), -d(r['largest_keep_m2']), d(r['used_mm']),
            int(r['cuts'] or 0), d(r['purchased_m2']), d(r['stock_m2']))


TIER = ['cost', 'scrap', 'largest keep', 'roll length', 'cuts', 'purchased', 'stock']
base, new = load(sys.argv[1]), load(sys.argv[2])
by = defaultdict(lambda: {'better': 0, 'equal': 0, 'worse': 0, 'tb': 0, 'tn': 0, 'n': 0, 'tiers': defaultdict(int)})
for k in sorted(base):
    if k not in new:
        continue
    b, n = key(base[k]), key(new[k])
    s = by[k[0]]
    s['n'] += 1
    s['tb'] += int(base[k]['ms'])
    s['tn'] += int(new[k]['ms'])
    if n < b:
        s['better'] += 1
    elif n > b:
        s['worse'] += 1
    else:
        s['equal'] += 1
    if n != b:
        first = next(i for i in range(len(b)) if b[i] != n[i])
        s['tiers'][('+' if n < b else '-') + TIER[first]] += 1
print(f"{'size':>5} {'runs':>4} {'better':>6} {'equal':>5} {'worse':>5} {'base ms':>8} {'new ms':>7}  decided by")
tot = {'better': 0, 'equal': 0, 'worse': 0}
for size in sorted(by):
    s = by[size]
    for t in tot:
        tot[t] += s[t]
    tiers = ', '.join(f"{k}×{v}" for k, v in sorted(s['tiers'].items()))
    print(f"{size:>5} {s['n']:>4} {s['better']:>6} {s['equal']:>5} {s['worse']:>5} {s['tb'] // s['n']:>8} {s['tn'] // s['n']:>7}  {tiers}")
print(f"total: better {tot['better']}, equal {tot['equal']}, worse {tot['worse']}")
