using UnityEngine;

/// The only thing in the boot scene: it puts the loading screen up and pulls the warehouse in
/// behind it.
///
/// This exists because a scene cannot cover its own load. Unity deserialises the first scene in the
/// build list before a line of game code runs, so when the warehouse was first, its several
/// thousand objects came up behind a frozen window with nothing on it. An almost empty scene loads
/// in milliseconds, and from there the warehouse is an ordinary asynchronous load with a screen
/// over it and a progress bar that means something.
///
/// It costs one extra scene load of nothing at all — the warehouse itself is still loaded exactly
/// once, and the total wait is what it always was, only now it is visible.
public class BootLoader : MonoBehaviour
{
    void Start()
    {
        // waitForGameStart is off: this lands on the main menu, and no shift starts until the
        // player picks one. LoadRoutine holds a few frames past activation instead, which covers
        // the Awake/Start storm and the first rendered frame.
        LoadingScreen.LoadScene(GameScenes.Warehouse, "Opening the depot...",
                                waitForGameStart: false, look: LoadingScreen.Look.Splash);
    }
}
