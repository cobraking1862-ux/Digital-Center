#!/usr/bin/env python3
"""List the top-level members of a class so big files can be split into partials.

Usage: python3 tools/_members.py <file.cs> [ClassName]
Prints "start-end  name" line ranges (1-based, inclusive) for every member of
the class body, including the doc comments / attributes that introduce it.
Comments and string literals are masked before counting braces, so interpolated
braces and braces inside text never confuse the scan.
"""
from __future__ import annotations

import io
import re
import sys


def mask(src: str) -> str:
    """Blank out comments and string/char literal contents (newlines kept)."""
    out = list(src)
    i, n = 0, len(src)

    def blank(a: int, b: int) -> None:
        for k in range(a, b):
            if out[k] not in '\r\n':
                out[k] = ' '

    while i < n:
        c = src[i]
        two = src[i:i + 2]
        three = src[i:i + 3]
        if two == '//':
            j = src.find('\n', i)
            j = n if j < 0 else j
            blank(i, j)
            i = j
            continue
        if two == '/*':
            j = src.find('*/', i + 2)
            j = n if j < 0 else j + 2
            blank(i, j)
            i = j
            continue
        prefixes = ''
        while src[i] in '$@':
            prefixes += src[i]
            i += 1
        if i < n and src[i] == '"':
            quotes = 0
            k = i
            while k < n and src[k] == '"':
                quotes += 1
                k += 1
            if quotes >= 3:
                close = src.find('"' * quotes, k)
                j = n if close < 0 else close + quotes
            elif '@' in prefixes:
                j = k
                while j < n:
                    if src[j] == '"':
                        if j + 1 < n and src[j + 1] == '"':
                            j += 2
                            continue
                        j += 1
                        break
                    j += 1
            else:
                j = k
                while j < n:
                    if src[j] == '\\':
                        j += 2
                        continue
                    if src[j] in '"\n':
                        j += 1
                        break
                    j += 1
            blank(i, j)
            i = j
            continue
        if i < n and src[i] == "'":
            j = i + 1
            while j < n:
                if src[j] == '\\':
                    j += 2
                    continue
                if src[j] in "'\n":
                    j += 1
                    break
                j += 1
            blank(i, j)
            i = j
            continue
        i += 1
    return ''.join(out)


def find_class(src: str, msk: str, name: str) -> tuple[int, int]:
    m = re.search(r'(?m)^[^\S\n]*(?:\[[^\]]*\][^\S\n]*)*(?:public|internal|file|abstract|sealed|static|partial|\s)*'
                  r'(?:class|record)\s+' + re.escape(name) + r'\b', src)
    if not m:
        raise SystemExit(f'class {name} not found')
    open_idx = msk.index('{', m.end())
    depth, i = 0, open_idx
    while i < len(msk):
        if msk[i] == '{':
            depth += 1
        elif msk[i] == '}':
            depth -= 1
            if depth == 0:
                return open_idx, i
        i += 1
    raise SystemExit('unbalanced class body')


def member_name(text: str) -> str:
    """Name a member from its declaration line (comments and attributes skipped)."""
    body = text
    while body:
        nl = body.find('\n')
        first = (body[:nl] if nl >= 0 else body).strip()
        if first.startswith(('///', '//', '/*', '*')) or not first:
            body = body[nl + 1:] if nl >= 0 else ''
            continue
        if first.startswith('[') and first.endswith(']'):
            body = body[nl + 1:] if nl >= 0 else ''
            continue
        break
    if not body.strip():
        return '???'
    decl = body.strip().split('\n')[0]
    m = re.search(r'\b(?:class|record|struct|enum|interface)\s+(\w+)', decl)
    if m:
        return m.group(1)
    for pattern in (r'(\w+)\s*\(', r'(\w+)\s*(?:=>|\{)', r'(\w+)\s*;', r'(\w+)\s*='):
        m = re.search(pattern, decl)
        if m:
            return m.group(1)
    return decl.split()[-1]


def spans(src: str, msk: str, cls_open: int, cls_close: int) -> list[tuple[int, int]]:
    lines_start = [0] + [i + 1 for i, ch in enumerate(src) if ch == '\n']

    def line_of(idx: int) -> int:
        lo, hi = 0, len(lines_start) - 1
        while lo < hi:
            mid = (lo + hi + 1) // 2
            if lines_start[mid] <= idx:
                lo = mid
            else:
                hi = mid - 1
        return lo

    out = []
    pos = cls_open + 1
    while True:
        while pos < cls_close and msk[pos] in ' \t\r\n':
            pos += 1
        if pos >= cls_close:
            break
        # attach preceding doc comments / attributes
        line = line_of(pos)
        while line > 0:
            prev = src[lines_start[line - 1]:lines_start[line]].strip()
            if prev.startswith(('///', '//', '/*', '*')) or (prev.startswith('[') and prev.endswith(']')):
                line -= 1
                continue
            break
        start = lines_start[line]
        # scan to the end of the member
        depth, i, saw_block = 0, pos, False
        end = None
        while i < cls_close:
            ch = msk[i]
            if ch == '{':
                depth += 1
                saw_block = True
            elif ch == '}':
                depth -= 1
                if depth == 0 and saw_block:
                    end = i + 1
                    # a property can carry an initializer after its accessors
                    k = end
                    while k < cls_close and msk[k] in ' \t\r\n':
                        k += 1
                    if k < cls_close and msk[k] == '=':
                        saw_block = False
                        i = k
                        continue
                    break
            elif ch == ';' and depth == 0:
                end = i + 1
                break
            i += 1
        if end is None:
            raise SystemExit(f'cannot find the end of the member at offset {pos}')
        out.append((start, end))
        pos = end
    return out


def main() -> int:
    path = sys.argv[1]
    src = io.open(path, encoding='utf-8').read()
    msk = mask(src)
    names = re.findall(r'(?m)^[^\S\n]*(?:public|internal|file|abstract|sealed|static|partial|\s)*class\s+(\w+)', src)
    wanted = sys.argv[2] if len(sys.argv) > 2 else names[0]
    cls_open, cls_close = find_class(src, msk, wanted)
    print(f'{path}: class {wanted}  (types in file: {", ".join(names)})')
    result = []
    for a, b in spans(src, msk, cls_open, cls_close):
        text = src[a:b]
        ln_a = src.count('\n', 0, a) + 1
        ln_b = src.count('\n', 0, b) + (0 if src[b - 1] == '\n' else 1)
        result.append((ln_a, ln_b, member_name(text)))
        print(f'  {ln_a:5d}-{ln_b:<5d} {member_name(text)}')
    # round-trip proof: the spans plus whatever sits between them (section
    # banners and blank lines) must reproduce the class body exactly.
    found = spans(src, msk, cls_open, cls_close)
    gaps = []
    last = cls_open + 1
    for a, b in found:
        gaps.append(src[last:a])
        last = b
    gaps.append(src[last:cls_close])
    rebuilt = ''.join(src[a:b] for a, b in found)
    original = src[cls_open + 1:cls_close]
    stripped = lambda t: re.sub(r'\s+', '', t)
    interleaved = []
    last = cls_open + 1
    for a, b in found:
        interleaved.append(src[last:a])
        interleaved.append(src[a:b])
        last = b
    interleaved.append(src[last:cls_close])
    members_ok = stripped(''.join(interleaved)) == stripped(original)
    print(f'  round-trip: {"OK" if members_ok else "CHECK"} '
          f'({len(result)} members, {sum(1 for g in gaps if g.strip())} banner/blank gap(s))')
    return 0


if __name__ == '__main__':
    sys.exit(main())
