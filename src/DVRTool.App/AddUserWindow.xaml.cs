using System.Windows;
using DVRTool.Core;

namespace DVRTool.App;

/// <summary>
/// Collects the account to create. Nothing is written from here: OK closes the dialog and the
/// caller re-reads the fleet, plans, and asks again naming every recorder — this is the form,
/// not the confirmation.
/// </summary>
/// <remarks>
/// The password lives in this window's <see cref="System.Windows.Controls.PasswordBox"/> and in
/// the caller's local for the duration of the write. It is never saved, never logged, and never
/// read back: both vendors treat passwords as write-only, so the tool can confirm an account
/// exists but never that two accounts share a password.
/// </remarks>
public partial class AddUserWindow : Window
{
    /// <summary>The account to create, or null when the operator cancelled.</summary>
    public NewUser? Result { get; private set; }

    public AddUserWindow(IReadOnlyList<string> recorders)
    {
        InitializeComponent();
        TargetText.Text = string.Join(", ", recorders);
        NameBox.Focus();
    }

    private UserRole SelectedRole => RoleCombo.SelectedIndex switch
    {
        1 => UserRole.Viewer,
        2 => UserRole.Admin,
        _ => UserRole.Operator,
    };

    private void OnInputChanged(object sender, RoutedEventArgs e)
    {
        // Fires while XAML is still applying initial values, before every control exists.
        if (OkButton is null)
            return;

        string name = NameBox.Text.Trim();
        string password = PasswordBox1.Password;

        var complaints = new List<string>();
        if (password.Length > 0)
            complaints.AddRange(UserPasswordRules.Check(password, name).Select(c => "The password " + c));

        // Only nag about the mismatch once the second box has something in it: complaining
        // while it is still being typed is noise.
        bool mismatch = PasswordBox2.Password.Length > 0 && password != PasswordBox2.Password;
        if (mismatch)
            complaints.Add("The two passwords differ.");

        ComplaintText.Text = string.Join("\n", complaints);
        OkButton.IsEnabled =
            name.Length > 0 &&
            password.Length > 0 &&
            password == PasswordBox2.Password &&
            UserPasswordRules.Check(password, name).Count == 0;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = new NewUser(NameBox.Text.Trim(), PasswordBox1.Password, SelectedRole);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
