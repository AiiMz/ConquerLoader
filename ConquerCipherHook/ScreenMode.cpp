#include "stdafx.h"
#include "ScreenMode.h"

#include <stdio.h>
#include <string.h>

static int g_width = 0;
static int g_height = 0;
static LONG g_installed = 0;
static LONG g_modeChanged = 0;
static WNDPROC g_originalWndProc = NULL;
static char g_logPath[MAX_PATH] = { 0 };

static void Trace(const char* format, ...)
{
	char message[256];
	va_list args;
	va_start(args, format);
	_vsnprintf(message, sizeof(message) - 1, format, args);
	message[sizeof(message) - 1] = 0;
	va_end(args);
	OutputDebugStringA(message);

	if (g_logPath[0] != 0)
	{
		FILE* file = fopen(g_logPath, "a");
		if (file != NULL)
		{
			SYSTEMTIME now;
			GetLocalTime(&now);
			fprintf(file, "%02d:%02d:%02d %s", now.wHour, now.wMinute, now.wSecond, message);
			fclose(file);
		}
	}
}

static void RestoreDisplayMode()
{
	if (InterlockedExchange(&g_modeChanged, 0) != 0)
	{
		ChangeDisplaySettingsExA(NULL, NULL, NULL, 0, NULL);
		Trace("[ScreenMode] Restored the desktop display mode.\n");
	}
}

/**
* Puts the primary display at width x height. True when it is at that size
* afterwards, whether it changed or already was.
*/
static bool ApplyDisplayMode()
{
	DEVMODEA current;
	ZeroMemory(&current, sizeof(current));
	current.dmSize = sizeof(current);
	if (EnumDisplaySettingsA(NULL, ENUM_CURRENT_SETTINGS, &current)
		&& (int)current.dmPelsWidth == g_width && (int)current.dmPelsHeight == g_height)
	{
		Trace("[ScreenMode] The display is already %dx%d.\n", g_width, g_height);
		return true;
	}

	DEVMODEA mode;
	ZeroMemory(&mode, sizeof(mode));
	mode.dmSize = sizeof(mode);
	mode.dmPelsWidth = g_width;
	mode.dmPelsHeight = g_height;
	mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;

	LONG result = ChangeDisplaySettingsExA(NULL, &mode, NULL, CDS_FULLSCREEN, NULL);
	if (result != DISP_CHANGE_SUCCESSFUL)
	{
		Trace("[ScreenMode] The display refused %dx%d (%d); the client stays windowed.\n", g_width, g_height, result);
		return false;
	}

	InterlockedExchange(&g_modeChanged, 1);
	Trace("[ScreenMode] Display switched from %dx%d to %dx%d.\n",
		(int)current.dmPelsWidth, (int)current.dmPelsHeight, g_width, g_height);
	return true;
}

struct FindWindowState
{
	DWORD processId;
	HWND found;
	HWND largest;
	int largestArea;
};

static BOOL CALLBACK FindClientWindow(HWND window, LPARAM parameter)
{
	FindWindowState* state = (FindWindowState*)parameter;

	DWORD processId = 0;
	GetWindowThreadProcessId(window, &processId);
	if (processId != state->processId) return TRUE;
	if (!IsWindowVisible(window) || GetWindow(window, GW_OWNER) != NULL) return TRUE;

	char className[64];
	if (GetClassNameA(window, className, sizeof(className)) > 0 && strcmp(className, "#32770") == 0) return TRUE;

	RECT client;
	if (!GetClientRect(window, &client)) return TRUE;
	int width = client.right - client.left;
	int height = client.bottom - client.top;

	// Remembered in case Windows clamped the framed window: with the desktop at
	// exactly the client size, a window plus its caption can be made shorter.
	if ((GetWindowLongA(window, GWL_STYLE) & WS_CAPTION) == WS_CAPTION && width >= 640 && height >= 480
		&& width * height > state->largestArea)
	{
		state->largest = window;
		state->largestArea = width * height;
	}

	if (width != g_width || height != g_height) return TRUE;

	state->found = window;
	return FALSE;
}

static void PlaceFrameless(HWND window)
{
	LONG style = GetWindowLongA(window, GWL_STYLE);
	style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MAXIMIZEBOX | WS_MINIMIZEBOX);
	style |= WS_POPUP;
	SetWindowLongA(window, GWL_STYLE, style);

	LONG exStyle = GetWindowLongA(window, GWL_EXSTYLE);
	exStyle &= ~(WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_WINDOWEDGE);
	SetWindowLongA(window, GWL_EXSTYLE, exStyle);

	MONITORINFO monitor;
	ZeroMemory(&monitor, sizeof(monitor));
	monitor.cbSize = sizeof(monitor);
	int left = 0;
	int top = 0;
	if (GetMonitorInfoA(MonitorFromWindow(window, MONITOR_DEFAULTTOPRIMARY), &monitor))
	{
		left = monitor.rcMonitor.left;
		top = monitor.rcMonitor.top;
	}

	SetWindowPos(window, HWND_TOP, left, top, g_width, g_height,
		SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);
}

/**
* Puts the desktop mode back as soon as the window goes, rather than leaving it
* to Windows at process exit - the client can spend a while shutting down.
*/
static LRESULT CALLBACK FullscreenWndProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
	if (message == WM_DESTROY)
	{
		RestoreDisplayMode();
	}
	return CallWindowProcA(g_originalWndProc, window, message, wParam, lParam);
}

static DWORD WINAPI InstallThread(LPVOID)
{
	// First, before the client has created its window or its Direct3D device:
	// changing the mode under a live device would lose it.
	if (!ApplyDisplayMode())
	{
		return 0;
	}

	FindWindowState state;
	state.processId = GetCurrentProcessId();
	state.found = NULL;
	state.largest = NULL;
	state.largestArea = 0;

	// The client takes a few seconds to open its window; two minutes is far
	// longer than it ever needs.
	for (int attempt = 0; attempt < 1200 && state.found == NULL; attempt++)
	{
		state.largest = NULL;
		state.largestArea = 0;
		EnumWindows(FindClientWindow, (LPARAM)&state);

		// After 15 seconds, take the client's main window even if its client
		// area is not exactly the requested size; it is resized below anyway.
		if (state.found == NULL && attempt >= 150 && state.largest != NULL)
		{
			Trace("[ScreenMode] No exact %dx%d window; using the largest client window.\n", g_width, g_height);
			state.found = state.largest;
		}

		if (state.found == NULL) Sleep(100);
	}

	if (state.found == NULL)
	{
		Trace("[ScreenMode] No %dx%d client window appeared; restoring the display.\n", g_width, g_height);
		RestoreDisplayMode();
		return 0;
	}

	PlaceFrameless(state.found);
	g_originalWndProc = (WNDPROC)SetWindowLongPtrA(state.found, GWLP_WNDPROC, (LONG_PTR)FullscreenWndProc);
	Trace("[ScreenMode] Client window is now frameless at %dx%d.\n", g_width, g_height);
	return 0;
}

void ScreenMode_Install(bool fullscreen, int width, int height)
{
	if (!fullscreen || width <= 0 || height <= 0)
	{
		return;
	}

	if (InterlockedExchange(&g_installed, 1) != 0)
	{
		return;
	}

	g_width = width;
	g_height = height;

	GetModuleFileNameA(NULL, g_logPath, MAX_PATH);
	for (int i = (int)strlen(g_logPath) - 1; i >= 0; i--)
	{
		if (g_logPath[i] == '\\')
		{
			g_logPath[i + 1] = 0;
			break;
		}
	}
	strcat(g_logPath, "CLHook.screen.log");
	DeleteFileA(g_logPath);

	// Not from DllMain: changing the display mode broadcasts messages, and the
	// loader lock is held until DllMain returns.
	HANDLE thread = CreateThread(NULL, 0, InstallThread, NULL, 0, NULL);
	if (thread != NULL)
	{
		CloseHandle(thread);
	}
}
