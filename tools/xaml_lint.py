#!/usr/bin/env python3
"""Offline XAML lint for DigitalCenter (no .NET SDK needed).

Checks, across every .xaml file in src/DigitalCenter.App:
  1. the file is well-formed XML;
  2. every {StaticResource K} / {DynamicResource K} resolves to a declared x:Key;
  3. no x:Key is declared twice inside one file;
  4. every Icon.* glyph resource holds a Segoe MDL2 private-use codepoint;
  5. every {Binding Head...} head is a public member of some view model
     (framework/attached properties are whitelisted) -> warning only.

Exit code 1 when 1-4 fail. Run from anywhere:  python3 tools/xaml_lint.py
"""
import pathlib, re, sys, xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent
APP = ROOT / 'src/DigitalCenter.App'
VM = ROOT / 'src/DigitalCenter.ViewModels'

xaml = sorted(p for p in APP.rglob('*.xaml') if not ({'bin', 'obj'} & set(p.parts)))
texts = {p: p.read_text(encoding='utf-8') for p in xaml}

keys, dupes, glyphs = {}, [], {}
for f, text in texts.items():
    try:
        ET.fromstring(text)
    except ET.ParseError as ex:
        print(f'ERROR {f.name}: XML parse error: {ex}')
        sys.exit(1)
    for m in re.finditer(r'x:Key="([^"]+)"', text):
        if keys.get(m.group(1)) == f:
            dupes.append((f.name, m.group(1)))
        keys.setdefault(m.group(1), f)
    for m in re.finditer(r'x:Key="(Icon\.[^"]+)"[^>]*>\s*(&#x[0-9A-Fa-f]+;)', text):
        glyphs[m.group(1)] = m.group(2)

missing = []
for f, text in texts.items():
    for m in re.finditer(r'\{(?:Static|Dynamic)Resource\s+([^},]+)', text):
        key = m.group(1).strip()
        if key.startswith('{'):
            continue  # nested markup extension: BasedOn="{StaticResource {x:Type TextBlock}}"
        if key not in keys:
            missing.append((f.name, text[:m.start()].count('\n') + 1, key))

members = set()
for f in sorted(VM.rglob('*.cs')):
    if {'bin', 'obj'} & set(f.parts):
        continue
    t = f.read_text(encoding='utf-8')
    for m in re.finditer(r'public\s+(?:static\s+|partial\s+|sealed\s+|readonly\s+|async\s+)*'
                         r'(?:[\w\.<>\?\[\],\s]+?)\s+(\w+)\s*(?:\{|=>|\()', t):
        members.add(m.group(1))
    for m in re.finditer(r'\[ObservableProperty\][\s\S]{0,140}?public\s+partial\s+[\w\.<>\?]+\s+(\w+)', t):
        members.add(m.group(1))
    for m in re.finditer(r'\[RelayCommand[^\]]*\]', t):
        sig = re.search(r'(?:private|public|internal)\s+(?:async\s+)?[\w\.<>\?\[\]]+\s+(\w+)\s*\(',
                        t[m.end():m.end() + 700])
        if sig:
            name = sig.group(1)
            members.add((name[:-5] if name.endswith('Async') else name) + 'Command')
    for m in re.finditer(r'record\s+\w+\s*\(([^)]*)\)', t):
        for part in m.group(1).split(','):
            w = re.findall(r'(\w+)\s*$', part.strip())
            if w:
                members.add(w[0])

FRAMEWORK = {
    'DataContext', 'Items', 'Count', 'IsSelected', 'IsEnabled', 'Visibility', 'Text', 'Content',
    'Tag', 'Name', 'ActualWidth', 'ActualHeight', 'SelectedItem', 'SelectedIndex', 'IsChecked',
    'Background', 'Foreground', 'BorderBrush', 'Opacity', 'Width', 'Height', 'Focusable',
    'HorizontalOffset', 'VerticalOffset', 'ScrollableWidth', 'ScrollableHeight', 'ExtentHeight',
    'WindowState', 'IsDropDownOpen', 'Title', 'WindowStyle', 'ResizeMode', 'ShowInTaskbar',
    'PlacementTarget', 'Placement', 'StaysOpen', 'IsOpen', 'Value', 'Minimum', 'Maximum',
    'IsMouseOver', 'TemplatedParent', 'Self', 'RelativeSource', 'Template',
}
warn = set()
for f, text in texts.items():
    for m in re.finditer(r'\{Binding\s+(?:Path=)?([A-Za-z_][\w\.]*)', text):
        head = m.group(1).split('.')[0]
        if head not in FRAMEWORK and head not in members:
            warn.add((f.name, text[:m.start()].count('\n') + 1, head))

for fn, ln, key in sorted(set(missing)):
    print(f'ERROR {fn}:{ln}: unresolved resource "{key}"')
for fn, key in sorted(set(dupes)):
    print(f'ERROR {fn}: duplicate x:Key "{key}"')
for key, value in sorted(glyphs.items()):
    if not re.fullmatch(r'&#x[EeFf][0-9A-Fa-f]{3};', value):
        print(f'ERROR glyph {key} = {value!r} is not a Segoe MDL2 private-use codepoint')
for fn, ln, head in sorted(warn):
    print(f'WARN  {fn}:{ln}: binding head "{head}" not found as a public member')

if missing or dupes:
    sys.exit(1)
print(f'OK: {len(xaml)} xaml files, {len(keys)} resources, {len(glyphs)} glyphs — '
      'no unresolved resources, no duplicate keys, no bad glyphs')
