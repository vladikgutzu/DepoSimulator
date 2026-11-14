using UnityEngine;

/// Swaps each shelf's sticker decal texture to show its ShelfRegistry number
/// (matching the minimap and the floating world label), using MaterialPropertyBlock
/// so no new material assets are needed. Client-local visual only — same pattern
/// as PartHighlighter's property-block tinting.
public class ShelfStickerNumberer : MonoBehaviour
{
    static readonly int DecalBaseID = Shader.PropertyToID("_decal_base");

    [Tooltip("Texture shown on the shelf whose ShelfRegistry number matches. " +
             "Shelves beyond #7 (no texture assigned) keep their default sticker.")]
    [SerializeField] Texture2D number1;
    [SerializeField] Texture2D number2;
    [SerializeField] Texture2D number3;
    [SerializeField] Texture2D number4;
    [SerializeField] Texture2D number5;
    [SerializeField] Texture2D number6;
    [SerializeField] Texture2D number7;
    [SerializeField] Texture2D number8;
    [SerializeField] Texture2D number9;
    [SerializeField] Texture2D number10;

    MaterialPropertyBlock _mpb;

    Texture2D[] Textures => new[]
    {
        number1, number2, number3, number4, number5,
        number6, number7, number8, number9, number10
    };

    void Start() => Apply();

    public void Apply()
    {
        _mpb ??= new MaterialPropertyBlock();
        var textures = Textures;

        foreach (var entry in ShelfRegistry.GetOrderedShelves())
        {
            int idx = entry.Number - 1;
            if (idx < 0 || idx >= textures.Length || textures[idx] == null) continue;

            foreach (var r in entry.GO.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.sharedMaterial == null || r.sharedMaterial.name != "shelfLarge_sticker") continue;

                _mpb.Clear();
                r.GetPropertyBlock(_mpb);
                _mpb.SetTexture(DecalBaseID, textures[idx]);
                r.SetPropertyBlock(_mpb);
            }
        }
    }
}
