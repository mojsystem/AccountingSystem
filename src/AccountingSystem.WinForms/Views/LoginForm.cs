using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Views;

/// <summary>فرم ورود. اگر هنوز کاربری ثبت نشده باشد، همین فرم برای ساخت نخستین مدیر سیستم استفاده می‌شود.</summary>
internal sealed class LoginForm : Form
{
    private readonly AppServices _services;
    private readonly Label _title = new() { AutoSize = true, Location = new Point(20, 15), Font = new Font("Tahoma", 11f, FontStyle.Bold) };
    private readonly Label _fullNameLabel = UiHelpers.MakeLabel("نام و نام خانوادگی:", new Point(20, 62));
    private readonly TextBox _fullName = new() { Location = new Point(170, 58), Width = 210 };
    private readonly Label _userLabel = UiHelpers.MakeLabel("نام کاربری:", new Point(20, 102));
    private readonly TextBox _username = new() { Location = new Point(170, 98), Width = 210 };
    private readonly Label _passLabel = UiHelpers.MakeLabel("رمز عبور:", new Point(20, 142));
    private readonly TextBox _password = new() { Location = new Point(170, 138), Width = 210, UseSystemPasswordChar = true };
    private readonly Button _ok = new() { Text = "ورود", Location = new Point(170, 186), Width = 100 };
    private readonly Button _cancel = new() { Text = "انصراف", Location = new Point(280, 186), Width = 100, DialogResult = DialogResult.Cancel };
    private bool _setupMode;

    public LoginForm(AppServices services)
    {
        _services = services;
        Text = "ورود به سیستم حسابداری";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 235);
        Font = new Font("Tahoma", 9f);

        Controls.AddRange(new Control[] { _title, _fullNameLabel, _fullName, _userLabel, _username, _passLabel, _password, _ok, _cancel });
        AcceptButton = _ok;
        CancelButton = _cancel;

        _ok.Click += async (_, _) => await SubmitAsync();
        Shown += async (_, _) => await InitializeAsync();
    }

    public CurrentUser? SignedInUser { get; private set; }

    private async Task InitializeAsync()
    {
        try
        {
            _setupMode = await _services.Users.CountUsersAsync() == 0;
            _title.Text = _setupMode ? "ایجاد نخستین کاربر (مدیر سیستم)" : "ورود به سیستم";
            _ok.Text = _setupMode ? "ایجاد و ورود" : "ورود";
            _fullNameLabel.Visible = _setupMode;
            _fullName.Visible = _setupMode;
            if (_setupMode)
            {
                _fullName.Focus();
            }
            else
            {
                _username.Focus();
            }
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
            DialogResult = DialogResult.Abort;
            Close();
        }
    }

    private async Task SubmitAsync()
    {
        try
        {
            if (_setupMode)
            {
                SignedInUser = await _services.Users.CreateFirstAdminAsync(_username.Text, _fullName.Text, _password.Text, DateTime.Now);
            }
            else
            {
                SignedInUser = await _services.Users.SignInAsync(_username.Text, _password.Text);
                if (SignedInUser is null)
                {
                    throw new BusinessRuleException("نام کاربری یا رمز عبور اشتباه است.");
                }
            }

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
