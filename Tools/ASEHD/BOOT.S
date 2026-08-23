;=============================================================================
; BOOT.S - boot sector of the ASEHD ACSI hard disk driver
;
; TOS reads sector 0 of ACSI target 0 during start-up and, if its 256 words
; add up to $1234, calls it. This sector does exactly one job: bring the
; resident driver (ASEHD.S, stored in the reserved sectors right behind this
; one) into memory and call its init entry. Everything with any brains in it
; - partition table parsing, BPBs, the transfer engine - lives in the driver,
; where there are 15 KB to spend instead of 450 bytes.
;
; Assemble with vasm, flat binary, no optimisations (the installer relies on
; the header offsets below and nothing here may move):
;
;   vasmm68k_mot -Fbin -no-opt -o ASEHD.BOT BOOT.S
;
; The output must be exactly 512 bytes; tools/asehd/build.cmd checks that.
; The installer (AtariHdDriver.cs) fills in drv_sects/drv_bytes, splices the
; disk's partition table over $1C2..$1FF and fixes up the boot checksum -
; deliberately last, so a half-written install leaves a disk that does not
; boot rather than one that boots into rubbish.
;
; Layout contract with the installer (do not move these):
;   $000  BRA.S to the code
;   $002  'ASEHD2' signature
;   $008  driver length in sectors (word, installer fills in)
;   $00A  driver length in bytes (long, installer fills in)
;   $1C2  Atari partition table (installer splices the disk's own)
;   $1FE  boot checksum word
;
; GEMDOS is already alive when a DMA boot sector runs, so Malloc/Mfree and
; Cconws are available - that is what makes a loader this small possible.
;=============================================================================

;--------------------------------- System variables --------------------------
flock       equ     $43E            ; non-zero locks the DMA against the VBL

;--------------------------------- DMA / HDC registers -----------------------
; Written SIGN-EXTENDED to 32 bits, which is what short absolute addressing
; (.w) needs: the assembler checks the value fits a signed 16-bit word, so it
; wants $FFFF8604 and rejects both $FF8604 and $8604 as "data too large".
diskctl     equ     $FFFF8604       ; FDC/HDC data or sector count
dmamode     equ     $FFFF8606       ; DMA mode/control
dmahigh     equ     $FFFF8609       ; DMA address, high byte
dmamid      equ     $FFFF860B       ; DMA address, mid byte
dmalow      equ     $FFFF860D       ; DMA address, low byte
gpip        equ     $FFFFFA01       ; MFP GPIP; bit 5 low = FDC/HDC interrupt

; DMA mode bits. A1 (bit 1) low marks the FIRST byte of a command packet; the
; controller takes the target from that byte's top three bits. Bits 6-7 set
; mean "DMA disabled", bit 3 selects the HDC over the FDC, bit 4 selects the
; sector-count register, and bit 8 is the direction (set = write to disk).
M_SCTCNT_RD equ     $0090           ; sector count register, read direction
M_SCTCNT_WR equ     $0190           ; sector count register, write direction
M_CMD_FIRST equ     $0088           ; HDC, A1 low  -> first packet byte
M_CMD_NEXT  equ     $008A           ; HDC, A1 high -> following packet bytes
M_CMD_LAST  equ     $000A           ; as above with DMA enabled, read
M_IDLE      equ     $0080           ; FDC selected, DMA off: the bus at rest

DRV_SECTOR  equ     1               ; first sector of the resident driver

;=============================================================================
; Header - the installer patches drv_sects/drv_bytes here.
;=============================================================================

boot:       bra.s   boot_go
            dc.b    'ASEHD2'        ; signature: how the installer knows its own
drv_sects:  dc.w    0               ; sectors of driver to load (installer fills in)
drv_bytes:  dc.l    0               ; driver size in bytes (installer fills in)

boot_go:    movem.l d1-d7/a0-a6,-(sp)

            ; --- Ask GEMDOS for a home for the driver -----------------------
            move.l  drv_bytes(pc),d0
            beq     boot_fail       ; never installed: nothing to load
            move.l  d0,-(sp)
            move.w  #$48,-(sp)      ; Malloc
            trap    #1
            addq.l  #6,sp
            tst.l   d0
            beq     boot_fail       ; no memory: leave the machine alone
            move.l  d0,a5           ; a5 = driver base

            ; --- Read the driver off the disk -------------------------------
            move.l  a5,d1           ; destination
            moveq   #DRV_SECTOR,d2  ; LBA
            moveq   #0,d3
            move.w  drv_sects(pc),d3 ; count (at most 31: one READ(6) covers it)
            bsr     b_read
            tst.l   d0
            bne.s   boot_free

            ; --- Do the sectors really hold our driver? ---------------------
            ; A disk whose boot sector survived but whose driver area was
            ; overwritten would otherwise be jumped into blind.
            cmpi.l  #'ASEH',4(a5)
            bne.s   boot_free
            cmpi.w  #'D2',8(a5)
            bne.s   boot_free

            ; --- Hand over to the driver ------------------------------------
            ; Entry +0 is init: it captures the old vectors, parses the
            ; partition table, mounts the drives, installs itself and prints
            ; the banner. d0 comes back as the mask of drives it mounted;
            ; zero means it found nothing and installed nothing (it has
            ; already said so on the screen), so its memory goes back.
            jsr     (a5)
            tst.l   d0
            bne.s   boot_done
            bsr.s   b_free          ; nothing mounted: the driver already said
            bra.s   boot_done       ; why on the screen; just clean up

boot_free:  bsr.s   b_free          ; load failed: free, then complain
            bra.s   boot_fail

b_free:     move.l  a5,-(sp)        ; give the driver's memory back (Mfree)
            move.w  #$49,-(sp)
            trap    #1
            addq.l  #6,sp
            rts

boot_done:  movem.l (sp)+,d1-d7/a0-a6
            moveq   #0,d0           ; not a bootable program: TOS carries on
            rts

            ; Nothing else reports this: TOS just carries on to the floppy, and
            ; a machine that silently comes up without C: is a puzzle.
boot_fail:  pea     msg_fail(pc)
            move.w  #9,-(sp)        ; Cconws
            trap    #1
            addq.l  #6,sp
            movem.l (sp)+,d1-d7/a0-a6
            moveq   #0,d0
            rts

msg_fail:   dc.b    13,10,'ASEHD: driver not loaded',13,10,0
            even

;-----------------------------------------------------------------------------
; b_read - read d3 sectors from LBA d2 into d1. Cut-down copy of the driver's
; own routine: the driver is not in memory yet, so the bootstrap cannot borrow
; it. Class 0 only - the driver lives in sectors 1..31, far inside the 21-bit
; LBA and the 8-bit count. Returns d0 = 0 on success.
;-----------------------------------------------------------------------------
b_read:     movem.l d1-d5/a0,-(sp)

            move.w  #-1,flock.w     ; keep the VBL out of the DMA

            ; DMA address
            move.l  d1,d0
            move.b  d0,dmalow.w
            lsr.l   #8,d0
            move.b  d0,dmamid.w
            lsr.l   #8,d0
            move.b  d0,dmahigh.w

            ; Toggling the direction bit clears the DMA FIFO; the count then
            ; goes in with the sector-count register selected.
            move.w  #M_SCTCNT_WR,dmamode.w
            move.w  #M_SCTCNT_RD,dmamode.w
            move.w  d3,diskctl.w

            ; READ(6): opcode $08, 21-bit LBA, count, control
            move.w  #M_CMD_FIRST,dmamode.w
            move.w  #$0008,diskctl.w        ; target 0, opcode 8
            move.w  #M_CMD_NEXT,dmamode.w
            move.l  d2,d0
            swap    d0
            andi.w  #$1F,d0                 ; LBA bits 16-20
            move.w  d0,diskctl.w
            move.w  #M_CMD_NEXT,dmamode.w
            move.l  d2,d0
            lsr.l   #8,d0
            move.w  d0,diskctl.w            ; LBA bits 8-15
            move.w  #M_CMD_NEXT,dmamode.w
            move.w  d2,diskctl.w            ; LBA bits 0-7
            move.w  #M_CMD_NEXT,dmamode.w
            move.w  d3,diskctl.w            ; sector count
            move.w  #M_CMD_LAST,dmamode.w   ; DMA on for the final byte
            move.w  #0,diskctl.w            ; control byte -> executes

            bsr.s   b_wait
            tst.l   d0
            bne.s   b_rdout

            ; Read the controller status back
            move.w  #M_CMD_NEXT,dmamode.w
            move.w  diskctl.w,d0
            andi.l  #$FF,d0

            ; Give the DMA chip back to the FDC before releasing the lock -
            ; the floppy boot that may follow expects the bus where TOS
            ; leaves it. Does not touch d0, the status.
b_rdout:    move.w  #M_IDLE,dmamode.w
            clr.w   flock.w
            movem.l (sp)+,d1-d5/a0
            rts

;-----------------------------------------------------------------------------
; b_wait - wait for the controller's interrupt on GPIP5 (active low), with a
; timeout so a missing drive cannot hang the boot. d0 = 0 when it arrived.
;-----------------------------------------------------------------------------
b_wait:     move.l  #400000,d1
b_wait1:    btst    #5,gpip.w
            beq.s   b_wait_ok
            subq.l  #1,d1
            bne.s   b_wait1
            moveq   #-1,d0                  ; timed out
            rts
b_wait_ok:  moveq   #0,d0
            rts

; The partition table lives at $1C2..$1FF of this sector and is written by
; the installer, which also fixes up the $1234 checksum.
            dcb.b   $1C2-(*-boot),0
            dc.l    0               ; $1C2 hd_siz
            dcb.b   12*4,0          ; $1C6 four partition entries
            dc.l    0               ; $1F6 bad sector list start
            dc.l    0               ; $1FA bad sector list count
            dc.w    0               ; $1FE checksum

            end
