using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace ASE;

public partial class FileList : Window
{
    public string[] files { get; set; }

    public FileList()
    { 
    }

    public FileList(string fileList)
    {
        files = fileList.Split('|');
        Array.Sort(files);

        InitializeComponent();
        DataContext = this;
    }

    private string GetSelectedFile()
    {
        var lstFiles = this.FindControl<ListBox>("lstFiles");
        return lstFiles?.SelectedItem as string;
    }

    private void lstFiles_DoubleTapped(object sender, TappedEventArgs e)
    {
        var selected = GetSelectedFile();
        if (selected != null)
            Close(selected);
    }

    private void InsertDisk_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedFile();
        if (selected != null)
            Close(selected);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void ListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox) return;

        var key = e.Key.ToString();
        if (key.Length != 1) return; // Filtrar teclas especiales

        var items = listBox.ItemsSource?.Cast<string>().ToList();
        if (items == null) return;

        var match = items.FirstOrDefault(item =>
            item.StartsWith(key, StringComparison.CurrentCultureIgnoreCase));

        if (match != null)
        {
            listBox.SelectedItem = match;
            listBox.ScrollIntoView(match);
        }
    }
}
