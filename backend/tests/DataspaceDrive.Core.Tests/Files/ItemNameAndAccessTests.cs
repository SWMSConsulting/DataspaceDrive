using DataspaceDrive.Core;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Files;

namespace DataspaceDrive.Core.Tests.Files;

public class ItemNameAndAccessTests
{
    [Theory]
    [InlineData("  Bericht.pdf ", "Bericht.pdf")]
    [InlineData("Prüfbericht WEA 17", "Prüfbericht WEA 17")]
    public void Valid_names_are_trimmed(string input, string expected) => Assert.Equal(expected, ItemName.Validate(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("Ende.")]
    [InlineData("tab\there")]
    [InlineData("frage?")]
    public void Invalid_names_are_rejected(string input) => Assert.Throws<InvalidInputException>(() => ItemName.Validate(input));

    [Fact]
    public void Too_long_names_are_rejected() => Assert.Throws<InvalidInputException>(() => ItemName.Validate(new string('a', 256)));

    [Theory]
    [InlineData("Bericht.pdf", 2, "Bericht (2).pdf")]
    [InlineData("Ordner", 3, "Ordner (3)")]
    [InlineData(".env", 2, ".env (2)")]
    public void Counter_goes_before_the_extension(string name, int counter, string expected) =>
        Assert.Equal(expected, ItemName.WithCounter(name, counter));

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static DrivePrincipal Principal(Guid id, string[]? roles = null, string[]? groups = null) =>
        new(id, (roles ?? []).ToHashSet(), (groups ?? []).ToHashSet());

    [Fact]
    public void Personal_space_belongs_to_its_owner_only_even_for_admins()
    {
        var space = new Space { Id = Guid.NewGuid(), Kind = SpaceKind.Personal, Name = "Meine Dateien", OwnerUserId = Owner };

        Assert.Equal(Permission.Manage, SpaceAccess.Evaluate(space, [], Principal(Owner)));
        Assert.Equal(Permission.None, SpaceAccess.Evaluate(space, [], Principal(Other, [DrivePrincipal.RoleAdmin])));
    }

    [Fact]
    public void Team_space_uses_the_strongest_matching_acl_entry()
    {
        var space = new Space { Id = Guid.NewGuid(), Kind = SpaceKind.Team, Name = "Service" };
        AclEntry[] acl =
        [
            new() { SpaceId = space.Id, PrincipalType = PrincipalType.Group, PrincipalKey = "service", Permission = Permission.Read },
            new() { SpaceId = space.Id, PrincipalType = PrincipalType.Role, PrincipalKey = "drive-admin", Permission = Permission.Manage },
            new() { SpaceId = space.Id, PrincipalType = PrincipalType.User, PrincipalKey = Other.ToString(), Permission = Permission.Write },
            new() { SpaceId = Guid.NewGuid(), PrincipalType = PrincipalType.Group, PrincipalKey = "service", Permission = Permission.Manage },
        ];

        Assert.Equal(Permission.Read, SpaceAccess.Evaluate(space, acl, Principal(Owner, groups: ["service"])));
        Assert.Equal(Permission.Write, SpaceAccess.Evaluate(space, acl, Principal(Other, groups: ["service"])));
        Assert.Equal(Permission.Manage, SpaceAccess.Evaluate(space, acl, Principal(Owner, [DrivePrincipal.RoleAdmin])));
        Assert.Equal(Permission.None, SpaceAccess.Evaluate(space, acl, Principal(Guid.NewGuid())));
    }
}
