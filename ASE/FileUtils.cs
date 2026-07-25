using Avalonia.Controls;
using System.IO;
using System.Security.Cryptography;
using TinyDialogsNet;

namespace ASE;

public static class FileUtils
{
    /// <summary>
    /// Show directory selector for configuration windows
    /// </summary>
    /// <param name="target">Textbox to show selected path</param>
    /// <param name="title">Title in the path selector</param>
    public static void BrowseDirectory(TextBox target, string title)
    {
        var (canceled, path) = TinyDialogs.SelectFolderDialog(title, Config.DialogStartFolder(target.Text));

        if (!canceled && !string.IsNullOrWhiteSpace(path))
            target.Text = path;
    }

    /// <summary>
    /// Calculates SHA1 checksum of a given file
    /// </summary>
    /// <param name="file">File to calculate</param>
    /// <returns>SHA1 checksum</returns>
    public static string CalculateSHA1(string file)
    {
        using FileStream stream = File.OpenRead(file);
        using SHA1 sha1 = SHA1.Create();

        byte[] hash = sha1.ComputeHash(stream);

        return Convert.ToHexString(hash);
    }
}