"""Помощники для прохода по пробному сайту коллизий в Edge без окна (Selenium). Подключение из своего скрипта:

    import sys; sys.path.insert(0, r'D:\\Code\\RAS_v_2.0\\.claude\\skills\\site-check')
    from site_browser import *
    d = browser(1560); base = 'http://localhost:5099'
    login(d, base); open_project(d, base, 4); side(d, 'Сводка'); sub(d, 'Аналитика'); tab(d, 'Динамика')
    shot(d, r'...\\dyn.png'); print(page_errors(d)); d.quit()
"""
import sys
import time

from selenium import webdriver
from selenium.webdriver.common.by import By
from selenium.webdriver.edge.options import Options
from selenium.webdriver.support.ui import Select

sys.stdout.reconfigure(encoding='utf-8')

# опрос моста в Revit (порты 5181–5185, BridgeServer.Ports) — к проверке сайта не относится
BRIDGE_NOISE = tuple('127.0.0.1:%d' % p for p in range(5181, 5186))


def browser(width=1560, height=1000):
    o = Options()
    o.add_argument('--headless=new')
    o.add_argument('--window-size=%d,%d' % (width, height))
    d = webdriver.Edge(options=o)
    d.implicitly_wait(1)
    return d


def click(d, el, wait=1.5):
    # щелчок скриптом: элемент мог быть за краем экрана или под всплывающим меню
    d.execute_script("arguments[0].scrollIntoView({block:'center'}); arguments[0].click();", el)
    time.sleep(wait)


def login(d, base, user='egor', password='secret1'):
    d.get(base + '/login')
    time.sleep(1)
    d.find_element(By.NAME, 'login').send_keys(user)
    d.find_element(By.NAME, 'password').send_keys(password)
    d.find_element(By.CSS_SELECTOR, '.login-box button').click()
    time.sleep(3)


def open_project(d, base, project_id):
    d.get('%s/project/%d' % (base, project_id))
    time.sleep(3)


def by_text(d, css, starts):
    found = [e for e in d.find_elements(By.CSS_SELECTOR, css) if e.text.strip().startswith(starts)]
    if not found:
        raise LookupError('нет «%s» среди %s: %s' % (starts, css, [e.text.strip()[:30] for e in d.find_elements(By.CSS_SELECTOR, css)]))
    return found[0]


def side(d, title):
    """Раздел боковой панели проекта («Сводка», «Коллизии», …); подпункты видны только после щелчка по нему."""
    click(d, by_text(d, '.side-btn', title), 2)


def sub(d, title):
    """Подпункт открытого раздела боковой панели («Аналитика», блок, категория)."""
    click(d, by_text(d, '.side-sub', title), 3)


def tab(d, title):
    """Вкладка «Аналитики» («По проверкам», «Динамика», …)."""
    click(d, by_text(d, '.ana-tabs button', title), 3)


def select(d, css, value=None, index=None, text=None):
    s = Select(d.find_element(By.CSS_SELECTOR, css))
    if value is not None:
        s.select_by_value(value)
    elif index is not None:
        s.select_by_index(index)
    else:
        s.select_by_visible_text(text)
    time.sleep(1.5)


def texts(d, css):
    return [e.text.replace('\n', ' ¦ ') for e in d.find_elements(By.CSS_SELECTOR, css)]


def shot(d, path):
    d.save_screenshot(path)
    print('снимок:', path)


def page_errors(d):
    return [e['message'][:200] for e in d.get_log('browser')
            if e['level'] == 'SEVERE' and not any(n in e['message'] for n in BRIDGE_NOISE)]
