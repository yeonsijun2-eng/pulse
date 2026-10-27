namespace Pulse.Models;

public enum LicenseKind
{
    SevenDay,
    ThirtyDay,
    Permanent
}

public enum LicenseState
{
    Active,
    Expired,
    Revoked
}

public sealed record LicenseDetails(
    LicenseKind Kind,
    LicenseState State,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset ServerTime);

public sealed record AuthenticatedLicense(
    string SessionToken,
    DateTimeOffset SessionExpiresAt,
    LicenseDetails License);

public enum LicenseAuthFailure
{
    None,
    InvalidKey,
    Expired,
    Revoked,
    ConnectionFailed
}

public sealed record LicenseAuthResult(
    AuthenticatedLicense? Session,
    LicenseAuthFailure Failure,
    string? Message = null)
{
    public bool IsSuccess => Session is not null
                             && Failure == LicenseAuthFailure.None
                             && Session.License.State == LicenseState.Active;
}
