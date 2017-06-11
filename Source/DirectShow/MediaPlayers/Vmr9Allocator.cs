using System;
using System.Runtime.InteropServices;
using System.Threading;
using DirectShowLib;
using WPFMediaKit.DirectX;

namespace WPFMediaKit.DirectShow.MediaPlayers
{
    /// <summary>
    /// The Vmr9Allocator is a custom allocator for the VideoMixingRenderer9
    /// </summary>
    [ComVisible(true)]
    public class Vmr9Allocator : IVMRSurfaceAllocator9, IVMRImagePresenter9, ICustomAllocator
    {
        /// <summary>
        /// Base constant for FAIL error codes
        /// </summary>
        private const int E_FAIL = unchecked((int)0x80004005);

        /// <summary>
        /// Lock for shared resources
        /// </summary>
        private static object m_staticLock = new object();

        /// <summary>
        /// Lock for instance's resources
        /// </summary>
        private readonly object m_instanceLock = new object();

        /// <summary>
        /// Part of the "Dispose" pattern
        /// </summary>
        private bool m_disposed;

        /// <summary>
        /// Applications use this interface to set a custom allocator-presenter 
        /// and the allocator-presenter uses this interface to inform the VMR of 
        /// changes to the system environment that affect the Direct3D surfaces.
        /// </summary>
        private IVMRSurfaceAllocatorNotify9 m_allocatorNotify;

        /// <summary>
        /// Fires each time a frame needs to be presented
        /// </summary>
        public event Action NewAllocatorFrame;

        /// <summary>
        /// Fires when new D3D surfaces are allocated
        /// </summary>
        public event NewAllocatorSurfaceDelegate NewAllocatorSurface;

        /// <summary>
        /// Private surface for YUV stuffs
        /// </summary>
        private IDirect3DSurface9 m_privateSurface;

        /// <summary>
        /// Private texture for YUV stuffs
        /// </summary>
        private IDirect3DTexture9 m_privateTexture;

        private readonly DeviceManager _deviceManager;
        private int _deviceVersion;

        /// <summary>
        /// Creates a new VMR9 custom allocator to use with Direct3D
        /// </summary>
        public Vmr9Allocator(DeviceManager manager)
        {
            _deviceManager = manager;
            _deviceManager.Initialize();
            _deviceManager.CreateDevice();
            _deviceVersion = _deviceManager.DeviceVersion;
        }

        /// <summary>
        /// Fires the OnNewAllocatorSurface event, notifying the
        /// subscriber that new surfaces are available
        /// </summary>
        private void InvokeNewSurfaceEvent(IntPtr pSurface, IntPtr pTexture)
        {
            if (NewAllocatorSurface != null)
                NewAllocatorSurface(this, pSurface, pTexture);
        }

        /// <summary>
        /// Fires the NewAllocatorFrame event notifying the
        /// subscriber that a new frame is ready to be presented
        /// </summary>
        private void InvokeNewAllocatorFrame()
        {
            Action newAllocatorFrameHandler = NewAllocatorFrame;
            if (newAllocatorFrameHandler != null) newAllocatorFrameHandler();
        }

        /// <summary>
        /// Frees any remaining unmanaged memory
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            //GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Part of the dispose pattern
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            //if (m_disposed) return;

            if (disposing)
            {
                InvokeNewSurfaceEvent(IntPtr.Zero, IntPtr.Zero);
                /* Pass a dummy cookie to TerminateDevice */
                TerminateDevice(IntPtr.Zero);
               
                if (m_allocatorNotify != null)
                {
                    Marshal.FinalReleaseComObject(m_allocatorNotify);
                    m_allocatorNotify = null;
                }
            }

            m_disposed = true;
        }

        /// <summary>
        /// Current Direct3D surfaces our allocator has ready and allocated
        /// </summary>
        private IntPtr[] DxSurfaces { get; set; }

        /// <summary>
        /// The StartPresenting method is called just before the video starts playing. 
        /// The allocator-presenter should perform any necessary configuration in this method.
        /// </summary>
        /// <param name="userId">
        /// An application-defined DWORD_PTR cookie that uniquely identifies this instance of the 
        /// VMR for use in scenarios when one instance of the allocator-presenter is used with multiple VMR instances.
        /// </param>
        /// <returns>Returns an HRESULT</returns>
        public int StartPresenting(IntPtr userId)
        {
            return _deviceManager.Device == null ? E_FAIL : 0;
        }

        /// <summary>
        /// The StopPresenting method is called just after the video stops playing. 
        /// The allocator-presenter should perform any necessary cleanup in this method.
        /// </summary>
        /// <param name="dwUserID">
        /// An application-defined DWORD_PTR cookie that uniquely identifies this instance of the 
        /// VMR for use in scenarios when one instance of the allocator-presenter is used with multiple VMR instances.
        /// </param>
        /// <returns></returns>
        public int StopPresenting(IntPtr dwUserID)
        {
            return 0;
        }

        /// <summary>
        /// The PresentImage method is called at precisely the moment this video frame should be presented.
        /// </summary>
        /// <param name="dwUserID">
        /// An application-defined DWORD_PTR that uniquely identifies this instance of the VMR in scenarios when 
        /// multiple instances of the VMR are being used with a single instance of an allocator-presenter.
        /// </param>
        /// <param name="lpPresInfo">
        /// Specifies a VMR9PresentationInfo structure that contains information about the video frame.
        /// </param>
        /// <returns>Returns an HRESULT</returns>
        public int PresentImage(IntPtr dwUserID, ref VMR9PresentationInfo lpPresInfo)
        {
            VMR9PresentationInfo presInfo = lpPresInfo;

            int hr = 0;

            try
            {
                lock(m_staticLock)
                {
                    /* Test to see if our device was lost, is so fix it */
                    TestRestoreLostDevice();

                    if (m_privateSurface != null)
                        hr = _deviceManager.Device.StretchRect(presInfo.lpSurf,
                                                               presInfo.rcSrc,
                                                               m_privateSurface,
                                                               presInfo.rcDst,
                                                               0);
                    if (hr < 0)
                        return hr;
                }

                /* Notify to our listeners we just got a new frame */
                InvokeNewAllocatorFrame();

                hr = 0;
            }
            catch (Exception)
            {
                hr = E_FAIL;
            }

            return hr;
        }

        /// <summary>
        /// Tests if the D3D device has been lost and if it has
        /// it is retored. This happens on XP with things like
        /// resolution changes or pressing ctrl + alt + del.  With
        /// Vista, this will most likely never be called unless the
        /// video driver hangs or is changed.
        /// </summary>
        private void TestRestoreLostDevice()
        {
            if (_deviceManager.TestRestoreLostDevice(_deviceVersion))
            {
                FreeSurfaces();
                _deviceVersion = _deviceManager.DeviceVersion;

                /* TODO: This is bad. FIX IT! 
                 * Figure out how to tell when the new
                 * device is ready to use */
                Thread.Sleep(1500);

                IntPtr pDev = GetComPointer(_deviceManager.Device);

                /* Update with our new device */
                m_allocatorNotify.ChangeD3DDevice(pDev, GetAdapterMonitor(0));
            }
        }

        /// <summary>
        /// Gets the pointer to the adapter monitor
        /// </summary>
        /// <param name="adapterOrdinal">The ordinal of the adapter</param>
        /// <returns>A pointer to the adaptor monitor</returns>
        private IntPtr GetAdapterMonitor(uint adapterOrdinal)
        {
            IntPtr pMonitor =_deviceManager.D3d.GetAdapterMonitor(adapterOrdinal);

            return pMonitor;
        }

        /// <summary>
        /// The InitializeDevice method is called by the Video Mixing Renderer 9 (VMR-9) 
        /// when it needs the allocator-presenter to allocate surfaces.
        /// </summary>
        /// <param name="userId">
        /// Application-defined identifier. This value is the same value that the application 
        /// passed to the IVMRSurfaceAllocatorNotify9.AdviseSurfaceAllocator method in the 
        /// dwUserID parameter.
        /// </param>
        /// <param name="lpAllocInfo">
        /// Pointer to a VMR9AllocationInfo structure that contains a description of the surfaces to create.
        /// </param>
        /// <param name="lpNumBuffers">
        /// On input, specifies the number of surfaces to create. When the method returns, 
        /// this parameter contains the number of buffers that were actually allocated.
        /// </param>
        /// <returns>Returns an HRESULT code</returns>
        public int InitializeDevice(IntPtr userId, ref VMR9AllocationInfo lpAllocInfo, ref int lpNumBuffers)
        {
            if (m_allocatorNotify == null)
            {
                return E_FAIL;
            }

            try
            {
                int hr;

                lock (m_staticLock)
                {
                    /* These two pointers are passed to the the helper
                     * to create our D3D surfaces */
                    var pDevice = GetComPointer(_deviceManager.Device);
                    var pMonitor = GetAdapterMonitor(0);

                    /* Setup our D3D Device with our renderer */
                    hr = m_allocatorNotify.SetD3DDevice(pDevice, pMonitor);
                    DsError.ThrowExceptionForHR(hr);

                    /* This is only used if the AllocateSurfaceHelper is used */
                    lpAllocInfo.dwFlags |= VMR9SurfaceAllocationFlags.TextureSurface;

                    /* Make sure our old surfaces are free'd */
                    FreeSurfaces();

                    /* This is an IntPtr array of pointers to D3D surfaces */
                    DxSurfaces = new IntPtr[lpNumBuffers];

                    /* This is where the magic happens, surfaces are allocated */
                    hr = m_allocatorNotify.AllocateSurfaceHelper(ref lpAllocInfo, ref lpNumBuffers, DxSurfaces);

                    if (hr < 0)
                    {
                        FreeSurfaces();

                        
                        if (lpAllocInfo.Format > 0)
                        {
                            hr = _deviceManager.Device.CreateTexture(lpAllocInfo.dwWidth, 
                                                                     lpAllocInfo.dwHeight, 
                                                                     1, 
                                                                     1,
                                                                     D3DFORMAT.D3DFMT_X8R8G8B8, 
                                                                     0, 
                                                                     out m_privateTexture, 
                                                                     IntPtr.Zero);

                            DsError.ThrowExceptionForHR(hr);

                            hr = m_privateTexture.GetSurfaceLevel(0, out m_privateSurface);
                            DsError.ThrowExceptionForHR(hr);
                        }

                        lpAllocInfo.dwFlags &= ~VMR9SurfaceAllocationFlags.TextureSurface;
                        lpAllocInfo.dwFlags |= VMR9SurfaceAllocationFlags.OffscreenSurface;

                        DxSurfaces = new IntPtr[lpNumBuffers];

                        hr = m_allocatorNotify.AllocateSurfaceHelper(ref lpAllocInfo, 
                                                                     ref lpNumBuffers,
                                                                     DxSurfaces);
                        if (hr < 0)
                        {
                            FreeSurfaces();
                            return hr;
                        }
                    }
                }

                /* Nofity to our listeners we have new surfaces */
                InvokeNewSurfaceEvent(m_privateSurface != null ? GetComPointer(m_privateSurface) : DxSurfaces[0], m_privateSurface != null ? GetComPointer(m_privateTexture) : IntPtr.Zero);

                return hr;
            }
            catch
            {
                return E_FAIL;
            }
        }

        /// <summary>
        /// The TerminateDevice method releases the Direct3D device.
        /// </summary>
        /// <param name="id">
        /// Application-defined identifier. This value is the same value that the application 
        /// passed to the IVMRSurfaceAllocatorNotify9.AdviseSurfaceAllocator method 
        /// in the dwUserID parameter.
        /// </param>
        /// <returns></returns>
        public int TerminateDevice(IntPtr id)
        {
            FreeSurfaces();
            return 0;
        }

        /// <summary>
        /// The GetSurface method retrieves a Direct3D surface
        /// </summary>
        /// <param name="userId">
        /// Application-defined identifier. This value is 
        /// the same value that the application passed to the 
        /// IVMRSurfaceAllocatorNotify9.AdviseSurfaceAllocator 
        /// method in the dwUserID parameter.
        /// </param>
        /// <param name="surfaceIndex">
        /// Specifies the index of the surface to retrieve. 
        /// </param>
        /// <param name="surfaceFlags"></param>
        /// <param name="lplpSurface">
        /// Address of a variable that receives an IDirect3DSurface9 
        /// interface pointer. The caller must release the interface.</param>
        /// <returns></returns>
        public int GetSurface(IntPtr userId, int surfaceIndex, int surfaceFlags, out IntPtr lplpSurface)
        {
            lplpSurface = IntPtr.Zero;

            if (DxSurfaces == null)
                return E_FAIL;

            if (surfaceIndex > DxSurfaces.Length)
                return E_FAIL;

            lock (m_instanceLock)
            {
                if (DxSurfaces == null)
                    return E_FAIL;

                lplpSurface = DxSurfaces[surfaceIndex];

                /* Increment the reference count to our surface, 
                 * which is a pointer to a COM object */
                Marshal.AddRef(lplpSurface);
                return 0;
            }
        }

        /// <summary>
        /// The AdviseNotify method provides the allocator-presenter with the VMR-9 filter's 
        /// interface for notification callbacks. If you are using a custom allocator-presenter, 
        /// the application must call this method on the allocator-presenter, with a pointer to 
        /// the VMR's IVMRSurfaceAllocatorNotify9 interface. The allocator-presenter uses this 
        /// interface to communicate with the VMR. 
        /// </summary>
        /// <param name="lpIVMRSurfAllocNotify">
        /// Specifies the IVMRSurfaceAllocatorNotify9 interface that the allocator-presenter will 
        /// use to pass notifications back to the VMR.</param>
        /// <returns>Returns an HRESULT value</returns>
        public int AdviseNotify(IVMRSurfaceAllocatorNotify9 lpIVMRSurfAllocNotify)
        {
            lock (m_staticLock)
            {
                m_allocatorNotify = lpIVMRSurfAllocNotify;

                var pMonitor = GetAdapterMonitor(0);
                var pDevice = GetComPointer(_deviceManager.Device);
                return m_allocatorNotify.SetD3DDevice(pDevice, pMonitor);
            }
        }

        /// <summary>
        /// Gets a native pointer to a COM object.  This method does not
        /// add a reference count.
        /// </summary>
        /// <param name="comObj">The RCW to the COM object</param>
        /// <returns>Pointer to the COM object</returns>
        private static IntPtr GetComPointer(object comObj)
        {
            if (!Marshal.IsComObject(comObj))
                throw new ArgumentException("comObj");

            IntPtr pComObj = Marshal.GetIUnknownForObject(comObj);

            /* Get IUnknownForObject adds a reference count
             * to the COM object so we remove a reference count
             * before we return the pointer to avoid any possible
             * memory leaks with COM */
            Marshal.Release(pComObj);

            return pComObj;
        }

        ~Vmr9Allocator()
        {
            Dispose();
        }

        /// <summary>
        /// Releases reference to all allocated D3D surfaces
        /// </summary>
        private void FreeSurfaces()
        {
            lock (m_instanceLock)
            {
                if(m_privateSurface != null)
                {
                    Marshal.ReleaseComObject(m_privateSurface);
                    m_privateSurface = null;
                }

                if (m_privateTexture != null)
                {
                    Marshal.ReleaseComObject(m_privateTexture);
                    m_privateTexture = null;
                }

                if (DxSurfaces != null)
                {
                    foreach (var ptr in DxSurfaces)
                    {
                        if (ptr != IntPtr.Zero)
                        {
                            /* Release COM reference */
                            Marshal.Release(ptr);
                        }
                    }
                }

                /* Make sure we uninitialize the pointer array
                 * so the rest of our code knows it is invalid */
                DxSurfaces = null;
            }
        }
    }
}