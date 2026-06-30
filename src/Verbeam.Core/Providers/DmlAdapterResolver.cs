using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Verbeam.Core.Providers;

/// <summary>
/// Resolves the DirectML <c>device_id</c> of the INTEGRATED GPU (e.g. Intel Iris Xe) so OCR can be
/// offloaded off the discrete NVIDIA/AMD card — freeing its VRAM for the translation model and, for
/// the small transfer-bound detection workload, often running faster (unified memory, no PCIe copy,
/// no contention).
/// <para>
/// ONNX Runtime's DirectML <c>device_id</c> indexes adapters in <b>high-performance preference</b>
/// order (discrete first), NOT raw <c>IDXGIFactory1::EnumAdapters1</c> order. (Measured: raw
/// EnumAdapters1 lists the iGPU at #0, but DML <c>device_id=1</c> is the iGPU.) So this enumerates via
/// <c>IDXGIFactory6::EnumAdapterByGpuPreference(HIGH_PERFORMANCE)</c> and de-dupes by adapter LUID to
/// match DML's numbering, then returns the index of the low-VRAM (integrated) adapter. Returns null
/// for a single-GPU box or on any failure so callers fall back to the configured device id.
/// Windows-only (DXGI); harmless elsewhere.
/// </para>
/// </summary>
internal static class DmlAdapterResolver
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll", PreserveSig = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapterByGpuPreferenceFn(
        IntPtr self, uint adapter, uint gpuPreference, ref Guid riid, out IntPtr ppvAdapter);

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IDXGIObject
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        // IDXGIAdapter
        [PreserveSig] int EnumOutputs(uint output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(IntPtr desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
        // IDXGIAdapter1
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIFactory6 = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    private static readonly Guid IID_IDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");

    private const uint DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE = 2;
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    private const uint VendorNvidia = 0x10DE;

    // IDXGIFactory6::EnumAdapterByGpuPreference is vtable slot 29 (IUnknown 3 + IDXGIObject 4 +
    // Factory 5 + Factory1 2 + Factory2 11 + Factory3 1 + Factory4 2 + Factory5 1 = 29).
    private const int VtblSlot_EnumAdapterByGpuPreference = 29;

    public readonly record struct AdapterInfo(int Index, string Description, uint VendorId, long DedicatedVideoMemoryBytes);

    /// <summary>Enumerates hardware adapters in DirectML <c>device_id</c> order (high-performance, LUID-deduped).</summary>
    public static IReadOnlyList<AdapterInfo> Enumerate()
    {
        var list = new List<AdapterInfo>();
        if (!OperatingSystem.IsWindows())
        {
            return list;
        }

        var factoryIid = IID_IDXGIFactory1;
        if (CreateDXGIFactory1(ref factoryIid, out var factory) != 0 || factory == IntPtr.Zero)
        {
            return list;
        }

        var factory6 = IntPtr.Zero;
        try
        {
            var f6Iid = IID_IDXGIFactory6;
            if (Marshal.QueryInterface(factory, ref f6Iid, out factory6) != 0 || factory6 == IntPtr.Zero)
            {
                return list; // pre-1803 Windows without GPU-preference enumeration
            }

            var vtbl = Marshal.ReadIntPtr(factory6);
            var fnPtr = Marshal.ReadIntPtr(vtbl, VtblSlot_EnumAdapterByGpuPreference * IntPtr.Size);
            var enumByPref = Marshal.GetDelegateForFunctionPointer<EnumAdapterByGpuPreferenceFn>(fnPtr);

            var seenLuids = new HashSet<long>();
            var deviceId = 0;
            for (uint i = 0; ; i++)
            {
                var adapterIid = IID_IDXGIAdapter1;
                if (enumByPref(factory6, i, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, ref adapterIid, out var adapterPtr) != 0 ||
                    adapterPtr == IntPtr.Zero)
                {
                    break;
                }

                try
                {
                    var adapter = (IDXGIAdapter1)Marshal.GetObjectForIUnknown(adapterPtr);
                    try
                    {
                        if (adapter.GetDesc1(out var desc) == 0 &&
                            (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0 &&
                            seenLuids.Add(desc.AdapterLuid))
                        {
                            // DirectML counts each physical adapter once; deviceId tracks that deduped index.
                            list.Add(new AdapterInfo(
                                deviceId++,
                                desc.Description ?? string.Empty,
                                desc.VendorId,
                                (long)(ulong)desc.DedicatedVideoMemory));
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
                finally
                {
                    Marshal.Release(adapterPtr);
                }
            }
        }
        catch
        {
            // DXGI interop failure -> caller falls back to the configured device id.
        }
        finally
        {
            if (factory6 != IntPtr.Zero)
            {
                Marshal.Release(factory6);
            }

            Marshal.Release(factory);
        }

        return list;
    }

    /// <summary>
    /// Returns the DirectML device id of the integrated GPU (smallest dedicated VRAM among the
    /// hardware adapters) when a distinct iGPU exists alongside a discrete card; otherwise null.
    /// </summary>
    public static int? FindIntegratedDeviceId()
    {
        var adapters = Enumerate();
        if (adapters.Count < 2)
        {
            return null; // single GPU -> nothing to offload to
        }

        AdapterInfo? best = null;
        foreach (var a in adapters)
        {
            if (best is null || a.DedicatedVideoMemoryBytes < best.Value.DedicatedVideoMemoryBytes)
            {
                best = a;
            }
        }

        // Only treat it as an iGPU if it is clearly the low-VRAM, non-NVIDIA-discrete one.
        if (best is { } pick &&
            pick.VendorId != VendorNvidia &&
            pick.DedicatedVideoMemoryBytes < 2L * 1024 * 1024 * 1024)
        {
            return pick.Index;
        }

        return null;
    }
}
