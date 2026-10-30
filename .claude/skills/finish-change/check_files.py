"""Проверка изменённых файлов перед коммитом: окончания строк, BOM, невидимые управляющие знаки (CLAUDE.md, «Стиль»).

python check_files.py            — изменённые и новые файлы рабочей папки
python check_files.py <файл>...  — указанные файлы
"""
import os
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..'))
CRLF = ('.cmd', '.ps1', '.sln')
LF = ('.cs', '.csproj', '.props', '.xaml', '.json', '.md', '.razor', '.css', '.js', '.py', '.txt', '.svg', '.html')
BOM = b'\xef\xbb\xbf'
# пробел нулевой ширины и BOM внутри текста — не видны в редакторе
INVISIBLE = (chr(0x200B), chr(0xFEFF))


def changed():
    out = subprocess.run(['git', '-C', ROOT, 'status', '--porcelain', '-uall'], capture_output=True).stdout.decode('utf-8')
    files = []
    for line in out.splitlines():
        path = line[3:].split(' -> ')[-1].strip().strip('"')
        if line[:2].strip() != 'D' and os.path.isfile(os.path.join(ROOT, path)):
            files.append(path)
    return files


def check(path):
    full = os.path.abspath(path) if os.path.exists(path) else os.path.join(ROOT, path)
    ext = os.path.splitext(full)[1].lower()
    if ext not in CRLF + LF:
        return []
    b = open(full, 'rb').read()
    problems = []
    crlf = b.count(b'\r\n')
    lf = b.count(b'\n') - crlf
    if ext in CRLF and lf:
        problems.append('нужны CRLF, а %d строк — LF' % lf)
    if ext in LF and crlf:
        problems.append('нужны LF, а %d строк — CRLF' % crlf)
    if ext == '.ps1' and not b.startswith(BOM):
        problems.append('.ps1 без BOM — Windows PowerShell 5.1 исказит русский текст')
    if ext == '.cmd' and b.startswith(BOM):
        problems.append('.cmd с BOM — cmd.exe споткнётся о первую строку')
    try:
        text = b.decode('utf-8-sig')
    except UnicodeDecodeError as e:
        return problems + ['не UTF-8: %s' % e]
    for n, line in enumerate(text.split('\n'), 1):
        bad = [c for c in line.rstrip('\r') if ord(c) < 32 and c != '\t' or c in INVISIBLE]
        if bad:
            problems.append('строка %d: управляющие знаки %s' % (n, sorted({'U+%04X' % ord(c) for c in bad})))
    return problems


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    files = sys.argv[1:] or changed()
    bad = 0
    for f in files:
        for p in check(f):
            print('%s: %s' % (f, p))
            bad += 1
    print('проверено файлов: %d, замечаний: %d' % (len(files), bad))
    sys.exit(1 if bad else 0)
