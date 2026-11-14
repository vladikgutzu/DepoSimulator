using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>
/// Создаёт нормализованные ghost-референс-префабы в Resources/AssemblyReferences/.
/// Запускать один раз после изменения структуры объектов.
/// Можно перезапустить — идемпотентно.
/// </summary>
public class SetupGhostReferences
{
    private const string OutputFolder = "Assets/Resources/AssemblyReferences";

    public static void Execute()
    {
        // 1. Убедимся что папка Resources/AssemblyReferences существует
        EnsureFolder("Assets/Resources");
        EnsureFolder(OutputFolder);

        // 2. Создаём нормализованные префабы из объектов сцены
        BuildPrefabFromSceneGroup("GameplayRoot/AssemblyReferences/Ref_moto_drive", "order_moto_drive");
        BuildPrefabFromSceneGroup("GameplayRoot/AssemblyReferences/Ref_moto_frame", "order_moto_frame");
        BuildPrefabFromSceneGroup("GameplayRoot/AssemblyReferences/Ref_moto_arms",  "order_moto_arms");

        // 3. Удаляем старый контейнер AssemblyReferences из сцены
        var oldRefs = GameObject.Find("AssemblyReferences");
        if (oldRefs != null)
        {
            Object.DestroyImmediate(oldRefs);
            Debug.Log("[GhostRef] Удалён AssemblyReferences из сцены.");
        }

        // 4. Перемещаем AssemblyGhostVisualizer к верстаку
        var vizGO = GameObject.Find("AssemblyGhostVisualizer");
        if (vizGO != null)
        {
            var bench = GameObject.Find("AssemblyBench");
            if (bench != null)
            {
                Vector3 benchPos = bench.transform.position;
                // Чуть выше и спереди верстака — ghost появится над ним
                vizGO.transform.position = new Vector3(benchPos.x, benchPos.y + 0.6f, benchPos.z + 0.2f);
                Debug.Log($"[GhostRef] AssemblyGhostVisualizer → {vizGO.transform.position}");
            }
        }

        // 5. Assign material if not already set
        var viz = vizGO?.GetComponent<AssemblyGhostVisualizer>();
        if (viz != null)
        {
            var so = new SerializedObject(viz);
            var matProp = so.FindProperty("_ghostMaterial");
            if (matProp.objectReferenceValue == null)
            {
                var ghostMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/utility/M_shdr_alpha.mat");
                matProp.objectReferenceValue = ghostMat;
                so.ApplyModifiedProperties();
            }
            // Очищаем старый _entries если остался
            var entriesProp = so.FindProperty("_entries");
            if (entriesProp != null)
            {
                entriesProp.arraySize = 0;
                so.ApplyModifiedProperties();
            }
        }

        AssetDatabase.Refresh();
        EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Debug.Log("[GhostRef] Готово! Prefab-ы в Resources/AssemblyReferences/");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    static void BuildPrefabFromSceneGroup(string hierarchyPath, string prefabName)
    {
        // Найти GO в иерархии (поиск по полному пути)
        var parts = hierarchyPath.Split('/');
        GameObject current = null;
        foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (root.name == parts[0]) { current = root; break; }
        }
        for (int i = 1; i < parts.Length && current != null; i++)
        {
            var child = current.transform.Find(parts[i]);
            current = child != null ? child.gameObject : null;
        }

        if (current == null)
        {
            Debug.LogWarning($"[GhostRef] Не найден '{hierarchyPath}' — пропущен.");
            return;
        }

        // Клонируем чтобы не менять сцену
        var clone = Object.Instantiate(current);
        clone.name = prefabName;

        // Нормализуем: смещаем все дочерние GO так, чтобы центроид был на (0,0,0)
        // (clone root уже на 0,0,0 — children имеют localPos = исходные world-позиции)
        var renderers = clone.GetComponentsInChildren<MeshRenderer>(true);
        if (renderers.Length > 0)
        {
            Vector3 centroid = Vector3.zero;
            foreach (var r in renderers) centroid += r.transform.position;
            centroid /= renderers.Length;

            // Смещаем прямых детей на -centroid (clone root в мире на 0,0,0)
            foreach (Transform child in clone.transform)
                child.localPosition -= centroid;

            Debug.Log($"[GhostRef] {prefabName}: центроид {centroid:F2}, {renderers.Length} мешей");
        }

        // Сохраняем как prefab
        string savePath = $"{OutputFolder}/{prefabName}.prefab";
        PrefabUtility.SaveAsPrefabAsset(clone, savePath);
        Object.DestroyImmediate(clone);
        Debug.Log($"[GhostRef] Сохранён: {savePath}");
    }

    static void EnsureFolder(string path)
    {
        if (!AssetDatabase.IsValidFolder(path))
        {
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            var folder = Path.GetFileName(path);
            AssetDatabase.CreateFolder(parent, folder);
        }
    }
}
