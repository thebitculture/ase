#define MOIRA_C_EXPORTS
#include "Moira_dotnet.h"

#include "Moira.h"

#if MOIRA_VIRTUAL_API != true
#error "This wrapper requires MOIRA_VIRTUAL_API == true"
#endif

class MoiraHost : public moira::Moira {
private:
    moira_callbacks cb;
    
    // Campos para manejar el bus error pendiente
    bool pendingBusError;
    uint32_t busErrorAddress;
    bool busErrorIsWrite;

    // Método helper para lanzar bus error si está pendiente
    void throwPendingBusErrorIfNeeded() const {
        if (pendingBusError) {
            auto* self = const_cast<MoiraHost*>(this);
            self->pendingBusError = false;
            
            moira::StackFrame frame;
            frame.code = (uint16_t)(busErrorIsWrite ? 0x0000 : 0x0010);
            frame.addr = busErrorAddress;
            frame.ird = getIRD();
            frame.sr = getSR();
            frame.pc = getPC();
            
            throw moira::BusError(frame);
        }
    }

public:
    explicit MoiraHost(const moira_callbacks& cbs) : cb(cbs), 
        pendingBusError(false), busErrorAddress(0), busErrorIsWrite(false) {
        // Vectors for IRQs will be managed by ASE
        irqMode = moira::IrqMode::USER;
    }

    void sync(int cycles) override {
        throwPendingBusErrorIfNeeded();
        
        if (cb.sync) cb.sync(cb.user, cycles);
        else moira::Moira::sync(cycles);
    }

    uint8_t read8(uint32_t addr) const override {
        uint8_t result = cb.read8(cb.user, addr);
        throwPendingBusErrorIfNeeded();
        return result;
    }

    uint16_t read16(uint32_t addr) const override {
        uint16_t result = cb.read16(cb.user, addr);
        throwPendingBusErrorIfNeeded();
        return result;
    }

    void write8(uint32_t addr, uint8_t v) const override {
        cb.write8(cb.user, addr, v);
        throwPendingBusErrorIfNeeded();
    }

    void write16(uint32_t addr, uint16_t v) const override {
        cb.write16(cb.user, addr, v);
        throwPendingBusErrorIfNeeded();
    }

    uint16_t readIrqUserVector(uint8_t level) const override {
        return cb.readIrqUserVector ? cb.readIrqUserVector(cb.user, level) : 0;
    }

    void scheduleBusError(uint32_t faultaddress, bool isWrite) {
        pendingBusError = true;
        busErrorAddress = faultaddress;
        busErrorIsWrite = isWrite;
    }
};

static inline MoiraHost* H(moira_handle h) { 
    return static_cast<MoiraHost*>(h); 
}

extern "C" {

// Creation/destruction
moira_handle moira_create(const moira_callbacks* cb) {
    if (!cb || !cb->read8 || !cb->read16 || !cb->write8 || !cb->write16) 
        return nullptr;
    
    try {
        return new MoiraHost(*cb);
    }
    catch (...) {
        return nullptr;
    }
}

void moira_destroy(moira_handle h) {
    try { delete H(h); }
    catch (...) {}
}

// Running CPU
void moira_reset(moira_handle h) { H(h)->reset(); }
void moira_execute(moira_handle h) { H(h)->execute(); }
void moira_execute_cycles(moira_handle h, int64_t cycles) { H(h)->execute(cycles); }
void moira_execute_until(moira_handle h, int64_t cycle) { H(h)->executeUntil(cycle); }
void moira_setSupervisorMode(moira_handle h, bool s) { H(h)->setSupervisorMode(s); }
void moira_triggerBusError(moira_handle h, uint32_t faultaddress, bool isWrite) { 
    H(h)->scheduleBusError(faultaddress, isWrite);
}

// Clock
int64_t moira_getClock(moira_handle h) { return H(h)->getClock(); }
void moira_setClock(moira_handle h, int64_t v) { H(h)->setClock(v); }

// Data registers
uint32_t moira_getD(moira_handle h, int n) { return H(h)->getD(n); }
void moira_setD(moira_handle h, int n, uint32_t v) { H(h)->setD(n, v); }

// Address registers
uint32_t moira_getA(moira_handle h, int n) { return H(h)->getA(n); }
void moira_setA(moira_handle h, int n, uint32_t v) { H(h)->setA(n, v); }

// Program counter
uint32_t moira_getPC(moira_handle h) { return H(h)->getPC(); }
void moira_setPC(moira_handle h, uint32_t v) { H(h)->setPC(v); }

uint32_t moira_getPC0(moira_handle h) { return H(h)->getPC0(); }
void moira_setPC0(moira_handle h, uint32_t v) { H(h)->setPC0(v); }

// Instruction registers
uint16_t moira_getIRC(moira_handle h) { return H(h)->getIRC(); }
void moira_setIRC(moira_handle h, uint16_t v) { H(h)->setIRC(v); }

uint16_t moira_getIRD(moira_handle h) { return H(h)->getIRD(); }
void moira_setIRD(moira_handle h, uint16_t v) { H(h)->setIRD(v); }

// Status registers
uint8_t moira_getCCR(moira_handle h) { return H(h)->getCCR(); }
void moira_setCCR(moira_handle h, uint8_t v) { H(h)->setCCR(v); }

uint16_t moira_getSR(moira_handle h) { return H(h)->getSR(); }
void moira_setSR(moira_handle h, uint16_t v) { H(h)->setSR(v); }

// Stack pointer
uint32_t moira_getSP(moira_handle h) { return H(h)->getSP(); }
void moira_setSP(moira_handle h, uint32_t v) { H(h)->setSP(v); }

// Interrupt level
uint8_t moira_getIPL(moira_handle h) { return H(h)->getIPL(); }
void moira_setIPL(moira_handle h, uint8_t v) { H(h)->setIPL(v); }

// Disassembler / dumps
int moira_disassemble(moira_handle h, char* str, uint32_t addr) {
    return H(h)->disassemble(str, addr);
}

void moira_disassembleSR(moira_handle h, char* str) {
    H(h)->disassembleSR(str);
}

void moira_dump8(moira_handle h, char* str, uint8_t v) { H(h)->dump8(str, v); }
void moira_dump16(moira_handle h, char* str, uint16_t v) { H(h)->dump16(str, v); }
void moira_dump24(moira_handle h, char* str, uint32_t v) { H(h)->dump24(str, v); }
void moira_dump32(moira_handle h, char* str, uint32_t v) { H(h)->dump32(str, v); }

// StackFrame
void moira_getStackFrame(moira_handle h, moira_stackframe* frame) {
    if (!frame) return;

    auto& moira = *H(h);
    frame->code = 0;
    frame->addr = 0;
    frame->ird = moira.getIRD();
    frame->sr = moira.getSR();
    frame->pc = moira.getPC();
    frame->fc = 0;
    frame->ssw = 0;
}

void moira_setStackFrame(moira_handle h, const moira_stackframe* frame) {
    if (!frame) return;

    auto& moira = *H(h);
    moira.setIRD(frame->ird);
    moira.setSR(frame->sr);
    moira.setPC(frame->pc);
}

} // extern "C"
