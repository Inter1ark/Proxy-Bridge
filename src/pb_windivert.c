// WinDivert service repair: when WinDivertOpen fails because the "WinDivert" service
// entry belongs to another program (DPI bypass tools such as zapret or GoodbyeDPI ship
// the same driver under the same service name), is marked disabled or is otherwise
// stale, the entry is removed so that WinDivertOpen can re-create it from our own
// WinDivert64.sys. A driver that is loaded and in use by another program cannot be
// removed; that case is reported so the user can close the other program.
#include "pb_internal.h"

static const char *start_type_name(DWORD t)
{
    switch (t)
    {
        case SERVICE_DISABLED: return "disabled";
        case SERVICE_DEMAND_START: return "manual";
        case SERVICE_AUTO_START: return "auto";
        case SERVICE_BOOT_START: return "boot";
        case SERVICE_SYSTEM_START: return "system";
        default: return "unknown";
    }
}

PB_WD_REPAIR pb_windivert_repair_plan(DWORD open_err, DWORD start_type, DWORD state, BOOL our_path)
{
    switch (open_err)
    {
        case 2:    // ERROR_FILE_NOT_FOUND: our driver file is missing, or the entry points elsewhere
        case 3:    // ERROR_PATH_NOT_FOUND
            if (our_path) return PB_WD_NO_REPAIR;
            break;
        case 433:  // ERROR_NO_SUCH_DEVICE: the loaded driver is not ours, or the start failed
        case 1053: // ERROR_SERVICE_REQUEST_TIMEOUT
        case 1058: // ERROR_SERVICE_DISABLED
        case 1062: // ERROR_SERVICE_NOT_ACTIVE
        case 1067: // ERROR_PROCESS_ABORTED
        case 1072: // ERROR_SERVICE_MARKED_FOR_DELETE
            break;
        default:
            return PB_WD_NO_REPAIR; // access denied, signature, blocked by policy: nothing to remove
    }
    (void)start_type;
    if (state == SERVICE_RUNNING || state == SERVICE_START_PENDING || state == SERVICE_STOP_PENDING)
        return PB_WD_IN_USE; // a loaded driver (another program's, or a stuck one) must be stopped first
    return PB_WD_DELETE;     // disabled, pointing elsewhere or dead: remove it, WinDivertOpen re-creates it
}

static BOOL our_driver_path(char *buf, size_t size)
{
    HMODULE self = NULL;
    if (!GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            (LPCSTR)&our_driver_path, &self))
        return FALSE;
    char path[MAX_PATH];
    DWORD n = GetModuleFileNameA(self, path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return FALSE;
    char *slash = strrchr(path, '\\');
    if (!slash) return FALSE;
    *slash = 0;
    snprintf(buf, size, "%s\\WinDivert64.sys", path);
    return TRUE;
}

// Service image paths come in many spellings: "\??\C:\...", "C:\...", quoted, with system32 relative names.
static BOOL same_file(const char *a, const char *b)
{
    if (!a || !b) return FALSE;
    const char *pa = a, *pb = b;
    if (strncmp(pa, "\\??\\", 4) == 0) pa += 4;
    if (strncmp(pb, "\\??\\", 4) == 0) pb += 4;
    if (*pa == '"') pa++;
    if (*pb == '"') pb++;
    size_t la = strlen(pa), lb = strlen(pb);
    while (la > 0 && pa[la - 1] == '"') la--;
    while (lb > 0 && pb[lb - 1] == '"') lb--;
    return la == lb && _strnicmp(pa, pb, la) == 0;
}

BOOL pb_windivert_repair(DWORD open_err, char *detail, size_t detail_size)
{
    if (detail_size) detail[0] = 0;
    SC_HANDLE scm = OpenSCManagerA(NULL, NULL, SC_MANAGER_CONNECT);
    if (!scm)
    {
        snprintf(detail, detail_size, "cannot open the service manager (%lu)", GetLastError());
        return FALSE;
    }
    SC_HANDLE svc = OpenServiceA(scm, "WinDivert", SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS | SERVICE_STOP | DELETE);
    if (!svc)
    {
        DWORD e = GetLastError();
        snprintf(detail, detail_size, e == ERROR_SERVICE_DOES_NOT_EXIST ? "no WinDivert service entry" : "cannot open the WinDivert service (%lu)", e);
        CloseServiceHandle(scm);
        return FALSE;
    }

    char cfg_buf[8192];
    QUERY_SERVICE_CONFIGA *cfg = (QUERY_SERVICE_CONFIGA *)cfg_buf;
    DWORD needed = 0;
    DWORD start_type = (DWORD)-1;
    const char *image = "";
    if (QueryServiceConfigA(svc, cfg, sizeof(cfg_buf), &needed))
    {
        start_type = cfg->dwStartType;
        image = cfg->lpBinaryPathName ? cfg->lpBinaryPathName : "";
    }
    SERVICE_STATUS st;
    DWORD state = 0;
    if (QueryServiceStatus(svc, &st)) state = st.dwCurrentState;

    char ours[MAX_PATH] = "";
    BOOL have_ours = our_driver_path(ours, sizeof(ours));
    BOOL same = have_ours && same_file(image, ours);
    log_message("WinDivert service entry: start=%s, state=%lu, image=%s (%s)", start_type_name(start_type),
        (unsigned long)state, image[0] ? image : "?", same ? "our driver" : "not our driver");

    PB_WD_REPAIR plan = pb_windivert_repair_plan(open_err, start_type, state, same);
    BOOL retry = FALSE;
    switch (plan)
    {
        case PB_WD_IN_USE:
            snprintf(detail, detail_size, "the WinDivert driver (%s) is loaded and in use by another program", image[0] ? image : "?");
            if (ControlService(svc, SERVICE_CONTROL_STOP, &st))
            {
                Sleep(500);
                if (DeleteService(svc) || GetLastError() == ERROR_SERVICE_MARKED_FOR_DELETE)
                {
                    log_message("WinDivert service stopped and its entry removed");
                    retry = TRUE;
                }
            }
            break;
        case PB_WD_DELETE:
            if (DeleteService(svc))
            {
                log_message("Removed the stale WinDivert service entry (start=%s, image=%s); retrying with our driver",
                    start_type_name(start_type), image[0] ? image : "?");
                retry = TRUE;
            }
            else
            {
                DWORD e = GetLastError();
                snprintf(detail, detail_size, e == ERROR_SERVICE_MARKED_FOR_DELETE
                    ? "a stale WinDivert service entry is marked for deletion; a reboot removes it"
                    : "could not remove the stale WinDivert service entry (%lu)", e);
            }
            break;
        default:
            snprintf(detail, detail_size, "the WinDivert service entry (start=%s, image=%s) is not the cause",
                start_type_name(start_type), image[0] ? image : "?");
            break;
    }
    CloseServiceHandle(svc);
    CloseServiceHandle(scm);
    return retry;
}
