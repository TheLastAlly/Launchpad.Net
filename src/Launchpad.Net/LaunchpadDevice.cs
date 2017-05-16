using Launchpad.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Launchpad
{
    public class LaunchpadDevice : IDisposable
    {
        public event Action<IReadOnlyList<LaunchpadEvent>> Tick;

        public static IReadOnlyList<string> DeviceNames { get; private set; }

        static LaunchpadDevice()
        {
            RefreshDevices();
        }
        public static void RefreshDevices()
        {
            var deviceNames = new List<string>();
            int inDeviceCount = NativeMethods.midiInGetNumDevs();
            for (uint i = 0; i < inDeviceCount; i++)
            {
                var caps = new MIDIINCAPS();
                NativeMethods.midiInGetDevCaps(i, ref caps, MIDIINCAPS.Size);
                if (caps.szPname.Contains("Launchpad MK2"))
                    deviceNames.Add(caps.szPname);
            }
            DeviceNames = deviceNames;
        }

        private uint _inDeviceId, _outDeviceId;
        private IntPtr _inDeviceHandle, _outDeviceHandle;
        private NativeMethods.MidiInProc _inputCallback;
        private NativeMethods.MidiOutProc _outputCallback;
        private ManualResetEventSlim _inputClosed, _outputClosed;

        private ConcurrentQueue<LaunchpadEvent> _queuedEvents;
        private Task _task;
        private CancellationTokenSource _cancelToken;

        private MidiBuffer _outBuffer;
        private byte[] _normalMsg, _pulseMsg, _flashMsg;
        private LED[] _leds;
        private bool _ledsInvalidated;

        public string Name { get; }
        public bool IsConnected { get; private set; }
        public bool IsRunning { get; private set; }

        public LaunchpadDevice(string name)
        {
            Name = name;
        }
        public void Dispose()
        {
            Stop();
        }

        public void Start(int tps, int skip = 0)
        {
            Stop();
            _queuedEvents = new ConcurrentQueue<LaunchpadEvent>();
            _cancelToken = new CancellationTokenSource();
            _task = RunTask(tps, skip, _cancelToken.Token);
            _leds = new LED[80];
            _normalMsg = new byte[8 + (2 * 80)];
            _pulseMsg = new byte[8 + (3 * 80)];
            _flashMsg = new byte[8 + (3 * 80)];
            _inputClosed = new ManualResetEventSlim(false);
            _outputClosed = new ManualResetEventSlim(false);

            WriteHeader(_normalMsg, 0x0A);
            WriteHeader(_pulseMsg, 0x28);
            WriteHeader(_flashMsg, 0x23);
        }
        public void Stop()
        { 
            if (_task != null)
            {
                _cancelToken.Cancel();
                _task.GetAwaiter().GetResult();
                _task = null;
            }
            Disconnect();
        }

        private Task RunTask(int tps, int skip, CancellationToken cancelToken)
        {
            return Task.Run(async () =>
            {
                IsRunning = true;
                var events = new List<LaunchpadEvent>();

                var tickLength = TimeSpan.FromSeconds((1.0 / tps * (skip + 1)));
                var nextTick = DateTimeOffset.UtcNow + tickLength;
                while (!cancelToken.IsCancellationRequested)
                {
                    //Limit loop to tps rate
                    var now = DateTimeOffset.UtcNow;
                    if (now < nextTick)
                    {
                        await Task.Delay(nextTick - now);
                        continue;
                    }
                    nextTick += tickLength;

                    //If not connected to the device, reconnect
                    if (!IsConnected)
                    {
                        if (!Connect())
                            continue;
                    }

                    //Fetch all queued events for this tick
                    while (_queuedEvents.TryDequeue(out var evnt))
                        events.Add(evnt);

                    Tick?.Invoke(events);
                    Render();

                    events.Clear();
                }
                IsRunning = false;
            });
        }

        private bool Connect()
        {
            int inDeviceCount = NativeMethods.midiInGetNumDevs();
            uint? inDeviceId = null;
            for (uint i = 0; i < inDeviceCount; i++)
            {
                var caps = new MIDIINCAPS();
                NativeMethods.midiInGetDevCaps(i, ref caps, MIDIINCAPS.Size);
                if (caps.szPname == Name)
                {
                    inDeviceId = i;
                    break;
                }
            }
            if (inDeviceId == null)
                return false;

            int outDeviceCount = NativeMethods.midiOutGetNumDevs();
            uint? outDeviceId = null;
            for (uint i = 0; i < outDeviceCount; i++)
            {
                var caps = new MIDIOUTCAPS();
                NativeMethods.midiOutGetDevCaps(i, ref caps, MIDIOUTCAPS.Size);
                if (caps.szPname == Name)
                {
                    outDeviceId = i;
                    break;
                }
            }
            if (outDeviceId == null)
                return false;

            _inputClosed.Reset();
            _outputClosed.Reset();
            _inputCallback = InputEvent;
            _outputCallback = OutputEvent;
            if (NativeMethods.midiInOpen(out var inDeviceHandle, inDeviceId.Value, _inputCallback, 0, 0x00030000) != 0)
                return false;
            if (NativeMethods.midiOutOpen(out var outDeviceHandle, outDeviceId.Value, _outputCallback, 0, 0x00030000) != 0)
                return false;

            if (NativeMethods.midiInStart(inDeviceHandle) != 0)
                return false;

            _outBuffer = new MidiBuffer(outDeviceHandle, (uint)_flashMsg.Length);

            _inDeviceId = inDeviceId.Value;
            _outDeviceId = outDeviceId.Value;
            _inDeviceHandle = inDeviceHandle;
            _outDeviceHandle = outDeviceHandle;
            _ledsInvalidated = true;
            IsConnected = true;
            return true;
        }
        private void Disconnect()
        {
            if (IsConnected)
            {
                NativeMethods.midiInStop(_inDeviceHandle);

                //Clear launchpad LEDs
                var clearMsg = new byte[8 + 1];
                WriteHeader(clearMsg, 0x0E);
                clearMsg[7] = 0; //No color
                Send(clearMsg, clearMsg.Length - 1);

                IsConnected = false;
            }
            if (_outBuffer != null)
            {
                _outBuffer.Dispose();
                _outBuffer = null;
            }
            if (_inDeviceHandle != IntPtr.Zero)
            {
                NativeMethods.midiInClose(_inDeviceHandle);
                _inDeviceHandle = IntPtr.Zero;
                _inputClosed.Wait();
            }
            if (_outDeviceHandle != IntPtr.Zero)
            {
                NativeMethods.midiOutClose(_outDeviceHandle);
                _outDeviceHandle = IntPtr.Zero;
                _outputClosed.Wait();
            }
            _inputCallback = null;
            _outputCallback = null;
            _inDeviceId = 0;
            _outDeviceId = 0;
        }

        public void Clear()
        {
            for (int i = 0; i < _leds.Length; i++)
            {
                if (_leds[i].Mode != LEDMode.Off)
                    _ledsInvalidated = true;
                _leds[i].Mode = LEDMode.Off;
                _leds[i].Color = 0;
                _leds[i].FlashColor = 0;
            }
        }
        public void Set(LED[] leds)
        {
            if (leds.Length != 80)
                throw new InvalidOperationException("Array must be 80 elements");
            _leds = leds;
            _ledsInvalidated = true;
        }
        public void Set(int x, int y, byte color, bool allowSystem = false)
        {
            if (!IsValidLED(x, y, allowSystem))
                return;
            int index = GetLEDIndex(x, y);
            _leds[index].Mode = LEDMode.Normal;
            _leds[index].Color = color;
            _leds[index].FlashColor = 0;
            _ledsInvalidated = true;
        }
        public void SetOff(int x, int y, bool allowSystem = false)
        {
            if (!IsValidLED(x, y, allowSystem))
                return;
            int index = GetLEDIndex(x, y);
            _leds[index].Mode = LEDMode.Off;
            _leds[index].Color = 0;
            _leds[index].FlashColor = 0;
            _ledsInvalidated = true;
        }
        public void SetPulse(int x, int y, byte color, bool allowSystem = false)
        {
            if (!IsValidLED(x, y, allowSystem))
                return;
            int index = GetLEDIndex(x, y);
            _leds[index].Mode = LEDMode.Pulse;
            _leds[index].Color = color;
            _leds[index].FlashColor = 0;
            _ledsInvalidated = true;
        }
        public void SetFlash(int x, int y, byte fromColor, byte toColor, bool allowSystem = false)
        {
            if (!IsValidLED(x, y, allowSystem))
                return;
            int index = GetLEDIndex(x, y);
            _leds[index].Mode = LEDMode.Flash;
            _leds[index].Color = fromColor;
            _leds[index].FlashColor = toColor;
            _ledsInvalidated = true;
        }
        public void Render()
        {
            if (!_ledsInvalidated)
                return;

            int normalPos = 7;
            int pulsePos = 7;
            int flashPos = 7;
            for (byte y = 1, i = 0; y <= 9; y++)
            {
                for (byte x = 1; x <= 9; x++)
                {
                    if (x == 9 && y == 9)
                        break;
                    byte id = GetButtonId(x, y);
                    var led = _leds[i++];
                    switch (led.Mode)
                    {
                        case LEDMode.Off:
                            _normalMsg[normalPos++] = id;
                            _normalMsg[normalPos++] = 0;
                            break;
                        case LEDMode.Normal:
                            _normalMsg[normalPos++] = id;
                            _normalMsg[normalPos++] = led.Color;
                            break;
                        case LEDMode.Pulse:
                            _pulseMsg[pulsePos++] = id;
                            _pulseMsg[pulsePos++] = led.Color;
                            break;
                        case LEDMode.Flash:
                            _normalMsg[normalPos++] = id;
                            _normalMsg[normalPos++] = led.Color;
                            _flashMsg[flashPos++] = 0;
                            _flashMsg[flashPos++] = id;
                            _flashMsg[flashPos++] = led.FlashColor;
                            break;
                    }
                }
            }
            Send(_normalMsg, normalPos);
            Send(_pulseMsg, pulsePos);
            Send(_flashMsg, flashPos);

            _ledsInvalidated = false;
        }

        private void InputEvent(IntPtr hMidiIn, uint wMsg, uint dwInstance, uint dwParam1, uint dwParam2)
        {
            switch (wMsg)
            {
                case 0x3C1: //MM_MIM_OPEN (Connected)
                    break;
                case 0x3C2: //MM_MIM_CLOSE (Disconnected)
                    _inputClosed.Set();
                    break;
                case 0x3C3: //MM_MIM_DATA
                    byte status = (byte)(dwParam1 & 0xFF);
                    byte button = (byte)((dwParam1 >> 8) & 0xFF);
                    byte velocity = (byte)((dwParam1 >> 16) & 0xFF);
                    if (velocity != 0)
                        _queuedEvents.Enqueue(new LaunchpadEvent(EventType.ButtonDown, button));
                    else
                        _queuedEvents.Enqueue(new LaunchpadEvent(EventType.ButtonUp, button));
                    break;
                default:
                    break;
            }
        }
        private void OutputEvent(IntPtr hMidiIn, uint wMsg, uint dwInstance, uint dwParam1, uint dwParam2)
        {
            switch (wMsg)
            {
                case 0x3C7: //MM_MOM_OPEN (Connected)
                    break;
                case 0x3C8: //MM_MOM_CLOSE (Disconnected)
                    _outputClosed.Set();
                    break;
                default:
                    break;
            }
        }

        private void Send(byte[] buffer, int count)
        {
            if (!IsConnected)
                return;
            if (count != 7) //Blank msg
            {
                buffer[count++] = 0xF7;
                if (_outBuffer.Prepare(buffer, buffer.Length))
                {
                    try
                    {
                        NativeMethods.midiOutLongMsg(_outDeviceHandle, _outBuffer.Ptr, MIDIHDR.Size);
                    }
                    finally { _outBuffer.Unprepare(); }
                }
            }
        }
        private static byte GetButtonId(int x, int y)
        {
            byte id = (byte)((y * 10U) + x);
            if (y >= 9)
                id += 13;
            return id;
        }
        private static byte GetLEDIndex(int x, int y)
            => (byte)((y - 1) * 9 + (x - 1));
        private static void WriteHeader(byte[] data, byte mode)
        {
            data[0] = 0xF0;
            data[1] = 0x00;
            data[2] = 0x20;
            data[3] = 0x29;
            data[4] = 0x02;
            data[5] = 0x18;
            data[6] = mode;
        }
        private static bool IsValidLED(int x, int y, bool allowSystem)
        {
            if (allowSystem)
            {
                if (x < 1 || y < 1 || x > 9 || y > 9 || (x == 9 && y == 9))
                    return false;
            }
            else
            {
                if (x < 1 || y < 1 || x > 8 || y > 8)
                    return false;
            }
            return true;
        }
    }
}
