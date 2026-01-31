/*
 * 
 * Ups and downs interrupts
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 * 
 */

namespace ASE
{
    public class InterruptController
    {
        private int currentIRQLevel = 0;
        private bool vblPending = false;
        private bool hblPending = false;
        private bool mfpPending = false;

        public void RaiseMFP()
        {
            mfpPending = true;
            UpdateIRQ();
        }

        public void ClearMFP()
        {
            mfpPending = false;
            UpdateIRQ();
        }

        public void RaiseVBL()
        {
            vblPending = true;
            UpdateIRQ();
        }

        public void ClearVBL()
        {
            vblPending = false;
            UpdateIRQ();
        }

        public void RaiseHBL()
        {
            hblPending = true;
            UpdateIRQ();
        }

        public void ClearHBL()
        {
            hblPending = false;
            UpdateIRQ();
        }

        private void UpdateIRQ()
        {
            byte newLevel = 0;

            if (mfpPending)
                newLevel = 6;
            else if (vblPending)
                newLevel = 4;
            else if (hblPending)
                newLevel = 2;

            if (newLevel != currentIRQLevel)
            {
                currentIRQLevel = newLevel;
                CPU._moira.IPL = newLevel;
            }
        }
    }
}
