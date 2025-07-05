# dailyVertViewer

google calendar + toggl + clickup

### launcher build

cl hotkey_launcher.cpp /Fehotkey_launcher.exe /nologo /O2 /MT user32.lib

###  main.py build

pyinstaller main.py --onefile --noconsole --hidden-import=win32pipe --hidden-import=win32file --hidden-import=pywintypes
