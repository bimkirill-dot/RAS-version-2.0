"""Копия базы сайта коллизий для пробного сайта: его база читается только на чтение (mode=ro), всё остальное — в копии.

python site_data.py copy <папка> [--password secret1]
"""
import argparse
import base64
import hashlib
import os
import shutil
import sqlite3
import sys

REAL = os.path.expandvars(r'%LocalAppData%\RAS_v_2.0\RAS-Clash Analysis\Site\clashes.db')


def password_hash(password):
    # тот же вид, что у сайта (SiteAuth): pbkdf2-sha256$итерации$соль$хэш
    salt = os.urandom(16)
    digest = hashlib.pbkdf2_hmac('sha256', password.encode('utf-8'), salt, 120000, 32)
    return 'pbkdf2-sha256$120000$' + base64.b64encode(salt).decode() + '$' + base64.b64encode(digest).decode()


def copy(folder, password):
    if os.path.normcase(os.path.abspath(folder)) == os.path.normcase(os.path.dirname(REAL)):
        sys.exit('Это папка его сайта — копию нужно класть во временную папку.')
    if not os.path.isfile(REAL):
        # сайт коллизий стоит только у ведущего разработчика; у остальных — пробный сайт на пустой папке
        sys.exit('Базы сайта на этом компьютере нет (%s). Запустите пробный сайт на пустой папке данных — он создаст базу сам.' % REAL)
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    dst = os.path.join(folder, 'clashes.db')
    src = sqlite3.connect('file:' + REAL + '?mode=ro', uri=True)
    db = sqlite3.connect(dst)
    src.backup(db)
    src.close()
    q = lambda sql: db.execute(sql).fetchall()
    print('проекты:', q("SELECT Id, Name FROM Projects"))
    print('отделы:', q("SELECT Id, Name FROM Departments"))
    print('люди:', q("SELECT Login, Name, Role, DepartmentId, Active FROM Users"))
    print('отчёты по проектам (дней, с, по):', q("SELECT ProjectId, COUNT(DISTINCT substr(ReportDate,1,10)), MIN(ReportDate), MAX(ReportDate) FROM Reports GROUP BY ProjectId"))
    print('отметки статусов по проектам (всего, дней):', q("""SELECT cl.ProjectId, COUNT(*), COUNT(DISTINCT substr(ev.Time,1,10))
        FROM Events ev JOIN Clashes cl ON cl.Id = ev.ClashId WHERE ev.Kind = 'status' AND ev.NewState IS NOT NULL GROUP BY cl.ProjectId"""))
    if password:
        db.execute("UPDATE Users SET PasswordHash = ?, Active = 1", (password_hash(password),))
        print('пароль всех учётных записей копии:', password)
    db.commit()
    db.close()
    print('копия:', dst)


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    p = argparse.ArgumentParser()
    p.add_argument('action', choices=['copy'])
    p.add_argument('folder')
    p.add_argument('--password', default='secret1')
    a = p.parse_args()
    copy(a.folder, a.password)
