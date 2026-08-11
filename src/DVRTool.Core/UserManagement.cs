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
