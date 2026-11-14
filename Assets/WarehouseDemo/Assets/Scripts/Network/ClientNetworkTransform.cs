using Unity.Netcode.Components;

/// Owner-authoritative NetworkTransform.
/// Attach this instead of NetworkTransform so the owning client
/// can write their own position without server relay.
[UnityEngine.DisallowMultipleComponent]
public class ClientNetworkTransform : NetworkTransform
{
    protected override bool OnIsServerAuthoritative() => false;
}
