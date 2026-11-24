// hdrtoggle: each active display's advanced colour (HDR) state, and switching it, through the
// display configuration API (wingdi.h): QueryDisplayConfig, DisplayConfigGetDeviceInfo with
// DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO and _GET_SOURCE_NAME, and
// DisplayConfigSetDeviceInfo with DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE (and _GET_ADVANCED_COLOR_INFO_2). No registry,
// no settings app. A test tool of the HDR check (README.md), not part of the product.
//   hdrtoggle list                       one line per display: GDI name, supported, enabled
//   hdrtoggle set \\.\DISPLAY1 on|off    switch HDR (SET_HDR_STATE, Windows 11 24H2); exit 0 when
//                                        the active colour mode read back is the one asked for
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

// Windows 11 24H2's view (SDK 10.0.26100): which advanced colour mode is active (SDR, wide colour,
// HDR) and whether HDR itself is supported, apart from wide colour; DISPLAYCONFIG_SET_HDR_STATE
// switches HDR alone. The older info's advancedColorEnabled is also set for wide colour (automatic
// colour management) on an SDR panel, which is what j0009165 switched on.
static LONG ColorInfo2(const DISPLAYCONFIG_PATH_INFO *p, DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 *c)
{
    memset(c, 0, sizeof *c);
    c->header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2;
    c->header.size = sizeof *c;
    c->header.adapterId = p->targetInfo.adapterId;
    c->header.id = p->targetInfo.id;
    return DisplayConfigGetDeviceInfo(&c->header);
}

static const wchar_t *Mode(DISPLAYCONFIG_ADVANCED_COLOR_MODE m)
{
    return m == DISPLAYCONFIG_ADVANCED_COLOR_MODE_SDR ? L"SDR" : m == DISPLAYCONFIG_ADVANCED_COLOR_MODE_WCG ? L"WCG" : m == DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR ? L"HDR" : L"?";
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
            else wprintf(L"%ls supported %u enabled %u wideColorEnforced %u forceDisabled %u bits %u", name, c.advancedColorSupported, c.advancedColorEnabled,
                         c.wideColorEnforced, c.advancedColorForceDisabled, c.bitsPerColorChannel);
            DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 c2;
            LONG r2 = ColorInfo2(&paths[i], &c2);
            if (r2 == ERROR_SUCCESS)
                wprintf(L"; hdrSupported %u hdrUserEnabled %u wideColorSupported %u wideColorUserEnabled %u active %ls\n", c2.highDynamicRangeSupported,
                        c2.highDynamicRangeUserEnabled, c2.wideColorSupported, c2.wideColorUserEnabled, Mode(c2.activeColorMode));
            else wprintf(L"; advanced colour info 2 failed (%ld)\n", r2);
            continue;
        }
        if (_wcsicmp(name, argv[2]) != 0) continue;
        found = 1;
        if (r != ERROR_SUCCESS || !c.advancedColorSupported) { wprintf(L"%ls does not support advanced colour\n", name); rc = 3; continue; }
        // HDR itself (SET_HDR_STATE), not wide colour: only on a display that supports HDR.
        DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 c2;
        if (ColorInfo2(&paths[i], &c2) != ERROR_SUCCESS || !c2.highDynamicRangeSupported) { wprintf(L"%ls does not support HDR\n", name); rc = 3; continue; }
        DISPLAYCONFIG_SET_HDR_STATE s = {0};
        s.header.type = DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE;
        s.header.size = sizeof s;
        s.header.adapterId = paths[i].targetInfo.adapterId;
        s.header.id = paths[i].targetInfo.id;
        s.enableHdr = on ? 1 : 0;
        LONG w = DisplayConfigSetDeviceInfo(&s.header);
        // The mode changes asynchronously: read it back for up to 10 s.
        int want = on ? DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR : -1;
        for (int t = 0; t < 40; t++)
        {
            Sleep(250);
            ColorInfo2(&paths[i], &c2);
            if (on ? (int)c2.activeColorMode == want : c2.activeColorMode != DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR) break;
        }
        wprintf(L"%ls set HDR %ls: DisplayConfigSetDeviceInfo %ld, active now %ls\n", name, on ? L"on" : L"off", w, Mode(c2.activeColorMode));
        if (w != ERROR_SUCCESS || (on ? c2.activeColorMode != DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR : c2.activeColorMode == DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR)) rc = 1;
    }
    if (set && !found) { wprintf(L"no active display %ls\n", argv[2]); rc = 4; }
    free(paths);
    return rc;
}
