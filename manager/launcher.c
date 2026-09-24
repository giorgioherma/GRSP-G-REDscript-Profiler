#include <windows.h>

#define APP_RELATIVE_PATH L"\\app\\G-REDscript-Profiler.App.exe"
#define PACKAGE_ROOT_ENV L"G_REDSCRIPT_PROFILER_PACKAGE_ROOT"

static const wchar_t* SkipExecutableToken(const wchar_t* commandLine)
{
    const wchar_t* p = commandLine;

    while (*p == L' ' || *p == L'\t')
        ++p;

    if (*p == L'"')
    {
        ++p;
        while (*p && *p != L'"')
            ++p;
        if (*p == L'"')
            ++p;
    }
    else
    {
        while (*p && *p != L' ' && *p != L'\t')
            ++p;
    }

    while (*p == L' ' || *p == L'\t')
        ++p;

    return p;
}

static int Fail(const wchar_t* message)
{
    MessageBoxW(
        NULL,
        message,
        L"G-REDscript Profiler",
        MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
    return 1;
}

int WINAPI wWinMain(
    HINSTANCE instance,
    HINSTANCE previousInstance,
    PWSTR commandLineUnused,
    int showCommand)
{
    (void)instance;
    (void)previousInstance;
    (void)commandLineUnused;
    (void)showCommand;

    wchar_t packageRoot[32768];
    DWORD rootLength = GetModuleFileNameW(NULL, packageRoot, ARRAYSIZE(packageRoot));
    if (rootLength == 0 || rootLength >= ARRAYSIZE(packageRoot))
        return Fail(L"Could not resolve the profiler package directory.");

    wchar_t* separator = packageRoot + rootLength;
    while (separator > packageRoot &&
           separator[-1] != L'\\' &&
           separator[-1] != L'/')
    {
        --separator;
    }

    if (separator == packageRoot)
        return Fail(L"Could not resolve the profiler package directory.");

    separator[-1] = L'\0';

    const SIZE_T rootChars = (SIZE_T)lstrlenW(packageRoot);
    const SIZE_T relativeChars = (SIZE_T)lstrlenW(APP_RELATIVE_PATH);
    if (rootChars + relativeChars + 1 >= ARRAYSIZE(packageRoot))
        return Fail(L"The profiler package path is too long.");

    wchar_t appPath[32768];
    CopyMemory(appPath, packageRoot, rootChars * sizeof(wchar_t));
    CopyMemory(
        appPath + rootChars,
        APP_RELATIVE_PATH,
        (relativeChars + 1) * sizeof(wchar_t));

    DWORD attributes = GetFileAttributesW(appPath);
    if (attributes == INVALID_FILE_ATTRIBUTES ||
        (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
    {
        return Fail(
            L"The internal application is missing.\r\n\r\n"
            L"Expected: app\\G-REDscript-Profiler.App.exe\r\n\r\n"
            L"Re-extract the complete profiler package.");
    }

    SetEnvironmentVariableW(PACKAGE_ROOT_ENV, packageRoot);

    const wchar_t* forwarded = SkipExecutableToken(GetCommandLineW());
    const BOOL headless = forwarded != NULL && *forwarded != L'\0';

    const SIZE_T appChars = (SIZE_T)lstrlenW(appPath);
    const SIZE_T forwardedChars = headless ? (SIZE_T)lstrlenW(forwarded) : 0;
    const SIZE_T commandChars = appChars + forwardedChars + 5;

    wchar_t* childCommandLine = (wchar_t*)HeapAlloc(
        GetProcessHeap(),
        HEAP_ZERO_MEMORY,
        commandChars * sizeof(wchar_t));
    if (childCommandLine == NULL)
        return Fail(L"Could not allocate memory to start the profiler.");

    SIZE_T pos = 0;
    childCommandLine[pos++] = L'"';
    CopyMemory(childCommandLine + pos, appPath, appChars * sizeof(wchar_t));
    pos += appChars;
    childCommandLine[pos++] = L'"';

    if (headless)
    {
        childCommandLine[pos++] = L' ';
        CopyMemory(childCommandLine + pos, forwarded, (forwardedChars + 1) * sizeof(wchar_t));
    }
    else
    {
        childCommandLine[pos] = L'\0';
    }

    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    ZeroMemory(&startup, sizeof(startup));
    ZeroMemory(&process, sizeof(process));
    startup.cb = sizeof(startup);

    BOOL started = CreateProcessW(
        appPath,
        childCommandLine,
        NULL,
        NULL,
        TRUE,
        0,
        NULL,
        packageRoot,
        &startup,
        &process);

    HeapFree(GetProcessHeap(), 0, childCommandLine);

    if (!started)
        return Fail(L"Windows could not start the internal profiler application.");

    CloseHandle(process.hThread);

    if (!headless)
    {
        CloseHandle(process.hProcess);
        return 0;
    }

    WaitForSingleObject(process.hProcess, INFINITE);

    DWORD exitCode = 1;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hProcess);

    return (int)exitCode;
}
