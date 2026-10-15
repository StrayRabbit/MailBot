using System.Windows;

namespace MailBot.App.Controls;

public partial class LinkDialog : Window
{
    public string Url => UrlTextBox.Text;

    public LinkDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => UrlTextBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlTextBox.Text))
        {
            MessageBoxEx.Show(this, "URL 不能为空", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}