using NesEmu.Mapper;
using NesEmu.Rom;
using SDL2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace NesEmu.PPU {
    public class PPU {
        public byte[] ChrRom;
        public byte[] PaletteTable;
        public byte[] Vram;
        public byte[] OamData;
        public byte OamAddr;
        public Bus.Bus Bus;

        public int DotsDrawn;
        public int CurrentScanline;
        public ulong TotalCycles;

        byte InternalDataBuffer;
        MaskRegister Mask;
        ControlRegister Ctrl;
        StatusRegister Status;

        bool NmiInterrupt;

        uint[] FrameBuffer = new uint[256 * 240];
        bool[] BgIsOpaque = new bool[256 * 240];

        // Handling these is an absolute nightmare
        // https://wiki.nesdev.org/w/index.php/PPU_scrolling
        // https://www.youtube.com/watch?v=-THeUXqR3zY
        // https://github.com/OneLoneCoder/olcNES/blob/master/Part%20%234%20-%20PPU%20Backgrounds/olc2C02.cpp
        Loopy T_Loopy;
        Loopy V_Loopy;
        byte fineX = 0;
        //// Used when setting/updating T and V
        bool WriteLatch = false;

        byte BgNextTileId,
             BgNextTileAttribute,
             BgNextTileLsb,
             BgNextTileMsb;

        ushort BgShifterPatternLo,
               BgShifterPatternHi,
               BgShifterAttributeLo,
               BgShifterAttributeHi;

        byte[] SpriteShifterPatternLo = new byte[8];
        byte[] SpriteShifterPatternHi = new byte[8];

        struct SpriteEntry {
            public byte YPosition = 0;
            public byte XPosition = 0;
            public byte TileId = 0;
            public byte Attribute = 0;
            public bool SpriteZero = false;
        }
        SpriteEntry[] evaluatedSprites = new SpriteEntry[8];
        byte spriteCount = 0;

        bool SpriteZeroPossible = false;
        bool SpriteZeroRendered = false;

        public PPU(byte[] chrRom) {
            ChrRom = chrRom;
            OamAddr = 0;
            OamData = new byte[256];
            PaletteTable = new byte[128];
            Vram = new byte[0x800];

            InternalDataBuffer = 0;
            Mask = new();
            Ctrl = new();
            Status = new();

            TotalCycles = 0;
            CurrentScanline = 0;
            DotsDrawn = 0;
            NmiInterrupt = false;

            T_Loopy = new();
            V_Loopy = new();
        }

        public void RegisterBus(Bus.Bus bus) {
            Bus = bus;
        }

        public void Save(BinaryWriter writer) {
            writer.Write(ChrRom);
            writer.Write(PaletteTable);
            writer.Write(Vram);
            writer.Write(OamData);
            writer.Write(OamAddr);
            writer.Write(DotsDrawn);
            writer.Write(CurrentScanline);
            writer.Write(TotalCycles);
            writer.Write(InternalDataBuffer);
            writer.Write(Mask.Get());
            writer.Write(Ctrl.Get());
            writer.Write(Status.GetSnapshot());
            writer.Write(NmiInterrupt);
            writer.Write(T_Loopy.GetAddress());
            writer.Write(V_Loopy.GetAddress());
            writer.Write(fineX);
            writer.Write(WriteLatch);
            writer.Write(BgNextTileId);
            writer.Write(BgNextTileAttribute);
            writer.Write(BgNextTileLsb);
            writer.Write(BgNextTileMsb);
            writer.Write(BgShifterPatternLo);
            writer.Write(BgShifterPatternHi);
            writer.Write(BgShifterAttributeLo);
            writer.Write(BgShifterAttributeHi);
            writer.Write(SpriteShifterPatternLo);
            writer.Write(SpriteShifterPatternHi);
        }

        public void Load(BinaryReader reader) {
            ChrRom = reader.ReadBytes(ChrRom.Length);
            PaletteTable = reader.ReadBytes(PaletteTable.Length);
            Vram = reader.ReadBytes(Vram.Length);
            OamData = reader.ReadBytes(OamData.Length);
            OamAddr = reader.ReadByte();
            DotsDrawn = reader.ReadInt32();
            CurrentScanline = reader.ReadInt32();
            TotalCycles = reader.ReadUInt64();
            InternalDataBuffer = reader.ReadByte();
            Mask.Update(reader.ReadByte());
            Ctrl.Update(reader.ReadByte());
            Status.Update(reader.ReadByte());
            NmiInterrupt = reader.ReadBoolean();
            T_Loopy.Update(reader.ReadUInt16());
            V_Loopy.Update(reader.ReadUInt16());
            fineX = reader.ReadByte();
            WriteLatch = reader.ReadBoolean();
            BgNextTileId = reader.ReadByte();
            BgNextTileAttribute = reader.ReadByte();
            BgNextTileLsb = reader.ReadByte();
            BgNextTileMsb = reader.ReadByte();
            BgShifterPatternLo = reader.ReadUInt16();
            BgShifterPatternHi = reader.ReadUInt16();
            BgShifterAttributeLo = reader.ReadUInt16();
            BgShifterAttributeHi = reader.ReadUInt16();
            SpriteShifterPatternLo = reader.ReadBytes(8);
            SpriteShifterPatternHi = reader.ReadBytes(8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void IncrementVramAddr() {
            V_Loopy.Increment(Ctrl.GetVramAddrIncrement());
        }

        // Horizontal:
        //   [ A1 ] [ a2 ]
        //   [ B1 ] [ b2 ]

        // Vertical:
        //   [ A1 ] [ B1 ]
        //   [ a2 ] [ b2 ]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ushort MirrorVramAddr(ushort addr) {
            // Mirrors values like 0x3000-0x3eff down to 0x2000-0x2eff
            var mirroredAddr = addr & 0b10111111111111;
            // Get absolute value within vram
            ushort vector = (ushort)(mirroredAddr - 0x2000);
            
            switch (Bus.Mapper.GetMirroring()) {
                case ScreenMirroring.Vertical:
                    if (vector >= 0x0000 && vector <= 0x03FF)
                        return (ushort)(vector & 0x03FF); 

                    if (vector >= 0x0400 && vector <= 0x07FF)
                        return (ushort)((vector & 0x03FF) + 0x400);

                    if (vector >= 0x0800 && vector <= 0x0BFF)
                        return (ushort)(vector & 0x03FF);

                    if (vector >= 0x0C00 && vector <= 0x0FFF)
                        return (ushort)((vector & 0x03FF) + 0x400);
                    break;
                case ScreenMirroring.Horizontal:
                    if (vector >= 0x0000 && vector <= 0x03FF)
                        return (ushort)(vector & 0x03FF);

                    if (vector >= 0x0400 && vector <= 0x07FF)
                        return (ushort)(vector & 0x03FF);

                    if (vector >= 0x0800 && vector <= 0x0BFF)
                        return (ushort)((vector & 0x03FF) + 0x400);

                    if (vector >= 0x0C00 && vector <= 0x0FFF)
                        return (ushort)((vector & 0x03FF) + 0x400);
                    break;
                case ScreenMirroring.OneScreenLower:
                    if (vector >= 0x0000 && vector <= 0x03FF)
                        return (ushort)(vector & 0x03FF);

                    if (vector >= 0x0400 && vector <= 0x07FF)
                        return (ushort)(vector & 0x03FF);

                    if (vector >= 0x0800 && vector <= 0x0BFF)
                        return (ushort)(vector & 0x03FF);

                    if (vector >= 0x0C00 && vector <= 0x0FFF)
                        return (ushort)(vector & 0x03FF);
                    break;
                case ScreenMirroring.OneScreenUpper:
                    if (vector >= 0x0000 && vector <= 0x03FF)
                        return (ushort)((vector & 0x03FF) + 0x400);

                    if (vector >= 0x0400 && vector <= 0x07FF)
                        return (ushort)((vector & 0x03FF) + 0x400);

                    if (vector >= 0x0800 && vector <= 0x0BFF)
                        return (ushort)((vector & 0x03FF) + 0x400);

                    if (vector >= 0x0C00 && vector <= 0x0FFF)
                        return (ushort)((vector & 0x03FF) + 0x400);
                    break;
                case ScreenMirroring.FourScreen:
                default:
                    break;
            }
            return vector;
        }

        public byte GetChrRom(int addr) {
            var mapperValue = Bus.Mapper.PPURead((ushort)addr);
            if (Bus.Mapper.DidMap()) {
                return mapperValue;
            }
            return ChrRom[addr];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Clock() {
            TotalCycles++;

            if (CurrentScanline >= -1 && CurrentScanline < 240) {
                if (CurrentScanline == 0 && DotsDrawn == 0) {
                    DotsDrawn = 1;
                }

                if (CurrentScanline == -1 && DotsDrawn == 1) {
                    Status.SetVBlank(false);
                    Status.SetSpriteZeroHit(false);
                    Status.SetSpriteOverflow(false);
                    //SpriteZeroPossible = false;
                    for (var j = 0; j < 8; j++) {
                        SpriteShifterPatternLo[j] = 0;
                        SpriteShifterPatternHi[j] = 0;
                    }
                }

                if ((DotsDrawn >= 2 && DotsDrawn < 258) || (DotsDrawn >= 321 && DotsDrawn < 338)) {
                    UpdateShifters();

                    switch ((DotsDrawn - 1) % 8) {
                        case 0:
                            LoadBackgroundShifters();
                            BgNextTileId = Vram[MirrorVramAddr((ushort)(0x2000 | (V_Loopy.GetAddress() & 0x0FFF)))];
                            break;
                        case 2:
                            BgNextTileAttribute = Vram[MirrorVramAddr((ushort)(0x23c0 |
                                (V_Loopy.NametableY << 11) |
                                (V_Loopy.NametableX << 10) |
                                ((V_Loopy.CoarseY >> 2) << 3) |
                                (V_Loopy.CoarseX >> 2)))
                            ];

                            if ((V_Loopy.CoarseY & 0x02) != 0) {
                                BgNextTileAttribute >>= 4;
                            }
                            if ((V_Loopy.CoarseX & 0x02) != 0) {
                                BgNextTileAttribute >>= 2;
                            }
                            BgNextTileAttribute &= 0x03;
                            break;
                        case 4:
                            BgNextTileLsb = GetChrRom(Ctrl.GetBackgroundPatternAddr() + (BgNextTileId * 16) + V_Loopy.FineY);
                            break;
                        case 6:
                            BgNextTileMsb = GetChrRom(Ctrl.GetBackgroundPatternAddr() + (BgNextTileId * 16) + V_Loopy.FineY + 8);
                            break;
                        case 7:
                            IncrementScrollX();
                            break;
                    }
                }

                if (DotsDrawn == 256) {
                    IncrementScrollY();
                }

                if (DotsDrawn == 257) {
                    LoadBackgroundShifters();
                    ResetAddressX();
                }

                if (DotsDrawn == 338 || DotsDrawn == 340) {
                    BgNextTileId = Vram[MirrorVramAddr((ushort)(0x2000 | (V_Loopy.GetAddress() & 0x0FFF)))];
                }

                if (CurrentScanline == -1 && DotsDrawn >= 280 && DotsDrawn < 305) {
                    ResetAddressY();
                }

                // Sprite evaluation
                if (DotsDrawn == 257 && CurrentScanline >= 0) {
                    spriteCount = 0;

                    for (var j = 0; j < 8; j++) {
                        SpriteShifterPatternLo[j] = 0;
                        SpriteShifterPatternHi[j] = 0;
                    }

                    var index = 0;
                    SpriteZeroPossible = false;

                    unsafe {
                        fixed(SpriteEntry* ptr = evaluatedSprites) {
                            foreach (var oamEntry in OamData) {
                                if (spriteCount >= 8) {
                                    Status.SetSpriteOverflow(true);
                                    break;
                                }
                                if (index >= 64) {
                                    break;
                                }

                                var yPosition = OamData[index * 4];
                                var tileIndex = OamData[(index * 4) + 1];
                                var attributes = OamData[(index * 4) + 2];
                                var xPosition = OamData[(index * 4) + 3];

                                if (yPosition == 0 && tileIndex == 0 && attributes == 0 && xPosition == 0) {
                                    index++;
                                    continue;
                                }

                                var yDiff = CurrentScanline - yPosition;
                                if (yDiff >= 0 && yDiff < Ctrl.GetSpriteSize()) {
                                    if (spriteCount < 8) {
                                        SpriteEntry sprite;
                                        sprite.YPosition = yPosition;
                                        sprite.XPosition = xPosition;
                                        sprite.Attribute = attributes;
                                        sprite.TileId = tileIndex;
                                        if (index == 0) {
                                            sprite.SpriteZero = true;
                                            SpriteZeroPossible = true;
                                        } else {
                                            sprite.SpriteZero = false;
                                        }

                                        *(ptr + spriteCount) = sprite;
                                        spriteCount++;
                                    } else {
                                        spriteCount++;
                                    }
                                }

                                index++;
                            }
                        }
                    }
                } 

                if (DotsDrawn == 340) {
                    var spriteIndex = 0;
                    foreach (var sprite in evaluatedSprites) {
                        byte patternBitsLo, patternBitsHi;
                        ushort patternAddrLo, patternAddrHi;

                        var paletteVal = sprite.Attribute & 0b11;
                        var priority = (sprite.Attribute >> 5 & 1) == 0;
                        var flipHorizontal = (sprite.Attribute >> 6 & 1) == 1;
                        var flipVertical = (sprite.Attribute >> 7 & 1) == 1;

                        if (Ctrl.GetSpriteSize() == 8) {
                            if (flipVertical) {
                                patternAddrLo = (ushort)(Ctrl.GetSpritePatternAddr() | (sprite.TileId * 16) | (byte)(7 - (CurrentScanline - sprite.YPosition)));
                            } else {
                                patternAddrLo = (ushort)(Ctrl.GetSpritePatternAddr() | (sprite.TileId * 16) | (byte)(CurrentScanline - sprite.YPosition));
                            }

                        } else {
                            if (flipVertical) {
                                if (CurrentScanline - sprite.YPosition < 8) {
                                    patternAddrLo = (ushort)(
                                        (sprite.TileId & 1) << 12 |
                                        (((sprite.TileId & 0xFE) + 1) * 16) |
                                        (byte)(7 - (CurrentScanline - sprite.YPosition) & 0b111)
                                    );
                                } else {
                                    patternAddrLo = (ushort)(
                                        (sprite.TileId & 1) << 12 |
                                        ((sprite.TileId & 0xFE) * 16) |
                                        (byte)(7 - (CurrentScanline - sprite.YPosition) & 0b111)
                                    );
                                }
                            } else {
                                if (CurrentScanline - sprite.YPosition < 8) {
                                    patternAddrLo = (ushort)(
                                        (sprite.TileId & 1) << 12 |
                                        ((sprite.TileId & 0xFE) * 16) |
                                        (byte)((CurrentScanline - sprite.YPosition) & 0b111)
                                    );
                                } else {
                                    patternAddrLo = (ushort)(
                                        (sprite.TileId & 1) << 12 |
                                        (((sprite.TileId & 0xFE) + 1) * 16) |
                                        (byte)((CurrentScanline - sprite.YPosition) & 0b111)
                                    );
                                }
                            }
                        }

                        patternAddrHi = (ushort)(patternAddrLo + 8);
                        patternBitsLo = GetChrRom(patternAddrLo);
                        patternBitsHi = GetChrRom(patternAddrHi);

                        if (flipHorizontal) {
                            // https://stackoverflow.com/a/2602885
                            // What the fuck
                            patternBitsLo = (byte)((patternBitsLo & 0xF0) >> 4 | (patternBitsLo & 0x0F) << 4);
                            patternBitsLo = (byte)((patternBitsLo & 0xCC) >> 2 | (patternBitsLo & 0x33) << 2);
                            patternBitsLo = (byte)((patternBitsLo & 0xAA) >> 1 | (patternBitsLo & 0x55) << 1);

                            patternBitsHi = (byte)((patternBitsHi & 0xF0) >> 4 | (patternBitsHi & 0x0F) << 4);
                            patternBitsHi = (byte)((patternBitsHi & 0xCC) >> 2 | (patternBitsHi & 0x33) << 2);
                            patternBitsHi = (byte)((patternBitsHi & 0xAA) >> 1 | (patternBitsHi & 0x55) << 1);
                        }

                        SpriteShifterPatternLo[spriteIndex] = patternBitsLo;
                        SpriteShifterPatternHi[spriteIndex] = patternBitsHi;

                        spriteIndex++;

                        if (spriteIndex >= spriteCount) {
                            break;
                        }
                    }
                } 

            }

            if (CurrentScanline == 240) {
                // Nothing?
            }

            if (CurrentScanline == 241 && DotsDrawn == 1) {
                Status.SetVBlank(true);
                if (Ctrl.ShouldGenerateVBlank()) {
                    NmiInterrupt = true;
                }
            }

            byte bgPixel = 0;
            byte bgPalette = 0;

            byte fgPixel = 0;
            byte fgPalette = 0;

            bool spritePriority = false;

            if (Mask.GetBackground() && (DotsDrawn > 0 && DotsDrawn <= 256 && CurrentScanline >= 0 && CurrentScanline < 240)) {
                ushort bitMux = (ushort)(0x8000 >> fineX);

                byte p0Pixel = (byte)((BgShifterPatternLo & bitMux) > 0 ? 1 : 0);
                byte p1Pixel = (byte)((BgShifterPatternHi & bitMux) > 0 ? 1 : 0);
                bgPixel = (byte)((p1Pixel << 1) | p0Pixel);

                byte p0Palette = (byte)((BgShifterAttributeLo & bitMux) > 0 ? 1 : 0);
                byte p1Palette = (byte)((BgShifterAttributeHi & bitMux) > 0 ? 1 : 0);
                bgPalette = (byte)((p1Palette << 1) | p0Palette);
            }

            if (Mask.GetSprite() && (DotsDrawn > 0 && DotsDrawn <= 256 && CurrentScanline >= 0 && CurrentScanline < 240)) {
                SpriteZeroRendered = false;

                var index = 0;
                foreach (var sprite in evaluatedSprites) {
                    if (index >= spriteCount) {
                        break;
                    }

                    if (sprite.XPosition == 0) {
                        var spritePixelLo = (SpriteShifterPatternLo[index] & 0x80) > 0 ? 1 : 0;
                        var spritePixelHi = (SpriteShifterPatternHi[index] & 0x80) > 0 ? 1 : 0;

                        fgPixel = (byte)((spritePixelHi << 1) | spritePixelLo);
                        fgPalette = (byte)((sprite.Attribute & 0b11) + 0b100);
                        spritePriority = (sprite.Attribute & 0x20) == 0;

                        if (fgPixel != 0) {
                            if (sprite.SpriteZero) {
                                SpriteZeroRendered = true;
                            }

                            // Since sprites are sorted in priority, if we actually find a sprite for the pixel, we can just skip the rest
                            break;
                        }
                    }
                    index++;
                }
            }

            byte renderPixel = 0;
            byte renderPalette = 0;

            if (bgPixel == 0 && fgPixel == 0) {
                // Just continue;
            } else if (bgPixel == 0 && fgPixel > 0) {
                renderPixel = fgPixel;
                renderPalette = fgPalette;
            } else if (bgPixel > 0 && fgPixel == 0) {
                renderPixel = bgPixel;
                renderPalette = bgPalette;
            } else if (bgPixel > 0 && fgPixel > 0) {
                if (spritePriority) {
                    renderPixel = fgPixel;
                    renderPalette = fgPalette;
                } else {
                    renderPixel = bgPixel;
                    renderPalette = bgPalette;
                }

                if (SpriteZeroPossible && SpriteZeroRendered) {
                    if (Mask.GetBackground() && Mask.GetSprite()) {
                        var backgroundLeft = Mask.GetBackgroundLeftColumn();
                        var spriteLeft = Mask.GetSpriteLeftColumn();

                        if (backgroundLeft && spriteLeft) {
                            if (DotsDrawn >= 1 && DotsDrawn <= 258) {
                                Status.SetSpriteZeroHit(true);
                            }
                        } else {
                            if (DotsDrawn >= 9 && DotsDrawn <= 258) {
                                Status.SetSpriteZeroHit(true);
                            }
                        }
                    } 
                }
            }

            var color = Palette.SystemPalette[PaletteTable[(renderPalette << 2) + renderPixel] & 0x3f];
            SetPixel(DotsDrawn - 1, CurrentScanline, color);

            DotsDrawn++;
            if (DotsDrawn >= 341) {
                DotsDrawn = 0;
                CurrentScanline++;
                if (CurrentScanline >= 261) {
                    CurrentScanline = -1;
                    Status.ResetVBlank();
                    NmiInterrupt = false;
                    return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsInterrupt() {
            return NmiInterrupt;
        }

        /// <summary>
        /// Warning: Sets NmiInterrupt to false after getting value
        /// If you just want to check, use IsInterrupt()
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GetInterrupt() {
            var interrupt = NmiInterrupt;
            NmiInterrupt = false;
            return interrupt;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort GetBackgroundPatternAddr() {
            return Ctrl.GetBackgroundPatternAddr();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort GetSpritePatternAddr() {
            return Ctrl.GetSpritePatternAddr();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetSpriteSize() {
            return Ctrl.GetSpriteSize();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteCtrl(byte value) {
            T_Loopy.NametableX = (byte)(value & 0b01);
            T_Loopy.NametableY = (byte)((value & 0b10) >> 1);

            var beforeNmi = Ctrl.ShouldGenerateVBlank();
            Ctrl.Update(value);
            if (!beforeNmi && Ctrl.ShouldGenerateVBlank() && Status.IsVBlank()) {
                NmiInterrupt = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteMask(byte value) {
            Mask.Update(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void IncrementScrollX() {
            if (Mask.GetSprite() || Mask.GetBackground()) {
                if (V_Loopy.CoarseX == 31) {
                    V_Loopy.CoarseX = 0;

                    // Flip the X bit
                    V_Loopy.NametableX = (byte)(~V_Loopy.NametableX);
                } else {
                    V_Loopy.CoarseX++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void IncrementScrollY() {
            if (Mask.GetSprite() || Mask.GetBackground()) {
                if (V_Loopy.FineY < 7) {
                    V_Loopy.FineY++;
                } else {
                    V_Loopy.FineY = 0;

                    if (V_Loopy.CoarseY == 29) {
                        V_Loopy.CoarseY = 0;
                        // Flip the Y bit
                        V_Loopy.NametableY = (byte)~V_Loopy.NametableY;
                    } else if (V_Loopy.CoarseY == 31) {
                        V_Loopy.CoarseY = 0;
                    } else {
                        V_Loopy.CoarseY++;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ResetAddressX() {
            if (Mask.GetSprite() || Mask.GetBackground()) {
                V_Loopy.NametableX = T_Loopy.NametableX;//(byte)((V_Loopy.Nametable & 0b1) | ( & 0b10));
                V_Loopy.CoarseX = T_Loopy.CoarseX;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ResetAddressY() {
            if (Mask.GetSprite() || Mask.GetBackground()) {
                V_Loopy.NametableY = T_Loopy.NametableY;//(byte)((V_Loopy.Nametable & 0b10) | (T_Loopy.Nametable & 0b1));
                V_Loopy.FineY = T_Loopy.FineY;
                V_Loopy.CoarseY = T_Loopy.CoarseY;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LoadBackgroundShifters() {
            BgShifterPatternLo = (ushort)((BgShifterPatternLo & 0xFF00) | BgNextTileLsb);
            BgShifterPatternHi = (ushort)((BgShifterPatternHi & 0xFF00) | BgNextTileMsb);

            var attributeLo = BgNextTileAttribute & 0b01;
            var attributeHi = BgNextTileAttribute & 0b10;

            if (attributeLo != 0) {
                BgShifterAttributeLo = (ushort)((BgShifterAttributeLo & 0xFF00) | 0xFF);
            } else {
                BgShifterAttributeLo = (ushort)((BgShifterAttributeLo & 0xFF00));
            }
            if (attributeHi != 0) {
                BgShifterAttributeHi = (ushort)((BgShifterAttributeHi & 0xFF00) | 0xFF);
            } else {
                BgShifterAttributeHi = (ushort)((BgShifterAttributeHi & 0xFF00));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateShifters() {
            unsafe {
                fixed (SpriteEntry* ptr = evaluatedSprites) {
                    if (Mask.GetBackground()) {
                        BgShifterAttributeLo <<= 1;
                        BgShifterAttributeHi <<= 1;
                        BgShifterPatternLo <<= 1;
                        BgShifterPatternHi <<= 1;
                    }

                    if (Mask.GetSprite() && DotsDrawn >= 1 && DotsDrawn < 258) {
                        for (var index  = 0; index < spriteCount; index++) {
                            var sprite = *(ptr + index);
                            if (sprite.XPosition > 0) {
                                sprite.XPosition -= 1;
                                *(ptr + index) = sprite;
                            } else {
                                SpriteShifterPatternLo[index] <<= 1;
                                SpriteShifterPatternHi[index] <<= 1;
                            }
                        }
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (byte r, byte g, byte b) GetPaletteFromMemory(byte palette, byte pixel) {
            return Palette.SystemPalette[PaletteTable[(palette << 2) + pixel] & 0x3f];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteScroll(byte value) {
            if (WriteLatch) {
                var coarseY = value >> 3;
                var fineY = value & 0b111;

                T_Loopy.CoarseY = (byte)coarseY;
                T_Loopy.FineY = (byte)fineY;

                WriteLatch = false;
            } else {
                fineX = (byte)(value & 0b111);
                var coarseX = value >> 3;
                T_Loopy.CoarseX = (byte)coarseX;

                WriteLatch = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WritePPUAddr(byte value) {
            if (WriteLatch) {
                T_Loopy.Update((ushort)((T_Loopy.GetAddress() & 0xFF00) | value));
                V_Loopy.Update(T_Loopy.GetAddress());
                WriteLatch = false;
            } else {
                T_Loopy.Update((ushort)(((value & 0x3f) << 8) | (T_Loopy.GetAddress() & 0x00ff)));
                WriteLatch = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetData() {
            var addr = V_Loopy.GetAddress();
            IncrementVramAddr();

            return Read(addr);
        }

        public byte Read(ushort addr) {
            var mapperValue = Bus.Mapper.PPURead(addr);
            if (Bus.Mapper.DidMap()) {
                return mapperValue;
            }

            if (addr >= 0 && addr <= 0x1fff) {
                var result = InternalDataBuffer;
                InternalDataBuffer = GetChrRom(addr);
                return result;
            }

            // Nametable
            if (addr >= 0x2000 && addr <= 0x2fff) {
                var result = InternalDataBuffer;
                InternalDataBuffer = Vram[MirrorVramAddr(addr)];
                return result;
            }

            if (addr >= 0x3000 && addr <= 0x3eff) {
                // Normally undefined address space, but some games depend on this like Zelda
                var result = InternalDataBuffer;
                InternalDataBuffer = Vram[MirrorVramAddr(addr)];
                return result;
            }

            if (addr == 0x3f10 || addr == 0x3f14 || addr == 0x3f18 || addr == 0x3f1c) {
                var mirror = addr - 0x10;
                return PaletteTable[mirror - 0x3f00];
            }

            if (addr >= 0x3f00 && addr <= 0x3fff) {
                return PaletteTable[addr - 0x3f00];
            }

            throw new Exception("Reached unknown address");
        }

        public void WriteData(byte value) {
            var addr = V_Loopy.GetAddress();
            IncrementVramAddr();

            Bus.Mapper.PPUWrite(addr, value);
            if (Bus.Mapper.DidMap()) {
                return;
            }

            if (addr >= 0 && addr <= 0x1fff) {
                ChrRom[addr] = value;
                return;
            }

            if (addr >= 0x2000 && addr <= 0x2fff) {
                var mirror = MirrorVramAddr(addr);
                Vram[mirror] = value;
                return;
            }

            if (addr >= 0x3000 && addr <= 0x3eff) {
                //Console.WriteLine("0x3000 > x 0x3eff being used");
                //throw new NotImplementedException("Shouldn't be used");
                return;
            }


            if (addr == 0x3f10 || addr == 0x3f14 || addr == 0x3f18 || addr == 0x3f1c) {
                PaletteTable[(addr - 0x10) - 0x3f00] = value;
                return;
            }

            if (addr >= 0x3f00 && addr <= 0x3fff) {
                PaletteTable[addr - 0x3f00] = value;
                return;
            }
        }

        public byte GetStatus() {
            var status = Status.GetSnapshot();
            Status.ResetVBlank();
            WriteLatch = false;
            return status;
        }

        public byte GetOAMData() {
            return OamData[OamAddr];
        }
        public void WriteOAMData(byte value) {
            OamData[OamAddr] = value;
            OamAddr++;
        }

        public byte GetOAMAddr() {
            return OamAddr;
        }
        public void WriteOAMAddr(byte value) {
            OamAddr = value;
        }

        public void WriteDMA(byte[] data) {
            foreach (var b in data) {
                OamData[OamAddr] = b;
                OamAddr++;
            }
        }

        public ushort GetNameTableAddress() {
            return Ctrl.GetNameTableAddress();
        }

        public byte[] GetNametableTilePalette(byte[] nametable, byte tileX, byte tileY) {
            var attrTableIndex = tileY / 4 * 8 + tileX / 4;
            var attrValue = nametable[0x3c0 + attrTableIndex];

            var segmentX = tileX % 4 / 2;
            var segmentY = tileY % 4 / 2;

            byte paletteIndex = 0;
            if (segmentX == 0) {
                if (segmentY == 0) {
                    paletteIndex = (byte)(attrValue & 0b11);
                } else if(segmentY == 1) {
                    paletteIndex = (byte)((attrValue >> 4) & 0b11);
                }
            } else {
                if (segmentY == 0) {
                    paletteIndex = (byte)((attrValue >> 2) & 0b11);
                } else if (segmentY == 1){
                    paletteIndex = (byte)((attrValue >> 6) & 0b11);
                }
            }

            var paletteStart =  1 + paletteIndex * 4;
            return new byte[] {
                PaletteTable[0],
                PaletteTable[paletteStart],
                PaletteTable[paletteStart + 1],
                PaletteTable[paletteStart + 2],
            };
        }

        public byte[] GetSpritePalette(byte spriteIndex) {
            var paletteStart = 17 + (spriteIndex * 4);
            return new byte[] {
                0,
                PaletteTable[paletteStart],
                PaletteTable[paletteStart + 1],
                PaletteTable[paletteStart + 2],
            };
        }

        public void DrawFrame(ref IntPtr renderer, ref IntPtr Texture) {
            unsafe {
                SDL.SDL_Rect rect;
                rect.w = 256 * 3;
                rect.h = 240 * 3;
                rect.x = 0;
                rect.y = 0;

                fixed (uint* pArray = FrameBuffer) {
                    IntPtr intPtr = new(pArray);

                    SDL.SDL_UpdateTexture(Texture, ref rect, intPtr, 256 * 4);
                }

                SDL.SDL_RenderCopy(renderer, Texture, IntPtr.Zero, ref rect);
                SDL.SDL_RenderPresent(renderer);
            }
        }

        private void SetPixel(int x, int y, (byte r, byte g, byte b) color) {
            if (x < 0 || x > 255 || y < 0 || y >= 240) {
                return;
            }

            FrameBuffer[
                x +
                (y * 256)
            ] = (uint)((color.r << 16) | (color.g << 8 | (color.b << 0)));
        }
    }
}
