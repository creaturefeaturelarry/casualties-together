/* Send, receive, and flush on a thread Unity's runtime does not know about.
   A blocking Steam call on a managed thread stalls the game thread until it
   returns. This thread is CreateThread, never attached to Mono, and it never
   calls back into managed code. */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define OUT_CAP 2048
#define IN_CAP 1024
#define FLUSH_CAP 256
#define FAIL_CAP 64
#define LATER_CAP 512
#define CONN_CAP 64
#define RECV_CAP 256
#define RESULT_OK 1
#define RESULT_NO_CONNECTION 3
#define RESULT_LIMIT 26
#define SEEN_MISSING 0
#define SEEN_PENDING 1
#define SEEN_READY 2

typedef struct IoConn {
    uint64_t steamId;
    uint32_t conn;
    uint32_t ready;
} IoConn;

typedef struct OutMsg {
    uint64_t steamId;
    uint32_t conn;
    int32_t flag;
    int32_t epoch;
    int32_t wait;
    int32_t seenGen;
    int32_t size;
    void* data;
} OutMsg;

typedef struct InMsg {
    uint64_t steamId;
    int32_t size;
    void* data;
} InMsg;

typedef struct FailMsg {
    int32_t result;
    uint32_t conn;
    int32_t msg;
    int32_t known;
} FailMsg;

typedef void* (__cdecl *FnGet)(void);
typedef int (__cdecl *FnRecv)(void* self, uint32_t conn, void** msgs, int max);
typedef int (__cdecl *FnSend)(void* self, uint32_t conn, const void* data, uint32_t cb, int flags, int64_t* outNum);
typedef int (__cdecl *FnFlush)(void* self, uint32_t conn);
typedef void (__cdecl *FnRelease)(void* msg);
typedef int (__cdecl *FnStatus)(void* self, uint32_t conn, void* status, int nLanes, void* lanes);

typedef struct StatusSlot {
    uint64_t steamId;
    int32_t ok;
    int32_t state;
    int32_t ping;
    float qLocal;
    float qRemote;
    float outPps;
    float outBps;
    float inPps;
    float inBps;
    int32_t sendRate;
    int32_t pendingUnrel;
    int32_t pendingRel;
    int32_t sentUnacked;
    int32_t pad;
    int64_t queueTime;
} StatusSlot;

typedef struct Io {
    volatile LONG stop;
    HANDLE wake;
    HANDLE thread;
    CRITICAL_SECTION connLock;
    IoConn conns[CONN_CAP];
    int connCount;
    uint32_t snapGen;
    uint32_t lookGen;
    volatile LONG isClient;
    volatile LONG isGameServer;
    int boundServer;
    void* socks;
    FnRecv recv;
    FnSend send;
    FnFlush flush;
    FnRelease release;
    FnStatus statusFn;
    StatusSlot statusSlots[CONN_CAP];
    int statusCount;
    volatile LONG statusPollUs;
    LARGE_INTEGER nextStatus;
    void* ptrs[RECV_CAP];
    OutMsg nowSlots[OUT_CAP];
    volatile LONG nowW;
    volatile LONG nowR;
    OutMsg pacedSlots[OUT_CAP];
    volatile LONG pacedW;
    volatile LONG pacedR;
    uint32_t flushSlots[FLUSH_CAP];
    volatile LONG flushW;
    volatile LONG flushR;
    InMsg inSlots[IN_CAP];
    volatile LONG inW;
    volatile LONG inR;
    FailMsg failSlots[FAIL_CAP];
    volatile LONG failW;
    volatile LONG failR;
    OutMsg later[LATER_CAP];
    int laterCount;
    int held;
    OutMsg heldMsg;
    int haveBatch;
    OutMsg batchHeld;
    int batchEpoch;
    LARGE_INTEGER nextPaced;
    LARGE_INTEGER qpcFreq;
    volatile LONG64 limitSteamId;
    volatile LONG64 inBytes;
    volatile LONG64 inMsgs;
    volatile LONG64 outBytes;
    volatile LONG64 outMsgs;
} Io;

static void free_msg(OutMsg* msg) {
    if (msg->data) {
        free(msg->data);
        msg->data = NULL;
    }
}

static int ring_push_out(OutMsg* slots, volatile LONG* w, volatile LONG* r, OutMsg msg) {
    LONG head = *w;
    LONG tail = *r;
    if ((ULONG)(head - tail) >= OUT_CAP) {
        return 0;
    }
    slots[head % OUT_CAP] = msg;
    MemoryBarrier();
    InterlockedExchange(w, head + 1);
    return 1;
}

static int ring_pop_out(OutMsg* slots, volatile LONG* w, volatile LONG* r, OutMsg* out) {
    LONG tail = *r;
    LONG head = *w;
    if (tail == head) {
        return 0;
    }
    MemoryBarrier();
    *out = slots[tail % OUT_CAP];
    InterlockedExchange(r, tail + 1);
    return 1;
}

static void note_out(Io* io, int size) {
    if (size <= 0) {
        return;
    }
    InterlockedAdd64(&io->outBytes, size);
    InterlockedIncrement64(&io->outMsgs);
}

static void note_in(Io* io, int size) {
    if (size <= 0) {
        return;
    }
    InterlockedAdd64(&io->inBytes, size);
    InterlockedIncrement64(&io->inMsgs);
}

static void push_fail(Io* io, int result, uint32_t conn, int msg, int known) {
    LONG head = io->failW;
    LONG tail = io->failR;
    if ((ULONG)(head - tail) >= FAIL_CAP) {
        return;
    }
    io->failSlots[head % FAIL_CAP].result = result;
    io->failSlots[head % FAIL_CAP].conn = conn;
    io->failSlots[head % FAIL_CAP].msg = msg;
    io->failSlots[head % FAIL_CAP].known = known;
    MemoryBarrier();
    InterlockedExchange(&io->failW, head + 1);
}

static int push_in(Io* io, uint64_t steamId, void* data, int size) {
    LONG head = io->inW;
    LONG tail = io->inR;
    if ((ULONG)(head - tail) >= IN_CAP) {
        return 0;
    }
    io->inSlots[head % IN_CAP].steamId = steamId;
    io->inSlots[head % IN_CAP].size = size;
    io->inSlots[head % IN_CAP].data = data;
    MemoryBarrier();
    InterlockedExchange(&io->inW, head + 1);
    return 1;
}

static int look(Io* io, uint32_t conn) {
    IoConn local[CONN_CAP];
    int n;
    uint32_t gen;
    EnterCriticalSection(&io->connLock);
    n = io->connCount;
    gen = io->snapGen;
    if (n > 0) {
        memcpy(local, io->conns, (size_t)n * sizeof(IoConn));
    }
    LeaveCriticalSection(&io->connLock);
    io->lookGen = gen;
    if (gen == 0) {
        return SEEN_PENDING;
    }
    for (int i = 0; i < n; i++) {
        if (local[i].conn == conn) {
            return local[i].ready ? SEEN_READY : SEEN_PENDING;
        }
    }
    return SEEN_MISSING;
}

/* 0 = send now, 1 = caller still owns it and should keep it, message already updated. */
static int defer(Io* io, OutMsg* msg) {
    int seen = look(io, msg->conn);
    if (seen == SEEN_READY) {
        return 0;
    }
    if (seen == SEEN_MISSING && msg->seenGen != (int32_t)io->lookGen) {
        msg->wait++;
        msg->seenGen = (int32_t)io->lookGen;
    }
    if (msg->wait >= 40) {
        free_msg(msg);
        return 2;
    }
    return 1;
}

static int try_send(Io* io, OutMsg* msg) {
    int64_t number = 0;
    const void* bytes = msg->data;
    static const char empty = 0;
    if (!bytes) {
        bytes = &empty;
    }
    int result = io->send(io->socks, msg->conn, bytes, (uint32_t)msg->size, msg->flag, &number);
    if (result == RESULT_OK) {
        note_out(io, msg->size);
        free_msg(msg);
        return 1;
    }
    if (result == RESULT_LIMIT) {
        if (msg->steamId != 0) {
            InterlockedExchange64(&io->limitSteamId, (LONG64)msg->steamId);
        }
        return 0;
    }
    if (result != RESULT_NO_CONNECTION) {
        push_fail(io, result, msg->conn, msg->size > 0 && msg->data ? ((unsigned char*)msg->data)[0] : 0, msg->size > 0);
    }
    free_msg(msg);
    return 1;
}

static void keep_later(Io* io, OutMsg* msg) {
    if (io->laterCount >= LATER_CAP) {
        free_msg(msg);
        return;
    }
    io->later[io->laterCount++] = *msg;
}

static void send_queued(Io* io) {
    if (!io->socks) {
        return;
    }
    int keep = 0;
    for (int i = 0; i < io->laterCount; i++) {
        OutMsg msg = io->later[i];
        int held = defer(io, &msg);
        if (held == 1) {
            io->later[keep++] = msg;
            continue;
        }
        if (held == 2) {
            continue;
        }
        if (!try_send(io, &msg)) {
            io->held = 1;
            io->heldMsg = msg;
            for (int j = i + 1; j < io->laterCount; j++) {
                io->later[keep++] = io->later[j];
            }
            io->laterCount = keep;
            return;
        }
    }
    io->laterCount = keep;

    if (io->held) {
        if (!try_send(io, &io->heldMsg)) {
            return;
        }
        io->held = 0;
    }

    OutMsg msg;
    while (ring_pop_out(io->nowSlots, &io->nowW, &io->nowR, &msg)) {
        int held = defer(io, &msg);
        if (held == 1) {
            keep_later(io, &msg);
            continue;
        }
        if (held == 2) {
            continue;
        }
        if (!try_send(io, &msg)) {
            io->held = 1;
            io->heldMsg = msg;
            return;
        }
    }

    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    if (now.QuadPart < io->nextPaced.QuadPart) {
        return;
    }
    if (io->haveBatch) {
        if (!try_send(io, &io->batchHeld)) {
            io->held = 1;
            io->heldMsg = io->batchHeld;
            io->haveBatch = 0;
            return;
        }
        io->haveBatch = 0;
    }
    while (ring_pop_out(io->pacedSlots, &io->pacedW, &io->pacedR, &msg)) {
        int held = defer(io, &msg);
        if (held == 1) {
            keep_later(io, &msg);
            continue;
        }
        if (held == 2) {
            continue;
        }
        if (io->batchEpoch == -1) {
            io->batchEpoch = msg.epoch;
        }
        if (msg.epoch != io->batchEpoch) {
            io->haveBatch = 1;
            io->batchHeld = msg;
            io->batchEpoch = -1;
            io->nextPaced.QuadPart = now.QuadPart + io->qpcFreq.QuadPart / 100;
            return;
        }
        if (!try_send(io, &msg)) {
            io->held = 1;
            io->heldMsg = msg;
            io->batchEpoch = -1;
            io->nextPaced.QuadPart = now.QuadPart + io->qpcFreq.QuadPart / 100;
            return;
        }
    }
    io->batchEpoch = -1;
    io->nextPaced.QuadPart = now.QuadPart + io->qpcFreq.QuadPart / 100;
}

static void flush_queued(Io* io) {
    if (!io->socks) {
        return;
    }
    for (;;) {
        LONG tail = io->flushR;
        LONG head = io->flushW;
        if (tail == head) {
            break;
        }
        MemoryBarrier();
        uint32_t conn = io->flushSlots[tail % FLUSH_CAP];
        InterlockedExchange(&io->flushR, tail + 1);
        if (conn == 0) {
            continue;
        }
        if (look(io, conn) != SEEN_READY) {
            continue;
        }
        io->flush(io->socks, conn);
    }
}

static void load_steam(Io* io) {
    int server = io->isGameServer;
    if (io->socks && io->boundServer == server) {
        return;
    }
    HMODULE mod = GetModuleHandleA("steam_api64.dll");
    if (!mod) {
        return;
    }
    const char* getName = server
        ? "SteamAPI_SteamGameServerNetworkingSockets_SteamAPI_v012"
        : "SteamAPI_SteamNetworkingSockets_SteamAPI_v012";
    FnGet get = (FnGet)GetProcAddress(mod, getName);
    io->recv = (FnRecv)GetProcAddress(mod, "SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection");
    io->send = (FnSend)GetProcAddress(mod, "SteamAPI_ISteamNetworkingSockets_SendMessageToConnection");
    io->flush = (FnFlush)GetProcAddress(mod, "SteamAPI_ISteamNetworkingSockets_FlushMessagesOnConnection");
    io->release = (FnRelease)GetProcAddress(mod, "SteamAPI_SteamNetworkingMessage_t_Release");
    io->statusFn = (FnStatus)GetProcAddress(mod, "SteamAPI_ISteamNetworkingSockets_GetConnectionRealTimeStatus");
    if (!get || !io->recv || !io->send || !io->flush || !io->release) {
        return;
    }
    void* socks = get();
    if (!socks) {
        return;
    }
    io->socks = socks;
    io->boundServer = server;
}

static void receive_queued(Io* io) {
    if (!io->socks) {
        return;
    }
    if ((ULONG)(io->inW - io->inR) > IN_CAP - RECV_CAP) {
        return;
    }
    IoConn local[CONN_CAP];
    int n;
    EnterCriticalSection(&io->connLock);
    n = io->connCount;
    if (n > CONN_CAP) {
        n = CONN_CAP;
    }
    if (n > 0) {
        memcpy(local, io->conns, (size_t)n * sizeof(IoConn));
    }
    LeaveCriticalSection(&io->connLock);
    int remaining = RECV_CAP;
    for (int c = 0; c < n; c++) {
        if (io->isClient && remaining <= 0) {
            break;
        }
        if (!local[c].ready || local[c].conn == 0) {
            continue;
        }
        int take = io->isClient ? remaining : RECV_CAP;
        int count = io->recv(io->socks, local[c].conn, io->ptrs, take);
        if (count <= 0) {
            continue;
        }
        if (count > take) {
            count = take;
        }
        for (int i = 0; i < count; i++) {
            void* message = io->ptrs[i];
            if (!message) {
                continue;
            }
            void* src = *(void**)message;
            int size = *(int*)((char*)message + 8);
            if (size < 0) {
                size = 0;
            }
            void* copy = NULL;
            if (size > 0 && size <= 512 * 1024 && src) {
                copy = malloc((size_t)size);
                if (copy) {
                    memcpy(copy, src, (size_t)size);
                } else {
                    size = 0;
                }
            } else if (size > 512 * 1024) {
                size = 0;
            }
            io->release(message);
            if (!push_in(io, local[c].steamId, copy, size)) {
                free(copy);
                continue;
            }
            note_in(io, size);
        }
        if (io->isClient) {
            remaining -= count;
        }
    }
}

static void status_poll(Io* io) {
    if (!io->socks || !io->statusFn || io->qpcFreq.QuadPart == 0) {
        return;
    }
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    double sec = (double)(now.QuadPart - io->nextStatus.QuadPart) / (double)io->qpcFreq.QuadPart;
    if (sec < 1.0) {
        return;
    }
    io->nextStatus = now;
    IoConn local[CONN_CAP];
    int n = 0;
    EnterCriticalSection(&io->connLock);
    n = io->connCount;
    if (n > CONN_CAP) {
        n = CONN_CAP;
    }
    if (n > 0) {
        memcpy(local, io->conns, (size_t)n * sizeof(IoConn));
    }
    LeaveCriticalSection(&io->connLock);
    StatusSlot out[CONN_CAP];
    int got = 0;
    LARGE_INTEGER t0;
    LARGE_INTEGER t1;
    QueryPerformanceCounter(&t0);
    for (int i = 0; i < n; i++) {
        unsigned char buf[256];
        memset(buf, 0, sizeof(buf));
        int result = io->statusFn(io->socks, local[i].conn, buf, 0, NULL);
        StatusSlot slot;
        memset(&slot, 0, sizeof(slot));
        slot.steamId = local[i].steamId;
        slot.ok = result == RESULT_OK;
        slot.state = *(int*)(buf + 0);
        slot.ping = *(int*)(buf + 4);
        slot.qLocal = *(float*)(buf + 8);
        slot.qRemote = *(float*)(buf + 12);
        slot.outPps = *(float*)(buf + 16);
        slot.outBps = *(float*)(buf + 20);
        slot.inPps = *(float*)(buf + 24);
        slot.inBps = *(float*)(buf + 28);
        slot.sendRate = *(int*)(buf + 32);
        slot.pendingUnrel = *(int*)(buf + 36);
        slot.pendingRel = *(int*)(buf + 40);
        slot.sentUnacked = *(int*)(buf + 44);
        slot.queueTime = *(int64_t*)(buf + 48);
        if (got < CONN_CAP) {
            out[got++] = slot;
        }
    }
    QueryPerformanceCounter(&t1);
    int us = (int)((t1.QuadPart - t0.QuadPart) * 1000000 / io->qpcFreq.QuadPart);
    EnterCriticalSection(&io->connLock);
    if (got > 0) {
        memcpy(io->statusSlots, out, (size_t)got * sizeof(StatusSlot));
    }
    io->statusCount = got;
    io->statusPollUs = us;
    LeaveCriticalSection(&io->connLock);
}

static DWORD WINAPI thread_main(LPVOID param) {
    Io* io = (Io*)param;
    while (!io->stop) {
        load_steam(io);
        send_queued(io);
        flush_queued(io);
        receive_queued(io);
        status_poll(io);
        if (io->stop) {
            break;
        }
        /* Leave the connection lock free long enough for Steam to finish a join. */
        WaitForSingleObject(io->wake, 5);
    }
    return 0;
}

static OutMsg copy_out(uint64_t steamId, uint32_t conn, int flag, int epoch, const void* data, int size) {
    OutMsg msg;
    memset(&msg, 0, sizeof(msg));
    msg.steamId = steamId;
    msg.conn = conn;
    msg.flag = flag;
    msg.epoch = epoch;
    msg.size = size > 0 ? size : 0;
    if (msg.size > 0 && data) {
        msg.data = malloc((size_t)msg.size);
        if (!msg.data) {
            msg.size = -1;
            return msg;
        }
        memcpy(msg.data, data, (size_t)msg.size);
    }
    return msg;
}

__declspec(dllexport) int SteamIo_CopyStatus(void* handle, StatusSlot* dst, int cap, int* pollUs) {
    Io* io = (Io*)handle;
    if (!io || !dst || cap <= 0) {
        return 0;
    }
    EnterCriticalSection(&io->connLock);
    int n = io->statusCount;
    if (n > cap) {
        n = cap;
    }
    if (n > 0) {
        memcpy(dst, io->statusSlots, (size_t)n * sizeof(StatusSlot));
    }
    if (pollUs) {
        *pollUs = io->statusPollUs;
    }
    LeaveCriticalSection(&io->connLock);
    return n;
}

__declspec(dllexport) void* SteamIo_Create(void) {
    Io* io = (Io*)calloc(1, sizeof(Io));
    if (!io) {
        return NULL;
    }
    io->batchEpoch = -1;
    io->boundServer = -1;
    InitializeCriticalSection(&io->connLock);
    io->wake = CreateEventA(NULL, FALSE, FALSE, NULL);
    QueryPerformanceFrequency(&io->qpcFreq);
    QueryPerformanceCounter(&io->nextStatus);
    return io;
}

__declspec(dllexport) int SteamIo_Running(void* handle) {
    Io* io = (Io*)handle;
    return io && io->thread ? 1 : 0;
}

__declspec(dllexport) void SteamIo_Start(void* handle) {
    Io* io = (Io*)handle;
    if (!io || io->thread) {
        return;
    }
    io->stop = 0;
    io->thread = CreateThread(NULL, 0, thread_main, io, 0, NULL);
    if (io->thread) {
        SetThreadPriority(io->thread, THREAD_PRIORITY_BELOW_NORMAL);
    }
}

__declspec(dllexport) void SteamIo_Kick(void* handle) {
    Io* io = (Io*)handle;
    if (io && io->wake) {
        SetEvent(io->wake);
    }
}

__declspec(dllexport) void SteamIo_SetMode(void* handle, int isClient, int isGameServer) {
    Io* io = (Io*)handle;
    if (!io) {
        return;
    }
    io->isClient = isClient ? 1 : 0;
    io->isGameServer = isGameServer ? 1 : 0;
}

__declspec(dllexport) void SteamIo_SetConns(void* handle, const IoConn* conns, int count) {
    Io* io = (Io*)handle;
    if (!io) {
        return;
    }
    if (count < 0) {
        count = 0;
    }
    if (count > CONN_CAP) {
        count = CONN_CAP;
    }
    EnterCriticalSection(&io->connLock);
    if (count > 0 && conns) {
        memcpy(io->conns, conns, (size_t)count * sizeof(IoConn));
    }
    io->connCount = count;
    io->snapGen++;
    if (io->snapGen == 0) {
        io->snapGen = 1;
    }
    LeaveCriticalSection(&io->connLock);
}

__declspec(dllexport) int SteamIo_PushOut(void* handle, uint64_t steamId, uint32_t conn, int flag, int epoch, const void* data, int size) {
    Io* io = (Io*)handle;
    if (!io) {
        return 0;
    }
    OutMsg msg = copy_out(steamId, conn, flag, epoch, data, size);
    if (msg.size < 0) {
        return 0;
    }
    if (!ring_push_out(io->nowSlots, &io->nowW, &io->nowR, msg)) {
        free_msg(&msg);
        return 0;
    }
    return 1;
}

__declspec(dllexport) int SteamIo_PushPaced(void* handle, uint64_t steamId, uint32_t conn, int flag, int epoch, const void* data, int size) {
    Io* io = (Io*)handle;
    if (!io) {
        return 0;
    }
    OutMsg msg = copy_out(steamId, conn, flag, epoch, data, size);
    if (msg.size < 0) {
        return 0;
    }
    if (!ring_push_out(io->pacedSlots, &io->pacedW, &io->pacedR, msg)) {
        free_msg(&msg);
        return 0;
    }
    return 1;
}

__declspec(dllexport) int SteamIo_PushFlush(void* handle, uint32_t conn) {
    Io* io = (Io*)handle;
    if (!io) {
        return 0;
    }
    LONG head = io->flushW;
    LONG tail = io->flushR;
    if ((ULONG)(head - tail) >= FLUSH_CAP) {
        return 0;
    }
    io->flushSlots[head % FLUSH_CAP] = conn;
    MemoryBarrier();
    InterlockedExchange(&io->flushW, head + 1);
    return 1;
}

__declspec(dllexport) int SteamIo_PopIn(void* handle, uint64_t* steamId, void** data, int* size) {
    Io* io = (Io*)handle;
    if (!io) {
        return 0;
    }
    LONG tail = io->inR;
    LONG head = io->inW;
    if (tail == head) {
        return 0;
    }
    MemoryBarrier();
    InMsg msg = io->inSlots[tail % IN_CAP];
    InterlockedExchange(&io->inR, tail + 1);
    if (steamId) {
        *steamId = msg.steamId;
    }
    if (data) {
        *data = msg.data;
    }
    if (size) {
        *size = msg.size;
    }
    return 1;
}

__declspec(dllexport) void SteamIo_Free(void* data) {
    free(data);
}

__declspec(dllexport) int SteamIo_PopFail(void* handle, int* result, uint32_t* conn, int* msg, int* known) {
    Io* io = (Io*)handle;
    if (!io) {
        return 0;
    }
    LONG tail = io->failR;
    LONG head = io->failW;
    if (tail == head) {
        return 0;
    }
    MemoryBarrier();
    FailMsg fail = io->failSlots[tail % FAIL_CAP];
    InterlockedExchange(&io->failR, tail + 1);
    if (result) {
        *result = fail.result;
    }
    if (conn) {
        *conn = fail.conn;
    }
    if (msg) {
        *msg = fail.msg;
    }
    if (known) {
        *known = fail.known;
    }
    return 1;
}

__declspec(dllexport) int64_t SteamIo_TakeLimit(void* handle) {
    Io* io = (Io*)handle;
    if (!io) {
        return 0;
    }
    return (int64_t)InterlockedExchange64(&io->limitSteamId, 0);
}

__declspec(dllexport) void SteamIo_TakeBytes(void* handle, int64_t* inBytes, int64_t* inMsgs, int64_t* outBytes, int64_t* outMsgs) {
    Io* io = (Io*)handle;
    int64_t ib = 0, im = 0, ob = 0, om = 0;
    if (io) {
        ib = (int64_t)InterlockedExchange64(&io->inBytes, 0);
        im = (int64_t)InterlockedExchange64(&io->inMsgs, 0);
        ob = (int64_t)InterlockedExchange64(&io->outBytes, 0);
        om = (int64_t)InterlockedExchange64(&io->outMsgs, 0);
    }
    if (inBytes) *inBytes = ib;
    if (inMsgs) *inMsgs = im;
    if (outBytes) *outBytes = ob;
    if (outMsgs) *outMsgs = om;
}

__declspec(dllexport) void SteamIo_Stop(void* handle) {
    Io* io = (Io*)handle;
    if (!io) {
        return;
    }
    io->stop = 1;
    if (io->wake) {
        SetEvent(io->wake);
    }
    if (io->thread) {
        WaitForSingleObject(io->thread, 2000);
        CloseHandle(io->thread);
        io->thread = NULL;
    }
}

__declspec(dllexport) void SteamIo_Destroy(void* handle) {
    Io* io = (Io*)handle;
    if (!io) {
        return;
    }
    SteamIo_Stop(io);
    OutMsg msg;
    while (ring_pop_out(io->nowSlots, &io->nowW, &io->nowR, &msg)) free_msg(&msg);
    while (ring_pop_out(io->pacedSlots, &io->pacedW, &io->pacedR, &msg)) free_msg(&msg);
    for (int i = 0; i < io->laterCount; i++) free_msg(&io->later[i]);
    if (io->held) free_msg(&io->heldMsg);
    if (io->haveBatch) free_msg(&io->batchHeld);
    for (;;) {
        LONG tail = io->inR;
        if (tail == io->inW) break;
        MemoryBarrier();
        free(io->inSlots[tail % IN_CAP].data);
        InterlockedExchange(&io->inR, tail + 1);
    }
    if (io->wake) CloseHandle(io->wake);
    DeleteCriticalSection(&io->connLock);
    free(io);
}
