using System.Runtime.InteropServices;

namespace BridgeRdp.Client.Interop;

/// <summary>
/// Обёртка над IWTSVirtualChannel* с прямыми вызовами через vtable — как это делают нативные плагины.
/// Потокобезопасна: Write можно вызывать из любого потока.
/// </summary>
internal sealed unsafe class NativeVirtualChannel : IDisposable
{
    // Порядок методов в vtable: IUnknown (0..2), затем Write (3), Close (4).
    private const int WriteSlot = 3;
    private const int CloseSlot = 4;

    private readonly object _sync = new();
    private IntPtr _ptr;

    public NativeVirtualChannel(IntPtr channel)
    {
        Marshal.AddRef(channel);
        _ptr = channel;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_ptr == IntPtr.Zero, this);

            var write = (delegate* unmanaged<IntPtr, uint, byte*, IntPtr, int>)GetMethod(WriteSlot);
            int hr;
            fixed (byte* buffer = data)
            {
                hr = write(_ptr, (uint)data.Length, buffer, IntPtr.Zero);
            }

            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            if (_ptr == IntPtr.Zero)
            {
                return;
            }

            var close = (delegate* unmanaged<IntPtr, int>)GetMethod(CloseSlot);
            Marshal.ThrowExceptionForHR(close(_ptr));
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_ptr != IntPtr.Zero)
            {
                Marshal.Release(_ptr);
                _ptr = IntPtr.Zero;
            }
        }
    }

    private IntPtr GetMethod(int slot) => (*(IntPtr**)_ptr)[slot];
}
