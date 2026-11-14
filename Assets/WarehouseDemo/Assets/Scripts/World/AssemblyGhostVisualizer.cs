using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Shows a ghost silhouette of the assembly for the active order.
///
/// HOW TO ADD A NEW OBJECT:
///   1. Prepare a prefab: root at (0,0,0), parts at correct offsets relative to the root
///   2. Name the prefab EXACTLY like the OrderDefinition name (e.g. "order_moto_drive")
///   3. Put it in  Assets/Resources/AssemblyReferences/
///   Done — the ghost will appear automatically when this order is accepted.
///
/// This GO should sit where the ghost should appear (near the bench).
/// </summary>
public class AssemblyGhostVisualizer : MonoBehaviour
{
    private const string ResourceFolder = "AssemblyReferences";

    [Tooltip("Ghost material. Assign: Assets/Materials/utility/M_shdr_alpha.mat")]
    [SerializeField] private Material _ghostMaterial;

    [Tooltip("If assigned — the ghost spawns here (e.g. a Transform on the pallet surface). " +
             "If null — uses this GO's position.")]
    [SerializeField] private Transform _ghostAnchor;

    private readonly List<GameObject> _activeGhosts = new();

    // ─── Unity callbacks ──────────────────────────────────────────────────────

    private void Start()
    {
        Subscribe();
        if (GameManager.Instance?.ActiveOrder != null)
            ShowGhostsFor(GameManager.Instance.ActiveOrder);
    }

    private void OnEnable()  => Subscribe();
    private void OnDisable() { Unsubscribe(); ClearGhosts(); }
    private void OnDestroy() => ClearGhosts();

    // ─── Subscriptions ────────────────────────────────────────────────────────

    private void Subscribe()
    {
        if (GameManager.Instance == null) return;
        GameManager.Instance.OnOrderAccepted  -= OnOrderAccepted;
        GameManager.Instance.OnOrderCompleted -= OnOrderCompleted;
        GameManager.Instance.OnOrderAccepted  += OnOrderAccepted;
        GameManager.Instance.OnOrderCompleted += OnOrderCompleted;
    }

    private void Unsubscribe()
    {
        if (GameManager.Instance == null) return;
        GameManager.Instance.OnOrderAccepted  -= OnOrderAccepted;
        GameManager.Instance.OnOrderCompleted -= OnOrderCompleted;
    }

    private void OnOrderAccepted(OrderDefinition order) => ShowGhostsFor(order);
    private void OnOrderCompleted(OrderDefinition _)    => ClearGhosts();

    // ─── Public API ───────────────────────────────────────────────────────────

    public void ShowGhostsFor(OrderDefinition order)
    {
        ClearGhosts();
        if (order == null || _ghostMaterial == null) return;

        var prefab = Resources.Load<GameObject>($"{ResourceFolder}/{order.name}");
        if (prefab == null)
        {
            Debug.LogWarning($"[AssemblyGhost] No reference for '{order.name}'. " +
                             $"Put a prefab at Resources/{ResourceFolder}/{order.name}.prefab");
            return;
        }

        // Instantiate temporarily at the anchor position (pallet or own GO)
        var anchor = _ghostAnchor != null ? _ghostAnchor : transform;
        var temp = Instantiate(prefab, anchor.position, anchor.rotation);
        temp.SetActive(false);

        SpawnGhostsFrom(temp);

        Destroy(temp);
    }

    public void ClearGhosts()
    {
        foreach (var g in _activeGhosts)
            if (g != null) Destroy(g);
        _activeGhosts.Clear();
    }

    // ─── Internal ─────────────────────────────────────────────────────────────

    private void SpawnGhostsFrom(GameObject source)
    {
        foreach (var src in source.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mf = src.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            var ghost = new GameObject($"Ghost_{src.name}");
            ghost.transform.SetPositionAndRotation(src.transform.position, src.transform.rotation);
            ghost.transform.localScale = src.transform.lossyScale;

            ghost.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;

            var mr = ghost.AddComponent<MeshRenderer>();
            var mats = new Material[src.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = _ghostMaterial;
            mr.sharedMaterials = mats;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows    = false;

            _activeGhosts.Add(ghost);
        }
    }
}
