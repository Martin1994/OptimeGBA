using System;
using System.Diagnostics;

namespace OptimeGBA
{
    public enum SioMode : byte
    {
        Normal8Bit = 0,
        Normal32Bit = 1,
        Multiplayer = 2,
        Uart = 3,
    }

    [Flags]
    public enum SioMultiplayerFlag : ushort
    {
        None = 0,
        BaudRateLow = 1 << 0,
        BaudRateHigh = 1 << 1,
        Child = 1 << 2,
        Ready = 1 << 3,
        PlayerIdLow = 1 << 4,
        PlayerIdHigh = 1 << 5,
        Error = 1 << 6,
        Start = 1 << 7,
        ModeLow = 1 << 12,
        ModeHigh = 1 << 13,
        Irq = 1 << 14,
    }

    [DebuggerDisplay("P{PlayerId} {BaudRate} Child={Child} Ready={Ready} Error={Error} Start={Start}")]
    public struct SioMultiplayerFlagStore
    {
        public GbaLink.BaudRate BaudRate;
        public bool Child;
        public bool Ready;
        public int PlayerId;
        public bool Error;
        public bool Start;

        public override string ToString()
        {
            return $"P{PlayerId} {BaudRate} Child={Child} Ready={Ready} Error={Error} Start={Start}";
        }
    }

    public sealed class SerialGba
    {
        private readonly Gba Gba;

        public GbaLink Link;

        public byte SendData0;
        public byte SendData1;

        public byte[] SioMultiplayerData = new byte[8];

        public SioMultiplayerFlagStore SioMultiplayerFlags;
        public SioMode SioMode = SioMode.Normal8Bit;
        public bool Irq;
        public byte SiocntUnusedBits; // Bits 8-11, R/W

        public ushort Rcnt;

        public SerialGba(Gba gba)
        {
            Gba = gba;
        }

        public byte ReadHwio8(uint addr)
        {
            switch (addr)
            {
                case 0x4000128: // SIOCNT - low
                    return (byte)(
                        (SioMultiplayerFlag)SioMultiplayerFlags.BaudRate |
                        (SioMultiplayerFlags.Child ? SioMultiplayerFlag.Child : SioMultiplayerFlag.None) |
                        (SioMultiplayerFlags.Ready ? SioMultiplayerFlag.Ready : SioMultiplayerFlag.None) |
                        (SioMultiplayerFlag)(SioMultiplayerFlags.PlayerId << 4) |
                        (SioMultiplayerFlags.Error ? SioMultiplayerFlag.Error : SioMultiplayerFlag.None) |
                        (SioMultiplayerFlags.Start ? SioMultiplayerFlag.Start : SioMultiplayerFlag.None)
                    );
                case 0x4000129: // SIOCNT - high
                    return (byte)((int)(
                        (SioMultiplayerFlag)(SiocntUnusedBits << 8) |
                        (SioMultiplayerFlag)((byte)SioMode << 12) |
                        (Irq ? SioMultiplayerFlag.Irq : SioMultiplayerFlag.None)
                    ) >> 8);

                case 0x4000120: // SIOMULTI0 - low
                case 0x4000121: // SIOMULTI0 - high
                case 0x4000122: // SIOMULTI1 - low
                case 0x4000123: // SIOMULTI1 - high
                case 0x4000124: // SIOMULTI2 - low
                case 0x4000125: // SIOMULTI2 - high
                case 0x4000126: // SIOMULTI3 - low
                case 0x4000127: // SIOMULTI3 - high
                    return SioMultiplayerData[addr - 0x4000120];
            }
            return 0;
        }

        public void WriteHwio8(uint addr, byte val)
        {
            switch (addr)
            {
                case 0x4000120: // SIOMULTI0 - low
                case 0x4000121: // SIOMULTI0 - high
                case 0x4000122: // SIOMULTI1 - low
                case 0x4000123: // SIOMULTI1 - high
                case 0x4000124: // SIOMULTI2 - low
                case 0x4000125: // SIOMULTI2 - high
                case 0x4000126: // SIOMULTI3 - low
                case 0x4000127: // SIOMULTI3 - high
                    SioMultiplayerData[addr - 0x4000120] = val;
                    break;

                case 0x4000128: // SIOCNT - low
                {
                    var flag = (SioMultiplayerFlag)val;
                    SioMultiplayerFlags.BaudRate = (GbaLink.BaudRate)((int)flag & 0b11);
                    if (!SioMultiplayerFlags.Child)
                    {
                        SioMultiplayerFlags.Start = (flag & SioMultiplayerFlag.Start) != SioMultiplayerFlag.None;
                        if (Link != null)
                        {
                            Link.MultiplayerBaudRate = SioMultiplayerFlags.BaudRate;
                            if (SioMultiplayerFlags.Start)
                            {
                                Link.MultiplayerStart();
                            }
                        }
                    }
                    break;
                }
                case 0x4000129: // SIOCNT - high
                {
                    var flag = (SioMultiplayerFlag)(val << 8);
                    SioMode = (SioMode)((val >> 4) & 0x3);
                    SiocntUnusedBits = (byte)(val & 0x0F);
                    Irq = (flag & SioMultiplayerFlag.Irq) != SioMultiplayerFlag.None;
                    break;
                }

                case 0x400012A:
                    SendData0 = val;
                    break;
                case 0x400012B:
                    SendData1 = val;
                    break;
            }
        }
    }
}
