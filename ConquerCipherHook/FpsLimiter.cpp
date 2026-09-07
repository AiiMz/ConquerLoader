#include "stdafx.h"
#include "FpsLimiter.h"

#include <stdio.h>
#include "detours.h"

#pragma comment(lib, "winmm.lib")

// ---------------------------------------------------------------------------
// The parts of Direct3D 8 this needs, restated.
//
// d3d8.h shipped with the old DirectX SDK and is in no Windows SDK, so there is
// no header to include. The limiter only ever indexes three methods out of two
// vtables and fills in one struct, so restating those is smaller - and easier
// to check against the documented layouts - than carrying a copy of a header
// that no longer ships anywhere.
// ---------------------------------------------------------------------------

static const UINT D3D8_SDK_VERSION = 220;

// IDirect3D8, counting the three IUnknown slots.
static const int kD3D8_GetAdapterDisplayMode = 8;
static const int kD3D8_CreateDevice = 15;
// IDirect3DDevice8, likewise. Present is the slot every frame goes through.
static const int kUnknown_Release = 2;
static const int kDevice8_Present = 15;

static const UINT D3DADAPTER_DEFAULT = 0;
static const UINT D3DDEVTYPE_HAL = 1;
static const UINT D3DSWAPEFFECT_DISCARD = 1;
static const DWORD D3DCREATE_FPU_PRESERVE = 0x00000002;
static const DWORD D3DCREATE_SOFTWARE_VERTEXPROCESSING = 0x00000020;

struct D3DDISPLAYMODE8
{
	UINT Width;
	UINT Height;
	UINT RefreshRate;
	UINT Format;
};

struct D3DPRESENT_PARAMETERS8
{
	UINT BackBufferWidth;
	UINT BackBufferHeight;
	UINT BackBufferFormat;
	UINT BackBufferCount;
	UINT MultiSampleType;
	UINT SwapEffect;
	HWND hDeviceWindow;
	BOOL Windowed;
	BOOL EnableAutoDepthStencil;
	UINT AutoDepthStencilFormat;
	DWORD Flags;
	UINT FullScreen_RefreshRateInHz;
	UINT FullScreen_PresentationInterval;
};

typedef void* (WINAPI* Direct3DCreate8Fn)(UINT SDKVersion);
typedef ULONG(STDMETHODCALLTYPE* ReleaseFn)(void* self);
typedef HRESULT(STDMETHODCALLTYPE* GetAdapterDisplayModeFn)(void* self, UINT adapter, D3DDISPLAYMODE8* mode);
typedef HRESULT(STDMETHODCALLTYPE* CreateDeviceFn)(void* self, UINT adapter, UINT deviceType, HWND focusWindow,
	DWORD behaviorFlags, D3DPRESENT_PARAMETERS8* presentationParameters, void** returnedDeviceInterface);
typedef HRESULT(STDMETHODCALLTYPE* PresentFn)(void* self, const RECT* sourceRect, const RECT* destRect,
	HWND destWindowOverride, const void* dirtyRegion);

// ---------------------------------------------------------------------------

static PresentFn g_originalPresent = NULL;
static LONGLONG g_frequency = 0;   // QPC ticks per second
static LONGLONG g_period = 0;      // QPC ticks per frame at the configured cap
static LONGLONG g_nextFrame = 0;   // when the next Present may go through
static LONG g_installed = 0;

static void Trace(const char* format, ...)
{
	char message[256];
	va_list args;
	va_start(args, format);
	_vsnprintf(message, sizeof(message) - 1, format, args);
	message[sizeof(message) - 1] = 0;
	va_end(args);
	OutputDebugStringA(message);
}

static void* VTableEntry(void* object, int index)
{
	return (*(void***)object)[index];
}

/**
* Waits until the current frame is due.
*
* Sleeps off all but the last millisecond and yields through the rest, because
* Sleep is only accurate to the timer period - which is why timeBeginPeriod(1)
* goes in with the hook.
*
* The deadline moves on by exactly one period per frame rather than being
* recomputed from "now", so a frame that overruns is absorbed by the next one
* instead of the cap drifting slow. A stall long enough to owe more than four
* frames - alt-tabbed, or loading a map - resyncs instead, so the client never
* comes back to a burst of catch-up frames.
*/
static LONGLONG WaitForFrame()
{
	// No cap configured: the hook is only in to be measured.
	if (g_period <= 0)
	{
		return 0;
	}

	LARGE_INTEGER entry;
	QueryPerformanceCounter(&entry);

	LARGE_INTEGER now = entry;

	if (g_nextFrame == 0 || now.QuadPart > g_nextFrame + (g_period * 4))
	{
		g_nextFrame = now.QuadPart;
	}

	while (now.QuadPart < g_nextFrame)
	{
		LONGLONG remainingUs = ((g_nextFrame - now.QuadPart) * 1000000) / g_frequency;
		if (remainingUs > 1500)
		{
			Sleep((DWORD)((remainingUs - 1000) / 1000));
		}
		else
		{
			Sleep(0);
		}
		QueryPerformanceCounter(&now);
	}

	g_nextFrame += g_period;
	return now.QuadPart - entry.QuadPart;
}

// ---------------------------------------------------------------------------
// Which Present call starts a frame.
//
// This client does not present once per frame. It presents three times, and the
// number it draws on screen counts frames rather than presents - so pacing every
// call gave a third of the cap that was asked for: 2151 calls/s while the client
// reported 700 FPS, and exactly 60.0 calls/s while it reported 19.
//
// The three calls are not alike, and that is what makes them separable. The gaps
// between them repeat, forever and to three decimal places: 0.025 ms, 1.207 ms,
// 0.162 ms. One of the three is the frame - the client thinking and drawing -
// and the other two are the same finished frame going out again microseconds
// later. So the rule is: pace a call when the client did a frame's worth of work
// in front of it, and let the re-presents through untouched.
//
// "A frame's worth" is measured against the recent maximum rather than a fixed
// number of milliseconds, because a fixed one cannot be right for both a client
// spending 1.2 ms a frame and one spending 20. Half the maximum splits the
// observed 1.207 from the 0.162 with room to spare.
//
// The halving is also what keeps a client that presents once per frame behaving
// as it always did: its gaps are all the same size, so every one of them clears
// half the maximum and every call is paced, which is the original behaviour
// exactly. A rule that paced only the single largest gap would have quietly run
// such a client at three times its cap.
// ---------------------------------------------------------------------------

static const int kGapHistory = 32;
static const double kStallMilliseconds = 100.0;

static LONGLONG g_gaps[kGapHistory];
static int g_gapNext = 0;
static int g_gapCount = 0;

static bool StartsAFrame(LONGLONG gap)
{
	// A gap this long is a map load or an alt-tab, not a frame. Letting one into
	// the history would raise the maximum so far that nothing cleared half of it
	// again and the cap would come off until it aged out.
	LONGLONG stall = (LONGLONG)(g_frequency * (kStallMilliseconds / 1000.0));
	if (gap > 0 && gap < stall)
	{
		g_gaps[g_gapNext] = gap;
		g_gapNext = (g_gapNext + 1) % kGapHistory;
		if (g_gapCount < kGapHistory)
		{
			g_gapCount++;
		}
	}

	// Nothing to compare against yet, or nothing but stalls: pace it. Every
	// uncertain case here errs towards the old behaviour rather than towards
	// letting the client run uncapped.
	if (g_gapCount == 0)
	{
		return true;
	}

	LONGLONG largest = 0;
	for (int i = 0; i < g_gapCount; i++)
	{
		if (g_gaps[i] > largest)
		{
			largest = g_gaps[i];
		}
	}

	return largest <= 0 || (gap * 2) >= largest;
}

// ---------------------------------------------------------------------------
// The diagnostic, off unless FPS_DEBUG=1.
//
// It exists because "the cap says 60 and the client says 20" has two entirely
// different causes - the cap missing its target, or the client drawing fewer
// frames than it presents - and no way to tell them apart from the outside. The
// gap list is the useful half: several Present calls microseconds apart, then a
// long pause, is one rendered frame presented more than once.
// ---------------------------------------------------------------------------

static const int kMaxSamples = 4000;
static bool g_debug = false;
static char g_logPath[MAX_PATH];
static LONGLONG g_windowStart = 0;
static LONGLONG g_samples[kMaxSamples];
static void* g_devices[kMaxSamples];
static HWND g_windows[kMaxSamples];
static bool g_paced[kMaxSamples];
static int g_sampleCount = 0;
static int g_callCount = 0;
static int g_pacedCount = 0;

static void WriteBlock(LONGLONG now)
{
	FILE* file = fopen(g_logPath, "a");
	if (file == NULL)
	{
		return;
	}

	double seconds = (double)(now - g_windowStart) / g_frequency;
	int n = g_sampleCount;

	fprintf(file, "==== cap %d FPS ====\n", (g_period > 0) ? (int)(g_frequency / g_period) : 0);
	fprintf(file, "Present calls: %d in %.2fs = %.1f calls/s\n", g_callCount, seconds,
		(seconds > 0) ? g_callCount / seconds : 0.0);
	fprintf(file, "paced as frames: %d = %.1f frames/s, %.2f presents per frame\n", g_pacedCount,
		(seconds > 0) ? g_pacedCount / seconds : 0.0,
		(g_pacedCount > 0) ? (double)g_callCount / g_pacedCount : 0.0);

	double covered = 0;
	for (int i = 0; i < n; i++)
	{
		covered += (double)g_samples[i] / g_frequency;
	}
	if (n > 0 && covered > 0)
	{
		fprintf(file, "mean gap: %.3f ms = %.1f calls/s\n", covered * 1000 / n, n / covered);
	}

	static const char* names[] = { "<0.05ms", "<0.1ms", "<0.2ms", "<0.5ms", "<1ms",
		"<2ms", "<5ms", "<10ms", "<20ms", "<50ms", ">=50ms" };
	static const double edges[] = { 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 1e18 };
	int buckets[11];
	for (int b = 0; b < 11; b++)
	{
		buckets[b] = 0;
	}
	for (int i = 0; i < n; i++)
	{
		double ms = (double)g_samples[i] * 1000.0 / g_frequency;
		for (int b = 0; b < 11; b++)
		{
			if (ms < edges[b]) { buckets[b]++; break; }
		}
	}
	fprintf(file, "gaps between Present calls:\n");
	for (int b = 0; b < 11; b++)
	{
		if (buckets[b] > 0) fprintf(file, "  %-8s %6d\n", names[b], buckets[b]);
	}

	void* devices[8]; int deviceCount = 0;
	HWND windows[8]; int windowCount = 0;
	for (int i = 0; i < n; i++)
	{
		bool seen = false;
		for (int k = 0; k < deviceCount; k++) if (devices[k] == g_devices[i]) seen = true;
		if (!seen && deviceCount < 8) devices[deviceCount++] = g_devices[i];
		seen = false;
		for (int k = 0; k < windowCount; k++) if (windows[k] == g_windows[i]) seen = true;
		if (!seen && windowCount < 8) windows[windowCount++] = g_windows[i];
	}
	fprintf(file, "devices: %d ->", deviceCount);
	for (int k = 0; k < deviceCount; k++) fprintf(file, " %p", devices[k]);
	fprintf(file, "\nwindows: %d ->", windowCount);
	for (int k = 0; k < windowCount; k++) fprintf(file, " %p", windows[k]);

	fprintf(file, "\nlast 60 client gaps (ms), * = paced as a frame:\n");
	int from = n - 60;
	if (from < 0) from = 0;
	for (int i = from; i < n; i++)
	{
		fprintf(file, "%8.3f%s%s", (double)g_samples[i] * 1000.0 / g_frequency,
			g_paced[i] ? "*" : " ", ((i - from + 1) % 10 == 0) ? "\n" : "");
	}
	fprintf(file, "\n\n");
	fclose(file);
}

static void Record(void* device, HWND window, LONGLONG gap, bool paced)
{
	LARGE_INTEGER now;
	QueryPerformanceCounter(&now);

	if (g_windowStart == 0)
	{
		g_windowStart = now.QuadPart;
		return;
	}

	if (g_sampleCount < kMaxSamples)
	{
		g_samples[g_sampleCount] = gap;
		g_devices[g_sampleCount] = device;
		g_windows[g_sampleCount] = window;
		g_paced[g_sampleCount] = paced;
		g_sampleCount++;
	}
	g_callCount++;
	if (paced)
	{
		g_pacedCount++;
	}

	if (now.QuadPart - g_windowStart >= g_frequency * 10)
	{
		WriteBlock(now.QuadPart);
		g_windowStart = now.QuadPart;
		g_sampleCount = 0;
		g_callCount = 0;
		g_pacedCount = 0;
	}
}

static LONGLONG g_previousEntry = 0;
static LONGLONG g_previousWait = 0;

static HRESULT STDMETHODCALLTYPE HookedPresent(void* self, const RECT* sourceRect, const RECT* destRect,
	HWND destWindowOverride, const void* dirtyRegion)
{
	LARGE_INTEGER entry;
	QueryPerformanceCounter(&entry);

	// What the client spent between the last call and this one, with any wait
	// this hook injected taken back out again - so the shape being measured is
	// the client's, and it does not change when a cap is switched on.
	LONGLONG gap = (g_previousEntry == 0) ? 0 : (entry.QuadPart - g_previousEntry - g_previousWait);
	g_previousEntry = entry.QuadPart;

	bool paced = StartsAFrame(gap);

	if (g_debug)
	{
		Record(self, destWindowOverride, gap, paced);
	}

	g_previousWait = paced ? WaitForFrame() : 0;

	return g_originalPresent(self, sourceRect, destRect, destWindowOverride, dirtyRegion);
}

/**
* Creates a throwaway device only to read one address out of its vtable, then
* throws the device away and detours the function that address points at.
*
* The two steps are not interchangeable. d3d8.dll gives every device its own
* copy of the vtable, allocated a few hundred bytes past the device itself, so
* overwriting the Present slot through this device would cap this device and
* nothing else. What the copies share is what they point at: slot 15 of a device
* created here and slot 15 of a device created anywhere else hold the same
* address inside d3d8.dll. Detouring the function at that address is therefore
* what catches every device - including one the client created before this DLL
* was injected, which is why there is no race to lose against the client's
* graphics startup and no need to be handed the real device by a hook on
* Direct3DCreate8.
*/
static bool HookPresent(HMODULE d3d8)
{
	Direct3DCreate8Fn direct3DCreate8 = (Direct3DCreate8Fn)GetProcAddress(d3d8, "Direct3DCreate8");
	if (direct3DCreate8 == NULL)
	{
		Trace("[FpsLimiter] d3d8.dll has no Direct3DCreate8.\n");
		return false;
	}

	void* d3d = direct3DCreate8(D3D8_SDK_VERSION);
	if (d3d == NULL)
	{
		Trace("[FpsLimiter] Direct3DCreate8 returned nothing.\n");
		return false;
	}

	bool hooked = false;
	HWND window = CreateWindowExA(0, "STATIC", "", WS_POPUP, 0, 0, 1, 1, NULL, NULL, GetModuleHandleA(NULL), NULL);

	if (window != NULL)
	{
		D3DDISPLAYMODE8 mode;
		ZeroMemory(&mode, sizeof(mode));
		GetAdapterDisplayModeFn getAdapterDisplayMode =
			(GetAdapterDisplayModeFn)VTableEntry(d3d, kD3D8_GetAdapterDisplayMode);
		getAdapterDisplayMode(d3d, D3DADAPTER_DEFAULT, &mode);

		D3DPRESENT_PARAMETERS8 parameters;
		ZeroMemory(&parameters, sizeof(parameters));
		parameters.BackBufferWidth = 1;
		parameters.BackBufferHeight = 1;
		parameters.BackBufferFormat = mode.Format;
		parameters.BackBufferCount = 1;
		parameters.SwapEffect = D3DSWAPEFFECT_DISCARD;
		parameters.hDeviceWindow = window;
		parameters.Windowed = TRUE;

		CreateDeviceFn createDevice = (CreateDeviceFn)VTableEntry(d3d, kD3D8_CreateDevice);
		DWORD behaviour = D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_FPU_PRESERVE;

		// HAL and nothing else. A reference-rasteriser device is a different
		// implementation with a different Present, so detouring that one would
		// report success and cap nothing - and a machine that cannot make a HAL
		// device cannot run the client either, so there is nothing to fall back
		// for.
		void* device = NULL;
		HRESULT created = createDevice(d3d, D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, window, behaviour, &parameters, &device);

		if (SUCCEEDED(created) && device != NULL)
		{
			g_originalPresent = (PresentFn)VTableEntry(device, kDevice8_Present);
			((ReleaseFn)VTableEntry(device, kUnknown_Release))(device);

			DetourTransactionBegin();
			DetourUpdateThread(GetCurrentThread());
			DetourAttach(&reinterpret_cast<PVOID&>(g_originalPresent), HookedPresent);
			LONG committed = DetourTransactionCommit();
			if (committed == NO_ERROR)
			{
				hooked = true;
			}
			else
			{
				Trace("[FpsLimiter] Could not detour Present (%d).\n", committed);
			}
		}
		else
		{
			Trace("[FpsLimiter] CreateDevice failed (0x%08X).\n", created);
		}

		DestroyWindow(window);
	}
	else
	{
		Trace("[FpsLimiter] Could not create the throwaway device window.\n");
	}

	((ReleaseFn)VTableEntry(d3d, kUnknown_Release))(d3d);
	return hooked;
}

static DWORD WINAPI InstallThread(LPVOID)
{
	// The client loads d3d8.dll a good while after this DLL is injected, and
	// creating a device can briefly collide with the client creating its own,
	// so this waits for the module and then keeps retrying rather than giving
	// up on the first failure. Two minutes is far longer than the client takes
	// to start, and waiting costs nothing.
	for (int attempt = 0; attempt < 240; attempt++)
	{
		HMODULE d3d8 = GetModuleHandleA("d3d8.dll");
		if (d3d8 != NULL && HookPresent(d3d8))
		{
			if (g_period > 0)
			{
				timeBeginPeriod(1);
				Trace("[FpsLimiter] Capped at %d FPS.\n", (int)(g_frequency / g_period));
			}
			else
			{
				Trace("[FpsLimiter] Uncapped; measuring only.\n");
			}
			return 0;
		}
		Sleep(500);
	}

	Trace("[FpsLimiter] Gave up waiting for Direct3D 8; the client stays uncapped.\n");
	return 0;
}

void FpsLimiter_Install(int maxFps, bool debug)
{
	if (maxFps <= 0 && !debug)
	{
		return;
	}

	if (InterlockedExchange(&g_installed, 1) != 0)
	{
		return;
	}

	LARGE_INTEGER frequency;
	if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0)
	{
		Trace("[FpsLimiter] No performance counter; the client stays uncapped.\n");
		return;
	}

	g_frequency = frequency.QuadPart;
	g_period = (maxFps > 0) ? (g_frequency / maxFps) : 0;
	if (maxFps > 0 && g_period <= 0)
	{
		return;
	}

	g_debug = debug;
	if (g_debug)
	{
		GetModuleFileNameA(NULL, g_logPath, MAX_PATH);
		for (int i = (int)strlen(g_logPath) - 1; i >= 0; i--)
		{
			if (g_logPath[i] == '\\')
			{
				g_logPath[i + 1] = 0;
				break;
			}
		}
		strcat(g_logPath, "CLHook.fps.log");
	}

	// Not from DllMain itself: this waits on d3d8.dll, and the loader lock is
	// held until DllMain returns. CreateThread is the one thing DllMain may do
	// here - the thread does not start running until the lock is released.
	HANDLE thread = CreateThread(NULL, 0, InstallThread, NULL, 0, NULL);
	if (thread != NULL)
	{
		CloseHandle(thread);
	}
}
