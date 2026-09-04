#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <shellapi.h>
#include <string>

// {D7AE012A-EC7C-4CC3-AD34-7EE7155518CE}
static const CLSID CLSID_StorageHubCopyHook =
{ 0xd7ae012a, 0xec7c, 0x4cc3, { 0xad, 0x34, 0x7e, 0xe7, 0x15, 0x55, 0x18, 0xce } };
static HMODULE g_module{};
static long g_objects{};
static constexpr wchar_t MarkerPrefix[] = L"StorageHubDrop-";

static bool IsHexToken(const std::wstring& value) {
    if (value.size() != 32) return false;
    for (wchar_t c : value) if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') || (c >= L'A' && c <= L'F'))) return false;
    return true;
}

static std::wstring LocalAppDataPath() {
    wchar_t path[MAX_PATH]{};
    return SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, SHGFP_TYPE_CURRENT, path)) ? path : L"";
}

static bool StartsWithPath(const std::wstring& path, const std::wstring& root) {
    return path.size() > root.size() && _wcsnicmp(path.c_str(), root.c_str(), root.size()) == 0 && path[root.size()] == L'\\';
}

static bool WriteReceipt(const std::wstring& token, const std::wstring& destination) {
    auto base = LocalAppDataPath();
    if (base.empty()) return false;
    auto inbox = base + L"\\StorageHub\\ShellDropInbox";
    CreateDirectoryW((base + L"\\StorageHub").c_str(), nullptr);
    CreateDirectoryW(inbox.c_str(), nullptr);
    auto receipt = inbox + L"\\" + token + L".drop";
    auto temporary = receipt + L".tmp";
    int byteCount = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, destination.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (byteCount <= 1) return false;
    std::string utf8(static_cast<size_t>(byteCount), '\0');
    if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, destination.c_str(), -1, &utf8[0], byteCount, nullptr, nullptr)) return false;
    HANDLE file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_TEMPORARY, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    DWORD written{};
    bool ok = WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size() - 1), &written, nullptr) && written == utf8.size() - 1;
    ok = CloseHandle(file) && ok;
    if (ok) ok = MoveFileExW(temporary.c_str(), receipt.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != FALSE;
    if (!ok) DeleteFileW(temporary.c_str());
    return ok;
}

class CopyHook final : public ICopyHookW {
    LONG refs_{1};
public:
    CopyHook() { InterlockedIncrement(&g_objects); }
    ~CopyHook() { InterlockedDecrement(&g_objects); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (iid == IID_IUnknown || iid == IID_ICopyHookW) { *value = static_cast<ICopyHookW*>(this); AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&refs_)); }
    ULONG STDMETHODCALLTYPE Release() override { auto count = InterlockedDecrement(&refs_); if (!count) delete this; return static_cast<ULONG>(count); }
    UINT STDMETHODCALLTYPE CopyCallback(HWND, UINT operation, UINT, LPCWSTR source, DWORD, LPCWSTR destination, DWORD) override {
        if (operation != FO_COPY || !source || !destination) return IDYES;
        wchar_t sourceFull[32768]{};
        if (!GetFullPathNameW(source, ARRAYSIZE(sourceFull), sourceFull, nullptr)) return IDYES;
        auto markerRoot = LocalAppDataPath() + L"\\StorageHub\\DragMarkers";
        std::wstring sourcePath(sourceFull);
        if (!StartsWithPath(sourcePath, markerRoot)) return IDYES;
        std::wstring markerName(PathFindFileNameW(sourceFull));
        if (markerName.rfind(MarkerPrefix, 0) != 0) return IDYES;
        auto token = markerName.substr(ARRAYSIZE(MarkerPrefix) - 1);
        if (!IsHexToken(token)) return IDYES;

        wchar_t destinationFull[32768]{};
        if (!GetFullPathNameW(destination, ARRAYSIZE(destinationFull), destinationFull, nullptr)) return IDYES;
        std::wstring target(destinationFull);
        if (_wcsicmp(PathFindFileNameW(destinationFull), markerName.c_str()) == 0) {
            PathRemoveFileSpecW(destinationFull);
            target = destinationFull;
        } else {
            auto attributes = GetFileAttributesW(destinationFull);
            if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY)) {
            PathRemoveFileSpecW(destinationFull);
            target = destinationFull;
            }
        }
        // Veto Explorer's marker copy only after the destination receipt is durable.
        return WriteReceipt(token, target) ? IDNO : IDYES;
    }
};

class Factory final : public IClassFactory {
    LONG refs_{1};
public:
    Factory() { InterlockedIncrement(&g_objects); }
    ~Factory() { InterlockedDecrement(&g_objects); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (iid == IID_IUnknown || iid == IID_IClassFactory) { *value = static_cast<IClassFactory*>(this); AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&refs_)); }
    ULONG STDMETHODCALLTYPE Release() override { auto count = InterlockedDecrement(&refs_); if (!count) delete this; return static_cast<ULONG>(count); }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** value) override {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto instance = new (std::nothrow) CopyHook();
        if (!instance) return E_OUTOFMEMORY;
        auto result = instance->QueryInterface(iid, value); instance->Release(); return result;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { lock ? InterlockedIncrement(&g_objects) : InterlockedDecrement(&g_objects); return S_OK; }
};

static HRESULT SetRegistryValue(HKEY root, const std::wstring& path, const wchar_t* name, const std::wstring& value) {
    HKEY key{}; auto status = RegCreateKeyExW(root, path.c_str(), 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr);
    if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
    status = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()), static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key); return HRESULT_FROM_WIN32(status);
}

extern "C" BOOL WINAPI DllMain(HMODULE module, DWORD reason, LPVOID) { if (reason == DLL_PROCESS_ATTACH) { g_module = module; DisableThreadLibraryCalls(module); } return TRUE; }
extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** value) {
    if (clsid != CLSID_StorageHubCopyHook) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) Factory(); if (!factory) return E_OUTOFMEMORY;
    auto result = factory->QueryInterface(iid, value); factory->Release(); return result;
}
extern "C" HRESULT WINAPI DllCanUnloadNow() { return g_objects == 0 ? S_OK : S_FALSE; }
extern "C" HRESULT WINAPI DllRegisterServer() {
    wchar_t modulePath[32768]{}; if (!GetModuleFileNameW(g_module, modulePath, ARRAYSIZE(modulePath))) return HRESULT_FROM_WIN32(GetLastError());
    constexpr wchar_t clsid[] = L"{D7AE012A-EC7C-4CC3-AD34-7EE7155518CE}";
    auto result = SetRegistryValue(HKEY_CURRENT_USER, std::wstring(L"Software\\Classes\\CLSID\\") + clsid + L"\\InprocServer32", nullptr, modulePath);
    if (FAILED(result)) return result;
    result = SetRegistryValue(HKEY_CURRENT_USER, std::wstring(L"Software\\Classes\\CLSID\\") + clsid + L"\\InprocServer32", L"ThreadingModel", L"Apartment");
    if (FAILED(result)) return result;
    result = SetRegistryValue(HKEY_CURRENT_USER, L"Software\\Classes\\Directory\\shellex\\CopyHookHandlers\\StorageHub", nullptr, clsid);
    if (SUCCEEDED(result)) SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return result;
}
extern "C" HRESULT WINAPI DllUnregisterServer() {
    constexpr wchar_t clsid[] = L"{D7AE012A-EC7C-4CC3-AD34-7EE7155518CE}";
    RegDeleteTreeW(HKEY_CURRENT_USER, (std::wstring(L"Software\\Classes\\CLSID\\") + clsid).c_str());
    RegDeleteTreeW(HKEY_CURRENT_USER, L"Software\\Classes\\Directory\\shellex\\CopyHookHandlers\\StorageHub");
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr); return S_OK;
}
