using UnityEngine;

/// Puts a weapon model into a hand, for whoever needs it done.
///
/// Two of them do: MeleeCombat, for the player holding it, and NetworkPlayer, for the puppet every
/// other client watches. The grip has to come out identical in both — a bat that sits in your fist
/// one way and in your teammate's another is the same bug as a box that only one of you can see.
public static class WeaponVisualPlacer
{
    /// Parents the model to the bone and moves it so its grip anchor lands exactly on that bone,
    /// falling back to the asset's own numbers for a model with no anchors.
    public static void PlaceInHand(GameObject visual, Transform hand, MeleeWeaponKind kind)
    {
        if (visual == null) return;

        visual.transform.SetParent(hand != null ? hand : visual.transform.parent, false);

        var anchors = visual.GetComponent<WeaponGripAnchors>();
        if (anchors != null && anchors.MainGrip != null)
        {
            var weapon = visual.transform;

            // The anchor's pose relative to the model root, which is fixed by how it was authored.
            Quaternion gripLocalRot = Quaternion.Inverse(weapon.rotation) * anchors.MainGrip.rotation;
            Vector3    gripLocalPos = weapon.InverseTransformPoint(anchors.MainGrip.position);

            weapon.localRotation = Quaternion.Inverse(gripLocalRot);
            weapon.localPosition = -(weapon.localRotation * gripLocalPos);
            return;
        }

        var def = MeleeWeaponRegistry.Get(kind);
        visual.transform.localPosition = def != null ? def.HandOffset : new Vector3(0.03f, 0.06f, 0.02f);
        visual.transform.localRotation = Quaternion.Euler(def != null ? def.HandRotation : Vector3.zero);
    }

    /// Makes a held model inert without taking its physics away: colliders off, so it cannot shove
    /// the character around, and the body kinematic, so it rides the bone instead of falling out of
    /// the hand.
    ///
    /// Switched off rather than stripped (it used to be Destroy) because the same object goes back
    /// to the world when it is let go — see Release. A weapon that had its body torn out on pickup
    /// had nothing to fall with, which is why a drop used to throw the model away and conjure a new
    /// one on the floor.
    public static void Hold(GameObject visual)
    {
        if (visual == null) return;

        EnsurePhysics(visual);

        foreach (var col in visual.GetComponentsInChildren<Collider>(true)) col.enabled = false;
        foreach (var rb in visual.GetComponentsInChildren<Rigidbody>(true))
        {
            rb.isKinematic       = true;
            rb.detectCollisions  = false;
            // Interpolation on a kinematic body parented to an animated bone makes it trail the
            // hand by a frame.
            rb.interpolation     = RigidbodyInterpolation.None;
        }
    }

    /// Takes the model off whatever it hangs from — the hand bone — exactly where it is, and hands
    /// it to physics with the given velocity. Colliders in `ignore` (the body that just let go of
    /// it) never touch it, or the character's capsule would kick it away on the first frame.
    public static Rigidbody Release(GameObject visual, Vector3 velocity, Collider[] ignore = null)
    {
        if (visual == null) return null;

        visual.transform.SetParent(null, true);
        EnsurePhysics(visual);

        var rb = visual.GetComponent<Rigidbody>();
        foreach (var col in visual.GetComponentsInChildren<Collider>(true))
        {
            col.enabled = true;
            if (ignore == null) continue;
            foreach (var other in ignore)
                if (other != null && other != col) Physics.IgnoreCollision(col, other, true);
        }

        rb.isKinematic      = false;
        rb.detectCollisions = true;
        rb.interpolation    = RigidbodyInterpolation.Interpolate;
        rb.linearVelocity   = velocity;
        // A little tumble so it lands like something let go of, not like something placed.
        rb.angularVelocity  = visual.transform.right * 3f;
        rb.WakeUp();
        return rb;
    }

    /// Gives a model a body and a solid collider on its root if it has none.
    ///
    /// The collider is a box fitted to the meshes in the root's own space, so it follows the model
    /// however the artist oriented it — the bat runs down -Y, the rest along +Z. It sits on the
    /// root because MeleeWeaponItem needs one there: interaction raycasts ignore triggers and look
    /// for the pickup on the collider they hit.
    public static void EnsurePhysics(GameObject visual)
    {
        if (visual == null) return;

        if (visual.GetComponent<Collider>() == null)
        {
            var box = visual.AddComponent<BoxCollider>();
            FitBox(visual.transform, box);
        }

        var rb = visual.GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = visual.AddComponent<Rigidbody>();
            ConfigureBody(rb);
        }
    }

    /// The body every weapon gets: a couple of kilos, and continuous collision because a bat is a
    /// thin fast thing that would otherwise tunnel through the floor when it lands on its end.
    public static void ConfigureBody(Rigidbody rb)
    {
        rb.mass                   = 1.5f;
        rb.linearDamping          = 0.05f;
        rb.angularDamping         = 0.5f;
        rb.interpolation          = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
    }

    /// Sizes a box collider to the meshes under `root`, measured in root space.
    public static void FitBox(Transform root, BoxCollider box)
    {
        bool any = false;
        var b = new Bounds();

        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            var mb = mf.sharedMesh.bounds;
            var toRoot = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;

            for (int i = 0; i < 8; i++)
            {
                var corner = mb.center + Vector3.Scale(mb.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = toRoot.MultiplyPoint3x4(corner);
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                else b.Encapsulate(p);
            }
        }

        if (!any) { box.center = Vector3.zero; box.size = new Vector3(0.1f, 0.1f, 0.6f); return; }

        // A thin wooden shaft still needs some thickness to rest on the floor without jittering.
        var size = b.size;
        size.x = Mathf.Max(size.x, 0.04f);
        size.y = Mathf.Max(size.y, 0.04f);
        size.z = Mathf.Max(size.z, 0.04f);

        box.center = b.center;
        box.size   = size;
    }
}
