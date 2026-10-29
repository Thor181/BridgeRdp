using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BridgeRdp.Agent;

internal static class SessionKeepAlive
{
    private const uint IdleIntervalMs = 3 * 60 * 1000;

    public static async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        uint lastMove = unchecked((uint)Environment.TickCount);
        int direction = 1;
        bool failureReported = false;
        while (await timer.WaitForNextTickAsync(ct))
        {
            var lastInput = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref lastInput))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read session idle time.");

            uint now = unchecked((uint)Environment.TickCount);
            // Unsigned subtraction also handles the 32-bit tick counter wrapping.
            if (unchecked(now - lastInput.Time) < IdleIntervalMs ||
                unchecked(now - lastMove) < IdleIntervalMs)
                continue;

            ct.ThrowIfCancellationRequested();
            Input[] inputs = [new() { Mouse = new MouseInput { Dx = direction, Flags = 0x0001 } }];
            lastMove = now;
            if (SendInput(1, inputs, Marshal.SizeOf<Input>()) == 1)
            {
                direction = -direction;
                failureReported = false;
            }
            else if (!failureReported)
            {
                Console.Error.WriteLine("Session keep-alive: Windows blocked mouse input; retrying in 3 minutes.");
                failureReported = true;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    // MOUSEINPUT is the largest INPUT union member on both x86 and x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
