#!/usr/bin/env python3
"""Split a big class into cohesive partial-class files without touching a member.

The class body is cut at member boundaries only: each member keeps its doc
comment, its attributes and any section banner that introduces it, and moves
verbatim into the part file named in the spec. Everything outside the class body
(other top-level types, records, the class doc comment) stays where it is.

After the split the tool proves the result: the members of every part plus the
members left behind must reassemble, whitespace aside, into the original body.
"""
from __future__ import annotations

import io
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from member_map import find_class, mask, member_name  # noqa: E402


def line_starts(src: str) -> list[int]:
    return [0] + [i + 1 for i, ch in enumerate(src) if ch == '\n']


def attach_comments(src: str, starts: list[int], idx: int) -> int:
    """Walk backwards over doc comments, attributes and section banners."""
    line = idx
    while line > 0:
        prev = src[starts[line - 1]:starts[line]].strip()
        if prev.startswith(('///', '//', '/*', '*')) or (prev.startswith('[') and prev.endswith(']')):
            line -= 1
            continue
        if prev == '':
            # a banner separated by a blank line still introduces the member
            probe = line - 1
            block = []
            while probe > 0:
                text = src[starts[probe - 1]:starts[probe]].strip()
                if text.startswith(('///', '//', '/*', '*')):
                    block.append(probe - 1)
                    probe -= 1
                    continue
                if text == '' and block:
                    probe -= 1
                    continue
                break
            if block:
                return block[-1]
        break
    return line


def members_with_names(src: str, msk: str, cls_open: int, cls_close: int):
    starts = line_starts(src)
    out = []
    pos = cls_open + 1
    while True:
        while pos < cls_close and msk[pos] in ' \t\r\n':
            pos += 1
        if pos >= cls_close:
            break
        idx = bisect_line(starts, pos)
        start = starts[attach_comments(src, starts, idx)]
        depth, i, saw_block, end = 0, pos, False, None
        while i < cls_close:
            ch = msk[i]
            if ch == '{':
                depth += 1
                saw_block = True
            elif ch == '}':
                depth -= 1
                if depth == 0 and saw_block:
                    end = i + 1
                    while end < cls_close and msk[end] in ' \t':
                        end += 1
                    if end < cls_close and msk[end] == ';':
                        end += 1
                        break
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
            raise SystemExit(f'member at offset {pos} has no end')
        out.append((start, end, member_name(src[start:end])))
        pos = end
    return out


def bisect_line(starts: list[int], idx: int) -> int:
    lo, hi = 0, len(starts) - 1
    while lo < hi:
        mid = (lo + hi + 1) // 2
        if starts[mid] <= idx:
            lo = mid
        else:
            hi = mid - 1
    return lo


def split(path: str, class_name: str, parts: dict[str, tuple[list[str], str]]) -> None:
    src = io.open(path, encoding='utf-8').read()
    msk = mask(src)
    cls_open, cls_close = find_class(src, msk, class_name)
    found = members_with_names(src, msk, cls_open, cls_close)
    by_name = {}
    for a, b, name in found:
        by_name.setdefault(name, []).append((a, b))

    decl_match = re.search(r'(?m)^[^\n]*\b(?:class|record)\s+' + re.escape(class_name) + r'\b[^\n]*$', src)
    assert decl_match, class_name
    decl = decl_match.group(0)
    add_partial = lambda text: text if 'partial' in text else re.sub(r'\b(class|record)\b', r'partial \1', text, count=1)
    # the file that keeps the class also keeps its base type / interface list;
    # the part files repeat only "modifiers partial class Name".
    kept_decl = add_partial(decl)
    head = re.match(r'\s*(.*?\b(?:class|record)\s+\w+)', decl).group(1).strip()
    partial_decl = add_partial(head)

    ns_match = re.search(r'(?m)^namespace [\w\.]+;$', src)
    assert ns_match, 'file-scoped namespace expected'
    header = src[:ns_match.end()]

    starts = line_starts(src)
    wanted = []
    for part_path, (names, doc) in parts.items():
        for name in names:
            if isinstance(name, tuple):
                a, b = name
                lo, hi = starts[a - 1], starts[b] if b < len(starts) else len(src)
                covering = [(x, y) for x, y, _ in found if x >= lo - 1 and y <= hi]
                assert covering, f'range {name} covers no whole member'
                for span in covering:
                    wanted.append((span, part_path, doc))
                continue
            assert name in by_name, f'{name} not found in {class_name}'
            assert len(by_name[name]) == 1, f'{name} is ambiguous in {class_name}'
            wanted.append((by_name[name][0], part_path, doc))
    seen, deduped = set(), []
    for span, part_path, doc in wanted:
        if span in seen:
            continue
        seen.add(span)
        deduped.append((span, part_path, doc))
    wanted = deduped

    # cut the moved members out of the body, back to front
    body = src[cls_open + 1:cls_close]
    moved: dict[str, list[str]] = {p: [] for p in parts}
    for (a, b), part_path, _ in sorted(wanted, key=lambda w: -w[0][0]):
        rel_a, rel_b = a - (cls_open + 1), b - (cls_open + 1)
        chunk = body[rel_a:rel_b]
        moved[part_path].insert(0, chunk)
        body = body[:rel_a] + body[rel_b:]
        seam = body[rel_a:]
        lead = len(seam) - len(seam.lstrip('\n'))
        if lead >= 3:
            body = body[:rel_a] + '\n\n' + seam.lstrip('\n')

    kept = src[:cls_open + 1] + body + src[cls_close:]
    kept = kept.replace(decl, kept_decl, 1)
    # a section banner whose members all moved away would be left dangling
    while True:
        cleaned = re.sub(r'\n[ \t]*// -{4,}[^\n]*\n(?:[ \t]*\n)*(?=[ \t]*\})', '\n', kept)
        if cleaned == kept:
            break
        kept = cleaned
    kept = re.sub(r'\n(?:[ \t]*\n)+(?=[ \t]*\})', '\n', kept)
    io.open(path, 'w', encoding='utf-8').write(kept)

    for part_path, (names, doc) in parts.items():
        text = header + '\n\n' + doc.strip() + '\n' + partial_decl.rstrip() + '\n{\n'
        text += ''.join(chunk.rstrip('\n') + '\n\n' for chunk in moved[part_path]).rstrip('\n') + '\n}\n'
        io.open(part_path, 'w', encoding='utf-8').write(text)
        print(f'  wrote {part_path} ({len(names)} members, {len(text.splitlines())} lines)')

    # proof: compare every written file against what the original source says it
    # must contain (whitespace aside), which catches both loss and duplication.
    strip = lambda t: re.sub(r'\s+', '', t)
    original_body = src[cls_open + 1:cls_close]
    moved_rel = sorted((a - (cls_open + 1), b - (cls_open + 1), part) for (a, b), part, _ in wanted)
    expected_kept = original_body
    for a, b, _ in reversed(moved_rel):
        expected_kept = expected_kept[:a] + expected_kept[b:]
    expected_parts: dict[str, str] = {}
    for a, b, part in moved_rel:
        expected_parts[part] = expected_parts.get(part, '') + original_body[a:b]

    kept_now = io.open(path, encoding='utf-8').read()
    k_open, k_close = find_class(kept_now, mask(kept_now), class_name)
    assert strip(kept_now[k_open + 1:k_close]) == strip(expected_kept), f'{path}: kept body differs'
    for part_path in parts:
        text = io.open(part_path, encoding='utf-8').read()
        p_open, p_close = find_class(text, mask(text), class_name)
        assert strip(text[p_open + 1:p_close]) == strip(expected_parts[part_path]), f'{part_path}: body differs'
        assert 'partial' in text and text.rstrip().endswith('}'), part_path
    for f in (path, *parts):
        t = io.open(f, encoding='utf-8').read()
        assert t.count('{') == t.count('}'), f'unbalanced braces in {f}'
    print(f'  {path}: {len(found)} members -> kept {len(kept_now.splitlines())} lines '
          f'+ {len(parts)} parts, content verified')


# The specs below were applied on 2026-10-23 (ImportService, UsbCopyService,
# DetailsViewModel, ManagementViewModel). A spec maps a source file to its class
# and then to part files; each part lists member names or (first_line, last_line)
# ranges of the ORIGINAL file, plus the doc comment the part file gets.
SPECS: dict[str, tuple[str, dict[str, tuple[list, str]]]] = {}


def main() -> int:
    for path, (class_name, parts) in SPECS.items():
        print(path)
        split(path, class_name, parts)
    return 0


if __name__ == '__main__':
    sys.exit(main())
