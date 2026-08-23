;=============================================================================
; ASEHD.S - resident part of the ASEHD ACSI hard disk driver, version 2.0
;
; The boot sector (BOOT.S) loads this file from the reserved sectors behind
; the root sector into memory it Mallocs, and calls the init entry at +0.
; From then on the driver owns hdv_rw/hdv_bpb/hdv_mediach for the drives it
; mounted and hands everything else - floppies included - to whoever owned
; the vectors before it.
;
; Assemble with vasm, flat binary, no optimisations:
;
;   vasmm68k_mot -Fbin -no-opt -o ASEHD.SYS ASEHD.S
;
; The code is fully PC-relative and runs wherever Malloc puts it. All data
; is dcb, never ds: the boot sector only loads what the file contains, so
; every buffer must exist IN the file.
;
; Layout contract with BOOT.S and the installer (AtariHdDriver.cs):
;   +0  BRA.W to init (must stay 4 bytes: $60 $00 xx xx)
;   +4  'ASEHD2',0,0 - the magic both of them check
;
; Semantics of the ACSI/DMA register dance are unchanged from 1.1 and match
; Hatari's hdc.c (which is what ASE emulates); the partition table rules
; follow AHDI (XGM links are relative to the FIRST extended root, GEM/BGM
; entries inside one are relative to the root they sit in).
;=============================================================================

;--------------------------------- System variables --------------------------
flock       equ     $43E            ; non-zero locks the DMA against the VBL
hdv_bpb     equ     $472            ; vector: return a drive's BPB
hdv_rw      equ     $476            ; vector: read/write logical sectors
hdv_mediach equ     $47E            ; vector: has the medium changed?
_drvbits    equ     $4C2            ; bitmap of mounted drives

;--------------------------------- DMA / HDC registers -----------------------
; Written SIGN-EXTENDED to 32 bits, which is what short absolute addressing
; (.w) needs - the usual Atari spelling, eight hex digits.
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
M_CMD_LASTW equ     $010A           ; as above, write
M_IDLE      equ     $0080           ; FDC selected, DMA off: the bus at rest

;--------------------------------- driver limits -----------------------------
MAXSLOTS    equ     14              ; mountable partitions: drives C: .. P:
CHUNK       equ     255             ; sectors per ACSI command (8-bit count,
                                    ; and 0 means 0 on this controller)
SLOTSIZE    equ     32              ; bytes per slot, see the S_* offsets

;--------------------------------- BIOS error codes --------------------------
ESECNF      equ     -8              ; sector not found
EWRITF      equ     -10             ; write fault
EREADF      equ     -11             ; read fault
EUNDEV      equ     -15             ; unknown device

;--------------------------------- slot layout -------------------------------
; One slot per mounted partition. S_DRV is -1 while the slot is free or the
; partition turned out to be unmountable; nothing else is looked at then.
S_DRV       equ     0               ; word: drive number (2 = C:), -1 = free
S_SHIFT     equ     2               ; word: log2(recsiz/512), 0 for 512-byte
S_START     equ     4               ; long: first physical sector, absolute
S_PSIZE     equ     8               ; long: length in physical sectors
S_BPB       equ     12              ; 9 words: recsiz clsiz clsizb rdlen fsiz
                                    ;          fatrec datrec numcl bflags

;=============================================================================
; Header. The BRA.W must stay 4 bytes - BOOT.S jumps to +0 and the installer
; checks for $60 $00 there; -no-opt keeps vasm's hands off it.
;=============================================================================

driver:     bra.w   d_init
            dc.b    'ASEHD2',0,0    ; +4: magic checked by BOOT.S and installer

            dc.b    'ASEHD driver 2.0',0
            even

;=============================================================================
; d_init - the one call from the bootstrap: supervisor mode, GEMDOS alive.
;
; Captures the vectors, reads the root sector, collects every partition the
; tables describe (phase 1), builds a BPB and assigns a drive letter to each
; one that carries a readable FAT boot record (phase 2), installs the vectors
; and prints the banner. Returns d0 = mask of the _drvbits bits it claimed;
; zero means nothing was mounted and nothing was installed - the bootstrap
; then frees the driver's memory again.
;=============================================================================

d_init:     movem.l d1-d7/a0-a6,-(sp)

            ; --- capture what we may replace --------------------------------
            ; Must happen before the install below: captured afterwards the
            ; vectors would point back into this driver, and every floppy
            ; call would loop into us forever. (On a 68000 PC-relative is a
            ; source-only mode, hence the LEA to write the variables.)
            lea     old_rw(pc),a0
            move.l  hdv_rw.w,(a0)
            lea     old_bpb(pc),a0
            move.l  hdv_bpb.w,(a0)
            lea     old_mediach(pc),a0
            move.l  hdv_mediach.w,(a0)

            ; --- clean slate ------------------------------------------------
            lea     n_found(pc),a0
            clr.w   (a0)
            lea     slots(pc),a3
            move.w  #MAXSLOTS-1,d0
in_clr:     move.w  #-1,S_DRV(a3)
            lea     SLOTSIZE(a3),a3
            dbra    d0,in_clr

            ; --- the root sector --------------------------------------------
            lea     secbuf(pc),a4   ; a4 stays on the sector buffer throughout
            move.l  a4,d1
            moveq   #0,d2
            moveq   #1,d3
            bsr     rd_abs
            tst.l   d0
            bne     in_noroot

            lea     disk_size(pc),a0
            move.l  $1C2(a4),(a0)   ; hd_siz: total sectors (0 = unknown)

            ; Copy both tables out of the buffer before anything reuses it -
            ; the XGM walk and the boot-record reads below all land in secbuf.
            ; Standard entries first, the ICD extras behind them: that is the
            ; order the drives mount in.
            lea     parttab(pc),a0
            lea     $1C6(a4),a1
            moveq   #48-1,d0
in_cp1:     move.b  (a1)+,(a0)+
            dbra    d0,in_cp1
            lea     $156(a4),a1
            move.w  #96-1,d0
in_cp2:     move.b  (a1)+,(a0)+
            dbra    d0,in_cp2

            ; --- phase 1: collect candidates --------------------------------
            lea     parttab(pc),a2
            moveq   #4-1,d5
in_std:     bsr     ent_std         ; GEM/BGM, or an XGM chain to follow
            lea     12(a2),a2
            dbra    d5,in_std
            moveq   #8-1,d5
in_icd:     bsr     ent_prim        ; ICD extras: plain GEM/BGM only
            lea     12(a2),a2
            dbra    d5,in_icd

            ; --- phase 2: mount ---------------------------------------------
            ; d6 = every drive bit claimed so far (system + ours, to pick free
            ; letters from), d7 = only the bits this driver adds.
            move.l  _drvbits.w,d6
            moveq   #0,d7
            lea     slots(pc),a3
            move.w  n_found(pc),d5
            beq.s   in_none
            subq.w  #1,d5
in_mnt:     move.w  d5,-(sp)
            bsr     mount_slot
            move.w  (sp)+,d5
            lea     SLOTSIZE(a3),a3
            dbra    d5,in_mnt

            tst.l   d7
            beq.s   in_none

            ; --- install ----------------------------------------------------
            move    sr,-(sp)
            ori     #$0700,sr       ; vectors must not change under an interrupt
            lea     d_rw(pc),a0
            move.l  a0,hdv_rw.w
            lea     d_bpb(pc),a0
            move.l  a0,hdv_bpb.w
            lea     d_mediach(pc),a0
            move.l  a0,hdv_mediach.w
            or.l    d7,_drvbits.w   ; publish the new drives
            move    (sp)+,sr

            bsr     d_banner
            move.l  d7,d0           ; tell the bootstrap something is mounted
            movem.l (sp)+,d1-d7/a0-a6
            rts

in_noroot:  pea     msg_noroot(pc)
            bra.s   in_msg
in_none:    pea     msg_nopart(pc)
in_msg:     move.w  #9,-(sp)        ; Cconws
            trap    #1
            addq.l  #6,sp
            moveq   #0,d0           ; nothing installed
            movem.l (sp)+,d1-d7/a0-a6
            rts

;-----------------------------------------------------------------------------
; ent_std - one entry of the standard table: GEM/BGM mounts, XGM opens an
; extended chain. a2 -> the 12-byte entry (flag, 3-char id, start.l, size.l).
;-----------------------------------------------------------------------------
ent_std:    btst    #0,(a2)         ; exists?
            beq.s   es_no
            cmpi.b  #'X',1(a2)
            bne.s   ent_prim2       ; not XGM: try it as a plain partition
            cmpi.b  #'G',2(a2)
            bne.s   es_no
            cmpi.b  #'M',3(a2)
            bne.s   es_no
            move.l  4(a2),d0        ; absolute sector of the first extended root
            bra     xgm_walk
es_no:      rts

;-----------------------------------------------------------------------------
; ent_prim - one primary entry, GEM/BGM only (the ICD extras never chain).
;-----------------------------------------------------------------------------
ent_prim:   btst    #0,(a2)
            beq.s   ep_no
ent_prim2:  cmpi.b  #'G',1(a2)
            beq.s   ep_gem
            cmpi.b  #'B',1(a2)
            bne.s   ep_no
            cmpi.b  #'G',2(a2)
            bne.s   ep_no
            bra.s   ep_id3
ep_gem:     cmpi.b  #'E',2(a2)
            bne.s   ep_no
ep_id3:     cmpi.b  #'M',3(a2)
            bne.s   ep_no
            move.l  4(a2),d0        ; start, absolute
            move.l  8(a2),d1        ; size
            bra     add_part
ep_no:      rts

;-----------------------------------------------------------------------------
; add_part - remember a candidate partition: d0 = first sector, d1 = length.
; Refuses the obviously broken (zero, or past the end of a disk whose size
; the table declares) rather than mounting garbage. Clobbers d0-d3/a1.
;-----------------------------------------------------------------------------
add_part:   tst.l   d1
            beq.s   ap_no
            tst.l   d0
            beq.s   ap_no           ; a partition cannot start at the root sector
            move.l  disk_size(pc),d2
            beq.s   ap_room         ; size unknown: nothing to check against
            move.l  d0,d3
            add.l   d1,d3
            bcs.s   ap_no
            cmp.l   d2,d3
            bhi.s   ap_no
ap_room:    move.w  n_found(pc),d2
            cmpi.w  #MAXSLOTS,d2
            bge.s   ap_no
            move.w  d2,d3
            lsl.w   #5,d3           ; * SLOTSIZE
            lea     slots(pc),a1
            adda.w  d3,a1
            move.l  d0,S_START(a1)
            move.l  d1,S_PSIZE(a1)
            move.w  #-1,S_DRV(a1)   ; not mounted yet
            addq.w  #1,d2
            lea     n_found(pc),a1
            move.w  d2,(a1)
ap_no:      rts

;-----------------------------------------------------------------------------
; xgm_walk - follow an extended (XGM) chain. d0 = absolute sector of the
; first extended root. AHDI semantics: a GEM/BGM entry inside an extended
; root is relative to THAT root; the XGM link to the next one is relative to
; the FIRST extended root. Guarded against loops and dead links.
;-----------------------------------------------------------------------------
xgm_walk:   movem.l d4-d7/a2,-(sp)
            move.l  d0,d4           ; d4 = first extended root (link base)
            move.l  d0,d5           ; d5 = the one being read
            moveq   #16,d7          ; chain length guard
xw_next:    move.l  a4,d1
            move.l  d5,d2
            moveq   #1,d3
            bsr     rd_abs
            tst.l   d0
            bne.s   xw_out
            ; copy its four entries out before add_part's callers reuse secbuf
            lea     exttab(pc),a2
            lea     $1C6(a4),a1
            moveq   #48-1,d0
xw_cp:      move.b  (a1)+,(a2)+
            dbra    d0,xw_cp
            lea     exttab(pc),a2
            moveq   #0,d6           ; next link (0 = chain ends here)
            bsr.s   xw_ent
            bsr.s   xw_ent
            bsr.s   xw_ent
            bsr.s   xw_ent
            tst.l   d6
            beq.s   xw_out
            cmp.l   d5,d6
            beq.s   xw_out          ; a self-link would loop forever
            move.l  d6,d5
            subq.w  #1,d7
            bne.s   xw_next
xw_out:     movem.l (sp)+,d4-d7/a2
            rts

; One entry of an extended root: GEM/BGM is a partition at d5+start, XGM sets
; the next link to d4+start. Advances a2 to the next entry.
xw_ent:     btst    #0,(a2)
            beq.s   xe_skip
            cmpi.b  #'X',1(a2)
            beq.s   xe_xgm
            cmpi.b  #'G',1(a2)
            beq.s   xe_g
            cmpi.b  #'B',1(a2)
            bne.s   xe_skip
            cmpi.b  #'G',2(a2)
            bne.s   xe_skip
            bra.s   xe_m
xe_g:       cmpi.b  #'E',2(a2)
            bne.s   xe_skip
xe_m:       cmpi.b  #'M',3(a2)
            bne.s   xe_skip
            move.l  4(a2),d0
            add.l   d5,d0           ; relative to the root it sits in
            move.l  8(a2),d1
            bsr     add_part
            bra.s   xe_skip
xe_xgm:     cmpi.b  #'G',2(a2)
            bne.s   xe_skip
            cmpi.b  #'M',3(a2)
            bne.s   xe_skip
            move.l  4(a2),d6
            add.l   d4,d6           ; relative to the FIRST extended root
xe_skip:    lea     12(a2),a2
            rts

;-----------------------------------------------------------------------------
; mount_slot - phase 2 for one slot (a3): read the partition's FAT boot
; record, build the BPB, and claim the next free drive letter. On any
; problem the slot just stays at S_DRV = -1 - the other partitions still
; mount, which is the point of doing this per slot.
;
; The values in the boot record are LITTLE endian (it is a PC-format BPB),
; so every word is assembled from its two bytes. All BPB fields are in
; LOGICAL sectors of recsiz bytes; S_SHIFT remembers the recsiz/512 ratio
; so hdv_rw can convert. The partition length comes from the partition
; table (32 bits) - the boot record's own 16-bit field is exactly what
; limits an unaided TOS partition to 32 MB, so it is ignored.
;
; In/out: d6 = all claimed drive bits, d7 = the bits this driver added.
; Preserves a3/a4; clobbers d0-d5/a0-a2.
;-----------------------------------------------------------------------------
mount_slot: move.l  a4,d1
            move.l  S_START(a3),d2
            moveq   #1,d3
            bsr     rd_abs
            tst.l   d0
            bne     ms_bad

            ; bytes per logical sector: at least 512 and a power of two
            moveq   #0,d1
            move.b  12(a4),d1
            lsl.w   #8,d1
            move.b  11(a4),d1
            cmpi.w  #512,d1
            blo     ms_bad
            move.w  d1,d0
            andi.w  #$1FF,d0
            bne     ms_bad
            move.w  d1,d2
            lsr.w   #8,d2
            lsr.w   #1,d2           ; /512
            moveq   #0,d3
ms_sh:      cmpi.w  #1,d2
            beq.s   ms_shok
            lsr.w   #1,d2
            bcs     ms_bad          ; a bit fell out: not a power of two
            addq.w  #1,d3
            cmpi.w  #6,d3
            blo.s   ms_sh
            bra     ms_bad          ; recsiz beyond 16 KB: not a FAT we trust
ms_shok:    move.w  d3,S_SHIFT(a3)

            ; sectors per cluster
            moveq   #0,d2
            move.b  13(a4),d2
            beq     ms_bad

            move.w  d1,S_BPB+0(a3)  ; recsiz
            move.w  d2,S_BPB+2(a3)  ; clsiz
            move.w  d1,d0
            mulu    d2,d0
            cmpi.l  #$FFFF,d0
            bhi     ms_bad
            move.w  d0,S_BPB+4(a3)  ; clsizb

            ; root directory length: entries * 32 / recsiz
            moveq   #0,d3
            move.b  18(a4),d3
            lsl.w   #8,d3
            move.b  17(a4),d3
            lsl.l   #5,d3
            divu    d1,d3           ; recsiz >= 512: cannot overflow
            move.w  d3,S_BPB+6(a3)  ; rdlen
            andi.l  #$FFFF,d3

            ; sectors per FAT (zero would be FAT32 territory: refuse)
            moveq   #0,d4
            move.b  23(a4),d4
            lsl.w   #8,d4
            move.b  22(a4),d4
            tst.w   d4
            beq     ms_bad
            move.w  d4,S_BPB+8(a3)  ; fsiz

            ; reserved sectors
            moveq   #0,d5
            move.b  15(a4),d5
            lsl.w   #8,d5
            move.b  14(a4),d5

            ; number of FATs; fatrec = the FAT TOS works with (the second
            ; when there are two)
            moveq   #0,d0
            move.b  16(a4),d0
            beq     ms_bad
            move.w  d5,d1
            cmpi.w  #1,d0
            beq.s   ms_1f
            add.w   d4,d1
ms_1f:      move.w  d1,S_BPB+10(a3) ; fatrec

            ; datrec = reserved + nfats*fsiz + rdlen
            mulu    d4,d0
            add.l   d5,d0
            add.l   d3,d0
            cmpi.l  #$FFFF,d0
            bhi     ms_bad
            move.w  d0,S_BPB+12(a3) ; datrec

            ; numcl = (logical partition size - datrec) / clsiz
            move.l  S_PSIZE(a3),d1
            move.w  S_SHIFT(a3),d4
            lsr.l   d4,d1
            cmp.l   d1,d0
            bhs     ms_bad          ; no room for a data area at all
            sub.l   d0,d1
            move.l  d1,d0
            swap    d0
            cmp.w   d2,d0
            bhs     ms_bad          ; DIVU would overflow: no 16-bit FAT is that big
            divu    d2,d1
            move.w  d1,S_BPB+14(a3) ; numcl

            ; bflags bit 0 says whether the FAT is 16-bit. Not a choice: 4085
            ; clusters and up is FAT16, fewer is FAT12, and getting it wrong
            ; makes GEMDOS misread every chain.
            moveq   #0,d0
            andi.l  #$FFFF,d1
            cmpi.l  #4085,d1
            blo.s   ms_f12
            moveq   #1,d0
ms_f12:     move.w  d0,S_BPB+16(a3) ; bflags

            ; the next free drive letter, C: upward. Letters already taken -
            ; the GEMDOS host drive publishes itself before the boot sector
            ; runs - are simply skipped.
            moveq   #2,d0
ms_bit:     btst    d0,d6
            beq.s   ms_take
            addq.w  #1,d0
            cmpi.w  #16,d0
            blo.s   ms_bit
            rts                     ; no letters left: the slot stays unmounted
ms_take:    bset    d0,d6
            bset    d0,d7
            move.w  d0,S_DRV(a3)
ms_bad:     rts

;-----------------------------------------------------------------------------
; find_slot - d0.w = drive number; returns EQ with a3 -> its slot, NE when no
; mounted partition answers to it. Clobbers d1.
;-----------------------------------------------------------------------------
find_slot:  lea     slots(pc),a3
            move.w  #MAXSLOTS-1,d1
fs_1:       cmp.w   S_DRV(a3),d0
            beq.s   fs_ok           ; Z set: found
            lea     SLOTSIZE(a3),a3
            dbra    d1,fs_1
            moveq   #1,d1           ; clears Z: not ours
fs_ok:      rts

;-----------------------------------------------------------------------------
; d_chain - hand the call on to the vector address in d0. The stack is left
; exactly as the caller built it, so the parameters are still where the old
; routine expects them and its own RTS returns straight to our caller.
;-----------------------------------------------------------------------------
d_chain:    move.l  d0,-(sp)
            rts

;-----------------------------------------------------------------------------
; hdv_mediach - a fixed disk never changes; 0 = medium unchanged. Every other
; drive is handed to the previous owner - answering "unchanged" for A: is
; what would make TOS keep serving the directory of a floppy that is no
; longer in the drive.
;-----------------------------------------------------------------------------
d_mediach:  move.w  4(sp),d0
            movem.l d1/a3,-(sp)
            bsr.s   find_slot
            movem.l (sp)+,d1/a3     ; movem does not touch the CCR
            bne.s   mch_no
            moveq   #0,d0
            rts
mch_no:     move.l  old_mediach(pc),d0
            bne.s   d_chain
            moveq   #0,d0           ; nothing to chain to: claim "unchanged"
            rts

;-----------------------------------------------------------------------------
; hdv_bpb - return a pointer to the BPB of the drive in 4(sp). The BPBs were
; built once at init: a fixed disk's file system does not move.
;-----------------------------------------------------------------------------
d_bpb:      move.w  4(sp),d0
            movem.l d1/a3,-(sp)
            bsr.s   find_slot
            bne.s   bpb_no
            lea     S_BPB(a3),a3
            move.l  a3,d0
            movem.l (sp)+,d1/a3
            rts
bpb_no:     movem.l (sp)+,d1/a3
            move.l  old_bpb(pc),d0
            bne.s   d_chain
            moveq   #0,d0           ; nothing to chain to: no BPB for that drive
            rts

;-----------------------------------------------------------------------------
; hdv_rw - read or write logical sectors.
;
;   4(sp).w  rwflag   bit 0: 0 = read, 1 = write
;                     bit 1: do not retry
;                     bit 3: physical mode (dev = physical unit)
;   6(sp).l  buffer
;  10(sp).w  count    in logical sectors of the drive's recsiz
;  12(sp).w  recno    -1 means "the real number is in lrecno"
;  14(sp).w  dev
;  16(sp).l  lrecno   32-bit sector number (TOS 1.04+)
;
; The lrecno extension is what makes a partition bigger than 32 MB work at
; all: with 512-byte sectors a 16-bit sector number runs out at 65535.
;-----------------------------------------------------------------------------
; Parameter offsets once d1-d7/a0-a6 (56 bytes) are saved:
P_MODE      equ     4+56
P_BUF       equ     6+56
P_CNT       equ     10+56
P_REC       equ     12+56
P_DEV       equ     14+56
P_LREC      equ     16+56

d_rw:       movem.l d1-d7/a0-a6,-(sp)

            move.w  P_MODE(sp),d7
            move.w  P_DEV(sp),d0

            btst    #3,d7
            beq.s   rw_log

            ; --- physical mode: dev 2 = ACSI unit 0, the whole disk ---------
            cmp.w   #2,d0
            blt     rw_chain        ; 0/1 are the floppies: theirs
            bne     rw_undev        ; other physical units: not fitted
            moveq   #0,d5           ; no logical shift
            moveq   #0,d2           ; from sector 0
            move.l  disk_size(pc),d6 ; limit (0 = size unknown: no check)
            bra.s   rw_par

rw_log:     bsr     find_slot
            bne     rw_chain
            move.w  S_SHIFT(a3),d5
            move.l  S_START(a3),d2
            move.l  S_PSIZE(a3),d6
            lsr.l   d5,d6           ; limit in logical sectors

            ; --- the starting record number ---------------------------------
rw_par:     move.w  P_REC(sp),d4
            cmp.w   #-1,d4
            bne.s   rw_r16
            move.l  P_LREC(sp),d4   ; 32-bit sector number
            bra.s   rw_rok
rw_r16:     andi.l  #$FFFF,d4       ; 16-bit, unsigned
rw_rok:     moveq   #0,d3
            move.w  P_CNT(sp),d3
            beq     rw_ok           ; nothing to move is a success

            ; --- range check, in logical sectors, before any shifting -------
            tst.l   d6
            beq.s   rw_nochk
            move.l  d4,d0
            add.l   d3,d0
            bcs     rw_range
            cmp.l   d6,d0
            bhi     rw_range
rw_nochk:
            ; --- to absolute physical sectors -------------------------------
            lsl.l   d5,d4
            lsl.l   d5,d3
            add.l   d2,d4
            move.l  d4,d2           ; d2 = advancing LBA
            move.l  P_BUF(sp),d1    ; d1 = advancing RAM address

            moveq   #0,d5           ; d5 = direction (shift is used up)
            btst    #0,d7
            beq.s   rw_dir
            moveq   #1,d5
rw_dir:     moveq   #3,d6           ; d6 = attempts per chunk
            btst    #1,d7
            beq.s   rw_att
            moveq   #1,d6
rw_att:
            ; The DMA cannot land on an odd address: those go the slow way.
            btst    #0,d1
            bne.s   rw_bnc

            ; --- even buffer: straight DMA, CHUNK sectors per command -------
rw_next:    tst.l   d3
            beq.s   rw_ok
            moveq   #0,d4
            move.w  #CHUNK,d4
            cmp.l   d4,d3
            bhs.s   rw_csz
            move.l  d3,d4
rw_csz:     move.w  d6,d7           ; d7 is free: the mode is fully decoded
rw_try:     bsr     d_xfer
            tst.l   d0
            beq.s   rw_adv
            subq.w  #1,d7
            bne.s   rw_try
            bra.s   rw_err
rw_adv:     add.l   d4,d2
            sub.l   d4,d3
            move.l  d4,d0
            lsl.l   #8,d0
            add.l   d0,d0           ; * 512
            add.l   d0,d1
            bra.s   rw_next

            ; --- odd buffer: one sector at a time through the bounce --------
rw_bnc:     tst.l   d3
            beq.s   rw_ok
            moveq   #1,d4
            tst.w   d5
            beq.s   rw_b1
            movea.l d1,a0           ; write: user memory -> bounce first
            lea     secbuf(pc),a1
            bsr     copy512
rw_b1:      move.l  d1,-(sp)        ; keep the user address across the DMA
            lea     secbuf(pc),a0
            move.l  a0,d1
            move.w  d6,d7
rw_btry:    bsr     d_xfer
            tst.l   d0
            beq.s   rw_bok
            subq.w  #1,d7
            bne.s   rw_btry
            move.l  (sp)+,d1
            bra.s   rw_err
rw_bok:     move.l  (sp)+,d1
            tst.w   d5
            bne.s   rw_b2
            lea     secbuf(pc),a0   ; read: bounce -> user memory
            movea.l d1,a1
            bsr     copy512
rw_b2:      addq.l  #1,d2
            subq.l  #1,d3
            addi.l  #512,d1
            bra.s   rw_bnc

            ; --- exits ------------------------------------------------------
rw_ok:      movem.l (sp)+,d1-d7/a0-a6
            moveq   #0,d0
            rts
rw_range:   movem.l (sp)+,d1-d7/a0-a6
            moveq   #ESECNF,d0
            rts
rw_err:     moveq   #EREADF,d0      ; before the restore: d5 still holds the
            tst.w   d5              ; direction (movem does not touch d0)
            beq.s   rw_e1
            moveq   #EWRITF,d0
rw_e1:      movem.l (sp)+,d1-d7/a0-a6
            rts
rw_undev:   movem.l (sp)+,d1-d7/a0-a6
            moveq   #EUNDEV,d0
            rts

; Not ours. hdv_rw is the vector EVERY drive goes through, floppies included,
; so this hands the call to the routine that owned the vector before us - TOS
; itself for A: and B:, another driver for anything it mounted.
rw_chain:   movem.l (sp)+,d1-d7/a0-a6
            move.l  old_rw(pc),d0
            bne     d_chain
            moveq   #EUNDEV,d0
            rts

;-----------------------------------------------------------------------------
; copy512 - one sector from (a0) to (a1), byte by byte: one side of a bounce
; is odd by definition. Clobbers d0/a0/a1.
;-----------------------------------------------------------------------------
copy512:    move.w  #512/4-1,d0
cp_1:       move.b  (a0)+,(a1)+
            move.b  (a0)+,(a1)+
            move.b  (a0)+,(a1)+
            move.b  (a0)+,(a1)+
            dbra    d0,cp_1
            rts

;-----------------------------------------------------------------------------
; rd_abs - read d3.w sectors (1..255) from absolute LBA d2 into d1: the
; init-time wrapper around d_xfer. Returns d0 = 0 on success.
;-----------------------------------------------------------------------------
rd_abs:     movem.l d4-d5,-(sp)
            moveq   #0,d5           ; direction: read
            move.w  d3,d4
            bsr.s   d_xfer
            movem.l (sp)+,d4-d5
            rts

;-----------------------------------------------------------------------------
; d_xfer - one ACSI command: move d4.w sectors (1..255) between LBA d2 and
; RAM at d1 (must be even), direction d5 (0 = read, 1 = write). d0 = 0 on
; success, else the controller status or -1 for a timeout.
;
; Class 0 commands carry a 21-bit LBA, so anything past sector 2097151 goes
; through the class 1 (10-byte) form, reached with the ICD $1F escape byte.
; Preserves everything but d0.
;-----------------------------------------------------------------------------
d_xfer:     movem.l d1-d4/a0,-(sp)
            move.w  #-1,flock.w     ; keep the VBL out of the DMA

            ; DMA address
            move.l  d1,d0
            move.b  d0,dmalow.w
            lsr.l   #8,d0
            move.b  d0,dmamid.w
            lsr.l   #8,d0
            move.b  d0,dmahigh.w

            ; Toggling the direction bit clears the DMA FIFO; the count then
            ; goes in with the sector-count register selected, and the
            ; toggle leaves the direction bit set the way the transfer runs.
            tst.w   d5
            bne.s   dx_wdir
            move.w  #M_SCTCNT_WR,dmamode.w
            move.w  #M_SCTCNT_RD,dmamode.w
            bra.s   dx_cnt
dx_wdir:    move.w  #M_SCTCNT_RD,dmamode.w
            move.w  #M_SCTCNT_WR,dmamode.w
dx_cnt:     move.w  d4,diskctl.w    ; sector count

            cmpi.l  #$1FFFFF,d2
            bhi.s   dx_c1

            ; ---- class 0: READ(6)/WRITE(6) ----
            move.w  #M_CMD_FIRST,dmamode.w
            moveq   #$08,d0         ; READ
            tst.w   d5
            beq.s   dx_c0op
            moveq   #$0A,d0         ; WRITE
dx_c0op:    move.w  d0,diskctl.w    ; target 0 in the top 3 bits = 0
            move.w  #M_CMD_NEXT,dmamode.w
            move.l  d2,d0
            swap    d0
            andi.w  #$1F,d0
            move.w  d0,diskctl.w    ; LBA bits 16-20
            move.w  #M_CMD_NEXT,dmamode.w
            move.l  d2,d0
            lsr.l   #8,d0
            move.w  d0,diskctl.w    ; LBA bits 8-15
            move.w  #M_CMD_NEXT,dmamode.w
            move.w  d2,diskctl.w    ; LBA bits 0-7
            move.w  #M_CMD_NEXT,dmamode.w
            move.w  d4,diskctl.w    ; sector count
            bsr     dx_last
            move.w  #0,diskctl.w    ; control byte -> executes
            bra     dx_fin

            ; ---- class 1: READ(10)/WRITE(10), via the ICD $1F escape ----
dx_c1:      move.w  #M_CMD_FIRST,dmamode.w
            move.w  #$001F,diskctl.w ; target 0, escape to class 1
            move.w  #M_CMD_NEXT,dmamode.w
            moveq   #$28,d0         ; READ(10)
            tst.w   d5
            beq.s   dx_c1op
            moveq   #$2A,d0         ; WRITE(10)
dx_c1op:    move.w  d0,diskctl.w
            move.w  #M_CMD_NEXT,dmamode.w
            move.w  #0,diskctl.w    ; LUN / reserved
            move.w  #M_CMD_NEXT,dmamode.w
            move.l  d2,d0
            swap    d0
            lsr.w   #8,d0
            move.w  d0,diskctl.w    ; LBA 31-24
            move.w  #M_CMD_NEXT,dmamode.w
            move.l  d2,d0
            swap    d0
            move.w  d0,diskctl.w    ; LBA 23-16
            move.w  #M_CMD_NEXT,dmamode.w
            move.l  d2,d0
            lsr.l   #8,d0
            move.w  d0,diskctl.w    ; LBA 15-8
            move.w  #M_CMD_NEXT,dmamode.w
            move.w  d2,diskctl.w    ; LBA 7-0
            move.w  #M_CMD_NEXT,dmamode.w
            move.w  #0,diskctl.w    ; reserved
            move.w  #M_CMD_NEXT,dmamode.w
            move.w  #0,diskctl.w    ; count, high byte (chunks stay under 256)
            move.w  #M_CMD_NEXT,dmamode.w
            move.w  d4,diskctl.w    ; count, low byte
            bsr     dx_last
            move.w  #0,diskctl.w    ; control byte -> executes

dx_fin:     bsr     dx_wait
            tst.l   d0
            bne.s   dx_out

            ; the controller status: 0 = OK
            move.w  #M_CMD_NEXT,dmamode.w
            move.w  diskctl.w,d0
            andi.l  #$FF,d0

            ; Give the DMA chip back to the FDC before releasing the lock.
            ; The floppy and the hard disk share these registers, and leaving
            ; the HDC selected is asking for trouble from anything that
            ; assumes the bus is where TOS leaves it. Does not touch d0.
dx_out:     move.w  #M_IDLE,dmamode.w
            clr.w   flock.w
            movem.l (sp)+,d1-d4/a0
            rts

; The last packet byte is written with the DMA already enabled, which is what
; makes the controller run the transfer as it executes the command.
dx_last:    tst.w   d5
            bne.s   dx_lw
            move.w  #M_CMD_LAST,dmamode.w
            rts
dx_lw:      move.w  #M_CMD_LASTW,dmamode.w
            rts

;-----------------------------------------------------------------------------
; dx_wait - wait for GPIP5 to go low, with a timeout so a missing drive can
; never hang the machine. Generous: the worst chunk is 255 sectors. d0 = 0
; when the interrupt arrived.
;-----------------------------------------------------------------------------
dx_wait:    move.l  #600000,d1
dx_w1:      btst    #5,gpip.w
            beq.s   dx_wok
            subq.l  #1,d1
            bne.s   dx_w1
            moveq   #-1,d0          ; timed out
            rts
dx_wok:     moveq   #0,d0
            rts

;-----------------------------------------------------------------------------
; d_banner - the sign-on, printed once by init:
;
;     ASEHD 2.0 - ACSI hard disk driver
;     Drive C: 512 MB
;     Drive D: 100 MB
;
; Cconws is available: the hard disk boot happens with GEMDOS already
; running, which is the same reason the bootstrap can call Malloc.
;-----------------------------------------------------------------------------
d_banner:   movem.l d0-d3/a0-a3,-(sp)

            pea     msg_head(pc)
            move.w  #9,-(sp)
            trap    #1
            addq.l  #6,sp

            lea     slots(pc),a3
            move.w  #MAXSLOTS-1,d3
bn_loop:    move.w  S_DRV(a3),d0
            bmi.s   bn_next

            lea     drv_letter(pc),a0
            addi.b  #'A',d0
            move.b  d0,(a0)
            pea     msg_drive(pc)
            move.w  #9,-(sp)
            trap    #1
            addq.l  #6,sp

            ; the size, in MB - or KB for something under 1 MB, because
            ; "0 MB" reads like a bug
            move.l  S_PSIZE(a3),d0
            lea     msg_kb(pc),a2
            cmpi.l  #2048,d0
            blo.s   bn_kb
            lea     msg_mb(pc),a2
            moveq   #11,d1
            lsr.l   d1,d0
            bra.s   bn_num
bn_kb:      lsr.l   #1,d0
bn_num:     bsr.s   d_decimal
            move.l  a0,-(sp)
            move.w  #9,-(sp)
            trap    #1
            addq.l  #6,sp
            move.l  a2,-(sp)
            move.w  #9,-(sp)
            trap    #1
            addq.l  #6,sp

bn_next:    lea     SLOTSIZE(a3),a3
            dbra    d3,bn_loop

            pea     msg_nl(pc)
            move.w  #9,-(sp)
            trap    #1
            addq.l  #6,sp

            movem.l (sp)+,d0-d3/a0-a3
            rts

;-----------------------------------------------------------------------------
; d_decimal - d0.w to decimal ASCII, no leading zeros. Returns a0 pointing at
; the text. 16-bit: the values are megabytes (tops out at 1024 here) or
; kilobytes (under 1024), so DIVU cannot overflow its 16-bit quotient.
;-----------------------------------------------------------------------------
d_decimal:  movem.l d1-d2,-(sp)
            andi.l  #$FFFF,d0
            lea     dec_end(pc),a0
            clr.b   (a0)            ; terminator

d_dec1:     divu    #10,d0
            swap    d0              ; remainder in the low word
            move.b  d0,d1
            addi.b  #'0',d1
            move.b  d1,-(a0)
            clr.w   d0
            swap    d0              ; quotient back in the low word
            tst.w   d0
            bne.s   d_dec1

            movem.l (sp)+,d1-d2
            rts

;--------------------------------- driver data -------------------------------
; Everything dcb/dc, never ds: these bytes must exist IN THE FILE - the
; bootstrap only ever loads what the file contains.
            even

; The three vectors this driver replaced, captured by init before they were
; overwritten. Everything not addressed to a mounted drive is handed on to
; them: they are TOS' own floppy routines, and a driver that keeps
; hdv_rw/hdv_bpb/hdv_mediach to itself leaves the machine with no drive A.
old_rw:     dc.l    0
old_bpb:    dc.l    0
old_mediach:
            dc.l    0

disk_size:  dc.l    0               ; hd_siz from the root sector (0 = unknown)
n_found:    dc.w    0               ; candidates collected in phase 1

slots:      dcb.b   MAXSLOTS*SLOTSIZE,0
parttab:    dcb.b   12*12,0         ; the root's tables, copied out of secbuf
exttab:     dcb.b   4*12,0          ; one extended root's entries, ditto

            even
secbuf:     dcb.b   512,0           ; DMA bounce / init-time sector buffer

;--------------------------------- banner text -------------------------------
msg_head:   dc.b    13,10,'ASEHD 2.0 - ACSI hard disk driver',13,10,'https://github.com/thebitculture/ase',13,10,13,10,0
msg_drive:  dc.b    'Drive '
drv_letter: dc.b    'C',': ',0
msg_mb:     dc.b    ' MB',13,10,0
msg_kb:     dc.b    ' KB',13,10,0
msg_nl:     dc.b    13,10,0
msg_noroot: dc.b    13,10,'ASEHD: cannot read the partition table',13,10,0
msg_nopart: dc.b    13,10,'ASEHD: no mountable GEM/BGM partition',13,10,0
            even

dec_buf:    dcb.b   6,0             ; up to 5 digits, filled backwards
dec_end:    dc.b    0
            even

            end
