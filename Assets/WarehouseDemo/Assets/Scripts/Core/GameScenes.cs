/// The scenes this game loads, by name.
///
/// By name rather than by build index, and in one place rather than spelled out at each call site,
/// because the boot scene had to be inserted ahead of the warehouse — which shifted every index by
/// one and silently changed what `LoadScene(0)` meant. Names do not move when the build list is
/// reordered.
public static class GameScenes
{
    /// The warehouse. Also where the main menu lives — it is a UIDocument inside this scene rather
    /// than a scene of its own, which is why "quit to menu" loads the warehouse again.
    public const string Warehouse = "urp_scene_with_training 1";

    /// Nearly empty, first in the build list. Exists only to hold the loading screen while the
    /// warehouse comes up, since a scene cannot cover its own load.
    public const string Boot = "Boot";
}
