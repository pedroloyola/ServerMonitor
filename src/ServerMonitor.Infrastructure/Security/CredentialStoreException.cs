namespace ServerMonitor.Infrastructure.Security;

public sealed class CredentialStoreException : Exception
{
    public CredentialStoreException(CredentialStoreOperation operation, int nativeErrorCode)
        : base($"Windows Credential Manager {operation.ToString().ToLowerInvariant()} failed with error code {nativeErrorCode}.")
    {
        Operation = operation;
        NativeErrorCode = nativeErrorCode;
    }

    public CredentialStoreOperation Operation { get; }

    public int NativeErrorCode { get; }
}
