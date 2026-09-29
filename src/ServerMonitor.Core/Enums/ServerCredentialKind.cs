namespace ServerMonitor.Core.Enums;

public enum ServerCredentialKind
{
    Password = 1,
    PrivateKeyPassphrase = 2,

    /// <summary>The password of a route's jump host. Independent of the target's credential.</summary>
    JumpPassword = 3,

    /// <summary>The private-key passphrase of a route's jump host. Independent of the target's credential.</summary>
    JumpPrivateKeyPassphrase = 4
}
