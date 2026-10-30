"""Хук перед командой Bash / PowerShell: то, что ИИ в этом проекте не делает сам (CLAUDE.md, «Работа в команде»).
Запрет — stderr с кодом 2: команда не выполняется, модель получает причину.

- git push — выгружает человек сам;
- выпуск (revit.cmd / navisworks.cmd / Release.ps1 / Navisworks.ps1: publish, promote, rollback, setup) — запускает человек сам;
- запись в базу сайта коллизий на этом компьютере — только чтение (mode=ro);
- коммит и слияние в main — только у ведущего разработчика; остальные работают в своей ветке.
"""
import json
import os
import re
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
# ведущий разработчик — логин Windows (%USERNAME%); он же выпускает плагин и сливает ветки
LEAD = 'Poletayev_BIM'

RELEASE = re.compile(r'(revit|navisworks)\.cmd["\']?\s+(publish|promote|rollback|setup)\b|'
                     r'(Release|Navisworks)\.ps1["\']?.*-Action\s+["\']?(publish|promote|rollback|setup)\b', re.I)
SITE_DB = re.compile(r'RAS-Clash Analysis[\\/]+Site[\\/]+clashes\.db', re.I)


def branch():
    r = subprocess.run(['git', '-C', ROOT, 'rev-parse', '--abbrev-ref', 'HEAD'], capture_output=True, text=True)
    return r.stdout.strip()


def reason(cmd):
    if re.search(r'\bgit\s+push\b', cmd):
        return 'git push делает человек сам — подскажите ему команду.'
    if RELEASE.search(cmd):
        return 'Выпуск запускает человек сам (по его решению) — подскажите команду и текст «что нового».'
    if SITE_DB.search(cmd) and 'mode=ro' not in cmd:
        return 'База сайта коллизий на этом компьютере — только чтение (mode=ro); пробовать — на копии (навык site-check).'
    lead = os.environ.get('USERNAME', '').lower() == LEAD.lower()
    if not lead and re.search(r'\bgit\s+(commit|merge|rebase|cherry-pick)\b', cmd) and branch() in ('main', 'master'):
        return ('Сейчас ветка main: коммиты и слияния в main делает только ведущий разработчик. '
                'Создайте свою ветку (навык start-task) и работайте в ней.')
    return None


def main():
    try:
        data = json.load(sys.stdin)
    except ValueError:
        return 0
    cmd = (data.get('tool_input') or {}).get('command') or ''
    why = reason(cmd)
    if not why:
        return 0
    sys.stderr.reconfigure(encoding='utf-8')
    sys.stderr.write('Не выполнено: ' + why + '\n')
    return 2


if __name__ == '__main__':
    sys.exit(main())
