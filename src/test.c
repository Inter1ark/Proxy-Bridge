#include <windows.h>

#define PROXYBRIDGE_API __declspec(dllexport)

static int test_var = 0;

PROXYBRIDGE_API void TestFunction(int value)
{
    test_var = value;
}

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpReserved)
{
    return TRUE;
}
