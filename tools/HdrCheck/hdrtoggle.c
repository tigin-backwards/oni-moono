// hdrtoggle: each active display's advanced colour (HDR) state, and switching it, through the
// display configuration API (wingdi.h): QueryDisplayConfig, DisplayConfigGetDeviceInfo with
// DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO and _GET_SOURCE_NAME, and
// DisplayConfigSetDeviceInfo with DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE. No registry,
// no settings app. A test tool of the HDR check (README.md), not part of the product.
//   hdrtoggle list                       one line per display: GDI name, supported, enabled
//   hdrtoggle set \\.\DISPLAY1 on|off    switch it; exit 0 when the state read back is the one set
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>

#pragma comment(lib, "user32.lib")

static int Paths(DISPLAYCONFIG_PATH_INFO **paths, UINT32 *count)
{
    UINT32 np = 0, nm = 0;
    if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &np, &nm) != ERROR_SUCCESS) return 0;
    DISPLAYCONFIG_PATH_INFO *p = calloc(np, sizeof *p);
    DISPLAYCONFIG_MODE_INFO *m = calloc(nm, sizeof *m);
    if (!p || !m || QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &np, p, &nm, m, NULL) != ERROR_SUCCESS) { free(p); free(m); return 0; }
    free(m);
    *paths = p;
    *count = np;
    return 1;
}

static void SourceName(const DISPLAYCONFIG_PATH_INFO *p, wchar_t name[CCHDEVICENAME])
{
    DISPLAYCONFIG_SOURCE_DEVICE_NAME s = {0};
    s.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
    s.header.size = sizeof s;
    s.header.adapterId = p->sourceInfo.adapterId;
    s.header.id = p->sourceInfo.id;
    name[0] = 0;
    if (DisplayConfigGetDeviceInfo(&s.header) == ERROR_SUCCESS) wcsncpy(name, s.viewGdiDeviceName, CCHDEVICENAME);
}

static LONG ColorInfo(const DISPLAYCONFIG_PATH_INFO *p, DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO *c)
{
    memset(c, 0, sizeof *c);
    c->header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
    c->header.size = sizeof *c;
    c->header.adapterId = p->targetInfo.adapterId;
    c->header.id = p->targetInfo.id;
    return DisplayConfigGetDeviceInfo(&c->header);
}

int wmain(int argc, wchar_t **argv)
{
    DISPLAYCONFIG_PATH_INFO *paths;
    UINT32 n;
    if (!Paths(&paths, &n)) { fprintf(stderr, "QueryDisplayConfig failed\n"); return 2; }
    int set = argc >= 4 && wcscmp(argv[1], L"set") == 0;
    int on = set && wcscmp(argv[3], L"on") == 0;
    int found = 0, rc = 0;
    for (UINT32 i = 0; i < n; i++)
    {
        wchar_t name[CCHDEVICENAME];
        SourceName(&paths[i], name);
        DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO c;
        LONG r = ColorInfo(&paths[i], &c);
        if (!set)
        {
            if (r != ERROR_SUCCESS) wprintf(L"%ls advanced colour info failed (%ld)\n", name, r);
            else wprintf(L"%ls supported %u enabled %u wideColorEnforced %u forceDisabled %u bits %u\n", name, c.advancedColorSupported, c.advancedColorEnabled,
                         c.wideColorEnforced, c.advancedColorForceDisabled, c.bitsPerColorChannel);
            continue;
        }
        if (_wcsicmp(name, argv[2]) != 0) continue;
        found = 1;
        if (r != ERROR_SUCCESS || !c.advancedColorSupported) { wprintf(L"%ls does not support advanced colour\n", name); rc = 3; continue; }
        DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE s = {0};
        s.header.type = DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE;
        s.header.size = sizeof s;
        s.header.adapterId = paths[i].targetInfo.adapterId;
        s.header.id = paths[i].targetInfo.id;
        s.enableAdvancedColor = on ? 1 : 0;
        LONG w = DisplayConfigSetDeviceInfo(&s.header);
        Sleep(1500);
        ColorInfo(&paths[i], &c);
        wprintf(L"%ls set %ls: DisplayConfigSetDeviceInfo %ld, enabled now %u\n", name, on ? L"on" : L"off", w, c.advancedColorEnabled);
        if (w != ERROR_SUCCESS || (int)c.advancedColorEnabled != on) rc = 1;
    }
    if (set && !found) { wprintf(L"no active display %ls\n", argv[2]); rc = 4; }
    free(paths);
    return rc;
}
