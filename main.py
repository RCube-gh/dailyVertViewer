import os
import sys
import time
import psutil
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from PyQt5.QtWidgets import QApplication, QWidget, QLabel, QVBoxLayout, QFrame, QHBoxLayout, QStackedWidget, QSizePolicy
from PyQt5.QtGui import QFont, QFontDatabase, QMovie
from PyQt5.QtCore import Qt, QTimer, QPoint, QPropertyAnimation, pyqtSignal, QThread
import qt_material
#import keyboard  # pip install keyboard
from pynput import keyboard as pkb

from PyQt5.QtCore import QAbstractNativeEventFilter
import ctypes
from ctypes import wintypes
from PyQt5 import sip  # or PyQt5.sip
import win32pipe
import win32file

from datetime import datetime, timedelta, timezone,date
from google_auth_oauthlib.flow import InstalledAppFlow
from googleapiclient.discovery import build
from google.oauth2.credentials import Credentials
from google.auth.transport.requests import Request

import requests
from dotenv import load_dotenv
from dateutil import parser
import json

from winotify import Notification



load_dotenv()

JST = timezone(timedelta(hours=9))

TOGGL_API_TOKEN = os.getenv("TOGGL_API_TOKEN")
TOGGL_WORKSPACE_ID = os.getenv("TOGGL_WORKSPACE_ID")
TOGGL_USER_AGENT = os.getenv("TOGGL_USER_AGENT")

WM_CUSTOM_HOTKEY=0x0400+123

log_path="dailyVertViewer.log"

# === スライドUI設定 ===

#screen=QApplication.primaryScreen()
#print(screen)
#SCREEN_WIDTH=screen.size().width()
#SCREEN_HEIGHT=screen.size().height()

#print(SCREEN_WIDTH,SCREEN_HEIGHT)
SCREEN_HEIGHT=None
SCREEN_WIDTH=None

WINDOW_WIDTH = 500
WINDOW_HEIGHT = None
START_X = None
END_X = None
Y_POSITION = 0
SLIDE_DURATION = 200
HTTP_PORT = 8765
PIXELS_PER_HOUR = None  # 1時間ごとの高さ
START_HOUR = 4
END_HOUR = 24
SIDEBAR_WIDTH=60
LEFT_WIDTH=200

SCOPES = ['https://www.googleapis.com/auth/calendar.readonly']
# グローバル変数としてウィジェット保持
global_widget = None



def fetch_all_clickup_tasks():
    list_id = os.getenv("CLICKUP_LIST_ID")
    token = os.getenv("CLICKUP_API_TOKEN")

    url = f"https://api.clickup.com/api/v2/list/{list_id}/task"
    headers = {
        "Authorization": token,
        "Accept": "application/json"
    }
    params = {
        "archived": "false",
        "subtasks": "true"
    }

    response = requests.get(url, headers=headers, params=params)
    if response.status_code == 200:
        return response.json().get("tasks", [])
    else:
        print(f"[ERROR] Failed to fetch tasks: {response.status_code} - {response.text}")
        return []


def get_effective_due_date(task, parent_lookup):
    due = task.get("due_date")
    if due:
        return int(due)
    
    parent_id = task.get("parent")
    if parent_id and parent_id in parent_lookup:
        parent_due = parent_lookup[parent_id].get("due_date")
        if parent_due:
            return int(parent_due)
    
    return None

def get_today_range_unix_ms():
    now = datetime.now()
    today = datetime(now.year, now.month, now.day)
    tomorrow = today + timedelta(days=1)

    today_ms = int(today.timestamp() * 1000)
    tomorrow_ms = int(tomorrow.timestamp() * 1000)
    return today_ms, tomorrow_ms

def is_due_today(task,parent_lookup,today_ms,tomorrow_ms):
    due = get_effective_due_date(task, parent_lookup)
    return due and today_ms <= due < tomorrow_ms






class SafeFullFetcherThread(QThread):
    finished=pyqtSignal(list,list,list,date)#events,toggl_entries,today
    def __init__(self,token_json_str):
        super().__init__()
        self.token_json_str=token_json_str

    def run(self):
        try:
            SCOPES = ['https://www.googleapis.com/auth/calendar.readonly']
            creds = Credentials.from_authorized_user_info(json.loads(self.token_json_str), SCOPES)
            service = build('calendar', 'v3', credentials=creds)

            calendar_colors = get_calendar_colors(service)
            events = fetch_today_events(service, calendar_colors)
            toggl_entries = get_structured_toggl_entries()
            clickup_tasks=fetch_all_clickup_tasks()
            today = datetime.now(JST).date()


            self.finished.emit(events, toggl_entries,clickup_tasks, today)

        except Exception as e:
            print(f"[ERROR] SafeFullFetcherThread failed: {e}")

class ToastRedirector:
    def write(self,message):
        message=message.strip()
        if message:
            toast=Notification(app_id="dailyVertViewer",title="stdout",msg=message)
            toast.show()
    def flush(self):
        pass





#sys.stdout=ToastRedirector()
#sys.stdout=sys.__stdout__
sys.stdout=open(log_path,"a",encoding="utf-8")
sys.stderr=sys.stdout



class SlideWidget(QWidget):
    trigger_slide_in = pyqtSignal()
    trigger_hide_slide = pyqtSignal()


    def __init__(self,app,service=None):
        super().__init__()
        self.service = service or get_calendar_service()
        self.app=app
        self.trigger_slide_in.connect(self.slide_in)
        self.trigger_hide_slide.connect(self.hide_slide)
        self.cached_date=None
        self.cached_events=[]
        self.cached_allday_events=[]
        self.cached_toggl_entries=[]
        self.cached_parent_tasks=[]
        self.cached_subtask_map={}
        self.init_ui()

    def init_ui(self):
        self.setWindowFlags(Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool)
        self.setGeometry(START_X, Y_POSITION, WINDOW_WIDTH, WINDOW_HEIGHT)
        self.setWindowTitle("dailyVertViewer")

        self.anim_in = QPropertyAnimation(self, b"pos")
        self.anim_in.setDuration(SLIDE_DURATION)
        self.anim_in.setEndValue(QPoint(END_X, Y_POSITION))

        self.anim_out = QPropertyAnimation(self, b"pos")
        self.anim_out.setDuration(SLIDE_DURATION)
        self.anim_out.setEndValue(QPoint(START_X, Y_POSITION))


        self.loading_layer=QWidget(self)
        self.loading_layer.setGeometry(0,0,WINDOW_WIDTH,WINDOW_HEIGHT)
        self.loading_layer.setAttribute(Qt.WA_TransparentForMouseEvents)
        self.loading_layer.setStyleSheet("background-color:rgba(0,0,0,0);")
        self.loading_layer.setProperty("permanent",True)
        self.loading_layer.raise_()


        # ▼ ① Movieオブジェクト生成（絶対に最初）
        self.loading_movie = QMovie("spinner.gif")
        self.loading_movie.setCacheMode(QMovie.CacheAll)
        self.loading_movie.setSpeed(100)
        self.loading_movie.setParent(self)

        # ▼ ② Spinner用 QLabel
        self.loading_spinner = QLabel(self.loading_layer)
        self.loading_spinner.setMovie(self.loading_movie)
        self.loading_spinner.setScaledContents(True)
        self.loading_spinner.setFixedSize(128, 128)
        self.loading_spinner.move(WINDOW_WIDTH // 2 - 64, WINDOW_HEIGHT // 2 - 64)
        self.loading_spinner.setProperty("permanent", True)
        self.loading_spinner.hide()

        # ▼ ③ Overlayの設定（黒背景半透明）
        self.loading_overlay=QWidget(self.loading_layer)
        self.loading_overlay.setStyleSheet("background-color: rgba(0,0,0,168);")
        self.loading_overlay.setGeometry(0, 0, WINDOW_WIDTH, WINDOW_HEIGHT)
        self.loading_overlay.setProperty("permanent", True)
        self.loading_overlay.hide()

        # ▼ ④ 起動チェックログ
        #print("[DEBUG] movie valid?", self.loading_movie.isValid())
        #QTimer.singleShot(1000, lambda: print("[DEBUG] movie frame:", self.loading_movie.currentFrameNumber()))


        self.left_width=LEFT_WIDTH
        self.right_width=WINDOW_WIDTH-SIDEBAR_WIDTH-LEFT_WIDTH
        self.right_x=SIDEBAR_WIDTH+self.left_width

        self.display_mode="calendar" # "calendar" or "todo"
        self.view_mode="calendar" #or "compare"
        self.stack=QStackedWidget(self)
        self.stack.setGeometry(0,0,WINDOW_WIDTH,WINDOW_HEIGHT)

        # Calendar Page
        self.page_calendar=QWidget()
        self.page_calendar.setObjectName("calendar_page")
        self.page_calendar.setStyleSheet("background-color:#1e1e1e;")
        self.now_line=QFrame(self.page_calendar)
        self.now_line.setGeometry(SIDEBAR_WIDTH,0,WINDOW_WIDTH-SIDEBAR_WIDTH,3)
        self.now_line.setStyleSheet("background-color: red; border: none;")
        self.now_line.raise_()
        #print("now_line_raise")
        self.now_line.show()


        self.now_timer=QTimer()
        self.now_timer.timeout.connect(self.update_now_line)
        self.update_now_line()
        self.now_timer.start(60000)


        # Todo Page
        self.page_todo=QWidget()
        self.page_todo.setObjectName("todo_page")
        self.page_todo.setStyleSheet("background-color:#2b2b2b;")
        layout_todo = QVBoxLayout(self.page_todo)
        self.todo_layout = layout_todo  # 保存しておく
        self.todo_layout.setAlignment(Qt.AlignTop)
        self.render_todo_content()  # 最初の表示
        #layout_todo=QVBoxLayout(self.page_todo)
        #title=QLabel("Today's Events")
        #title.setStyleSheet("color: white;font-size:20px;")
        #layout_todo.addWidget(title)
        #for i in range(3):
        #    allday_label=QLabel(f"All-Day event {i+1}")
        #    allday_label.setStyleSheet("color: white;font-size:16px;")
        #    layout_todo.addWidget(allday_label)

        #separator=QLabel("Today's Tasks")
        #separator.setStyleSheet("color: white;font-size:18px;")
        #layout_todo.addWidget(separator)
        #for i in range(5):
        #    task_label=QLabel(f"Task{i+1}:aaaa")
        #    task_label.setStyleSheet("color: white; font-size:16px;")
        #    layout_todo.addWidget(task_label)

        # add to stack
        self.stack.addWidget(self.page_calendar)
        self.stack.addWidget(self.page_todo)
        self.stack.setCurrentWidget(self.page_calendar)



        #self.add_hour_lines()
        #self.add_hour_labels()#DEBUG
        self.hide()

    def showEvent(self,event):
        super().showEvent(event)
        if not hasattr(self,"_hour_labels_aadded"):
            self.hour_labels_added=True
            self.add_hour_labels()
    def update_display_mode(self):
        if self.display_mode=="calendar":
            self.stack.setCurrentWidget(self.page_calendar)
            if self.view_mode=="calendar":
                self.clear_events()
                self.now_line.show()
                self.display_cached_events()
                self.show()
                self.raise_()
                self.activateWindow()
            elif self.view_mode=="compare":
                half=(WINDOW_WIDTH-SIDEBAR_WIDTH)//2
                self.clear_events()
                self.display_cached_events()
        elif self.display_mode=="todo":
            self.clear_events()
            self.stack.setCurrentWidget(self.page_todo)
            self.render_todo_content()


    def render_todo_content(self):
        #print("render_todo")
        # 1. 既存のレイアウトの中身を全消し
        for i in reversed(range(self.todo_layout.count())):
            widget = self.todo_layout.itemAt(i).widget()
            if widget:
                widget.setParent(None)

        # 2. セクションラベル追加
        self.todo_layout.addWidget(self.make_section_label("🗓️ Events"))

        if self.cached_allday_events:
            for allday_event in self.cached_allday_events:
                self.todo_layout.addWidget(self.make_task_card(allday_event.get("summary",[])))
        else:
            self.todo_layout.addWidget(self.make_info_card("No Events"))

        #for parent in parent_tasks:
        #    print("🔹", parent["name"])
        #    for sub in subtask_map.get(parent["id"], []):
        #        print("   ↳", sub["name"])

        self.todo_layout.addWidget(self.make_section_label("📝 Tasks"))
        if self.cached_parent_tasks or self.cached_subtask_map:
            for parent in self.cached_parent_tasks:
                title = parent.get("name", "(Untitled)")
                self.todo_layout.addWidget(self.make_task_card(title))
                for sub in self.cached_subtask_map.get(parent["id"],[]):
                    title = sub.get("name", "(Untitled)")
                    self.todo_layout.addWidget(self.make_subtask_card(title))
        else:
            self.todo_layout.addWidget(self.make_info_card("No Tasks"))

    def make_section_label(self, text):
        label = QLabel(text)
        label.setStyleSheet("color: #bbbbbb; font-size: 26px; font-weight: bold; margin-top: 10px;")
        return label

    def make_task_card(self, text):
        frame = QFrame()
        frame.setStyleSheet("""
            background-color: #3a3a3a;
            border-radius: 8px;
            padding: 8px;
            margin-top: 8px;
        """)
        frame.setSizePolicy(QSizePolicy.Preferred, QSizePolicy.Maximum)  # ← ここ追加！
        label = QLabel(text)
        label.setStyleSheet("color: white; font-size: 22px;margin-bottom:12px;")
        label.setAlignment(Qt.AlignLeft | Qt.AlignVCenter)
        label.setWordWrap(True)
        layout = QVBoxLayout(frame)
        layout.setAlignment(Qt.AlignVCenter)
        layout.addWidget(label)
        return frame

    def make_subtask_card(self, text):
        frame = QFrame()
        frame.setStyleSheet("""
            background-color: #2d2d2d;
            border-radius: 6px;
            padding: 4px;
            margin: 2px 0 2px 20px;  /* ← インデント */
        """)
        label = QLabel(f"↳ {text}")
        label.setStyleSheet("color: #cccccc; font-size: 20px;")
        label.setWordWrap(True)
        label.setAlignment(Qt.AlignLeft | Qt.AlignVCenter)
        layout = QVBoxLayout(frame)
        layout.setAlignment(Qt.AlignVCenter)
        layout.addWidget(label)
        return frame

    def make_info_card(self, text):
        frame = QFrame()
        frame.setStyleSheet("""
            background-color: #2a2a2a;
            border :none;
            padding: 10px;
            margin-bottom: 8px;
        """)
        label = QLabel(text)
        label.setStyleSheet("color: #aaaaaa; font-size: 26px; font-style: italic;")
        label.setAlignment(Qt.AlignCenter)
        layout = QVBoxLayout(frame)
        layout.addWidget(label)
        return frame



    def update_now_line(self):
        now=datetime.now(timezone(timedelta(hours=9)))
        y=int(((now.hour+now.minute/60)-START_HOUR)*PIXELS_PER_HOUR)
        self.now_line.move(SIDEBAR_WIDTH,y)

    def add_event(self, title, hour, minute, duration,color="#a2d5f2",side='left'):
        #print("add_event")
        start_y = int(((hour + minute / 60) - START_HOUR) * PIXELS_PER_HOUR)
        height = int(duration * PIXELS_PER_HOUR)

        x_offset=self.right_x if side=='right' else SIDEBAR_WIDTH
        width=0
        if side=='left':
            width=self.left_width
        elif side=='right':
            width=self.right_width
        elif side=='both':
            width=WINDOW_WIDTH-SIDEBAR_WIDTH

        event_frame = QFrame(self.page_calendar)
        event_frame.setProperty("is_event",True)
        event_frame.setGeometry(x_offset, start_y, width, height)
        event_frame.setStyleSheet(f"background-color: {color}; border: None; border-radius: 5px;")
        event_frame.show()
        event_frame.raise_()

        label = QLabel(title, event_frame)
        label.move(10, 5)
        label.setStyleSheet("font-size: 14px;")
        label.show()
    def clear_events(self):
        for child in self.findChildren(QFrame):
            if child==self.now_line or child.property("permanent")==True:
                continue
            if child.property("is_event")==True:
                child.deleteLater()

    def add_toggl_log(self):
        #self.add_event("Coding", 9, 30, 1.5, color="#f28b82", side='left')
        #self.add_event("Meeting", 11, 0, 1.0, color="#fbbc04", side='left')
        #self.add_event("Lunch", 12, 30, 1.0, color="#34a853", side='left')

        for entry in self.cached_toggl_entries:
            start_dt=parser.isoparse(entry["start"])
            end_dt=datetime.now(JST) if entry.get("running") else parser.isoparse(entry["end"])
            duration=(end_dt-start_dt).total_seconds()/3600
            self.add_event(
                    title=entry["description"]+"  ["+entry["project"]+"]",
                    hour=start_dt.hour,
                    minute=start_dt.minute,
                    duration=duration,
                    color=entry["color"],
                    side="left"
                )


    def update_events(self,force=False):
        #print("update_events")
        self.start_loading()
        today=datetime.now(JST).date()
        if force or self.cached_date!=today or not self.cached_events:
            #print("fetch data for update")
            #self.calendar_colors=get_calendar_colors(self.service)
            #self.cached_events=fetch_today_events(self.service,self.calendar_colors)
            #self.cached_toggl_entries=get_structured_toggl_entries()
            #self.cached_date=today
            with open("token.json","r") as f:
                token_str=f.read()
            self.fetcher_thread=SafeFullFetcherThread(token_str)
            self.fetcher_thread.finished.connect(self.handle_fetched_data)
            self.fetcher_thread.start()
        else:
            self.stop_loading()

    def is_all_day_event(self,event):
        return "date" in event.get("start", {})

    def handle_fetched_data(self,events,toggl_entries,clickup_tasks,today):
        normal_events=[]
        allday_events=[]
        self.cached_date=today
        for event in events:
            if self.is_all_day_event(event):
                allday_events.append(event)
            else:
                normal_events.append(event)
        self.cached_events=normal_events
        self.cached_allday_events=allday_events
        self.cached_toggl_entries=toggl_entries

        #Clickup
        today_ms, tomorrow_ms = get_today_range_unix_ms()
        parent_lookup = {task["id"]: task for task in clickup_tasks}
        today_tasks = [t for t in clickup_tasks if is_due_today(t,parent_lookup,today_ms,tomorrow_ms)]
        parent_tasks = []
        subtask_map = {}

        for task in today_tasks:
            if task.get("parent"):
                subtask_map.setdefault(task["parent"], []).append(task)
            else:
                parent_tasks.append(task)

        self.cached_parent_tasks=parent_tasks
        self.cached_subtask_map=subtask_map

        self.stop_loading()
        self.display_content()
        self.loading_overlay.raise_()
        self.loading_spinner.raise_()

    def display_cached_events(self):
        #print("display_cached_events")
        self.add_hour_labels()
        self.add_hour_lines()
        for event in self.cached_events:

            start_time=event.get('start_time')
            end_time=event.get('end_time')
            if not start_time or not end_time:
                continue
            start_time = event.get('start_time')
            end_time = event.get('end_time')

            hour= start_time.hour
            minute= start_time.minute
            duration= (end_time - start_time).total_seconds() / 3600
            color=event.get('color','#a2d5f2')
            side='both' if self.view_mode=='calendar' else 'right'
            self.add_event(event.get('summary', 'No Title'), hour, minute, duration,color,side=side)

        if self.view_mode=="compare":
            self.add_toggl_log()
        self.update_now_line()
        self.now_line.raise_()
        #print("now_line_raise")
        self.loading_layer.raise_()
        self.loading_overlay.raise_()
        self.loading_spinner.raise_()



    def slide_in(self):
        init_screen_dependent_values(self.app)
        self.setGeometry(START_X,Y_POSITION,WINDOW_WIDTH,WINDOW_HEIGHT)

        today = date.today()
        is_today_cached = (self.cached_date == today)
        
        if not is_today_cached:
            self.update_events()  # ← 非同期取得＆描画
        if self.isVisible():
            return

        self.display_mode = "calendar"
        self.view_mode = "calendar"
        self.stack.setCurrentWidget(self.page_calendar)
        self.clear_events()

        #self.update_events()
        if is_today_cached:
            self.display_content()  # ← キャッシュが使えるときだけ即描画！

        self.show()
        self.raise_()
        self.activateWindow()
        self.anim_in.setStartValue(self.pos())
        self.anim_in.start()


    def display_content(self):
        if self.display_mode=='calendar':
            self.display_cached_events()
        elif self.display_mode=='todo':
            self.render_todo_content()

    def hide_slide(self):
        self.anim_out.setStartValue(self.pos())
        self.anim_out.start()
        self.anim_out.finished.connect(self.hide)

    def add_hour_labels(self):
        if hasattr(self,'hour_labels'):
            for label in self.hour_labels:
                label.deleteLater()

        self.hour_labels=[]
        for hour in range(START_HOUR, END_HOUR):
            y = int((hour - START_HOUR) * PIXELS_PER_HOUR)
            label = QLabel(f"{hour:02d}:00", self.page_calendar)
            label.move(10, y-10)
            label.setFixedSize(SIDEBAR_WIDTH - 10, 20)
            label.setAlignment(Qt.AlignLeft | Qt.AlignVCenter)
            label.setStyleSheet("color: white;font-size: 12px;")
            label.show()
            label.raise_()
            self.hour_labels.append(label)
    def add_hour_lines(self):
        if hasattr(self,'hour_lines'):
            for line in self.hour_lines:
                line.deleteLater()
        self.hour_lines=[]
        for hour in range(START_HOUR,END_HOUR+1):
            y=int((hour-START_HOUR)*PIXELS_PER_HOUR)
            line=QFrame(self.page_calendar)
            line.setGeometry(SIDEBAR_WIDTH,y,WINDOW_WIDTH-SIDEBAR_WIDTH,1)
            line.setStyleSheet("background-color:#444;")
            line.show()
            line.raise_()
            self.hour_lines.append(line)


    def advance_spinner_frame(self):
        current = self.loading_movie.currentFrameNumber()
        success = self.loading_movie.jumpToNextFrame()
        #print(f"[DEBUG] frame jumped: {current} → {self.loading_movie.currentFrameNumber()} | success={success}")


    def start_loading(self):
        #print("start LOADING>>>>>>>>>")
        self.loading_layer.raise_()
        self.loading_layer.show()
        self.loading_overlay.show()
        self.loading_overlay.raise_()

        self.loading_spinner.show()
        self.loading_spinner.raise_()

        self.loading_movie.start()
        QApplication.processEvents()



    def stop_loading(self):
        #print("end LOADING<<<<<<<<<<<")
        if hasattr(self, 'movie_timer') and self.movie_timer.isActive():
            self.movie_timer.stop()
            #print("[DEBUG] movie_timer stopped")
        self.loading_movie.stop()
        self.loading_spinner.hide()
        self.loading_overlay.hide()

    def keyPressEvent(self, event):
        if event.key() == Qt.Key_Escape:
            self.hide_slide()
        elif event.key() == Qt.Key_R:
            self.clear_events()
            self.update_events(force=True)
            self.raise_()
            self.show()
            self.activateWindow()
        elif event.key()==Qt.Key_T and self.display_mode=='calendar':
            if self.view_mode=='calendar':
                self.view_mode='compare'
            elif self.view_mode=='compare':
                self.view_mode='calendar'
            self.update_display_mode()
        elif event.key()==Qt.Key_D:
            if self.display_mode=='calendar':
                self.display_mode='todo'
            elif self.display_mode=='todo':
                self.display_mode='calendar'
            self.update_display_mode()
        elif event.key()==Qt.Key_F12:
            self.slide_in()


class HotkeyEventFilter(QAbstractNativeEventFilter):
    def nativeEventFilter(self, eventType, message):
        try:
            msg_ptr = ctypes.cast(message, ctypes.POINTER(wintypes.MSG))
            msg = msg_ptr.contents
            if msg.message == WM_CUSTOM_HOTKEY:
                #print("[HOTKEY] Received WM_CUSTOM_HOTKEY")  # 通知で出るはず
                if global_widget:
                    global_widget.trigger_slide_in.emit()
        except Exception as e:
            print(f"[HOTKEY] Exception: {e}")
        return False, 0

class RequestHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        global global_widget
        if self.path == "/show":
            if global_widget:
                global_widget.trigger_slide_in.emit()
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b"OK: show")
        elif self.path == "/hide":
            if global_widget:
                global_widget.trigger_hide_slide.emit()
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b"OK: hide")
        else:
            self.send_response(404)
            self.end_headers()
            self.wfile.write(b"Not Found")

    def log_message(self, format, *args):
        return  # 静かにする

def run_http_server():
    server = HTTPServer(('localhost', HTTP_PORT), RequestHandler)
    server.serve_forever()




# グローバルスコープに参照を保持
#hotkey_job = None

#def run_hotkey_listener():
#    def on_hotkey():
#        # print("[HOTKEY] Ctrl+Alt+C pressed!")
#        if global_widget:
#            global_widget.trigger_slide_in.emit()
#
#    global hotkey_job
#    hotkey_job = keyboard.add_hotkey('ctrl+alt+c', on_hotkey)
#
#    while True:
#        time.sleep(1)
#

def pipe_listener():
    import traceback
    print("[PIPE] pipe_listener() started!")  # ← これが通知で出るか確認！！
    pipe_name = r'\\.\pipe\DailyVertPipe'

    while True:
        try:
            print("[PIPE] Creating pipe...")
            pipe = win32pipe.CreateNamedPipe(
                pipe_name,
                win32pipe.PIPE_ACCESS_INBOUND,
                win32pipe.PIPE_TYPE_MESSAGE | win32pipe.PIPE_READMODE_MESSAGE | win32pipe.PIPE_WAIT,
                1, 65536, 65536, 0, None
            )
            print("[PIPE] Pipe created, waiting for connection...")

            win32pipe.ConnectNamedPipe(pipe, None)
            print("[PIPE] Connected!")

            while True:
                result, data = win32file.ReadFile(pipe, 64*1024)
                msg = data.decode('utf-8').strip()
                print(f"[PIPE] Received: {msg}")
                if msg == "SHOW" and global_widget:
                    global_widget.trigger_slide_in.emit()
        except Exception as e:
            print("[PIPE] Exception caught:")
            traceback.print_exc()
        finally:
            try:
                win32file.CloseHandle(pipe)
            except:
                pass
            print("[PIPE] Pipe closed")



def get_calendar_colors(service):
    calendar_colors={}
    calendar_list=service.calendarList().list().execute()
    for cal in calendar_list.get('items',[]):
        cal_id=cal['id']
        color=cal.get('backgroundColor','#a2d5f2')
        calendar_colors[cal_id]=color
    return calendar_colors

def fetch_today_events(service,calendar_colors):
    # 今日の開始・終了（日本時間 → UTC）
    JST = timezone(timedelta(hours=9))
    range_start = datetime.now(JST).replace(hour=0, minute=0, second=0, microsecond=0).astimezone(timezone.utc).isoformat()
    range_end = datetime.now(JST).replace(hour=23, minute=59, second=59, microsecond=0).astimezone(timezone.utc).isoformat()
    all_events=[]
    calendar_list=service.calendarList().list().execute()
    for calendar in calendar_list.get('items',[]):
        cal_id=calendar['id']
        color=calendar_colors.get(cal_id,'#a2d5f2')
        try:
            events_result=service.events().list(
                calendarId=cal_id,
                timeMin=range_start,
                timeMax=range_end,
                singleEvents=True,
                orderBy='startTime',
            ).execute()
            for event in events_result.get('items',[]):
                event_start=event['start'].get('dateTime',event['start'].get('date'))
                event_end=event['end'].get('dateTime',event['end'].get('date'))
                if not event_start or not event_end:
                    continue
                event_start=datetime.fromisoformat(event_start)
                event_end=datetime.fromisoformat(event_end)
                event['color']=color
                event['start_time']=event_start
                event['end_time']=event_end
                all_events.append(event)
        except Exception as e:
            print(f"Error fetching events from calendar {cal_id}: {e}")
    return all_events



def get_calendar_service():
    creds = None
    if os.path.exists('token.json'):
        creds = Credentials.from_authorized_user_file('token.json', SCOPES)
    if not creds or not creds.valid:
        flow = InstalledAppFlow.from_client_secrets_file('credentials.json', SCOPES)
        creds = flow.run_local_server(port=0)
        with open('token.json', 'w') as token:
            token.write(creds.to_json())
    return build('calendar', 'v3', credentials=creds)
def init_screen_dependent_values(app):
    global SCREEN_WIDTH,SCREEN_HEIGHT
    global WINDOW_HEIGHT,START_X,END_X,PIXELS_PER_HOUR

    screen=app.primaryScreen()
    SCREEN_WIDTH=screen.size().width()
    SCREEN_HEIGHT=screen.size().height()
    WINDOW_HEIGHT=SCREEN_HEIGHT
    START_X=SCREEN_WIDTH
    END_X=SCREEN_WIDTH-WINDOW_WIDTH
    PIXELS_PER_HOUR=WINDOW_HEIGHT/(END_HOUR-START_HOUR)
    #print(f"SCREEN_WIDTH: {SCREEN_WIDTH}")
    #print(f"SCREEN_HEIGHT: {SCREEN_HEIGHT}")
    #print(f"PIXELS_PER_HOUR: {PIXELS_PER_HOUR}")



# Fetch project info for color mapping
def fetch_projects(workspace_id):
    url = f"https://api.track.toggl.com/api/v9/workspaces/{workspace_id}/projects"
    auth = (TOGGL_API_TOKEN, "api_token")
    r = requests.get(url, auth=auth)
    if r.status_code == 200:
        return {
            p["id"]: {
                "name": p.get("name", "(no name)"),
                "color": p.get("color", "#cccccc")
            }
            for p in r.json()
        }
    return {}

# Format a finished entry
def format_entry(entry, running=False):
    return {
        "description": entry.get("description", "(no description)"),
        "project": entry.get("project") or "(no project)",
        "start": parser.isoparse(entry["start"]).astimezone(JST).isoformat(),
        "end": parser.isoparse(entry["end"]).astimezone(JST).isoformat(),
        "duration_ms": entry.get("dur", 0),
        "color": entry.get("project_hex_color") or "#cccccc",
        "running": running
    }

# Format the running entry
def format_current_entry(entry, projects):
    start = parser.isoparse(entry["start"]).astimezone(JST)
    end = datetime.now(JST)
    duration = int((end - start).total_seconds() * 1000)

    pid = entry.get("project_id")
    project_info = projects.get(pid, {"name": "(current)", "color": "ff5555"})
    color=project_info.get("color","#cccccc")
    if not color.startswith("#"):
        color=f"#{color}"


    return {
        "description": entry.get("description", "(running)"),
        "project": project_info["name"],
        "start": start.isoformat(),
        "end": end.isoformat(),
        "duration_ms": duration,
        "color": color,
        "running": True
    }


def get_structured_toggl_entries()->list[dict]:
    structured=[]
    today = datetime.now(JST).date().isoformat()
    url = "https://api.track.toggl.com/reports/api/v2/details"
    params = {
        "workspace_id": TOGGL_WORKSPACE_ID,
        "since": today,
        "until": today,
        "user_agent": TOGGL_USER_AGENT
    }

    response = requests.get(url, auth=(TOGGL_API_TOKEN, "api_token"), params=params)


    if response.status_code == 200:
        raw_data = response.json().get("data", [])
        structured += [format_entry(e) for e in raw_data if e.get("project") is not None]
    else:
        print(f"[ERROR] v2 report API: {response.status_code}: {response.text}")

    # Step 2: Get current running entry
    r = requests.get("https://api.track.toggl.com/api/v9/me/time_entries/current", auth=(TOGGL_API_TOKEN, "api_token"))
    if r.status_code == 200:
        current_entry = r.json()
        if current_entry:
            workspace_id = current_entry.get("workspace_id")
            projects = fetch_projects(workspace_id)
            structured.append(format_current_entry(current_entry, projects))

    return structured


if __name__ == '__main__':

    print("[MAIN] App starting")
    sys.stdout.flush()
    app = QApplication(sys.argv)
    qt_material.apply_stylesheet(app, theme='dark_blue.xml')
    init_screen_dependent_values(app)
    app.installNativeEventFilter(HotkeyEventFilter())


    service= get_calendar_service()
    global_widget = SlideWidget(app,service)

    threading.Thread(target=run_http_server, daemon=True).start()
    #threading.Thread(target=run_hotkey_listener, daemon=True).start()
    print("[MAIN] Launching pipe_listener thread")
    threading.Thread(target=pipe_listener, daemon=True).start()


    sys.exit(app.exec_())
