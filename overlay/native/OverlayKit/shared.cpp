// The shared-memory side of the host: the directory, this process's host slot, the log.
#include "host.h"
#include <sddl.h>
#include <stdarg.h>
#include <stdio.h>

void Log(const char* format, ...)
{
    static SRWLOCK lock = SRWLOCK_INIT;
    wchar_t dir[MAX_PATH];
    DWORD n = GetTempPathW(MAX_PATH, dir);
    if (n == 0 || n > MAX_PATH - 40)
        return;
    wcscat_s(dir, L"OverlayKit");
    CreateDirectoryW(dir, nullptr);
    wchar_t path[MAX_PATH];
    swprintf_s(path, L"%s\\host-%lu.log", dir, GetCurrentProcessId());

    char line[1024];
    SYSTEMTIME t;
    GetLocalTime(&t);
    int prefix = sprintf_s(line, "%02d:%02d:%02d.%03d  ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list args;
    va_start(args, format);
    vsnprintf(line + prefix, sizeof(line) - prefix - 2, format, args);
    va_end(args);
    strcat_s(line, "\r\n");

    AcquireSRWLockExclusive(&lock);
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, 0, nullptr);
    if (file != INVALID_HANDLE_VALUE)
    {
        DWORD written;
        WriteFile(file, line, (DWORD)strlen(line), &written, nullptr);
        CloseHandle(file);
    }
    ReleaseSRWLockExclusive(&lock);
}

OkDirectory* Directory::Get()
{
    if (view_)
        return view_->magic == OK_MAGIC && view_->version == OK_VERSION ? view_ : nullptr;
    ULONGLONG now = GetTickCount64();
    if (now < nextAttempt_)
        return nullptr;
    nextAttempt_ = now + 1000;

    SECURITY_ATTRIBUTES sa{ sizeof(sa) };
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(OK_MAPPING_SDDL, SDDL_REVISION_1, &sd, nullptr))
        sa.lpSecurityDescriptor = sd;
    mapping_ = CreateFileMappingW(INVALID_HANDLE_VALUE, sd ? &sa : nullptr, PAGE_READWRITE, 0, sizeof(OkDirectory), OK_DIRECTORY_NAME);
    bool created = mapping_ && GetLastError() != ERROR_ALREADY_EXISTS;
    if (sd)
        LocalFree(sd);
    if (!mapping_)
    {
        mapping_ = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, OK_DIRECTORY_NAME);
        if (!mapping_)
            return nullptr;
    }
    view_ = static_cast<OkDirectory*>(MapViewOfFile(mapping_, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(OkDirectory)));
    if (!view_)
    {
        CloseHandle(mapping_);
        mapping_ = nullptr;
        return nullptr;
    }
    if (created)
    {
        view_->version = OK_VERSION;
        view_->hostCapacity = OK_MAX_HOSTS;
        view_->layerCapacity = OK_MAX_LAYERS;
        MemoryBarrier();
        view_->magic = OK_MAGIC;
    }
    Log("directory %s", created ? "created" : "opened");
    return view_->magic == OK_MAGIC && view_->version == OK_VERSION ? view_ : nullptr;
}

static bool ProcessAlive(uint32_t pid)
{
    HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, pid);
    if (!process)
        return GetLastError() == ERROR_ACCESS_DENIED; // exists, just not ours to open
    bool alive = WaitForSingleObject(process, 0) == WAIT_TIMEOUT;
    CloseHandle(process);
    return alive;
}

OkHost* Directory::Host()
{
    OkDirectory* dir = Get();
    if (!dir)
        return nullptr;
    uint32_t pid = GetCurrentProcessId();
    if (host_ && host_->pid == pid)
        return host_;
    host_ = nullptr;

    for (auto& h : dir->hosts)
        if (h.pid == pid)
            return host_ = &h;
    for (auto& h : dir->hosts)
    {
        uint32_t old = h.pid;
        if (old != 0 && ProcessAlive(old))
            continue;
        if ((uint32_t)InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(&h.pid), (LONG)pid, (LONG)old) != old)
            continue;
        wchar_t path[MAX_PATH];
        DWORD n = GetModuleFileNameW(nullptr, path, MAX_PATH);
        const wchar_t* name = path;
        for (DWORD i = 0; i < n; i++)
            if (path[i] == L'\\' || path[i] == L'/')
                name = path + i + 1;
        char exe[32] = {};
        for (int i = 0; i < 31 && name[i]; i++)
            exe[i] = (char)towlower(name[i] < 128 ? name[i] : L'?');
        memcpy(h.exeName, exe, sizeof(exe));
        h.frameCount = 0;
        Log("host slot %d claimed for %s", (int)(&h - dir->hosts), exe);
        return host_ = &h;
    }
    return nullptr;
}

void LayerCache::Close()
{
    if (pixels)
        UnmapViewOfFile(pixels);
    if (mapping)
        CloseHandle(mapping);
    pixels = nullptr;
    mapping = nullptr;
    bufferName.clear();
    generation = 0;
    uploadedSequence = UINT32_MAX;
    view.Reset();
    texture.Reset();
    width = height = 0;
}
