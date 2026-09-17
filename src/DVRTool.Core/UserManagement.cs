namespace DVRTool.Core;

public enum UserRole
{
    Admin,
    Operator,
    Viewer,
    Custom,
}

/// <summary>One account configured on the NVR.</summary>
/// <param name="Id">Vendor-native id: Hikvision numeric &lt;id&gt; as a string; Dahua ID, falling back to Name.</param>
/// <param name="Role">Normalized cross-vendor role.</param>
/// <param name="NativeLevel">
/// Raw vendor value: Hikvision userLevel ("Administrator"/"Operator"/"Viewer"),
/// Dahua group name ("admin"/"user"/...).
/// </param>
/// <param name="Reserved">Built-in account that cannot be deleted (Hikvision admin/inherent, Dahua Reserved).</param>
public sealed record NvrUser(
    string Id,
    string Name,
    UserRole Role,
    string NativeLevel,
    bool Reserved = false,
    string? Memo = null);

/// <summary>
/// Opt-in capability: read the NVR's account list. Implemented separately from
/// <see cref="INvrClient"/> because not every device exposes user administration.
/// </summary>
public interface IUserManagementClient
{
    Task<IReadOnlyList<NvrUser>> GetUsersAsync(CancellationToken ct = default);
}

/// <summary>An account to create, as the operator asked for it.</summary>
/// <param name="Password">
/// Held only for the duration of the write. It is never persisted, never logged and never
/// read back — both vendors treat passwords as write-only, so the tool can confirm that an
/// account exists but never that two accounts share a password.
/// </param>
public sealed record NewUser(string Name, string Password, UserRole Role);

/// <summary>
/// Opt-in capability: create accounts. Split from <see cref="IUserManagementClient"/> for the
/// same reason <see cref="IDeviceConfigWriter"/> is split from <see cref="IDeviceConfigClient"/>
/// — a vendor that can be read is not thereby a vendor that can be written, and a front end
/// must be able to ask which it has before it offers the button.
/// </summary>
public interface IUserAdminClient : IUserManagementClient
{
    /// <summary>
    /// Creates <paramref name="user"/> and returns the account <b>read back off the device</b>,
    /// never the one that was asked for: the recorder assigns the id, and it may keep a
    /// different level than the one requested. A create that the device accepts but that leaves
    /// no account behind is a failure, not a success.
    /// </summary>
    Task<NvrUser> CreateUserAsync(NewUser user, CancellationToken ct = default);
}
