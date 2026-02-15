#pragma once
#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#if defined(MOIRA_C_EXPORTS)
#define MOIRA_C_API __declspec(dllexport)
#else
#define MOIRA_C_API __declspec(dllimport)
#endif
#else
#define MOIRA_C_API __attribute__((visibility("default")))
#endif

    typedef void* moira_handle;

    // Callbacks de memoria (lo que Moira necesita)
    typedef uint8_t(*moira_read8_fn)(void* user, uint32_t addr);
    typedef uint16_t(*moira_read16_fn)(void* user, uint32_t addr);
    typedef void     (*moira_write8_fn)(void* user, uint32_t addr, uint8_t v);
    typedef void     (*moira_write16_fn)(void* user, uint32_t addr, uint16_t v);

    // Opcionales (Moira los tiene como virtual con default)
    typedef void     (*moira_sync_fn)(void* user, int cycles);
    typedef uint16_t(*moira_read_irq_user_vector_fn)(void* user, uint8_t level);

    typedef struct moira_callbacks {
        void* user;
        moira_read8_fn  read8;
        moira_read16_fn read16;
        moira_write8_fn write8;
        moira_write16_fn write16;
        moira_sync_fn sync; // puede ser NULL
        moira_read_irq_user_vector_fn readIrqUserVector; // puede ser NULL
    } moira_callbacks;

    // Stack Frame structure (for exception handling)
    typedef struct moira_stackframe {
        uint16_t code;
        uint32_t addr;
        uint16_t ird;
        uint16_t sr;
        uint32_t pc;
        uint16_t fc;
        uint16_t ssw;
    } moira_stackframe;

    // Lifecycle mínimo
    MOIRA_C_API moira_handle moira_create(const moira_callbacks* cb);
    MOIRA_C_API void         moira_destroy(moira_handle h);

    // Running CPU (1:1 con Moira)
    MOIRA_C_API void moira_reset(moira_handle h);
    MOIRA_C_API void moira_execute(moira_handle h);
    MOIRA_C_API void moira_execute_cycles(moira_handle h, int64_t cycles);
    MOIRA_C_API void moira_execute_until(moira_handle h, int64_t cycle);

    // Clock (1:1)
    MOIRA_C_API int64_t moira_getClock(moira_handle h);
    MOIRA_C_API void    moira_setClock(moira_handle h, int64_t v);

    // Registros (1:1)
    MOIRA_C_API uint32_t moira_getD(moira_handle h, int n);
    MOIRA_C_API void     moira_setD(moira_handle h, int n, uint32_t v);

    MOIRA_C_API uint32_t moira_getA(moira_handle h, int n);
    MOIRA_C_API void     moira_setA(moira_handle h, int n, uint32_t v);

    MOIRA_C_API uint32_t moira_getPC(moira_handle h);
    MOIRA_C_API void     moira_setPC(moira_handle h, uint32_t v);

    MOIRA_C_API uint32_t moira_getPC0(moira_handle h);
    MOIRA_C_API void     moira_setPC0(moira_handle h, uint32_t v);

    MOIRA_C_API uint16_t moira_getIRC(moira_handle h);
    MOIRA_C_API void     moira_setIRC(moira_handle h, uint16_t v);

    MOIRA_C_API uint16_t moira_getIRD(moira_handle h);
    MOIRA_C_API void     moira_setIRD(moira_handle h, uint16_t v);

    MOIRA_C_API uint8_t  moira_getCCR(moira_handle h);
    MOIRA_C_API void     moira_setCCR(moira_handle h, uint8_t v);

    MOIRA_C_API uint16_t moira_getSR(moira_handle h);
    MOIRA_C_API void     moira_setSR(moira_handle h, uint16_t v);

    MOIRA_C_API uint32_t moira_getSP(moira_handle h);
    MOIRA_C_API void     moira_setSP(moira_handle h, uint32_t v);

    MOIRA_C_API uint8_t  moira_getIPL(moira_handle h);
    MOIRA_C_API void     moira_setIPL(moira_handle h, uint8_t v);

    MOIRA_C_API void moira_setSupervisorMode(moira_handle h, bool s);
    MOIRA_C_API void moira_triggerBusError(moira_handle h, uint32_t faultaddress, bool isWrite);

    MOIRA_C_API int moira_disassemble(moira_handle h, char* str, uint32_t addr);
    MOIRA_C_API void moira_disassembleSR(moira_handle h, char* str);

    MOIRA_C_API void moira_dump8(moira_handle h, char* str, uint8_t v);
    MOIRA_C_API void moira_dump16(moira_handle h, char* str, uint16_t v);
    MOIRA_C_API void moira_dump24(moira_handle h, char* str, uint32_t v);
    MOIRA_C_API void moira_dump32(moira_handle h, char* str, uint32_t v);

    MOIRA_C_API void moira_getStackFrame(moira_handle h, moira_stackframe* frame);
    MOIRA_C_API void moira_setStackFrame(moira_handle h, const moira_stackframe* frame);

#ifdef __cplusplus
}
#endif
