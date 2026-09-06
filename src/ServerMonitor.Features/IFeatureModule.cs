using Microsoft.Extensions.DependencyInjection;

namespace ServerMonitor.Features;

/// <summary>
/// One composable capability: what it is, and the registrations that make it real.
/// <para>
/// Modules are named explicitly in a static list at the composition root and instantiated there. They are
/// NEVER discovered — no assembly scanning, no directory probing, no type resolution by name. That is the
/// security boundary recorded as <c>M14-MOD-1</c>: an assembly loaded dynamically into this process would
/// hold full in-process authority over SSH sessions, the Windows Credential Manager and the host-key
/// trust store, and in the unpackaged build the application directory is not protected.
/// </para>
/// </summary>
public interface IFeatureModule
{
    /// <summary>Identity and composition policy for this capability.</summary>
    FeatureDescriptor Descriptor { get; }

    /// <summary>
    /// Adds this capability's services. Called at most once, only from the composition root, and only
    /// after the module has been cleared for composition.
    /// </summary>
    void Register(IServiceCollection services);
}
