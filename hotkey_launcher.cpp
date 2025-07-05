#include <windows.h>
#include <iostream>

#define HOTKEY_ID 1
#define TARGET_HOTKEY MOD_CONTROL | MOD_ALT
#define TARGET_KEY 0x43  // 'C'

int send_pipe_message(const char* message) {
    HANDLE hPipe;
    const wchar_t* pipeName = L"\\\\.\\pipe\\DailyVertPipe";

    while (true) {
        hPipe = CreateFileW(
            pipeName,
            GENERIC_WRITE,
            0,
            NULL,
            OPEN_EXISTING,
            0,
            NULL
        );

        if (hPipe != INVALID_HANDLE_VALUE)
            break;

        if (GetLastError() != ERROR_PIPE_BUSY) {
            return 1;
        }

        if (!WaitNamedPipeW(pipeName, 5000)) {
            return 1;
        }
    }

    DWORD bytesWritten;
    BOOL success = WriteFile(hPipe, message, strlen(message), &bytesWritten, NULL);
    CloseHandle(hPipe);
    return success ? 0 : 1;
}

int WINAPI WinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, LPSTR lpCmdLine, int nCmdShow) {
    if (!RegisterHotKey(NULL, HOTKEY_ID, TARGET_HOTKEY, TARGET_KEY)) {
        MessageBoxW(NULL, L"Hotkey registration failed", L"Error", MB_ICONERROR);
        return 1;
    }

    MSG msg;
    while (GetMessage(&msg, NULL, 0, 0)) {
        if (msg.message == WM_HOTKEY && msg.wParam == HOTKEY_ID) {
            send_pipe_message("SHOW\n");
        }
    }

    UnregisterHotKey(NULL, HOTKEY_ID);
    return 0;
}
