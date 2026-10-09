using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

/// <summary>
/// سرفصل حساب‌ها (فقط مدیر سیستم): درخت چهار سطحی در سمت چپ و فرم افزودن، ویرایش و حذف در سمت راست.
/// حساب‌های پایه‌ی موتور حسابداری فقط نامشان قابل تغییر است.
/// </summary>
internal sealed class AccountsTab : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly Font _systemFont;
    private readonly TreeView _tree = new()
    {
        Dock = DockStyle.Fill,
        HideSelection = false,
        FullRowSelect = true,
        ShowLines = true,
        ShowPlusMinus = true,
        ItemHeight = 26,
    };
    private readonly TextBox _code = new() { Width = 320, MaxLength = 20 };
    private readonly TextBox _name = new() { Width = 320, MaxLength = 100 };
    private readonly ComboBox _parent = new() { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _type = new() { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _level = new() { AutoSize = true };
    private readonly CheckBox _active = new() { Text = "حساب فعال باشد (حساب غیرفعال سند نمی‌گیرد)", AutoSize = true };
    private readonly Label _status = new()
    {
        AutoSize = false,
        Width = 340,
        Height = 70,
    };
    private readonly Button _newChild = new() { Text = "زیرمجموعه‌ی تازه", AutoSize = true };
    private readonly Button _newGroup = new() { Text = "گروه تازه", AutoSize = true };
    private readonly Button _save = new() { Text = "ذخیره", AutoSize = true };
    private readonly Button _delete = new() { Text = "حذف", AutoSize = true };

    private IReadOnlyList<AccountInfo> _accounts = Array.Empty<AccountInfo>();
    private string? _editingCode;
    private bool _loading;

    public AccountsTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;
        _systemFont = new Font(_tree.Font, FontStyle.Bold);
        Disposed += (_, _) => _systemFont.Dispose();

        foreach (var type in AccountRules.AccountTypes)
        {
            _type.Items.Add(new ComboItem(type, AccountRules.TypeName(type)));
        }

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(12, 10, 12, 0),
            Text = "ساختار چهار سطحی: گروه ← کل ← معین ← تفصیلی. فقط حساب‌های فعال و بدون زیرمجموعه سند می‌گیرند. " +
                   "نوع حساب از گروه به زیرمجموعه‌ها به ارث می‌رسد.",
        };

        var fields = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(12, 4, 12, 4),
        };
        fields.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("کد حساب (حروف لاتین، عدد و خط تیره):"), _code,
            UiHelpers.MakeLabel("نام حساب:"), _name,
            UiHelpers.MakeLabel("حساب پدر:"), _parent,
            UiHelpers.MakeLabel("نوع حساب:"), _type,
            UiHelpers.MakeLabel("سطح:"), _level,
            _active,
            _status,
        });

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 8),
        };
        buttons.Controls.AddRange(new Control[] { _save, _newChild, _newGroup, _delete });

        var details = new Panel { Dock = DockStyle.Fill };
        details.Controls.Add(fields);
        details.Controls.Add(buttons);
        details.Controls.Add(hint);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 460,
            FixedPanel = FixedPanel.Panel1,
        };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(details);
        Controls.Add(split);

        _tree.AfterSelect += (_, e) =>
        {
            if (e.Node?.Tag is AccountInfo account)
            {
                LoadAccount(account);
            }
        };
        _parent.SelectedIndexChanged += (_, _) => ParentChanged();
        _newChild.Click += (_, _) =>
        {
            var parent = SelectedAccount();
            if (parent is null)
            {
                UiHelpers.ShowInfo(this, "ابتدا در درخت، حساب پدر را انتخاب کنید.");
            }
            else if (parent.IsSystem || parent.Level >= AccountRules.MaxLevel)
            {
                UiHelpers.ShowInfo(this, parent.IsSystem
                    ? "حساب‌های پایه‌ی موتور حسابداری زیرمجموعه نمی‌گیرند."
                    : "حساب تفصیلی زیرمجموعه نمی‌گیرد؛ ساختار حداکثر چهار سطح است.");
            }
            else
            {
                StartNew(parent);
            }
        };
        _newGroup.Click += (_, _) => StartNew(null);
        _save.Click += async (_, _) => await SaveAsync();
        _delete.Click += async (_, _) => await DeleteAsync();
        StartNew(null);
    }

    public async Task RefreshAsync()
    {
        var keep = _editingCode;
        _accounts = await _services.Accounts.GetAccountsAsync(_user);
        BuildTree();
        if (keep is not null && FindNode(_tree.Nodes, keep) is { } node)
        {
            _tree.SelectedNode = node;
        }
        else if (keep is null && _tree.SelectedNode is null)
        {
            StartNew(null);
        }
    }

    private void BuildTree()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var byParent = _accounts.ToLookup(a => a.ParentCode ?? string.Empty);
        AddChildren(_tree.Nodes, byParent, string.Empty);
        _tree.ExpandAll();
        _tree.EndUpdate();
    }

    private void AddChildren(TreeNodeCollection nodes, ILookup<string, AccountInfo> byParent, string parentCode)
    {
        foreach (var account in byParent[parentCode].OrderBy(a => a.Code, StringComparer.Ordinal))
        {
            var node = new TreeNode($"{account.Code} · {account.Name}  ({AccountRules.LevelName(account.Level)})")
            {
                Name = account.Code,
                Tag = account,
                ToolTipText = account.IsPostable ? "سند می‌گیرد" : "تجمیعی (سند نمی‌گیرد)",
            };
            if (account.IsSystem)
            {
                node.NodeFont = _systemFont;
            }
            if (!account.IsActive)
            {
                node.ForeColor = Color.Gray;
            }
            nodes.Add(node);
            AddChildren(node.Nodes, byParent, account.Code);
        }
    }

    private static TreeNode? FindNode(TreeNodeCollection nodes, string code)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Name == code)
            {
                return node;
            }
            if (FindNode(node.Nodes, code) is { } child)
            {
                return child;
            }
        }
        return null;
    }

    private AccountInfo? SelectedAccount() => _tree.SelectedNode?.Tag as AccountInfo;

    private string? SelectedParentCode() =>
        (_parent.SelectedItem as ComboItem)?.Value is { Length: > 0 } code ? code : null;

    private void LoadAccount(AccountInfo account)
    {
        _loading = true;
        _editingCode = account.Code;
        FillParentChoices(account.Code);
        _code.Text = account.Code;
        _name.Text = account.Name;
        UiHelpers.SelectByValue(_parent, account.ParentCode ?? string.Empty);
        UiHelpers.SelectByValue(_type, account.AccountType);
        _active.Checked = account.IsActive;
        _level.Text = $"{account.Level} · {AccountRules.LevelName(account.Level)}";

        _code.ReadOnly = account.IsSystem || account.HasChildren || account.HasPostings;
        _parent.Enabled = !account.IsSystem && !account.HasChildren;
        _type.Enabled = !account.IsSystem && !account.HasChildren && account.ParentCode is null;
        _active.Enabled = !account.IsSystem && !(account.IsActive && account.HasChildren);
        _delete.Enabled = !account.IsSystem && !account.HasChildren && !account.HasPostings;
        _status.Text = StatusText(account);
        _loading = false;
    }

    private static string StatusText(AccountInfo account)
    {
        if (account.IsSystem)
        {
            return "این حساب در معاملات و سندهای خودکار استفاده می‌شود؛ فقط نام آن قابل تغییر است.";
        }
        if (account.HasChildren)
        {
            return "این حساب زیرمجموعه دارد؛ حذف نمی‌شود و تا زمانی که زیرمجموعه دارد غیرفعال هم نمی‌شود.";
        }
        if (account.HasPostings)
        {
            return "این حساب سند دارد؛ حذف نمی‌شود. برای جلوگیری از استفاده‌ی بعدی، غیرفعالش کنید.";
        }
        return account.IsPostable ? "این حساب سند می‌گیرد." : "این حساب غیرفعال است و سند نمی‌گیرد.";
    }

    private void StartNew(AccountInfo? parent)
    {
        _loading = true;
        _editingCode = null;
        FillParentChoices(null);
        _code.Text = parent?.Code ?? string.Empty;
        _name.Text = string.Empty;
        UiHelpers.SelectByValue(_parent, parent?.Code ?? string.Empty);
        if (parent is not null)
        {
            UiHelpers.SelectByValue(_type, parent.AccountType);
        }
        else
        {
            _type.SelectedIndex = _type.Items.Count > 0 ? 0 : -1;
        }
        _active.Checked = true;
        _code.ReadOnly = false;
        _parent.Enabled = true;
        _active.Enabled = true;
        _type.Enabled = parent is null;
        _level.Text = parent is null ? "گروه (سطح ۱)" : $"سطح {parent.Level + 1} · {AccountRules.LevelName(parent.Level + 1)}";
        _delete.Enabled = false;
        _status.Text = parent is null
            ? "گروه تازه: کد یک رقم (۱ تا ۹) و نوع حساب را تعیین کنید."
            : $"زیرمجموعه‌ی «{parent.Name}»: کد باید با «{parent.Code}» شروع شود و نوع آن همان نوع پدر است.";
        _loading = false;
        _code.Focus();
        _code.SelectionStart = _code.TextLength;
    }

    private void FillParentChoices(string? excludeCode)
    {
        var previous = SelectedParentCode();
        _parent.Items.Clear();
        _parent.Items.Add(new ComboItem(string.Empty, "— بدون پدر (گروه) —"));
        foreach (var account in _accounts
                     .Where(a => !a.IsSystem && a.IsActive && a.Level < AccountRules.MaxLevel && a.Code != excludeCode)
                     .OrderBy(a => a.Code, StringComparer.Ordinal))
        {
            _parent.Items.Add(new ComboItem(account.Code, $"{account.Code} · {account.Name} ({AccountRules.LevelName(account.Level)})"));
        }
        UiHelpers.SelectByValue(_parent, previous ?? string.Empty);
    }

    private void ParentChanged()
    {
        if (_loading)
        {
            return;
        }
        var parentCode = SelectedParentCode();
        var parent = parentCode is null ? null : _accounts.FirstOrDefault(a => a.Code == parentCode);
        if (parent is null)
        {
            var editing = _editingCode is null ? null : _accounts.FirstOrDefault(a => a.Code == _editingCode);
            _type.Enabled = editing is not { IsSystem: true } and not { HasChildren: true };
            return;
        }
        UiHelpers.SelectByValue(_type, parent.AccountType);
        _type.Enabled = false;
    }

    private async Task SaveAsync()
    {
        try
        {
            var parentCode = SelectedParentCode();
            var type = (_type.SelectedItem as ComboItem)?.Value;
            string savedCode;
            if (_editingCode is null)
            {
                await _services.Accounts.CreateAsync(_user, _code.Text, _name.Text, parentCode, type, DateTime.Now);
                savedCode = _code.Text.Trim();
                UiHelpers.ShowInfo(this, "حساب تازه ثبت شد.");
            }
            else
            {
                await _services.Accounts.UpdateAsync(_user, _editingCode, _code.Text, _name.Text, parentCode, type, _active.Checked, DateTime.Now);
                savedCode = _code.Text.Trim();
                UiHelpers.ShowInfo(this, "تغییرات حساب ذخیره شد.");
            }
            _editingCode = savedCode;
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private async Task DeleteAsync()
    {
        if (_editingCode is null)
        {
            return;
        }
        var answer = MessageBox.Show(this, $"حساب «{_editingCode}» حذف شود؟ این کار برگشت‌پذیر نیست.",
            "تأیید حذف", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }
        try
        {
            await _services.Accounts.DeleteAsync(_user, _editingCode, DateTime.Now);
            UiHelpers.ShowInfo(this, "حساب حذف شد.");
            _editingCode = null;
            await RefreshAsync();
            StartNew(null);
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
