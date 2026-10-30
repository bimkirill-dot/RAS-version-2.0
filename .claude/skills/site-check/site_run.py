"""Пробный сайт коллизий: свежая публикация во временную папку и запуск на своих данных и своём порту.

python site_run.py start <папка данных> [--port 5099] [--scan 0]
python site_run.py stop

Публикация — рядом с папкой данных (<папка>/../sitecheck-app), копии базы — <папка>/../sitecheck-backups.
Останавливаются только пробные сайты (по пути программы), сайт разработчика не трогается.
"""
import argparse
import os
import subprocess
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..'))
PROJECT = os.path.join(ROOT, 'src', 'Modules', 'RAS-Clash Analysis', 'Site')
EXE = 'RASv2.ClashAnalysis.Site.exe'


def stop():
    # только процессы из папок sitecheck-app — рабочий сайт разработчика живёт в другой папке
    ps = ("Get-Process RASv2.ClashAnalysis.Site -ErrorAction SilentlyContinue | "
          "Where-Object { $_.Path -like '*sitecheck-app*' } | Stop-Process -Force")
    subprocess.run(['powershell', '-NoProfile', '-Command', ps])
    time.sleep(1)


def start(data, port, scan):
    data = os.path.abspath(data)
    base = os.path.dirname(data)
    app = os.path.join(base, 'sitecheck-app')
    backups = os.path.join(base, 'sitecheck-backups')
    stop()
    r = subprocess.run(['dotnet', 'publish', '-c', 'Release', '-o', app, '-nologo', '-v', 'q'], cwd=PROJECT,
                       capture_output=True, text=True, encoding='utf-8', errors='replace')
    errors = sorted({l.split('[')[0].strip() for l in r.stdout.splitlines() if ' error ' in l or 'warning CS' in l})
    for e in errors:
        print(e)
    if r.returncode != 0:
        sys.exit('публикация не удалась')
    os.makedirs(data, exist_ok=True)
    env = dict(os.environ, Site__DataFolder=data, Site__BackupFolder=backups, Site__ScanMinutes=str(scan),
               Urls='http://localhost:%d' % port)
    log_path = os.path.join(data, 'run.log')
    log = open(log_path, 'w', encoding='utf-8')
    subprocess.Popen([os.path.join(app, EXE)], cwd=app, env=env, stdout=log, stderr=subprocess.STDOUT,
                     creationflags=subprocess.CREATE_NO_WINDOW | subprocess.CREATE_NEW_PROCESS_GROUP)  # консоль нужна: сайт ставит её кодировку
    for _ in range(60):
        time.sleep(0.5)
        text = open(log_path, encoding='utf-8', errors='replace').read()
        if 'Now listening' in text:
            print('сайт: http://localhost:%d  (журнал: %s)' % (port, log_path))
            return
        if 'Unhandled exception' in text or 'fail:' in text:
            break
    print(open(log_path, encoding='utf-8', errors='replace').read()[-3000:])
    sys.exit('сайт не запустился')


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    p = argparse.ArgumentParser()
    p.add_argument('action', choices=['start', 'stop'])
    p.add_argument('data', nargs='?')
    p.add_argument('--port', type=int, default=5099)
    p.add_argument('--scan', type=int, default=0, help='минуты между разбором папок с отчётами; 0 — не разбирать')
    a = p.parse_args()
    if a.action == 'stop':
        stop()
    else:
        if not a.data:
            sys.exit('нужна папка данных')
        start(a.data, a.port, a.scan)
