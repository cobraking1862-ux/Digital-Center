#!/usr/bin/env python3
"""Offline C# literal lint: flags string/char literals left open at end of line.

That is the exact signature of CS1010 "Newline in constant" / CS1012 "Too many
characters in character literal", which is easy to introduce by accident when
code is generated or patched by tooling. Understands line and block comments,
verbatim, interpolated and interpolated-verbatim strings, and C# 11 raw string
literals (three or more quotes).

Exit code 1 when a real problem is found. Run: python3 tools/csharp_literal_lint.py
"""
import pathlib, sys

ROOT = pathlib.Path(__file__).resolve().parent.parent


def scan(text):
    problems = []
    i, n, line = 0, len(text), 1
    while i < n:
        c = text[i]
        if c == '\n':
            line += 1
            i += 1
            continue
        if c == '/' and text[i:i + 2] == '//':
            i = text.find('\n', i)
            if i < 0:
                break
            continue
        if c == '/' and text[i:i + 2] == '/*':
            end = text.find('*/', i + 2)
            end = n if end < 0 else end + 2
            line += text.count('\n', i, end)
            i = end
            continue
        # Any quote run of 3+ (optionally prefixed by $ and/or @) is a C# 11 raw
        # string literal, which may span lines legally.
        if c in '"$@':
            j = i
            while j < n and text[j] in '$@':
                j += 1
            run = 0
            while j + run < n and text[j + run] == '"':
                run += 1
            if run >= 3:
                close = text.find('"' * run, j + run)
                if close < 0:
                    problems.append((line, 'raw string'))
                    break
                line += text.count('\n', i, close)
                i = close + run
                continue

        if text[i:i + 2] in ('@"', '$"') or text[i:i + 3] in ('$@"', '@$"'):
            verbatim = '@' in text[i:i + 2] or text[i:i + 3] in ('$@"', '@$"')
            i = text.index('"', i) + 1
            while i < n:
                if text[i] == '"':
                    if verbatim and text[i:i + 2] == '""':
                        i += 2
                        continue
                    break
                if text[i] == '\n':
                    line += 1
                i += 1
            i += 1
            continue
        if c == '"':
            start, i, closed = line, i + 1, False
            while i < n:
                if text[i] == '\\':
                    i += 2
                    continue
                if text[i] == '"':
                    closed, i = True, i + 1
                    break
                if text[i] == '\n':
                    break
                i += 1
            if not closed:
                problems.append((start, 'string'))
            continue
        if c == "'":
            start, i, closed = line, i + 1, False
            while i < n:
                if text[i] == '\\':
                    i += 2
                    continue
                if text[i] == "'":
                    closed, i = True, i + 1
                    break
                if text[i] == '\n':
                    break
                i += 1
            if not closed:
                problems.append((start, 'char'))
            continue
        i += 1
    return problems


bad = 0
for f in sorted(ROOT.rglob('*.cs')):
    if {'bin', 'obj', '.git'} & set(f.parts):
        continue
    for ln, kind in scan(f.read_text(encoding='utf-8')):
        print(f'{f.relative_to(ROOT)}:{ln}: unterminated {kind} literal at end of line')
        bad += 1
print('unterminated literals:', bad)
sys.exit(1 if bad else 0)
