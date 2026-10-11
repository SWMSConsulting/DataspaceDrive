namespace DataspaceDrive.Core.Access;

public enum PrincipalType
{
    User,
    Role,
    Group,
}

public enum Permission
{
    None = 0,
    Read = 1,
    Write = 2,
    Manage = 3,
}

/// <summary>Recht auf einen Team-Bereich für einen Nutzer, eine IdP-Rolle oder eine IdP-Gruppe.</summary>
public sealed class AclEntry
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public PrincipalType PrincipalType { get; set; }
    public required string PrincipalKey { get; set; }
    public Permission Permission { get; set; }
}
