using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace RetroSpy
{
    public class SuperPacketDataEventArgs : EventArgs
    {
        public SuperPacketDataEventArgs(byte[] packet)
        {
            Packet = packet;
        }
        public byte[]? GetPacket() { return Packet; }

        private readonly byte[] Packet;
    }

    //public delegate void PacketEventHandler(object sender, PacketDataEventArgs e);

    public class SuperSerialMonitor : IDisposable
    {
        private const int BAUD_RATE = 921600;
        private const int TIMER_MS = 1;

        public event EventHandler<SuperPacketDataEventArgs>? PacketReceived;

        public event EventHandler? Disconnected;

        private SerialPort? _datPort;
        private readonly List<byte> _localBuffer;
        private DispatcherTimer? _timer;
        private readonly bool _printerMode;
        private readonly Stopwatch _stopWatch;
        private readonly bool _isFullSpeed;

        public SuperSerialMonitor(string? portName, bool useLagFix, bool isFullSpeed, bool printerMode = false)
        {
            _printerMode = printerMode;
            _localBuffer = new List<byte>();
            _isFullSpeed = isFullSpeed;
            _datPort = new SerialPort(portName != null ? portName.Split(' ')[0] : "", useLagFix ? 57600 : BAUD_RATE)
            {
                Handshake = Handshake.RequestToSend, // Improves support for devices expecting RTS & DTR signals.
                DtrEnable = true
            };
            _stopWatch = new Stopwatch();
        }

        public void Start()
        {
            if (_timer != null)
            {
                return;
            }

            _localBuffer.Clear();
            if (_datPort != null && _printerMode)
            {
                _datPort.ReadBufferSize = 1000000;
            }

            _datPort?.Open();

            _datPort?.Write("z");
            _datPort?.Write("z");
            _datPort?.Write("z");
            _datPort?.Write(_isFullSpeed ? "x" : "y");
            _datPort?.Write(_isFullSpeed ? "x" : "y");
            _datPort?.Write(_isFullSpeed ? "x" : "y");
            _datPort?.Write("s");

            _timer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(TIMER_MS)
            };
            _timer.Tick += Tick;
            _timer.Start();
        }

        public void Stop()
        {
            if (_datPort != null)
            {
                _datPort?.Write("z");
                _datPort?.Write("z");
                _datPort?.Write("z");
                try
                { // If the device has been unplugged, Close will throw an IOException.  This is fine, we'll just keep cleaning up.
                    _datPort?.Close();
                }
                catch (IOException) { }
                _datPort?.Dispose();
                _datPort = null;
            }
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }
        }

        private void Tick(object? sender, EventArgs e)
        {
            if (_datPort == null || !_datPort.IsOpen || PacketReceived == null)
            {
                return;
            }

            _datPort.Write("p");

            // Try to read some data from the COM port and append it to our localBuffer.
            // If there's an IOException then the device has been disconnected.
            try
            {
                int readCount = _datPort.BytesToRead;
                if (readCount > 0)
                {
                    _stopWatch.Restart();
                }

                byte[] readBuffer = new byte[readCount];
                _ = _datPort.Read(readBuffer, 0, readCount);
                //_datPort.DiscardInBuffer();
                _localBuffer.AddRange(readBuffer);
            }
            catch (IOException)
            {
                Stop();
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }
            catch (OverflowException)  // Linux throws this when the printer emulator is unplugged ???
            {
                Stop();
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (_printerMode)
            {
                // Keep original printer-mode behavior (special-case, time-based).
                int lastSplitIndex = _localBuffer.LastIndexOf(0x0A);
                if (lastSplitIndex <= 1)
                {
                    return;
                }

                byte[] array = _localBuffer.ToArray();
                string lastCommand = Encoding.UTF8.GetString(array, 0, lastSplitIndex);

                if (_stopWatch.ElapsedMilliseconds > 500 &&
                    (lastCommand.Contains("# Finished Pretending To Print for fun!") ||
                     lastCommand.Contains("Memory Waterline:") ||
                     lastCommand.Contains("// Timed Out (Memory Waterline: 4B out of 400B)") ||
                     lastCommand.Contains("// Timed Out (Memory Waterline: 6B out of 400B)")))
                {
                    PacketReceived(this, new SuperPacketDataEventArgs(_localBuffer.GetRange(0, lastSplitIndex).ToArray()));
                    _localBuffer.RemoveRange(0, lastSplitIndex);
                }

                return;
            }

            // Non-printer mode: emit every complete line between '\n' delimiters.
            int startIndex = 0;
            while (true)
            {
                int splitIndex = _localBuffer.IndexOf(0x0A, startIndex);
                if (splitIndex == -1)
                {
                    break; // no more complete lines
                }

                int lineLen = splitIndex - startIndex;
                if (lineLen > 0)
                {
                    PacketReceived(this, new SuperPacketDataEventArgs(_localBuffer.GetRange(startIndex, lineLen).ToArray()));
                }

                startIndex = splitIndex + 1; // move past '\n'
            }

            // Remove everything we have fully processed; keep any partial trailing line in buffer.
            if (startIndex > 0)
            {
                _localBuffer.RemoveRange(0, startIndex);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stop();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
