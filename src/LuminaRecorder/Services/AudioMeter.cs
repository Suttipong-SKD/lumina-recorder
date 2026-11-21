using System;
using System.Runtime.InteropServices;

namespace LuminaRecorder.Services
{
    /// <summary>
    /// Reads the live peak level of a speaker (render) or microphone (capture) endpoint via Windows Core Audio,
    /// so the UI can show level meters before recording starts.
    /// </summary>
    public sealed class AudioMeter : IDisposable
    {
        IAudioMeterInformation _meter;
        IAudioClient _client; // capture endpoints only report levels while a stream is open
        IMMDevice _device;

        public static AudioMeter TryCreate(bool capture, string deviceId)
        {
            try
            {
                var m = new AudioMeter();
                m.Open(capture, deviceId);
                return m;
            }
            catch
            {
                return null;
            }
        }

        void Open(bool capture, string deviceId)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDevice device = null;
            if (!string.IsNullOrEmpty(deviceId))
            {
                try { Marshal.ThrowExceptionForHR(enumerator.GetDevice(deviceId, out device)); }
                catch { device = null; }
            }
            if (device == null)
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(capture ? EDataFlow.Capture : EDataFlow.Render, ERole.Multimedia, out device));
            _device = device;

            var meterId = typeof(IAudioMeterInformation).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref meterId, CLSCTX_ALL, IntPtr.Zero, out object meterObj));
            _meter = (IAudioMeterInformation)meterObj;

            if (capture)
            {
                try
                {
                    var clientId = typeof(IAudioClient).GUID;
                    Marshal.ThrowExceptionForHR(device.Activate(ref clientId, CLSCTX_ALL, IntPtr.Zero, out object clientObj));
                    _client = (IAudioClient)clientObj;
                    Marshal.ThrowExceptionForHR(_client.GetMixFormat(out IntPtr fmt));
                    try
                    {
                        Marshal.ThrowExceptionForHR(_client.Initialize(0 /*shared*/, 0, 10_000_000, 0, fmt, IntPtr.Zero));
                    }
                    finally { Marshal.FreeCoTaskMem(fmt); }
                    Marshal.ThrowExceptionForHR(_client.Start());
                }
                catch
                {
                    _client = null; // meter still works if some other app is using the mic
                }
            }
        }

        /// <summary>Peak level 0–1.</summary>
        public float Peak
        {
            get
            {
                try { return _meter != null && _meter.GetPeakValue(out float v) == 0 ? v : 0f; }
                catch { return 0f; }
            }
        }

        public void Dispose()
        {
            try { _client?.Stop(); } catch { }
            if (_client != null) Marshal.ReleaseComObject(_client);
            if (_meter != null) Marshal.ReleaseComObject(_meter);
            if (_device != null) Marshal.ReleaseComObject(_device);
            _client = null; _meter = null; _device = null;
        }

        const int CLSCTX_ALL = 0x17;

        enum EDataFlow { Render = 0, Capture = 1, All = 2 }
        enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
            [PreserveSig] int GetMeteringChannelCount(out int count);
            [PreserveSig] int GetChannelsPeakValues(int count, IntPtr peaks);
            [PreserveSig] int QueryHardwareSupport(out int mask);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }
    }
}
