#!/usr/bin/env python3
"""Report members declared in src/ that nothing else in the solution references.

A member is "dead" when its name never appears outside its own declaration in
any .cs or .xaml file under src/ and tests/. Generated partial hooks
(OnSomethingChanged), RelayCommand methods whose generated command IS used, EF
migration files and XAML code-behind event handlers declared in markup are all
excluded, because they are referenced by generators or by the XAML parser.
"""
from __future__ import annotations

import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

DECL = re.compile(
    r'^\s*(?:\[[^\]]+\]\s*)*'
    r'(?:public|internal|protected|private|static|sealed|abstract|virtual|override|async|readonly|required|partial|\s)*'
    r'(?:[\w\.<>\[\]\?,\s]+?)\s+(?P<name>[A-Z]\w+)\s*(?:\{(?P<acc>get|set)|\(|=>|;)',
)

SKIP_DIRS = {'obj', 'bin', 'Migrations'}


def sources():
    for base in ('src', 'tests'):
        for dirpath, dirnames, filenames in os.walk(os.path.join(ROOT, base)):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for name in filenames:
                if name.endswith(('.cs', '.xaml')):
                    yield os.path.join(dirpath, name)


def main() -> int:
    files = {p: io.open(p, encoding='utf-8', errors='replace').read() for p in sources()}
    decls: dict[str, list[tuple[str, int]]] = {}

    for path, text in files.items():
        if not path.endswith('.cs'):
            continue
        lines = text.split('\n')
        for idx, line in enumerate(lines, start=1):
            stripped = line.strip()
            if stripped.startswith(('//', '///', '*', '/*')):
                continue
            m = DECL.match(line)
            if not m:
                continue
            name = m.group('name')
            if name in {'if', 'while', 'for', 'foreach', 'switch', 'catch', 'using', 'return', 'new', 'get', 'set'}:
                continue
            if re.match(r'^\s*(public|internal|protected|private|static|sealed|abstract|virtual|override|async|readonly|required|partial)', line) is None:
                continue
            # constructor / type declarations
            if re.search(r'\b(class|record|struct|interface|enum|namespace)\b', line):
                continue
            decls.setdefault(name, []).append((os.path.relpath(path, ROOT), idx))

    dead = []
    for name, sites in sorted(decls.items()):
        if re.fullmatch(r'On[A-Z]\w*Changed', name):
            continue  # invoked by the CommunityToolkit source generator
        if name.startswith(('Execute', 'Relay')):
            continue
        pattern = re.compile(r'(?<![A-Za-z0-9_])' + re.escape(name) + r'(?![A-Za-z0-9_])')
        hits = 0
        for path, text in files.items():
            hits += len(pattern.findall(text))
            if hits > len(sites):
                break
        # a command method counts as used when its generated command is bound
        cmd = name[:-5] + 'Command' if name.endswith('Async') else name + 'Command'
        cmd_hits = 0
        if name != cmd:
            cmdpat = re.compile(r'(?<![A-Za-z0-9_])' + re.escape(cmd) + r'(?![A-Za-z0-9_])')
            for path, text in files.items():
                cmd_hits += len(cmdpat.findall(text))
        if hits <= len(sites) and cmd_hits == 0:
            dead.append((name, sites, hits))

    print(f'scanned {len(files)} files, {len(decls)} declared member names')
    print(f'unreferenced: {len(dead)}')
    for name, sites, hits in dead:
        where = ', '.join(f'{p}:{ln}' for p, ln in sites[:3])
        print(f'  {name:38s} hits={hits}  {where}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
