using System.ComponentModel;
using System.Windows;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

public partial class LoginWindow : Window
{
    private readonly ILoginClient? client;
    private readonly Func<string,string,string,Task<ServiceResult<LoginSession>>>? connect;
    private readonly bool fixedUser;
    private bool busy;
    public LoginSession? Session { get; private set; }

    public LoginWindow(ILoginClient? client, string userName = "", bool fixedUser = false,
        string? serverUrl = null, Func<string,string,string,Task<ServiceResult<LoginSession>>>? connect = null)
    {
        InitializeComponent();
        this.client = client;
        this.connect = connect;
        if (client is null && connect is null) throw new ArgumentException("请提供登录接口。");
        ServerAddressInput.Text = serverUrl ?? "";
        ServerPanel.Visibility = connect is null ? Visibility.Collapsed : Visibility.Visible;
        this.fixedUser = fixedUser;
        UserNameBox.Text = userName;
        UserNameBox.IsReadOnly = fixedUser;
        Loaded += (_, _) => { if (string.IsNullOrEmpty(userName)) UserNameBox.Focus(); else PasswordInput.Focus(); };
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (string.IsNullOrWhiteSpace(UserNameBox.Text) || string.IsNullOrWhiteSpace(PasswordInput.Password))
        { LoginStatus.Text = "请输入用户名和密码。"; return; }
        busy = true;
        LoginButton.IsEnabled = CancelButton.IsEnabled = PasswordInput.IsEnabled = ServerAddressInput.IsEnabled = false;
        UserNameBox.IsReadOnly = true;
        LoginStatus.Text = "正在登录……";
        try
        {
            var result = connect is null ? await client!.LoginAsync(UserNameBox.Text, PasswordInput.Password) :
                await connect(ServerAddressInput.Text,UserNameBox.Text,PasswordInput.Password);
            PasswordInput.Clear();
            if (!result.Success || result.Data is null) { LoginStatus.Text = result.Message; return; }
            Session = result.Data;
            busy = false;
            DialogResult = true;
        }
        finally
        {
            busy = false;
            LoginButton.IsEnabled = CancelButton.IsEnabled = PasswordInput.IsEnabled = ServerAddressInput.IsEnabled = true;
            UserNameBox.IsReadOnly = fixedUser;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e) { if (busy) e.Cancel = true; }
}
