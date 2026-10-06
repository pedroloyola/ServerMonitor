using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.ViewModels;

/// <summary>UI.7C (B-16): the editor's fields that can carry an error, in visual (= tab) order.</summary>
public enum ServerEditorField
{
    Name,
    Host,
    Username,
    PrivateKey,
    Password,
    Port,
    JumpHost,
    JumpUsername,
    JumpPort,
    JumpPrivateKey,
    JumpPassword
}

/// <summary>
/// UI.7C (B-16, Figma 06): which field a validation error belongs to and the resource key of its message. Every rule comes
/// from <see cref="ServerValidationResult"/> (Core, incl. the H-UI7-1 write-only rules) or from the editor's own secret
/// rules (a password typed nowhere); nothing here decides validity, it only places and words the answer.
/// </summary>
public static class ServerEditorValidation
{
    public static (ServerEditorField Field, string MessageKey)? Place(ServerValidationErrorCode code) => code switch
    {
        ServerValidationErrorCode.NameRequired => (ServerEditorField.Name, "ServerEditorErrorNameRequired"),
        ServerValidationErrorCode.HostRequired => (ServerEditorField.Host, "ServerEditorErrorHostRequired"),
        ServerValidationErrorCode.HostFormatInvalid => (ServerEditorField.Host, "ServerEditorErrorHostFormat"),
        ServerValidationErrorCode.PortOutOfRange => (ServerEditorField.Port, "ServerEditorErrorPortRange"),
        ServerValidationErrorCode.UsernameRequired => (ServerEditorField.Username, "ServerEditorErrorUsernameRequired"),
        ServerValidationErrorCode.PrivateKeyPathRequired => (ServerEditorField.PrivateKey, "ServerEditorErrorPrivateKeyRequired"),
        ServerValidationErrorCode.PrivateKeyIsPublicKey => (ServerEditorField.PrivateKey, "ServerEditorErrorPrivateKeyIsPublic"),
        ServerValidationErrorCode.JumpHostRequired => (ServerEditorField.JumpHost, "ServerEditorErrorJumpHostRequired"),
        ServerValidationErrorCode.JumpEndpointIsTarget => (ServerEditorField.JumpHost, "ServerEditorErrorJumpIsTarget"),
        ServerValidationErrorCode.JumpPortOutOfRange => (ServerEditorField.JumpPort, "ServerEditorErrorJumpPortRange"),
        ServerValidationErrorCode.JumpUsernameRequired => (ServerEditorField.JumpUsername, "ServerEditorErrorJumpUsernameRequired"),
        ServerValidationErrorCode.JumpPrivateKeyPathRequired => (ServerEditorField.JumpPrivateKey, "ServerEditorErrorJumpPrivateKeyRequired"),
        ServerValidationErrorCode.JumpPrivateKeyIsPublicKey => (ServerEditorField.JumpPrivateKey, "ServerEditorErrorJumpPrivateKeyIsPublic"),
        _ => null
    };

    public const string PasswordMissingKey = "ServerEditorErrorPasswordRequired";

    public const string JumpPasswordMissingKey = "ServerEditorErrorJumpPasswordRequired";

    public static bool IsJumpField(ServerEditorField field) => field >= ServerEditorField.JumpHost;
}
