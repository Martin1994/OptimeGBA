using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OptimeGBA
{
    public class GbaLink
    {
        public enum BaudRate : byte
        {
            BPS9600 = 0,
            BPS38400 = 1,
            BPS57600 = 2,
            BPS115200 = 3,
        }

        private Gba[] gba;

        static private readonly int[][] multiplayerDelay = new[] {
            //      9600    38400   57600   115200
            new[] { 72527,  18132,  12088,  6044 },
            new[] { 106608, 26652,  17768,  8884 },
            new[] { 133692, 33423,  22282,  11141 }
        };

        public BaudRate MultiplayerBaudRate;

        private int transferCyclesLeft;
        private bool readyToSend = true;

        public bool ReadyToTransfer = false;

        /// <summary>
        /// Number of GBAs that have reached the current sync point.
        /// When all GBAs arrive, we can proceed.
        /// </summary>
        public GbaLink(Gba gba1, Gba gba2, Gba gba3 = null, Gba gba4 = null)
        {
            List<Gba> gbaList = new();
            SetupGba(gbaList, gba1, 0);
            SetupGba(gbaList, gba2, 1);
            SetupGba(gbaList, gba3, 2);
            SetupGba(gbaList, gba4, 3);
            gba = gbaList.ToArray();
        }

        private void SetupGba(List<Gba> gbaList, Gba gba, int id)
        {
            if (gba == null)
            {
                return;
            }

            gbaList.Add(gba);

            gba.Serial.SioMultiplayerFlags.PlayerId = id;
            gba.Serial.SioMultiplayerFlags.Child = id != 0;
            gba.Serial.SioMultiplayerFlags.Ready = true;
            gba.Serial.Link = this;
        }

        public void MultiplayerStart()
        {
            if (!readyToSend) {
                return;
            }
            readyToSend = false;

            transferCyclesLeft = multiplayerDelay[gba.Length - 2][(int)MultiplayerBaudRate];
            gba[0].Scheduler.AddEventRelative(SchedulerId.LinkTransfer, transferCyclesLeft, _ =>
            {
                ReadyToTransfer = true;
            });
            foreach (var current in gba)
            {
                current.Serial.SioMultiplayerFlags.Start = true;
                Array.Fill(current.Serial.SioMultiplayerData, (byte)0xFF);
            }
        }

        public void MultiplayerTransfer()
        {
            // Console.WriteLine("[{0}] Start transfer", gba[0].Scheduler.CurrentTicks);
            ReadyToTransfer = false;
            gba[0].Serial.SioMultiplayerFlags.Child = false;
            foreach (var current in gba)
            {
                current.Serial.SioMultiplayerFlags.Start = false;

                for (int i = 0; i < gba.Length; i++)
                {
                    current.Serial.SioMultiplayerData[i * 2] = gba[i].Serial.SendData0;
                    current.Serial.SioMultiplayerData[i * 2 + 1] = gba[i].Serial.SendData1;
                }

                if (current.Serial.Irq)
                {
                    current.HwControl.FlagInterrupt((uint)InterruptGba.Serial);
                }
            }
            readyToSend = true;
        }
    }
}
