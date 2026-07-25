#define MOIRA_C_EXPORTS
#include "Moira_dotnet.h"

#include "Moira.h"
#include <cstdio>
#include <cstdlib>

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

    // Breakpoint latch. didReachBreakpoint fires at the end of the instruction that
    // precedes the guarded one (pc0 == guarded address), so when the run loop stops
    // the guarded instruction has NOT been executed yet.
    bool bpHit;

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
        pendingBusError(false), busErrorAddress(0), busErrorIsWrite(false),
        bpHit(false) {
        // Vectors for IRQs will be managed by ASE
        irqMode = moira::IrqMode::USER;
    }

    // ---- Breakpoints ----

    void didReachBreakpoint(uint32_t addr) override { (void)addr; bpHit = true; }

    bool breakpointWasHit() const { return bpHit; }

    // Like Moira::executeUntil, but stops at the first breakpoint hit. The latch is
    // cleared on entry, so a stale hit (e.g. from single-stepping onto a guarded
    // address with moira_execute) never blocks a later run.
    void executeUntilBp(int64_t targetCycle) {
        bpHit = false;
        while (getClock() < targetCycle && !bpHit) execute();
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

    // ---- Diagnostic hooks (enabled with the MOIRA_TRACE_EXC environment variable) ----
    // Logs CPU fault exceptions (address/bus error, illegal, privilege, line-A/F) with the
    // full register context, plus the interrupt level when a fault is preceded by an IRQ.
    // Normal OS service calls (TRAP, TRAPV, CHK, divide-by-zero, trace) are not logged.
    void willExecute(moira::M68kException exc, uint16_t vector) override {
        using E = moira::M68kException;

        if (exc != E::BUS_ERROR && exc != E::ADDRESS_ERROR && exc != E::ILLEGAL &&
            exc != E::PRIVILEGE && exc != E::LINEA && exc != E::LINEF &&
            exc != E::FORMAT_ERROR && exc != E::IRQ_SPURIOUS) return;

        fprintf(stderr, "[MOIRA-EXC] vec=%u PC=%06X PC0=%06X SR=%04X SP=%06X IRD=%04X IRC=%04X\n",
                vector, getPC(), getPC0(), getSR(), getSP(), getIRD(), getIRC());
        fprintf(stderr, "   D0-7:");
        for (int i = 0; i < 8; i++) fprintf(stderr, " %08X", getD(i));
        fprintf(stderr, "\n   A0-7:");
        for (int i = 0; i < 8; i++) fprintf(stderr, " %08X", getA(i));
        fprintf(stderr, "\n");

        if (exc == E::ADDRESS_ERROR && std::getenv("MOIRA_DUMP_MEM")) {
            uint32_t base = (getPC0() & ~0xFFu);   // 256-byte window around the faulting instruction
            for (uint32_t row = 0; row < 0x100; row += 16) {
                fprintf(stderr, "   %06X:", base + row);
                for (uint32_t c = 0; c < 16; c++)
                    fprintf(stderr, " %02X", read8(base + row + c));
                fprintf(stderr, "\n");
            }
        }
        fflush(stderr);
    }

    void willInterrupt(uint8_t level) override {
        if (!std::getenv("MOIRA_TRACE_IRQ")) return;
        fprintf(stderr, "[MOIRA-IRQ] level=%u PC=%06X SR=%04X SP=%06X clock=%lld\n",
                level, getPC(), getSR(), getSP(), (long long)getClock());
        fflush(stderr);
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
void moira_execute_cycles(moira_handle h, int64_t cycles) { H(h)->executeUntilBp(H(h)->getClock() + cycles); }
void moira_execute_until(moira_handle h, int64_t cycle) { H(h)->executeUntilBp(cycle); }
void moira_setSupervisorMode(moira_handle h, bool s) { H(h)->setSupervisorMode(s); }
void moira_triggerBusError(moira_handle h, uint32_t faultaddress, bool isWrite) {
    H(h)->scheduleBusError(faultaddress, isWrite);
}

// Breakpoints
void moira_bp_setAt(moira_handle h, uint32_t addr) { H(h)->debugger.breakpoints.setAt(addr); }
void moira_bp_removeAt(moira_handle h, uint32_t addr) { H(h)->debugger.breakpoints.removeAt(addr); }
bool moira_bp_isSetAt(moira_handle h, uint32_t addr) { return H(h)->debugger.breakpoints.isSetAt(addr); }
int64_t moira_bp_count(moira_handle h) { return H(h)->debugger.breakpoints.elements(); }
void moira_bp_removeAll(moira_handle h) { H(h)->debugger.breakpoints.removeAll(); }
bool moira_bp_wasHit(moira_handle h) { return H(h)->breakpointWasHit(); }

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
