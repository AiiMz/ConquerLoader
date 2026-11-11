// ConquerCiperrHook.cpp : Defines the exported functions for the DLL application.

#include "stdafx.h"
#include "hook.h"
#include <iostream>
#include "LegacyCipher.h"
#include <fstream>
#include <sstream>
#include <string>
#include "buffer.h"
#include "FpsLimiter.h"
#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "user32.lib")

using namespace std;
using namespace ConquerCipherHookBufferUtils;

#define lib_func(lib, func) (GetProcAddress(GetModuleHandleA(lib), func))

char tqFormat[] = { 0x25, 0x73, 0xA3, 0xAC, 0xA1, 0xA3, 0x66, 0x64, 0x6A, 0x66, 0x2C, 0x6A, 0x6B, 0x67, 0x66, 0x6B, 0x6C, 0x00 };
char szPassword[32];
HOOK_STUB snprintf_stub, send_stub, connect_stub, shellexec_stub;
CLegacyCipher* legacy;


int csv3_snprintf(char* str, int len, const char* format, ...)
{
	va_list args;
	va_start(args, format);
	if (strcmp(format, tqFormat) == 0)
	{
		char* password = va_arg(args, PCHAR);
		strcpy(szPassword, password);
		str[0] = NULL;
		strcat(str, password);
		strcat(str, &tqFormat[2]);

		//MessageBoxA(NULL, szPassword, "Password", MB_OK);

		return strlen(str);
	}
	else
	{
		return vsnprintf(str, len, format, args);
	}
}
int __stdcall csv3_connect(SOCKET s, sockaddr_in* name, int len)
{
	int rPort;

	rPort = ntohs(name->sin_port);
	if (rPort >= 9950 && rPort <= 9970)
	{
		legacy = new CLegacyCipher();
		legacy->GenerateIV(0x13FA0F9D, 0x6D5C7962);
	}

	typedef int (__stdcall *LPFCONNECT)(SOCKET,sockaddr_in*,int);
	return ((LPFCONNECT)connect_stub.Address)(s, name, len);
}
//char password_encryption_key[8] = { 'C', 'O', 'L', '1', '0', '0', '5', 'R' }; // End with R for Releases, T for Test Version
////XOR Cryptor
//string XOR_PasswordEncrypt(string toEncrypt) {
//	string output = toEncrypt;
//
//	for (int i = 0; i < toEncrypt.size(); i++)
//		output[i] = toEncrypt[i] ^ password_encryption_key[i % (sizeof(password_encryption_key) / sizeof(char))];
//
//	return output;
//}
int __stdcall csv3_send(SOCKET s, PBYTE buf, int len, int flags)
{
	if (legacy)
	{
		legacy->Decrypt(buf, len);

		// Password without encryption
		strcat((char*)&buf[72], szPassword);

		// Password with encryption for Canyon Source
		//buffer::encrypt(szPassword, strlen(szPassword));
		//buffer::write_uint16(buf, 1542, 2);
		//buffer::write_char(buf, szPassword, strlen(szPassword), 60);

		// Password with XOR encription
		/*char* szPasswordEncrypted = new char[XOR_PasswordEncrypt(szPassword).length() + 1];
		strcpy(szPasswordEncrypted, XOR_PasswordEncrypt(szPassword).c_str());
		strcat((char*)&buf[72], szPasswordEncrypted);
		delete[] szPasswordEncrypted;*/

		legacy->Encrypt(buf, len);
		delete legacy;
		legacy = NULL;
	}

	// invoke old method
	typedef int (__stdcall *LPFSEND)(SOCKET,PBYTE,int,int);
	return ((LPFSEND)send_stub.Address)(s,buf,len,flags);
}
/**
* One sign-out page per group of clients, not one per client.
*
* Every client opens the sign-out page as it closes, so closing three clients
* opened three tabs. Each client maps the same small named section at startup;
* the first one to sign out flips the flag in it and opens the page, and every
* later one skips. The section lives as long as any client holds it, so the
* flag resets by itself once the last client has exited - the next session of
* clients gets its one page again. It is mapped at init rather than at exit
* because a client that created it on its way out would destroy it with itself.
* A crashed client needs no cleanup: the OS closes its handle.
*/
static volatile LONG* signout_flag = NULL;

static void signout_flag_init()
{
	HANDLE hMap = CreateFileMappingA(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, sizeof(LONG),
		"Local\\EternalAbyss.SignoutPageOpened");
	if (hMap == NULL)
		return; // no flag means the old behaviour: every client opens the page
	signout_flag = (volatile LONG*)MapViewOfFile(hMap, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(LONG));
	// the handle is deliberately kept open for the life of the process
}

int __stdcall Shellexec(HWND hWnd, CHAR* lpOperation, CHAR* lpFile, CHAR* lpParams, CHAR* lpDir, INT nCmd) {
	if (lpFile != NULL && strcmp("http://co.99.com/signout/", lpFile) == 0) {
		if (signout_flag != NULL && InterlockedCompareExchange(signout_flag, 1, 0) != 0)
			return 42; // another client already opened it; > 32 is ShellExecute's success
		lpFile = "https://eternalabyssco.com";
	}
	typedef INT(__stdcall* Shellexec)(HWND, CHAR*, CHAR*, CHAR*, CHAR*, INT);
	return ((Shellexec)shellexec_stub.Address)(hWnd, lpOperation, lpFile, lpParams, lpDir, nCmd);
}

BOOL __stdcall csv3_handler(PBYTE msg)
{
	return TRUE; // allow TQ to process this packet
}

PBYTE tq_handler;
__declspec(naked) BOOL __stdcall csv3_handler_hook()
{
	__asm
	{
		nop
		push ebp
		mov ebp, esp
		push dword ptr[ebp+12]
		push dword ptr[ebp+8]
		// ecx is already set
		mov eax, tq_handler		// this method cleans up after itself __stdcall
		call eax
		test eax,eax
		jz _leave
		// we have a packet... pass it to csv3_handler
		push dword ptr[ebp+12]
		call csv3_handler
		//
		_leave:
		pop ebp
		ret 8 // clean up after self
	}
}

PBYTE tq_send;
__declspec(naked) DWORD __stdcall csv3_tqsend(PBYTE msg, DWORD len)
{
	__asm
	{
		nop
		mov eax, tq_send
		jmp eax
	}
}

/**
* Reads one integer out of the CLHook.ini the loader writes next to the client.
*
* MAX_FPS absent or 0 means no cap, which is how the client has always run;
* FPS_DEBUG absent or 0 means no measurement either. The ini
* lives beside conquer.exe rather than beside this DLL because the Env_DX8 and
* Env_DX9 layouts run a copy of the executable from a subfolder and the loader
* copies the ini in next to it.
*/
static int read_ini_int(const char* key, int fallback)
{
	char szConfig[MAX_PATH];
	GetModuleFileNameA(NULL, szConfig, MAX_PATH);
	for (int i = (int)strlen(szConfig) - 1; i >= 0; i--)
	{
		if (szConfig[i] == '\\')
		{
			szConfig[i + 1] = 0;
			break;
		}
	}
	strcat(szConfig, "CLHook.ini");
	return GetPrivateProfileIntA("CLHook", key, fallback, szConfig);
}

void csv3_init(HMODULE hModule)
{
	float f = 0; // load support for floating operations -- thx ntl3fty!
	//CFlashFix flash;
	//flash.Hook();
	FpsLimiter_Install(read_ini_int("MAX_FPS", 0), read_ini_int("FPS_DEBUG", 0) != 0);
	memset(szPassword, 0, 32);
	signout_flag_init();
	CreateHook32(lib_func("msvcr90.dll", "_snprintf"), csv3_snprintf, &snprintf_stub);
	CreateHook32(lib_func("ws2_32.dll", "send"), csv3_send, &send_stub);
	CreateHook32(lib_func("ws2_32.dll", "connect"), csv3_connect, &connect_stub);
	CreateHook32(lib_func("Shell32.dll", "ShellExecuteA"), Shellexec, &shellexec_stub);
}