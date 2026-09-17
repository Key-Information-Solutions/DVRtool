namespace DVRTool.Core;

/// <summary>
/// What adding one account across a fleet would do: the devices to write, the devices that
/// already have the name, and the devices nobody could read. Pure — built from reads that have
/// already happened and no I/O — so the CLI's <c>--force</c> prompt and the GUI's confirmation
/// dialog describe the same write from the same arithmetic.
/// </summary>
/// <remarks>
/// The unreadable devices are the point of the type, as they are for <see cref="CardRevokePlan"/>:
/// an account is added so that somebody can log in, and a recorder that did not answer is a
/// recorder they still cannot log into — so "added everywhere" is a conclusion that can only be
/// drawn from a complete read.
/// </remarks>
public sealed record UserAddPlan
{
    /// <summary>The account name as the operator asked for it.</summary>
    public required string Name { get; init; }

    /// <summary>The level to create it at, normalized; the vendor spells it.</summary>
    public required UserRole Role { get; init; }

    /// <summary>Devices with no such account: every one of these is a write.</summary>
    public required IReadOnlyList<string> Creates { get; init; }

    /// <summary>
    /// Devices that already have an account by this name, with the level they hold it at
    /// ("Front Office NVR (Operator)"). Never written.
    /// </summary>
    /// <remarks>
    /// This is what keeps add-only honest. An existing name is left exactly as it is, even when
    /// its level differs from the one asked for: changing it would be a modify, the operator
    /// asked for an add, and silently promoting an account is the one mistake here that hands
    /// somebody rights nobody chose to give them.
    /// </remarks>
    public required IReadOnlyList<string> AlreadyPresent { get; init; }

    /// <summary>Devices that could not be read, with the reason. The account is not on these.</summary>
    public required IReadOnlyList<DeviceUsersResult> Unreadable { get; init; }

    /// <summary>True when there is a write to make.</summary>
    public bool HasWork => Creates.Count > 0;

    /// <summary>True when at least one device did not answer, so this plan is not the whole fleet.</summary>
    public bool IsPartial => Unreadable.Count > 0;

    /// <summary>
    /// Works out what adding <paramref name="name"/> would touch, given reads that have just
    /// happened.
    /// </summary>
    /// <remarks>
    /// Names are matched case-insensitively, the same pairing <see cref="UserMatrix.Build"/>
    /// uses: the recorders do not let two accounts differ only in case, so treating "James" and
    /// "james" as different here would plan a write the device is going to refuse.
    /// </remarks>
    public static UserAddPlan For(IEnumerable<DeviceUsersResult> read, string name, UserRole role)
    {
        string wanted = name.Trim();
        if (wanted.Length == 0)
            throw new ArgumentException("an add needs an account name.", nameof(name));

        var creates = new List<string>();
        var already = new List<string>();
        var unreadable = new List<DeviceUsersResult>();

        foreach (var device in read)
        {
            if (!device.Ok)
            {
                unreadable.Add(device);
                continue;
            }

            var existing = device.Users.FirstOrDefault(
                u => string.Equals(u.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                creates.Add(device.DeviceName);
            else
                already.Add($"{device.DeviceName} ({DescribeLevel(existing)})");
        }

        return new UserAddPlan
        {
            Name = wanted,
            Role = role,
            Creates = creates,
            AlreadyPresent = already,
            Unreadable = unreadable,
        };
    }

    /// <summary>The device's own word for an account's level, falling back to the normalized role.</summary>
    private static string DescribeLevel(NvrUser user) =>
        user.NativeLevel.Length > 0 ? user.NativeLevel : user.Role.ToString();
}

/// <summary>
/// The password floor both front ends check before anything is sent. Pure, so a password that
/// the recorder is certain to reject is refused at the keyboard rather than over the wire.
/// </summary>
/// <remarks>
/// This is deliberately the documented Hikvision minimum and not a house policy: a rule
/// stricter than the device's would refuse passwords the recorder would have taken, and the
/// operator would have no way to tell which of the two was complaining. The device remains the
/// authority — it can still reject a password this passes (firmware adds its own risk checks),
/// and when it does, its own message is what gets shown.
/// </remarks>
public static class UserPasswordRules
{
    public const int MinLength = 8;
    public const int MaxLength = 16;

    /// <summary>
    /// Everything wrong with <paramref name="password"/>, as sentences to show the operator.
    /// Empty means "nothing known to be wrong with it", never "the device will accept it".
    /// </summary>
    public static IReadOnlyList<string> Check(string password, string userName)
    {
        var complaints = new List<string>();

        if (password.Length < MinLength)
            complaints.Add($"must be at least {MinLength} characters.");
        if (password.Length > MaxLength)
            complaints.Add($"must be at most {MaxLength} characters — Hikvision truncates or refuses beyond that.");

        int classes =
            (password.Any(char.IsLower) ? 1 : 0) +
            (password.Any(char.IsUpper) ? 1 : 0) +
            (password.Any(char.IsDigit) ? 1 : 0) +
            (password.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        if (classes < 2)
            complaints.Add("must mix at least two of: lower case, upper case, digits, symbols.");

        string name = userName.Trim();
        if (name.Length > 0)
        {
            if (string.Equals(password, name, StringComparison.OrdinalIgnoreCase))
                complaints.Add("must not be the account name.");
            else if (string.Equals(password, Reverse(name), StringComparison.OrdinalIgnoreCase))
                complaints.Add("must not be the account name reversed.");
        }

        return complaints;
    }

    private static string Reverse(string value)
    {
        var chars = value.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }
}
