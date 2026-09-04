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
    auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(initialized)) return 12;
    CLSID brokerId{};
    auto parsed = CLSIDFromString(L"{D7AE012A-EC7C-4CC3-AD34-7EE7155518CE}", &brokerId);
    if (FAILED(parsed)) { CoUninitialize(); return 13; }
    ICopyHookW* hook{};
    auto activated = CoCreateInstance(brokerId, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&hook));
    if (FAILED(activated)) { CoUninitialize(); return 14; }
    auto decision = hook->CopyCallback(
        nullptr, FO_COPY, 0, marker.c_str(), 0, destination.c_str(), 0);
    hook->Release();
    CoUninitialize();
    auto receipt = inbox / (token + L".drop");
    bool captured = std::filesystem::exists(receipt);
    bool copyVetoed = decision == IDNO;
    std::wcout << L"callback_decision=" << decision << L" captured=" << captured << L" copy_vetoed=" << copyVetoed << std::endl;
    std::filesystem::remove_all(marker); std::filesystem::remove_all(destination); std::filesystem::remove(receipt);
    return captured && copyVetoed ? 0 : 1;
}
