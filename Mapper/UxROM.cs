using NesEmu.Rom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NesEmu.Mapper {
    public class UxROM : IMapper {
        Rom.Rom CurrentRom;

        const ushort BANK_SIZE = 0x4000;

        byte CurrentBank;
        int LastBankAddr;

        byte[] PrgRom;

        bool Handled;

        public void RegisterRom(Rom.Rom rom) {
            CurrentRom = rom;
            PrgRom = rom.PrgRom;
            CurrentBank = 0;
            LastBankAddr = PrgRom.Length - BANK_SIZE;
        }

        public bool DidMap() {
            return Handled;
        }

        public void SetProgramCounter(int pc) { }

        public void SetScanline(int scanline) { }

        public byte CpuRead(ushort address) {
            Handled = false;
            if (address >= 0x8000 && address < 0xC000) {
                Handled = true;
                return PrgRom[(CurrentBank * BANK_SIZE) + (address - 0x8000)];
            } else if (address >= 0xC000 && address <= 0xFFFF) {
                Handled = true;
                return PrgRom[LastBankAddr + (address - 0xC000)];
            }
            return 0;
        }

        public void CpuWrite(ushort address, byte value) {
            Handled = false;
            if (address >= 0x8000 && address <= 0xffff) {
                Handled = true;
                CurrentBank = (byte)(value & 0b1111);
            }
        }

        public byte PPURead(ushort address) {
            Handled = false;
            return 0;
        }

        public void PPUWrite(ushort address, byte value) {
            Handled = false;
        }

        public ScreenMirroring GetMirroring() {
            return CurrentRom.Mirroring;
        }

        public void Persist() {
            return;
        }

        public void Save(BinaryWriter writer) {
            writer.Write(CurrentBank);
            writer.Write(LastBankAddr);
            writer.Write(PrgRom);
        }

        public void Load(BinaryReader reader) {
            CurrentBank = reader.ReadByte();
            LastBankAddr = reader.ReadInt32();
            PrgRom = reader.ReadBytes(PrgRom.Length);
        }
    }
}
