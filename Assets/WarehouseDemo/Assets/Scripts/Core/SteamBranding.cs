using UnityEngine;

/// Canonical Steam identity used by every player-facing screen.
public static class SteamBranding
{
    public const uint FullGameAppId = 5173620;
    public const uint DemoAppId     = 5173640;
    public const string GameName    = "Shift Happens: Together";
    public const string WishlistUrl = "https://store.steampowered.com/app/5173620/Shift_Happens_Together/";

    public static void OpenWishlist() => Application.OpenURL(WishlistUrl);
}
