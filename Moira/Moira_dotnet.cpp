#define MOIRA_C_EXPORTS
#include "moira_dotnet.h"

#include "Moira.h"

#if MOIRA_VIRTUAL_API != true
#error "This wrapper requires MOIRA_VIRTUAL_API == true"
#endif

class MoiraHost : public moira::Moira {
public:
    moira_callbacks cb;

    explicit MoiraHost(const moira_callbacks& cbs) : cb(cbs) {
        // Vertors for IRQs will be managed by ASE
        irqMode = moira::IrqMode::USER;
    }

    void sync(int cycles) override {
        if (cb.sync) cb.sync(cb.user, cycles);
        else moira::Moira::sync(cycles);
    }

    uint8_t read8(uint32_t addr) const override {
        return cb.read8(cb.user, addr);
    }

    uint16_t read16(uint32_t addr) const override {
        return cb.read16(cb.user, addr);
    }

    void write8(uint32_t addr, uint8_t v) const override {
        cb.write8(cb.user, addr, v);
    }

    void write16(uint32_t addr, uint16_t v) const override {
        cb.write16(cb.user, addr, v);
    }

    uint16_t readIrqUserVector(uint8_t level) const override {
        return cb.readIrqUserVector ? cb.readIrqUserVector(cb.user, level) : 0;
    }
};

static inline MoiraHost* H(moira_handle h) { return static_cast<MoiraHost*>(h); }

extern "C" moira_handle moira_create(const moira_callbacks* cb) {
    if (!cb) return nullptr;
    if (!cb->read8 || !cb->read16 || !cb->write8 || !cb->write16) return nullptr;
    try {
        return new MoiraHost(*cb);
    }
    catch (...) {
        return nullptr;
    }
}

extern "C" void moira_destroy(moira_handle h) {
    try { delete H(h); }
    catch (...) {}
}

// Running CPU
extern "C" void moira_reset(moira_handle h) { H(h)->reset(); }
extern "C" void moira_execute(moira_handle h) { H(h)->execute(); }
extern "C" void moira_execute_cycles(moira_handle h, int64_t cycles) { H(h)->execute(cycles); }
extern "C" void moira_execute_until(moira_handle h, int64_t cycle) { H(h)->executeUntil(cycle); }
extern "C" void moira_setSupervisorMode(moira_handle h, bool s) { H(h)->setSupervisorMode(s); }

// Clock
extern "C" int64_t moira_getClock(moira_handle h) { return H(h)->getClock(); }
extern "C" void    moira_setClock(moira_handle h, int64_t v) { H(h)->setClock(v); }

// Registros
extern "C" uint32_t moira_getD(moira_handle h, int n) { return H(h)->getD(n); }
extern "C" void     moira_setD(moira_handle h, int n, uint32_t v) { H(h)->setD(n, v); }

extern "C" uint32_t moira_getA(moira_handle h, int n) { return H(h)->getA(n); }
extern "C" void     moira_setA(moira_handle h, int n, uint32_t v) { H(h)->setA(n, v); }

extern "C" uint32_t moira_getPC(moira_handle h) { return H(h)->getPC(); }
extern "C" void     moira_setPC(moira_handle h, uint32_t v) { H(h)->setPC(v); }

extern "C" uint32_t moira_getPC0(moira_handle h) { return H(h)->getPC0(); }
extern "C" void     moira_setPC0(moira_handle h, uint32_t v) { H(h)->setPC0(v); }

extern "C" uint16_t moira_getIRC(moira_handle h) { return H(h)->getIRC(); }
extern "C" void     moira_setIRC(moira_handle h, uint16_t v) { H(h)->setIRC(v); }

extern "C" uint16_t moira_getIRD(moira_handle h) { return H(h)->getIRD(); }
extern "C" void     moira_setIRD(moira_handle h, uint16_t v) { H(h)->setIRD(v); }

extern "C" uint8_t  moira_getCCR(moira_handle h) { return H(h)->getCCR(); }
extern "C" void     moira_setCCR(moira_handle h, uint8_t v) { H(h)->setCCR(v); }

extern "C" uint16_t moira_getSR(moira_handle h) { return H(h)->getSR(); }
extern "C" void     moira_setSR(moira_handle h, uint16_t v) { H(h)->setSR(v); }

extern "C" uint32_t moira_getSP(moira_handle h) { return H(h)->getSP(); }
extern "C" void     moira_setSP(moira_handle h, uint32_t v) { H(h)->setSP(v); }

extern "C" uint8_t  moira_getIPL(moira_handle h) { return H(h)->getIPL(); }
extern "C" void     moira_setIPL(moira_handle h, uint8_t v) { H(h)->setIPL(v); }

// Disassembler / dumps (tal cual)
extern "C" int moira_disassemble(moira_handle h, char* str, uint32_t addr) {
    return H(h)->disassemble(str, addr);
}

extern "C" void moira_disassembleSR(moira_handle h, char* str) {
    H(h)->disassembleSR(str);
}

extern "C" void moira_dump8(moira_handle h, char* str, uint8_t v) { H(h)->dump8(str, v); }
extern "C" void moira_dump16(moira_handle h, char* str, uint16_t v) { H(h)->dump16(str, v); }
extern "C" void moira_dump24(moira_handle h, char* str, uint32_t v) { H(h)->dump24(str, v); }
extern "C" void moira_dump32(moira_handle h, char* str, uint32_t v) { H(h)->dump32(str, v); }
