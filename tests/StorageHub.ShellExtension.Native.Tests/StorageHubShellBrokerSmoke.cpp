#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>

int wmain() {
    wchar_t local[MAX_PATH]{}; wchar_t temporary[MAX_PATH]{};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, SHGFP_TYPE_CURRENT, local)) || !GetTempPathW(ARRAYSIZE(temporary), temporary)) return 10;
    GUID id{}; if (FAILED(CoCreateGuid(&id))) return 11;
    wchar_t tokenBuffer[33]{};
    swprintf_s(tokenBuffer, L"%08x%04x%04x%02x%02x%02x%02x%02x%02x%02x%02x", id.Data1, id.Data2, id.Data3,
        id.Data4[0], id.Data4[1], id.Data4[2], id.Data4[3], id.Data4[4], id.Data4[5], id.Data4[6], id.Data4[7]);
    std::wstring token(tokenBuffer);
    std::filesystem::path marker = std::filesystem::path(local) / L"StorageHub" / L"DragMarkers" / (L"StorageHubDrop-" + token);
    std::filesystem::path inbox = std::filesystem::path(local) / L"StorageHub" / L"ShellDropInbox";
    std::filesystem::path destination = std::filesystem::path(temporary) / (L"StorageHubBrokerSmoke-" + token);
    std::filesystem::create_directories(marker); std::filesystem::create_directories(inbox); std::filesystem::create_directories(destination);
    std::ofstream(marker / L".storagehub-drop") << "marker";
    auto from = marker.wstring() + L'\0'; from.push_back(L'\0');
    auto to = destination.wstring() + L'\0'; to.push_back(L'\0');
    SHFILEOPSTRUCTW operation{}; operation.wFunc = FO_COPY; operation.pFrom = from.c_str(); operation.pTo = to.c_str(); operation.fFlags = FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI;
    int result = SHFileOperationW(&operation);
    auto receipt = inbox / (token + L".drop");
    bool captured = std::filesystem::exists(receipt);
    bool nativeCopyPrevented = !std::filesystem::exists(destination / marker.filename());
    std::wcout << L"shell_result=" << result << L" captured=" << captured << L" native_copy_prevented=" << nativeCopyPrevented << std::endl;
    std::filesystem::remove_all(marker); std::filesystem::remove_all(destination); std::filesystem::remove(receipt);
    return captured && nativeCopyPrevented ? 0 : 1;
}
