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
            int hr = Device.TestCooperativeLevel();

            /* Do nothing if S_OK */
            if (hr == 0)
                return lastVersion != DeviceVersion;

            CreateDevice(true);
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
