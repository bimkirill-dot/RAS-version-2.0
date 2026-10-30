"""Хук после Edit / Write: проверка правленого файла — окончания строк, BOM, невидимые знаки (CLAUDE.md, «Стиль»).
Замечания — в stderr с кодом 2: Claude Code возвращает их модели, и она исправляет файл сразу.
"""
import json
import os
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
sys.path.insert(0, os.path.join(ROOT, '.claude', 'skills', 'finish-change'))
from check_files import check  # noqa: E402


def main():
    try:
        data = json.load(sys.stdin)
    except ValueError:
        return 0
    path = (data.get('tool_input') or {}).get('file_path') or ''
    if not path or not os.path.isfile(path):
        return 0
    full = os.path.normcase(os.path.abspath(path))
    # только файлы проекта: рабочие скрипты в scratchpad проверять незачем
    if not full.startswith(os.path.normcase(ROOT) + os.sep):
        return 0
    problems = check(path)
    if not problems:
        return 0
    sys.stderr.reconfigure(encoding='utf-8')
    rel = os.path.relpath(path, ROOT)
    sys.stderr.write('Файл %s — исправить сейчас (CLAUDE.md, «Стиль»):\n%s\n' % (rel, '\n'.join('- ' + p for p in problems)))
    return 2


if __name__ == '__main__':
    sys.exit(main())
