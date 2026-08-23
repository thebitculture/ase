using TinyDialogsNet;

namespace ASE
{
    /// <summary>
    /// Wrappers over the TinyDialogs (tinyfiledialogs) dialogs with two macOS fixes; the
    /// other platforms keep the original synchronous, filtered behaviour.
    ///
    /// 1. On macOS the dialogs run on a background thread and are awaited. tinyfd blocks
    ///    the calling thread (the dialog lives in an osascript subprocess with no message
    ///    pump), so calling it from a menu handler froze Avalonia with the menu still
    ///    painted on top of the dialog. On Windows the native dialog pumps messages
    ///    itself, so it stays synchronous on the UI thread.
    /// 2. Open dialogs drop the extension filter on macOS: tinyfd builds AppleScript
    ///    "choose file of type {...}", which modern macOS resolves as UTIs — extensions
    ///    without a registered type (.snap/.st/.msa/.stx) come out greyed and unselectable.
    /// </summary>
    public static class Dialogs
    {
        // Only touched from the UI thread. On macOS the awaited dialog is not modal to our
        // window, so this keeps a second one from being opened on top of it from the menu.
        static bool _open;

        public static async Task<(bool Canceled, IEnumerable<string> Paths)> OpenFile(string title, string defaultPath, FileFilter filter)
        {
            if (!OperatingSystem.IsMacOS())
                return TinyDialogs.OpenFileDialog(title, defaultPath, false, filter);

            if (_open)
                return (true, []);

            _open = true;
            try
            {
                return await Task.Run(() => TinyDialogs.OpenFileDialog(title, defaultPath, false, null));
            }
            finally
            {
                _open = false;
            }
        }

        /// <summary>
        /// Save dialog. Same macOS treatment as <see cref="OpenFile"/>: run off the UI thread
        /// (tinyfd blocks the caller) and drop the filter, which AppleScript resolves as UTIs.
        /// </summary>
        public static async Task<(bool Canceled, string Path)> SaveFile(string title, string defaultPath, FileFilter filter)
        {
            if (!OperatingSystem.IsMacOS())
                return TinyDialogs.SaveFileDialog(title, defaultPath, filter);

            if (_open)
                return (true, "");

            _open = true;
            try
            {
                return await Task.Run(() => TinyDialogs.SaveFileDialog(title, defaultPath, null));
            }
            finally
            {
                _open = false;
            }
        }

        public static async Task<MessageBoxButton> MessageBox(string title, string message,
            MessageBoxDialogType type, MessageBoxIconType icon, MessageBoxButton defaultButton)
        {
            if (!OperatingSystem.IsMacOS())
                return TinyDialogs.MessageBox(title, message, type, icon, defaultButton);

            return await Task.Run(() => TinyDialogs.MessageBox(title, message, type, icon, defaultButton));
        }
    }
}
