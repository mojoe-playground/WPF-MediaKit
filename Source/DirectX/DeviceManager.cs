using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WPFMediaKit.DirectX
{
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Threading;

    public class DeviceManager:IDisposable
    {

        /// <summary>
        /// The SDK version of D3D we are using
        /// </summary>
        private const ushort D3D_SDK_VERSION = 32;

        private readonly static IntPtr _windowHandle;

        public readonly object StaticLock = new object();

        public IDirect3DDevice9Ex Device { get; private set; }

        public IDirect3D9Ex D3d { get; private set; }

        [DllImport("user32.dll", SetLastError = false)]
        private static extern IntPtr GetDesktopWindow();

        public int DeviceVersion { get; private set; }

        static DeviceManager()
        {
            _windowHandle = GetDesktopWindow();
        }

        public void Initialize()
        {
            if (D3d != null)
                return;

            Direct3D.Direct3DCreate9Ex(D3D_SDK_VERSION, out IDirect3D9Ex d3D);
            D3d = d3D;
        }

        public bool TestRestoreLostDevice(int lastVersion)
        {
            if (Device == null)
                return lastVersion != DeviceVersion;

            /* This will throw an exception
             * if the device is lost */
            int hr = Device.CheckDeviceState(_windowHandle);

            switch (hr)
            {
                case 0 /*S_OK*/:
                case 2168 /*S_PRESENT_OCCLUDED*/:
                case 2167 /*S_PRESENT_MODE_CHANGED*/:
                    // state is DeviceOK
                    hr = 0;
                    break;

                case 2152 /*D3DERR_DEVICELOST*/:
                case 2164 /*D3DERR_DEVICEHUNG*/:
                    // Lost/hung device. Destroy the device and create a new one.
                    CreateDevice(true);
                    break;

                case 2160 /*D3DERR_DEVICEREMOVED*/:
                    // This is a fatal error.
                    CreateDevice(true);
                    break;

                case unchecked((int)0x80070057L) /*E_INVALIDARG*/:
                    // CheckDeviceState can return E_INVALIDARG if the window is not valid
                    // We'll assume that the window was destroyed; we'll recreate the device
                    // if the application sets a new window.
                    hr = 0;
                    break;
            }

            /* Do nothing if S_OK */
            if (hr == 0)
                return lastVersion != DeviceVersion;

            return true;
        }

        public void CreateDevice()
        {
            CreateDevice(false);
        }

        /// <summary>
        /// Creates a Direct3D device
        /// </summary>
        [MethodImpl(MethodImplOptions.Synchronized)]
        private void CreateDevice(bool force)
        {
            if (!force && Device != null)
                return;

            if (Device != null)
                Marshal.FinalReleaseComObject(Device);

            var param = new D3DPRESENT_PARAMETERS
            {
                Windowed = 1,
                Flags = ((short)D3DPRESENTFLAG.D3DPRESENTFLAG_VIDEO),
                BackBufferFormat = D3DFORMAT.D3DFMT_X8R8G8B8,
                SwapEffect = D3DSWAPEFFECT.D3DSWAPEFFECT_COPY
            };

            /* The COM pointer to our D3D Device */
            IntPtr dev;

            D3d.CreateDeviceEx(0, D3DDEVTYPE.D3DDEVTYPE_HAL, _windowHandle,
                CreateFlags.D3DCREATE_SOFTWARE_VERTEXPROCESSING | CreateFlags.D3DCREATE_MULTITHREADED | CreateFlags.D3DCREATE_FPU_PRESERVE,
                ref param, IntPtr.Zero, out dev);

            DeviceVersion++;
            Device = (IDirect3DDevice9Ex)Marshal.GetObjectForIUnknown(dev);
            Marshal.Release(dev);
        }

        public void Dispose()
        {
            if (D3d != null)
            {
                Marshal.FinalReleaseComObject(D3d);
                D3d = null;
            }

            if (Device != null)
            {
                Marshal.FinalReleaseComObject(Device);
                Device = null;
            }
        }
    }
}
