using Microsoft.AspNetCore.DataProtection;

namespace VardyParty.LocalService.Compute;

public sealed class DataProtectionHostShareProtector(IDataProtector protector) : IHostShareProtector
{
    public const string Purpose = "VardyParty.LocalService.HostShare.v1";

    public byte[] Protect(byte[] plaintext) => protector.Protect(plaintext);

    public byte[] Unprotect(byte[] protectedPayload) => protector.Unprotect(protectedPayload);
}
