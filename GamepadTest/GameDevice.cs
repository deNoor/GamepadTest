using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace GamepadTest;

[SupportedOSPlatform("windows")]
public sealed partial class GameDevice : IDisposable
{
    private const string _gameInputInterfaceIid = "20EFC1C7-5D9A-43BA-B26F-B807FA48609C";
    private const string _gameInputReadingInterfaceIid = "C81C4CDE-ED1A-4631-A30F-C556A6241A1F";
    private const uint _gameInputKindGamepad = 0x00040000;
    private const uint _gameInputEnableBackgroundInput = 0x00000040;

    private static readonly Lock _retainedCallbacksGate = new();
    private static readonly List<ReadingCallback> _retainedCallbacks = [];

    private readonly IGameInput _gameInput;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Lock _lifetimeGate = new();
    private bool _isTracking;
    private bool _disposed;

    public GameDevice()
    {
        var iid = new Guid(_gameInputInterfaceIid);
        var result = GameInputInitialize(in iid, out var initializedGameInput);
        Marshal.ThrowExceptionForHR(result);

        _gameInput = initializedGameInput
            ?? throw new InvalidOperationException(
                "GameInput initialization succeeded without returning an interface.");

        _gameInput.SetFocusPolicy(_gameInputEnableBackgroundInput);
    }

    public async Task TrackGamepadInputAsync(
        Action<GamepadState> onStateChanged,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onStateChanged);

        CancellationTokenSource linkedCancellation;
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isTracking)
            {
                throw new InvalidOperationException(
                    "This GameInput instance is already tracking gamepad input.");
            }

            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCancellation.Token);
            _isTracking = true;
        }

        using (linkedCancellation)
        {
            await TrackGamepadInputCoreAsync(onStateChanged, linkedCancellation.Token)
                .ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task TrackGamepadInputCoreAsync(
        Action<GamepadState> onStateChanged,
        CancellationToken cancellationToken)
    {
        var token = cancellationToken;

        var states = Channel.CreateBounded<GamepadState>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

        ReadingCallback callback = (_, _, readingPointer, _) =>
        {
            IGameInputReading? reading = null;
            try
            {
                if (readingPointer == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "GameInput invoked the reading callback without a reading.");
                }

                unsafe
                {
                    reading = ComInterfaceMarshaller<IGameInputReading>.ConvertToManaged(
                        (void*) readingPointer);
                }

                if (reading is null)
                {
                    throw new InvalidOperationException(
                        "GameInput returned a null managed reading for a non-null COM pointer.");
                }

                if (reading.GetGamepadState(out var state))
                {
                    states.Writer.TryWrite(state);
                }
            }
            catch (Exception exception)
            {
                states.Writer.TryComplete(exception);
            }
            finally
            {
                GC.KeepAlive(reading);
            }
        };

        ulong callbackToken = 0;
        var callbackRegistered = false;
        try
        {
            token.ThrowIfCancellationRequested();

            var result = _gameInput.RegisterReadingCallback(
                IntPtr.Zero,
                _gameInputKindGamepad,
                IntPtr.Zero,
                callback,
                out callbackToken);
            Marshal.ThrowExceptionForHR(result);
            callbackRegistered = true;

            GamepadState previousState = default;
            await foreach (var currentState in states.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (previousState == currentState)
                {
                    continue;
                }

                onStateChanged(currentState);
                previousState = currentState;
            }
        }
        finally
        {
            states.Writer.TryComplete();
            try
            {
                if (callbackRegistered && !_gameInput.UnregisterCallback(callbackToken))
                {
                    _gameInput.StopCallback(callbackToken);
                    lock (_retainedCallbacksGate)
                    {
                        _retainedCallbacks.Add(callback);
                    }

                    Console.Error.WriteLine(
                        "GameInput could not unregister the reading callback; it was stopped instead.");
                }
            }
            finally
            {
                GC.KeepAlive(callback);
                lock (_lifetimeGate)
                {
                    _isTracking = false;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _disposeCancellation.Cancel();
        _disposeCancellation.Dispose();
    }

    [LibraryImport("GameInputRedist.dll", EntryPoint = "GameInputInitialize")]
    private static partial int GameInputInitialize(in Guid iid, out IGameInput gameInput);

    [GeneratedComInterface]
    [Guid(_gameInputInterfaceIid)]
    internal partial interface IGameInput
    {
        [PreserveSig]
        ulong GetCurrentTimestamp();

        [PreserveSig]
        int GetCurrentReading(
            uint inputKind,
            IntPtr device,
            [MarshalAs(UnmanagedType.Interface)] out IGameInputReading? reading);

        [PreserveSig]
        int GetNextReading(
            IntPtr referenceReading,
            uint inputKind,
            IntPtr device,
            out IntPtr reading);

        [PreserveSig]
        int GetPreviousReading(
            IntPtr referenceReading,
            uint inputKind,
            IntPtr device,
            out IntPtr reading);

        [PreserveSig]
        int RegisterReadingCallback(
            IntPtr device,
            uint inputKind,
            IntPtr context,
            [MarshalAs(UnmanagedType.FunctionPtr)] ReadingCallback callback,
            out ulong callbackToken);

        [PreserveSig]
        int RegisterDeviceCallback(
            IntPtr device,
            uint inputKind,
            uint statusFilter,
            uint enumerationKind,
            IntPtr context,
            [MarshalAs(UnmanagedType.FunctionPtr)] DeviceCallback callback,
            out ulong callbackToken);

        [PreserveSig]
        int RegisterSystemButtonCallback(
            IntPtr device,
            uint buttonFilter,
            IntPtr context,
            IntPtr callback,
            out ulong callbackToken);

        [PreserveSig]
        int RegisterKeyboardLayoutCallback(
            IntPtr device,
            IntPtr context,
            IntPtr callback,
            out ulong callbackToken);

        [PreserveSig]
        void StopCallback(ulong callbackToken);

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool UnregisterCallback(ulong callbackToken);

        [PreserveSig]
        int CreateDispatcher(out IntPtr dispatcher);

        [PreserveSig]
        int FindDeviceFromId(IntPtr deviceId, out IntPtr device);

        [PreserveSig]
        int FindDeviceFromPlatformString(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceString,
            out IntPtr device);

        [PreserveSig]
        void SetFocusPolicy(uint policy);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void ReadingCallback(
        ulong callbackToken,
        IntPtr context,
        IntPtr reading,
        [MarshalAs(UnmanagedType.I1)] bool hasOverrunOccurred);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void DeviceCallback(
        ulong callbackToken,
        IntPtr context,
        IntPtr device,
        ulong timestamp,
        uint currentStatus,
        uint previousStatus);

    [GeneratedComInterface]
    [Guid(_gameInputReadingInterfaceIid)]
    internal partial interface IGameInputReading
    {
        [PreserveSig]
        uint GetInputKind();
        [PreserveSig]
        ulong GetTimestamp();
        [PreserveSig]
        void GetDevice(IntPtr device);
        [PreserveSig]
        uint GetControllerAxisCount();
        [PreserveSig]
        uint GetControllerAxisState(uint count, IntPtr state);
        [PreserveSig]
        uint GetControllerButtonCount();
        [PreserveSig]
        uint GetControllerButtonState(uint count, IntPtr state);
        [PreserveSig]
        uint GetControllerSwitchCount();
        [PreserveSig]
        uint GetControllerSwitchState(uint count, IntPtr state);
        [PreserveSig]
        uint GetKeyCount();
        [PreserveSig]
        uint GetKeyState(uint count, IntPtr state);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool GetMouseState(IntPtr state);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool GetSensorsState(IntPtr state);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool GetArcadeStickState(IntPtr state);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool GetFlightStickState(IntPtr state);

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool GetGamepadState(out GamepadState state);
    }

    [Flags]
    public enum GamepadButtons : uint
    {
        A = 0x00000004,
        B = 0x00000008,
        X = 0x00000010,
        Y = 0x00000020,
        DPadUp = 0x00000040,
        DPadDown = 0x00000080,
        DPadLeft = 0x00000100,
        DPadRight = 0x00000200,
        LeftShoulder = 0x00000400,
        RightShoulder = 0x00000800,
        LeftThumbstick = 0x00001000,
        RightThumbstick = 0x00002000,
        Menu = 0x00000001,
        View = 0x00000002
    }

    [StructLayout(LayoutKind.Sequential)]
    public record struct GamepadState
    {
        public GamepadButtons Buttons;
        public float LeftTrigger;
        public float RightTrigger;
        public float LeftThumbstickX;
        public float LeftThumbstickY;
        public float RightThumbstickX;
        public float RightThumbstickY;
    }
}
