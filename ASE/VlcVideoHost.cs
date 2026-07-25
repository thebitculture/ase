using Avalonia.Controls;
using Avalonia.Platform;
using LibVLCSharp.Shared;

namespace ASE;

/// <summary>
/// Minimal native surface for LibVLC video output (what LibVLCSharp.Avalonia's VideoView
/// does, without its Avalonia 11 dependency): hosts a bare native child window and hands
/// its handle to the MediaPlayer, which renders into it directly.
/// </summary>
public class VlcVideoHost : NativeControlHost
{
    IPlatformHandle _handle;
    MediaPlayer _mediaPlayer;

    public MediaPlayer MediaPlayer
    {
        get => _mediaPlayer;
        set
        {
            _mediaPlayer = value;
            if (_handle != null && _mediaPlayer != null)
                Attach();
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _handle = base.CreateNativeControlCore(parent);

        if (_mediaPlayer != null)
            Attach();

        return _handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Detach();
        _handle = null;
        base.DestroyNativeControlCore(control);
    }

    // NativeReference goes to zero once the player is disposed; calling into libvlc
    // then is a native access violation (host destruction is deferred by Avalonia,
    // so it can run after the window's Closed handler already disposed the player)
    void Attach()
    {
        if (_mediaPlayer == null || _mediaPlayer.NativeReference == IntPtr.Zero)
            return;

        if (OperatingSystem.IsWindows())
            _mediaPlayer.Hwnd = _handle.Handle;
        else if (OperatingSystem.IsMacOS())
            _mediaPlayer.NsObject = _handle.Handle;
        else
            _mediaPlayer.XWindow = (uint)_handle.Handle;
    }

    void Detach()
    {
        if (_mediaPlayer == null || _mediaPlayer.NativeReference == IntPtr.Zero)
            return;

        if (OperatingSystem.IsWindows())
            _mediaPlayer.Hwnd = IntPtr.Zero;
        else if (OperatingSystem.IsMacOS())
            _mediaPlayer.NsObject = IntPtr.Zero;
        else
            _mediaPlayer.XWindow = 0;
    }
}
