namespace VardyParty.LocalService.Compute;

/// <summary>
/// Encrypts host-share bytes for the current machine/user. Implementations
/// must not write plaintext.
/// </summary>
public interface IHostShareProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedPayload);
}
