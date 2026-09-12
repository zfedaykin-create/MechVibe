// pipetest - end-to-end verification for the bridge, without needing the game.
//
// --write : opens the named pipe with plain fopen() and pushes a few 32-byte
//           EventData packets. This is the important one: Lua's io.open() is a thin
//           wrapper over the same CRT fopen(), so if fopen can talk to the pipe then
//           the UE4SS Lua mod can too - which is the assumption the whole
//           "no UE4SS rebuild" plan rests on.
//
// --read  : opens "MechVibeMemory" and dumps the ControlBlock plus the most
//           recent slots, i.e. plays the part of MechVibe.exe, so we can confirm the
//           packets actually landed in shared memory in the right layout.

#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <cstring>

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
constexpr size_t  TOTAL_BUFFER_SIZE_BYTES = sizeof(EventData) * BUFFER_SIZE + sizeof(ControlBlock);

constexpr const char* SHARED_MEMORY_NAME = "MechVibeMemory";
constexpr const char* PIPE_NAME          = "\\\\.\\pipe\\MechVibeBridge";

int DoWrite() {
    printf("[pipetest] fopen(\"%s\", \"wb\")...\n", PIPE_NAME);

    FILE* f = fopen(PIPE_NAME, "wb");
    if (!f) {
        printf("[pipetest] FAILED: fopen returned null (errno=%d).\n", errno);
        printf("[pipetest] Is MechVibeBridge.exe running?\n");
        return 1;
    }

    printf("[pipetest] OK - pipe opened via fopen. This is what Lua io.open does.\n");

    // Match what the Lua side will do: unbuffered, so every write goes out immediately
    // instead of sitting in the CRT buffer waiting for 4KB to accumulate.
    setvbuf(f, nullptr, _IONBF, 0);

    // Protocol reference: Footstep(2) = Int0 MassInTons, Float1 SpeedInKmh.
    //                     JumpJets(9) = Int0 Active.
    //                     Landed(11)  = Int0 MassInTons, Float0 AccelerationInKmh2.
    const EventData packets[] = {
        {2,  55, 0.0f, 42.5f, 0.0f, 0.0f, 0.0f, 0.0f},
        {2,  55, 0.0f, 44.0f, 0.0f, 0.0f, 0.0f, 0.0f},
        {9,   1, 0.0f, 0.0f,  0.0f, 0.0f, 0.0f, 0.0f},
        {11, 55, 9.81f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f},
    };

    const size_t count = sizeof(packets) / sizeof(packets[0]);
    for (size_t i = 0; i < count; i++) {
        const size_t written = fwrite(&packets[i], sizeof(EventData), 1, f);
        if (written != 1) {
            printf("[pipetest] FAILED: fwrite of packet %zu returned %zu.\n", i, written);
            fclose(f);
            return 1;
        }
        printf("[pipetest] sent packet %zu: EventCode=%d Int0=%d\n", i, packets[i].EventCode, packets[i].Int0);
    }

    fflush(f);
    fclose(f);
    printf("[pipetest] All %zu packets sent, pipe closed.\n", count);
    return 0;
}

int DoRead() {
    HANDLE mapFile = OpenFileMappingA(FILE_MAP_READ, FALSE, SHARED_MEMORY_NAME);
    if (!mapFile) {
        printf("[pipetest] FAILED: OpenFileMapping('%s'): %lu\n", SHARED_MEMORY_NAME, GetLastError());
        printf("[pipetest] Is MechVibeBridge.exe running?\n");
        return 1;
    }

    void* buffer = MapViewOfFile(mapFile, FILE_MAP_READ, 0, 0, TOTAL_BUFFER_SIZE_BYTES);
    if (!buffer) {
        printf("[pipetest] FAILED: MapViewOfFile: %lu\n", GetLastError());
        CloseHandle(mapFile);
        return 1;
    }

    const auto* control = static_cast<const ControlBlock*>(buffer);
    const auto* events  = reinterpret_cast<const EventData*>(static_cast<const char*>(buffer) + sizeof(ControlBlock));

    printf("[pipetest] ControlBlock: WriteIndex=%lld PacketNumber=%lld\n",
           static_cast<long long>(control->WriteIndex), static_cast<long long>(control->PacketNumber));

    if (control->PacketNumber == 0) {
        printf("[pipetest] No packets have been written yet.\n");
    } else {
        // Walk backwards from the newest slot so the output reads newest-last.
        const int64_t show = control->PacketNumber < 8 ? control->PacketNumber : 8;
        printf("[pipetest] Last %lld slot(s), oldest first:\n", static_cast<long long>(show));

        for (int64_t i = show - 1; i >= 0; i--) {
            const int64_t idx = ((control->WriteIndex - i) % BUFFER_SIZE + BUFFER_SIZE) % BUFFER_SIZE;
            const EventData& e = events[idx];
            printf("  slot %3lld: EventCode=%-4d Int0=%-6d F=[%.3f %.3f %.3f %.3f %.3f %.3f]\n",
                   static_cast<long long>(idx), e.EventCode, e.Int0,
                   e.Float0, e.Float1, e.Float2, e.Float3, e.Float4, e.Float5);
        }
    }

    UnmapViewOfFile(buffer);
    CloseHandle(mapFile);
    return 0;
}

} // namespace

int main(int argc, char** argv) {
    if (argc >= 2 && strcmp(argv[1], "--write") == 0)
        return DoWrite();
    if (argc >= 2 && strcmp(argv[1], "--read") == 0)
        return DoRead();

    printf("usage: pipetest --write | --read\n");
    printf("  --write : push test packets into the pipe using fopen (the Lua io.open path)\n");
    printf("  --read  : dump the shared memory ring buffer (what MechVibe.exe sees)\n");
    return 2;
}
