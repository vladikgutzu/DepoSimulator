using UnityEditor;
using UnityEngine;

/// Writes the grip you just set up in play mode back into the weapon's prefab and asset.
///
/// The weapon in the player's hand is a copy built at equip time and it dies with the play
/// session, so a grip tuned there is lost on Stop unless it is copied out.
///
/// What counts as "the grip" depends on where the weapon is when you save:
///
///   • In the fist, which is the normal case — its pose relative to the hand bone IS the grip.
///     Drag the weapon itself in the hierarchy until it sits right; that pose is what is written.
///     Both places that decide a weapon's placement are written from it, so the asset's
///     HandOffset/HandRotation and the model's GripPoint cannot end up disagreeing.
///
///   • On the carry socket, which nothing uses at the moment — the anchors are read as they stand
///     and the weapon's pose on the socket is saved alongside them.
///
/// MeleeCombat's live grip tuning re-seats the weapon from its anchor every frame; with it on the
/// weapon cannot be dragged at all, which is why it is off by default.
public static class SaveHeldWeaponGrip
{
    [MenuItem("Tools/Warehouse/Save Held Weapon Grip To Prefab")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        if (!Application.isPlaying) return "play mode only — there is no weapon in hand otherwise";

        var combat = Object.FindAnyObjectByType<MeleeCombat>();
        if (combat == null) return "no MeleeCombat in the running scene";

        var held = combat.WeaponVisual;
        if (held == null) return "nothing in hand — pick a weapon up first";

        var anchors = held.GetComponent<WeaponGripAnchors>();
        if (anchors == null || anchors.MainGrip == null)
            return $"{held.name} has no grip anchors — run Tools/Warehouse/Add Weapon Grip Anchors";

        var def = MeleeWeaponRegistry.Get(combat.CurrentWeapon);
        if (def == null || def.Prefab == null)
            return $"{combat.CurrentWeapon} has no prefab to write to";

        var parent   = held.transform.parent;
        bool inHand  = parent != null && parent.name.Contains("Hand");
        bool onSocket = parent != null && parent.name.Contains("Socket");

        // ── What the grip is ──────────────────────────────────────────────────
        Vector3    gripPos;
        Quaternion gripRot;
        string     where;

        if (inHand)
        {
            Vector3    localPos = held.transform.localPosition;
            Quaternion localRot = held.transform.localRotation;

            def.HandOffset   = localPos;
            def.HandRotation = localRot.eulerAngles;

            // Inverse of MeleeCombat.PlaceByGrip: the anchor has to sit where it produces exactly
            // this pose when it is matched onto the hand bone.
            gripRot = Quaternion.Inverse(localRot);
            gripPos = -(gripRot * localPos);

            where = $"  in hand at   pos {localPos}  rot {localRot.eulerAngles}";
        }
        else
        {
            // Not in the fist: the anchors are the only statement of the grip, so they are read
            // rather than derived.
            gripPos = held.transform.InverseTransformPoint(anchors.MainGrip.position);
            gripRot = Quaternion.Inverse(held.transform.rotation) * anchors.MainGrip.rotation;

            def.HandRotation = Quaternion.Inverse(gripRot).eulerAngles;
            def.HandOffset   = -(Quaternion.Inverse(gripRot) * gripPos);

            where = $"  GripPoint at pos {gripPos}  rot {gripRot.eulerAngles}";
        }

        Vector3 offPos = anchors.OffHandGrip != null
            ? held.transform.InverseTransformPoint(anchors.OffHandGrip.position)
            : Vector3.zero;

        // ── The socket pose, when that is where it is hanging ─────────────────
        string socketLine = "";
        if (onSocket)
        {
            def.CarryOffset   = held.transform.localPosition;
            def.CarryRotation = held.transform.localRotation.eulerAngles;

            var settings = LoadOrCreateSocketSettings();
            settings.LocalPosition = parent.localPosition;
            settings.LocalRotation = parent.localRotation.eulerAngles;
            EditorUtility.SetDirty(settings);

            socketLine = $"\n  on socket at pos {def.CarryOffset}  rot {def.CarryRotation}" +
                         $"\n  socket at    pos {settings.LocalPosition}";
        }

        // ── Write ─────────────────────────────────────────────────────────────
        string path = AssetDatabase.GetAssetPath(def.Prefab);
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            var prefabAnchors = root.GetComponent<WeaponGripAnchors>();
            if (prefabAnchors == null || prefabAnchors.MainGrip == null)
                return $"{System.IO.Path.GetFileName(path)} has no anchors to write to";

            prefabAnchors.MainGrip.localPosition = gripPos;
            prefabAnchors.MainGrip.localRotation = gripRot;

            if (anchors.OffHandGrip != null && prefabAnchors.OffHandGrip != null)
                prefabAnchors.OffHandGrip.localPosition = offPos;

            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }

        EditorUtility.SetDirty(def);
        AssetDatabase.SaveAssets();

        return $"{combat.CurrentWeapon}: saved to {System.IO.Path.GetFileName(path)} and its asset\n" +
               where + socketLine +
               "\n  GripPoint and the asset numbers now describe the same grip";
    }

    /// The socket settings asset, made on first save so nobody has to create it by hand.
    static WeaponCarrySocketSettings LoadOrCreateSocketSettings()
    {
        const string path = "Assets/Resources/" + WeaponCarrySocketSettings.ResourcePath + ".asset";

        var settings = AssetDatabase.LoadAssetAtPath<WeaponCarrySocketSettings>(path);
        if (settings != null) return settings;

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
        settings = ScriptableObject.CreateInstance<WeaponCarrySocketSettings>();
        AssetDatabase.CreateAsset(settings, path);
        return settings;
    }
}
