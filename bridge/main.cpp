// MechVibeBridge - standalone Win32 relay for MW5: Clans.
//
// Reads 32-byte EventData packets from a named pipe (written by the UE4SS Lua mod
// via string.pack) and republishes them into the "MechVibeMemory" shared
// memory ring buffer that MechVibe.exe reads.
//
// This deliberately has ZERO UE4SS/Unreal dependencies - it builds with nothing but
// cl.exe - which is the whole point: UE4SS C++ mods require building UE4SS core from
// source, which needs a private Epic-org submodule we can't access.
//
// The memory layout, ring-buffer indexing and the BridgeClosed shutdown packet are
// copied verbatim in behaviour from the original UEVR plugin
// (MW5-UEVR-Plugins/src/mechshaker_bridge/MechVibeBridge.cpp) so MechVibe.exe
// sees byte-for-byte the same protocol it always has.

#include <windows.h>

#define MECHVIBE_VERSION "1.0.0"

#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>

#include <map>
#include <mutex>
#include <set>
#include <string>
#include <thread>

namespace {

struct EventData {
    int32_t EventCode;
    int32_t Int0;
    float   Float0;
    float   Float1;
    float   Float2;
    float   Float3;
    float   Float4;
    float   Float5;
};

struct ControlBlock {
    int64_t WriteIndex;
    int64_t PacketNumber;
};

static_assert(sizeof(EventData) == 32, "EventData must stay at the 32-byte wire size");
static_assert(sizeof(ControlBlock) == 16, "ControlBlock must stay at the 16-byte header size");

constexpr int32_t BUFFER_SIZE             = 128;
constexpr size_t  BUFFER_SIZE_BYTES       = sizeof(EventData) * BUFFER_SIZE;
constexpr size_t  TOTAL_BUFFER_SIZE_BYTES = BUFFER_SIZE_BYTES + sizeof(ControlBlock);

constexpr const char* SHARED_MEMORY_NAME = "MechVibeMemory";
constexpr const char* PIPE_NAME          = "\\\\.\\pipe\\MechVibeBridge";
constexpr const char* SINGLE_INSTANCE_MUTEX = "MechVibeBridge_SingleInstance";

constexpr int32_t EVENT_CODE_BRIDGE_CLOSED = -1;

HANDLE        g_mapFile           = nullptr;
LPVOID        g_buffer            = nullptr;
ControlBlock* g_control           = nullptr;
int32_t       g_currentWriteIndex = 0;

// Clients are served on their own threads, so the ring-buffer write and the
// counters both need guarding.
std::mutex    g_writeMutex;
bool          g_verbose           = false;
volatile LONG g_shuttingDown      = 0;

// Console-output filters. These affect ONLY what gets printed - every packet is
// always relayed to shared memory regardless.
std::set<int32_t> g_onlyCodes;     // if non-empty, print only these
std::set<int32_t> g_excludeCodes;  // never print these

// Per-event-code totals, printed as a summary on exit. Damage alone runs to
// thousands of events per fight, so the counts say more than the scrollback does.
std::map<int32_t, uint64_t> g_eventCounts;

FILE* g_logFile = nullptr;

// Everything the bridge prints goes to the console AND to a log file next to the
// exe. When the Lua mod auto-starts us the console is minimised behind a
// fullscreen game, so the file is usually the only way anyone actually reads this.
void Out(const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    vprintf(fmt, args);
    va_end(args);

    if (g_logFile) {
        va_start(args, fmt);
        vfprintf(g_logFile, fmt, args);
        va_end(args);
        fflush(g_logFile);
    }
}

// Log next to the exe rather than the working directory, which is wherever the
// game happened to launch us from.
bool OpenLogFile() {
    char exePath[MAX_PATH]{};
    const DWORD len = GetModuleFileNameA(nullptr, exePath, MAX_PATH);
    if (len == 0 || len == MAX_PATH) return false;

    std::string path{exePath};
    const size_t slash = path.find_last_of("\\/");
    if (slash == std::string::npos) return false;
    path = path.substr(0, slash + 1) + "bridge.log";

    // _SH_DENYWR, not fopen: plain fopen takes the file exclusively, so nothing can
    // read the log while the bridge is running - which is exactly when you want to
    // look at it. This lets readers in while keeping other writers out.
    g_logFile = _fsopen(path.c_str(), "w", _SH_DENYWR);
    if (!g_logFile) return false;

    printf("Logging to: %s\n", path.c_str());
    return true;
}

const char* EventCodeName(int32_t code) {
    switch (code) {
        case -2: return "ClearFX";
        case -1: return "BridgeClosed";
        case 0:  return "NULL";
        case 1:  return "TorsoTwist";
        case 2:  return "Footstep";
        case 3:  return "Trace";
        case 4:  return "Projectile";
        case 5:  return "Missiles";
        case 6:  return "AMS";
        case 7:  return "Melee";
        case 8:  return "Dropship";
        case 9:  return "JumpJets";
        case 10: return "Airborne";
        case 11: return "Landed";
        case 12: return "MASC";
        case 13: return "Powering";
        case 14: return "PartDestruction";
        case 15: return "Damaged";
        // [Clans extension] not present in the original protocol.
        case 16: return "Impulse";
        default: return "<unknown>";
    }
}

bool ShouldPrint(int32_t code) {
    if (!g_verbose) return false;
    if (g_excludeCodes.count(code)) return false;
    if (!g_onlyCodes.empty() && !g_onlyCodes.count(code)) return false;
    return true;
}

// Parses "3,4,5" into a set of event codes. Accepts negative codes too (-1/-2).
void ParseCodeList(const char* arg, std::set<int32_t>& out) {
    const std::string s{arg};
    size_t start = 0;
    while (start <= s.size()) {
        const size_t comma = s.find(',', start);
        const std::string piece = s.substr(start, comma == std::string::npos ? std::string::npos : comma - start);
        if (!piece.empty()) {
            try {
                out.insert(std::stoi(piece));
            } catch (...) {
                Out("[bridge] WARNING: ignoring unparsable event code '%s'\n", piece.c_str());
            }
        }
        if (comma == std::string::npos) break;
        start = comma + 1;
    }
}

void PrintEventSummary() {
    std::lock_guard<std::mutex> lock(g_writeMutex);

    if (g_eventCounts.empty()) {
        Out("[bridge] No events were relayed.\n");
        return;
    }

    Out("\n[bridge] --- events relayed this session ---\n");
    uint64_t total = 0;
    for (const auto& [code, count] : g_eventCounts) {
        Out("[bridge]   %-15s %llu\n", EventCodeName(code), count);
        total += count;
    }
    Out("[bridge]   %-15s %llu\n", "TOTAL", total);
}

bool SetupMemoryMappedFile() {
    Out("[bridge] Creating memory mapped file '%s' (%zu bytes)...\n",
           SHARED_MEMORY_NAME, TOTAL_BUFFER_SIZE_BYTES);

    g_mapFile = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                                   static_cast<DWORD>(TOTAL_BUFFER_SIZE_BYTES), SHARED_MEMORY_NAME);

    if (g_mapFile == nullptr) {
        Out("[bridge] ERROR: could not create file mapping object: %lu\n", GetLastError());
        return false;
    }

    // Not fatal, but worth knowing: someone (an old bridge instance, or the real
    // UEVR plugin) already owns this mapping and we're just attaching to it.
    if (GetLastError() == ERROR_ALREADY_EXISTS)
        Out("[bridge] NOTE: shared memory already existed - attaching to it.\n");

    g_buffer = MapViewOfFile(g_mapFile, FILE_MAP_ALL_ACCESS, 0, 0, TOTAL_BUFFER_SIZE_BYTES);

    if (g_buffer == nullptr) {
        Out("[bridge] ERROR: could not map view of file: %lu\n", GetLastError());
        CloseHandle(g_mapFile);
        g_mapFile = nullptr;
        return false;
    }

    g_control = static_cast<ControlBlock*>(g_buffer);

    Out("[bridge] Memory mapped file ready.\n");
    return true;
}

void WriteToSharedMemory(const EventData* eventData) {
    if (!g_buffer || !g_control || !eventData)
        return;

    std::lock_guard<std::mutex> lock(g_writeMutex);

    const auto   index  = g_currentWriteIndex;
    const size_t offset = sizeof(ControlBlock) + static_cast<size_t>(index) * sizeof(EventData);
    g_currentWriteIndex = (g_currentWriteIndex + 1) % BUFFER_SIZE;

    CopyMemory(static_cast<char*>(g_buffer) + offset, eventData, sizeof(EventData));

    g_control->WriteIndex   = static_cast<int64_t>(index);
    g_control->PacketNumber = g_control->PacketNumber + 1;

    g_eventCounts[eventData->EventCode]++;
}

void Shutdown() {
    // Only the first caller runs this - Ctrl+C arrives on its own thread and can
    // race with the main loop unwinding.
    if (InterlockedExchange(&g_shuttingDown, 1) != 0)
        return;

    PrintEventSummary();

    if (g_buffer) {
        constexpr auto closeEvent = EventData{EVENT_CODE_BRIDGE_CLOSED, 0, 0, 0, 0, 0, 0, 0};
        WriteToSharedMemory(&closeEvent);
        Out("[bridge] Sent BridgeClosed(-1).\n");
        UnmapViewOfFile(g_buffer);
        g_buffer  = nullptr;
        g_control = nullptr;
    }

    if (g_mapFile) {
        CloseHandle(g_mapFile);
        g_mapFile = nullptr;
    }

    // Client pipe handles belong to their own detached threads and go away with the
    // process; there's no shared handle left to close here.

    if (g_logFile) {
        fclose(g_logFile);
        g_logFile = nullptr;
    }
}

BOOL WINAPI ConsoleHandler(DWORD signal) {
    if (signal == CTRL_C_EVENT || signal == CTRL_BREAK_EVENT || signal == CTRL_CLOSE_EVENT) {
        Out("\n[bridge] Shutting down...\n");
        Shutdown();
        ExitProcess(0);
    }
    return FALSE;
}

// Steam tracks a launched game's process tree (commonly via a Job Object) to know
// when it's safe to relaunch. This bridge is deliberately designed to outlive the
// game process - that's what makes an F6 Lua reload cheap - but if the game exits
// entirely while the bridge (its former child, via the Lua mod's `start` launch)
// is still alive, Steam sees a live descendant and refuses to relaunch with
// "game is already running", even though the actual game process is long gone.
//
// Fixed by having the bridge find its own connected client's process (the pipe is
// a straight line to the game, no Lua/protocol changes needed) and exit once that
// specific process is gone, rather than waiting indefinitely. An F6 reload never
// reaches this: the Lua mod keeps reusing the same pipe handle instead of
// reconnecting, so no new client - and no new watcher - shows up for it.
void WatchGameProcess(HANDLE pipe) {
    ULONG clientPid = 0;
    if (!GetNamedPipeClientProcessId(pipe, &clientPid))
        return;

    HANDLE gameProcess = OpenProcess(SYNCHRONIZE, FALSE, clientPid);
    if (!gameProcess)
        return;

    WaitForSingleObject(gameProcess, INFINITE);
    CloseHandle(gameProcess);

    if (InterlockedCompareExchange(&g_shuttingDown, 0, 0) != 0)
        return;  // already shutting down some other way (e.g. Ctrl+C)

    Out("[bridge] Game process (pid %lu) exited - shutting down so Steam doesn't\n", clientPid);
    Out("[bridge] see us as a leftover part of its session.\n");
    Shutdown();
    ExitProcess(0);
}

// Serve one connected client until it disconnects, then close its pipe instance.
// Runs on its own thread: when UE4SS reloads the Lua script, the old script's pipe
// handle is not closed promptly (the game process stays alive, so the handle lingers
// until GC). With a single-instance pipe that stale connection blocked every new
// one - the mod would log events while silently failing to send any of them. Serving
// each client on its own thread means a lingering dead connection costs nothing.
void ServeClient(HANDLE pipe) {
    // The pipe is a byte stream, so a single ReadFile can hand back a partial packet
    // or several packets at once. Accumulate and only drain whole 32-byte records.
    uint8_t accum[sizeof(EventData) * 16];
    size_t  accumUsed  = 0;
    uint64_t received  = 0;

    for (;;) {
        DWORD bytesRead = 0;
        const BOOL ok = ReadFile(pipe, accum + accumUsed,
                                 static_cast<DWORD>(sizeof(accum) - accumUsed), &bytesRead, nullptr);

        if (!ok || bytesRead == 0) {
            const DWORD err = GetLastError();
            if (err == ERROR_BROKEN_PIPE)
                Out("[bridge] Client disconnected (%llu packets relayed).\n", received);
            else
                Out("[bridge] Read failed: %lu (%llu packets relayed).\n", err, received);
            break;
        }

        accumUsed += bytesRead;

        size_t consumed = 0;
        while (accumUsed - consumed >= sizeof(EventData)) {
            EventData ev;
            CopyMemory(&ev, accum + consumed, sizeof(EventData));
            consumed += sizeof(EventData);
            received++;

            WriteToSharedMemory(&ev);

            if (ShouldPrint(ev.EventCode)) {
                Out("[bridge] #%llu %-15s Int0=%d F=[%.3f %.3f %.3f %.3f %.3f %.3f]\n",
                       received, EventCodeName(ev.EventCode), ev.Int0,
                       ev.Float0, ev.Float1, ev.Float2, ev.Float3, ev.Float4, ev.Float5);
            }
        }

        // Shift the leftover partial packet to the front.
        if (consumed > 0) {
            accumUsed -= consumed;
            if (accumUsed > 0)
                MoveMemory(accum, accum + consumed, accumUsed);
        } else if (accumUsed == sizeof(accum)) {
            // Can't happen while the buffer is a multiple of the packet size, but if it
            // ever did we'd spin forever on a full buffer - fail loudly instead.
            Out("[bridge] ERROR: accumulator full with no complete packet - desynced.\n");
            break;
        }
    }

    DisconnectNamedPipe(pipe);
    CloseHandle(pipe);
}

} // namespace

int main(int argc, char** argv) {
    // Unbuffered so the log is readable when stdout is redirected to a file.
    // _IOLBF would be the natural choice but MSVC's CRT treats it as _IOFBF, so
    // nothing would appear until the buffer filled or the process exited.
    setvbuf(stdout, nullptr, _IONBF, 0);

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--verbose") == 0 || strcmp(argv[i], "-v") == 0) {
            g_verbose = true;
        } else if (strcmp(argv[i], "--only") == 0 && i + 1 < argc) {
            ParseCodeList(argv[++i], g_onlyCodes);
            g_verbose = true;
        } else if (strcmp(argv[i], "--exclude") == 0 && i + 1 < argc) {
            ParseCodeList(argv[++i], g_excludeCodes);
            g_verbose = true;
        } else if (strcmp(argv[i], "--help") == 0 || strcmp(argv[i], "-h") == 0) {
            Out("usage: MechVibeBridge [--verbose] [--only CODES] [--exclude CODES]\n\n");
            Out("  --verbose      print every packet received\n");
            Out("  --only 3,4,5   print only these event codes (implies --verbose)\n");
            Out("  --exclude 15   never print these event codes (implies --verbose)\n\n");
            Out("Filters affect printing only - every packet is always relayed to\n");
            Out("shared memory, and the per-event summary on exit counts them all.\n\n");
            Out("Event codes: 1 TorsoTwist, 2 Footstep, 3 Trace, 4 Projectile,\n");
            Out("  5 Missiles, 6 AMS, 7 Melee, 8 Dropship, 9 JumpJets, 10 Airborne,\n");
            Out("  11 Landed, 12 MASC, 13 Powering, 14 PartDestruction, 15 Damaged,\n");
            Out("  16 Impulse (Clans extension)\n\n");
            Out("TorsoTwist(1) and Damaged(15) are by far the noisiest - exclude them\n");
            Out("when you want to watch weapon or movement events.\n");
            return 0;
        }
    }

    // Only one relay may own the pipe, and the Lua mod can auto-launch us - so a
    // second copy exits quietly instead of failing later on CreateNamedPipe.
    HANDLE instanceMutex = CreateMutexA(nullptr, TRUE, SINGLE_INSTANCE_MUTEX);
    if (instanceMutex == nullptr || GetLastError() == ERROR_ALREADY_EXISTS) {
        Out("MechVibeBridge is already running - this copy will exit.\n");
        if (instanceMutex) CloseHandle(instanceMutex);
        return 0;
    }

    OpenLogFile();

    Out("MechVibeBridge v%s - Lua pipe -> MechVibe shared memory relay\n", MECHVIBE_VERSION);
    Out("Verbose packet logging: %s (--help for filter options)\n", g_verbose ? "ON" : "off");
    if (!g_onlyCodes.empty()) {
        Out("Printing ONLY:");
        for (int32_t c : g_onlyCodes) Out(" %s", EventCodeName(c));
        Out("\n");
    }
    if (!g_excludeCodes.empty()) {
        Out("Excluding:");
        for (int32_t c : g_excludeCodes) Out(" %s", EventCodeName(c));
        Out("\n");
    }
    Out("\n");

    SetConsoleCtrlHandler(ConsoleHandler, TRUE);

    if (!SetupMemoryMappedFile())
        return 1;

    Out("[bridge] Waiting for the game on '%s' (Ctrl+C to quit)...\n", PIPE_NAME);

    // Accept loop. PIPE_UNLIMITED_INSTANCES plus a thread per client is what makes
    // reconnection reliable: a reloaded Lua script leaves its old handle open for a
    // while, and with a single instance that corpse blocked every subsequent connect.
    for (;;) {
        HANDLE pipe = CreateNamedPipeA(PIPE_NAME,
                                       PIPE_ACCESS_INBOUND,
                                       PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                                       PIPE_UNLIMITED_INSTANCES,
                                       0,                  // out buffer (unused, inbound only)
                                       sizeof(EventData) * BUFFER_SIZE,
                                       0,                  // default timeout
                                       nullptr);

        if (pipe == INVALID_HANDLE_VALUE) {
            Out("[bridge] ERROR: CreateNamedPipe failed: %lu\n", GetLastError());
            Shutdown();
            return 1;
        }

        // ERROR_PIPE_CONNECTED means the client beat us to it - that's still connected.
        const BOOL connected = ConnectNamedPipe(pipe, nullptr)
                                   ? TRUE
                                   : (GetLastError() == ERROR_PIPE_CONNECTED);

        if (!connected) {
            Out("[bridge] ConnectNamedPipe failed: %lu\n", GetLastError());
            CloseHandle(pipe);
            continue;
        }

        Out("[bridge] Client connected.\n");

        // Detached: this loop goes straight back to waiting, so a new connection is
        // accepted immediately even while an older one is still open.
        std::thread([pipe] {
            ServeClient(pipe);
            // The tally at disconnect is the natural moment to see what a session
            // produced - counts are cumulative across clients.
            PrintEventSummary();
        }).detach();

        // Separate thread, same pipe: this one only watches whether the game that
        // connected is still alive, independent of whatever ServeClient is doing
        // with the byte stream.
        std::thread([pipe] { WatchGameProcess(pipe); }).detach();
    }
}
