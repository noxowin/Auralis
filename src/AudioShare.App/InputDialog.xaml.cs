using System.Windows;

namespace AudioShare.App;

/// <summary>输入一行文字的小对话框，用来给方案起名字。</summary>
public partial class InputDialog : Window
{
    private InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var dialog = new InputDialog(title, prompt, initial) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.InputBox.Text.Trim() : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text)) return;
        DialogResult = true;
    }
}
